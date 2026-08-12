using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Motara.Tracking.Abstractions;

namespace Motara.App.Tracking;

internal readonly record struct MediaPipeInputFrame(
    ReadOnlyMemory<byte> Rgba,
    int Width,
    int Height,
    long TimestampMilliseconds);

internal interface IMediaPipeFrameProvider : IAsyncDisposable
{
    IAsyncEnumerable<MediaPipeInputFrame> ReadFramesAsync(CancellationToken cancellationToken);
}

internal interface IMediaPipeFrameProviderFactory
{
    ValueTask<IMediaPipeFrameProvider> CreateAsync(CancellationToken cancellationToken);
}

internal sealed class MediaPipeLocalTrackingSourceFactory : ITrackingSourceFactory
{
    internal const string SourceId = "mediapipe.local-camera";
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly string libraryPath;
    private readonly string modelPath;
    private readonly IMediaPipeFrameProviderFactory? frameProviderFactory;

    internal MediaPipeLocalTrackingSourceFactory(
        TimeProvider timeProvider,
        string? baseDirectory = null,
        ILogger? logger = null,
        IMediaPipeFrameProviderFactory? frameProviderFactory = null)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        string root = baseDirectory ?? AppContext.BaseDirectory;
        libraryPath = MediaPipeNativePaths.ResolveLibraryPath(
            root,
            Environment.GetEnvironmentVariable("MOTARA_MEDIAPIPE_NATIVE"));
        modelPath = MediaPipeNativePaths.ResolveModelPath(
            root,
            Environment.GetEnvironmentVariable("MOTARA_MEDIAPIPE_MODEL"));
        this.frameProviderFactory = frameProviderFactory;
    }

    public TrackingSourceDescriptor Descriptor { get; } = new(
        SourceId,
        "google-mediapipe",
        "Menu.Tracking.Source.MediaPipe",
        "Icon.Lucide.Camera",
        [TrackingChannel.Face]);

    public async ValueTask<TrackingSourceAvailability> CheckAvailabilityAsync(
        TrackingChannel channel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (channel != TrackingChannel.Face)
        {
            return TrackingSourceAvailability.Unavailable("tracking.channel.unsupported");
        }

        TrackingSourceAvailability availability = MediaPipeNativePaths.CheckAvailability(
            libraryPath,
            modelPath);
        if (!availability.IsAvailable)
        {
            MediaPipeTrackingLog.RuntimeUnavailable(logger, availability.ReasonCode!);
            return availability;
        }

        if (frameProviderFactory is null)
        {
            MediaPipeTrackingLog.CameraProviderUnavailable(logger);
            return TrackingSourceAvailability.Unavailable("tracking.mediapipe.camera_provider_missing");
        }

        if (frameProviderFactory is IMediaPipeFrameProviderAvailability availabilityProvider)
        {
            TrackingSourceAvailability cameraAvailability = await availabilityProvider
                .CheckAvailabilityAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!cameraAvailability.IsAvailable)
            {
                MediaPipeTrackingLog.CameraProviderUnavailable(logger);
                return cameraAvailability;
            }
        }

        return TrackingSourceAvailability.Available;
    }

    public async ValueTask<ITrackingSource> CreateAsync(
        TrackingChannel channel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (channel != TrackingChannel.Face)
        {
            throw new InvalidOperationException("MediaPipe supports only the face channel.");
        }

        TrackingSourceAvailability availability = await CheckAvailabilityAsync(
            channel,
            cancellationToken).ConfigureAwait(false);
        if (!availability.IsAvailable)
        {
            throw new InvalidOperationException(availability.ReasonCode);
        }

        if (frameProviderFactory is null)
        {
            throw new InvalidOperationException("MediaPipe camera provider is unavailable.");
        }

        return new MediaPipeLocalTrackingSource(
            libraryPath,
            modelPath,
            timeProvider,
            frameProviderFactory,
            logger);
    }
}

internal sealed class MediaPipeLocalTrackingSource : ITrackingSource, ITrackingSourceOutputLayout
{
    private const int BlendshapeCount = 52;
    private readonly string libraryPath;
    private readonly string modelPath;
    private readonly TimeProvider timeProvider;
    private readonly IMediaPipeFrameProviderFactory frameProviderFactory;
    private readonly ILogger logger;
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;

    internal MediaPipeLocalTrackingSource(
        string libraryPath,
        string modelPath,
        TimeProvider timeProvider,
        IMediaPipeFrameProviderFactory frameProviderFactory,
        ILogger logger)
    {
        this.libraryPath = libraryPath;
        this.modelPath = modelPath;
        this.timeProvider = timeProvider;
        this.frameProviderFactory = frameProviderFactory;
        this.logger = logger;
    }

    public string SourceId => MediaPipeLocalTrackingSourceFactory.SourceId;

