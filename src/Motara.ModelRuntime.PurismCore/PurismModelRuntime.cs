using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.ModelRuntime.Abstractions;

namespace Motara.ModelRuntime.PurismCore;

public sealed class PurismModelRuntime : IModelRuntime
{
    private readonly IPurismModelLoader _loader;
    private readonly ILogger<PurismModelRuntime> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPurismModelSession? _session;
    private ModelRenderFrame? _currentFrame;
    private long _frameRevision;
    private bool _disposed;

    public PurismModelRuntime()
        : this(new PurismModelLoader(), NullLogger<PurismModelRuntime>.Instance)
    {
    }

    internal PurismModelRuntime(IPurismModelLoader loader)
        : this(loader, NullLogger<PurismModelRuntime>.Instance)
    {
    }

    internal PurismModelRuntime(
        IPurismModelLoader loader,
        ILogger<PurismModelRuntime> logger)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(logger);
        _loader = loader;
        _logger = logger;
    }

    public ModelRuntimeState State { get; private set; } = ModelRuntimeState.Empty;

    public ModelCapabilities? Capabilities { get; private set; }

    public ModelRenderFrame? CurrentFrame => Volatile.Read(ref _currentFrame);

    public async Task<ModelLoadResult> LoadAsync(
        ModelLoadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool hadLoadedModel = CurrentFrame is not null;
            State = ModelRuntimeState.Loading;
            PurismRuntimeLog.LoadStarted(_logger);

            IPurismModelSession? candidate = null;
            byte[]? nativeModelBytes = null;
            try
            {
                nativeModelBytes = await ReadNativeModelAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                candidate = await _loader.LoadAsync(nativeModelBytes, cancellationToken)
                    .ConfigureAwait(false);
                PurismRuntimeLog.NativeLoadCompleted(_logger);
                cancellationToken.ThrowIfCancellationRequested();
                PurismModelSnapshot snapshot = PurismModelSnapshotBuilder.Build(
                    candidate,
                    request.TextureAssetIds.Length,
                    request.ParameterNames);

                IPurismModelSession? previous = _session;
                _session = candidate;
                candidate = null;
                Capabilities = snapshot.Capabilities;
                SetCurrentFrame(snapshot.Frame);
                _frameRevision = snapshot.Frame.Revision;
                State = ModelRuntimeState.Loaded;
                previous?.Dispose();
                if (snapshot.SelfMaskReferenceCount > 0)
                {
                    PurismRuntimeLog.SelfMasksLoaded(
                        _logger,
                        snapshot.SelfMaskReferenceCount);
                }
                if (snapshot.InvertedMaskCount > 0)
                {
                    PurismRuntimeLog.InvertedMasksLoaded(_logger, snapshot.InvertedMaskCount);
                }
                if (snapshot.NonDefaultBlendColorCount > 0)
                {
                    PurismRuntimeLog.BlendColorsLoaded(
                        _logger,
                        snapshot.NonDefaultBlendColorCount);
                }
                PurismRuntimeLog.LoadCompleted(
                    _logger,
                    snapshot.Capabilities.Parameters.Length,
                    snapshot.Frame.Drawables.Length,
                    snapshot.Capabilities.TextureCount);
                return ModelLoadResult.Loaded(snapshot.Capabilities, snapshot.Frame);
            }
            catch (OperationCanceledException)
            {
                State = hadLoadedModel ? ModelRuntimeState.Loaded : ModelRuntimeState.Empty;
                PurismRuntimeLog.LoadCancelled(_logger);
                throw;
            }
            catch (Exception exception)
            {
                State = hadLoadedModel ? ModelRuntimeState.Degraded : ModelRuntimeState.Faulted;
                ModelError error = MapError(exception, request.NativeModelAssetId);
                PurismRuntimeLog.LoadFailed(_logger, error.Code, exception.GetType().Name);
                return ModelLoadResult.Failed(error);
            }
            finally
            {
                candidate?.Dispose();
                if (nativeModelBytes is not null)
                {
                    CryptographicOperations.ZeroMemory(nativeModelBytes);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<byte[]> ReadNativeModelAsync(
        ModelLoadRequest request,
        CancellationToken cancellationToken)
    {
        long length = await request.Assets.GetLengthAsync(
            request.NativeModelAssetId,
            cancellationToken).ConfigureAwait(false);
        if (length <= 0 || length > PurismModelLoader.MaximumNativeModelBytes || length > int.MaxValue)
        {
            throw new InvalidDataException("The native model file size is invalid.");
        }

        byte[] bytes = new byte[checked((int)length)];
        try
        {
            await using Stream stream = await request.Assets.OpenReadAsync(
                request.NativeModelAssetId,
                cancellationToken).ConfigureAwait(false);
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    public async ValueTask<bool> ApplyParametersAsync(
        ModelParameterUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is null || Capabilities is null || State != ModelRuntimeState.Loaded)
            {
                return false;
            }

            foreach (ModelParameterValue value in update.Values)
            {
                if ((uint)value.ParameterIndex >= (uint)Capabilities.Parameters.Length)
                {
                    return false;
                }
            }

            if (update.Values.Length == 0 && update.PartOpacities.Length == 0)
            {
                return false;
            }

            _session.ApplyParameters(update.Values.AsSpan(), update.PartOpacities.AsSpan());
            SetCurrentFrame(PurismModelSnapshotBuilder.BuildFrame(
                _session,
                Capabilities.TextureCount,
                Capabilities.Canvas,
                checked(++_frameRevision)));
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _session?.Dispose();
            _session = null;
            Capabilities = null;
            SetCurrentFrame(null);
            State = ModelRuntimeState.Disposed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SetCurrentFrame(ModelRenderFrame? frame) =>
        Volatile.Write(ref _currentFrame, frame);

    private static ModelError MapError(Exception exception, string subject) => exception switch
    {
        NativeLibraryUnavailableException => new(ModelErrorCode.NativeLibraryUnavailable),
        FileNotFoundException => new(ModelErrorCode.MissingReference, subject),
        InvalidDataException => new(ModelErrorCode.IncompatibleMoc3, subject),
        IOException or UnauthorizedAccessException => new(ModelErrorCode.IoFailure, subject),
        _ => new(ModelErrorCode.NativeCallFailed),
    };
}

public sealed class PurismModelRuntimeFactory : IModelRuntimeFactory
{
    private readonly ILogger<PurismModelRuntime> logger;

    public PurismModelRuntimeFactory()
        : this(NullLogger<PurismModelRuntime>.Instance)
    {
    }

    public PurismModelRuntimeFactory(ILogger<PurismModelRuntime> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        this.logger = logger;
    }

    public IModelRuntime Create() => new PurismModelRuntime(new PurismModelLoader(), logger);
}

internal static partial class PurismRuntimeLog
{
    [LoggerMessage(6000, LogLevel.Information, "PurismCore model load started")]
    internal static partial void LoadStarted(ILogger logger);

    [LoggerMessage(6001, LogLevel.Debug, "PurismCore native data loaded")]
    internal static partial void NativeLoadCompleted(ILogger logger);

    [LoggerMessage(6002, LogLevel.Information,
        "PurismCore model load completed with {ParameterCount} parameters, {DrawableCount} drawables, and {TextureCount} textures")]
    internal static partial void LoadCompleted(
        ILogger logger,
        int parameterCount,
        int drawableCount,
        int textureCount);

    [LoggerMessage(6003, LogLevel.Warning,
        "PurismCore model load failed with {ErrorCode} from {ExceptionType}")]
    internal static partial void LoadFailed(
        ILogger logger,
        ModelErrorCode errorCode,
        string exceptionType);

    [LoggerMessage(6004, LogLevel.Debug, "PurismCore model load cancelled")]
    internal static partial void LoadCancelled(ILogger logger);

    [LoggerMessage(6005, LogLevel.Debug,
        "PurismCore loaded {SelfMaskReferenceCount} self-referencing drawable masks")]
    internal static partial void SelfMasksLoaded(
        ILogger logger,
        int selfMaskReferenceCount);

    [LoggerMessage(6006, LogLevel.Debug,
        "PurismCore loaded {InvertedMaskCount} inverted drawable masks")]
    internal static partial void InvertedMasksLoaded(
        ILogger logger,
        int invertedMaskCount);

    [LoggerMessage(6007, LogLevel.Debug,
        "PurismCore loaded {BlendColorDrawableCount} drawables with non-default blend colors")]
    internal static partial void BlendColorsLoaded(
        ILogger logger,
        int blendColorDrawableCount);
}