    public IReadOnlyList<TrackingOutputDefinition> OutputDefinitions { get; } = CreateOutputDefinitions();

    public async IAsyncEnumerable<RawTrackingFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetime.Token);
        if (!MediaPipeNativeLibrary.TryLoad(libraryPath, out MediaPipeNativeLibrary? library, out string? error)
            || library is null)
        {
            MediaPipeTrackingLog.NativeLoadFailed(logger, error ?? "unknown");
            throw new InvalidOperationException("MediaPipe native runtime could not be loaded.");
        }

        using (library)
        await using (IMediaPipeFrameProvider provider = await frameProviderFactory
            .CreateAsync(linked.Token)
            .ConfigureAwait(false))
        using (MediaPipeNativeSession session = library.CreateSession(modelPath))
        {
            long startedTimestamp = timeProvider.GetTimestamp();
            long sequence = 0;
            MediaPipeTrackingLog.Started(logger);
            try
            {
                await foreach (MediaPipeInputFrame input in provider.ReadFramesAsync(linked.Token)
                    .ConfigureAwait(false))
                {
                    MediaPipeNativeFrame nativeFrame = session.Process(
                        input.Rgba,
                        input.Width,
                        input.Height,
                        input.TimestampMilliseconds);
                    double[] values = MediaPipeBlendshapeMapper.Map(
                        nativeFrame.Blendshapes,
                        BlendshapeCount)
                        .Select(static value => value * 100d)
                        .ToArray();
                    ParameterValidity[] validity = Enumerable.Repeat(
                        nativeFrame.FaceDetected
                            ? ParameterValidity.Valid
                            : ParameterValidity.Missing,
                        BlendshapeCount)
                        .ToArray();
                    yield return new RawTrackingFrame(
                        SourceId,
                        sequence++,
                        timeProvider.GetElapsedTime(startedTimestamp),
                        DateTimeOffset.UtcNow,
                        values,
                        validity,
                        GetPresence(nativeFrame.FaceDetected));
                }
            }
            finally
            {
                MediaPipeTrackingLog.Stopped(logger);
            }
        }
    }

    internal static TrackingPresence GetPresence(bool faceDetected) =>
        faceDetected ? TrackingPresence.Tracked : TrackingPresence.Lost;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            lifetime.Cancel();
            lifetime.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static ImmutableArray<TrackingOutputDefinition> CreateOutputDefinitions()
    {
        string[] names =
        [
            "jawOpen", "browDown_L", "browDown_R", "browInnerUp", "browOuterUp_L", "browOuterUp_R",
            "cheekPuff", "cheekSquint_L", "cheekSquint_R", "eyeBlink_L", "eyeBlink_R",
            "eyeLookDown_L", "eyeLookDown_R", "eyeLookIn_L", "eyeLookIn_R", "eyeLookOut_L",
            "eyeLookOut_R", "eyeLookUp_L", "eyeLookUp_R", "eyeSquint_L", "eyeSquint_R",
            "eyeWide_L", "eyeWide_R", "jawForward", "jawLeft", "jawRight", "mouthClose",
            "mouthDimple_L", "mouthDimple_R", "mouthFrown_L", "mouthFrown_R", "mouthFunnel",
            "mouthLeft", "mouthLowerDown_L", "mouthLowerDown_R", "mouthPress_L", "mouthPress_R",
            "mouthPucker", "mouthRight", "mouthRollLower", "mouthRollUpper", "mouthShrugLower",
            "mouthShrugUpper", "mouthSmile_L", "mouthSmile_R", "mouthStretch_L", "mouthStretch_R",
            "mouthUpperUp_L", "mouthUpperUp_R", "noseSneer_L", "noseSneer_R", "tongueOut",
        ];
        return names
            .Select(static name => new TrackingOutputDefinition(
                $"BlendShape.{name}Percent",
                0,
                0,
                100,
                0))
            .ToImmutableArray();
    }
}

internal static partial class MediaPipeTrackingLog
{
    [LoggerMessage(6800, LogLevel.Information, "MediaPipe tracking source started")]
    internal static partial void Started(ILogger logger);

    [LoggerMessage(6801, LogLevel.Information, "MediaPipe tracking source stopped")]
    internal static partial void Stopped(ILogger logger);

    [LoggerMessage(6802, LogLevel.Warning, "MediaPipe runtime unavailable; reason={ReasonCode}")]
    internal static partial void RuntimeUnavailable(ILogger logger, string reasonCode);

    [LoggerMessage(6803, LogLevel.Warning, "MediaPipe camera frame provider is unavailable")]
    internal static partial void CameraProviderUnavailable(ILogger logger);

    [LoggerMessage(6804, LogLevel.Error, "MediaPipe native runtime failed to load: {Error}")]
    internal static partial void NativeLoadFailed(ILogger logger, string error);
}
