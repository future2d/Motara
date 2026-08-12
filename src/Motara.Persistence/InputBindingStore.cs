using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Motara.Persistence;

public interface IInputBindingStore
{
    Task<InputBindingProfile> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(InputBindingProfile profile, CancellationToken cancellationToken);
}

public sealed class InputBindingStore : IInputBindingStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AccessGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string targetPath;
    private readonly SemaphoreSlim accessGate;
    private readonly ILogger<InputBindingStore> logger;

    public InputBindingStore(string targetPath)
        : this(targetPath, NullLogger<InputBindingStore>.Instance)
    {
    }

    public InputBindingStore(string targetPath, ILogger<InputBindingStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(logger);
        this.targetPath = Path.GetFullPath(targetPath);
        this.logger = logger;
        accessGate = AccessGates.GetOrAdd(this.targetPath, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<InputBindingProfile> LoadAsync(CancellationToken cancellationToken)
    {
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(targetPath))
            {
                return InputBindingProfile.Default;
            }

            try
            {
                await using FileStream stream = new(
                    targetPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                InputBindingProfile profile = await JsonSerializer.DeserializeAsync<InputBindingProfile>(
                    stream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false)
                    ?? InputBindingProfile.Default;
                profile.Validate();
                InputBindingStoreLog.LoadCompleted(logger, profile.SchemaVersion, profile.Bindings.Length);
                return profile;
            }
            catch (JsonException exception)
            {
                InputBindingStoreLog.LoadFailed(logger, exception.GetType().Name);
                return InputBindingProfile.Default;
            }
            catch (ArgumentException exception)
            {
                InputBindingStoreLog.LoadFailed(logger, exception.GetType().Name);
                return InputBindingProfile.Default;
            }
        }
        finally
        {
            accessGate.Release();
        }
    }

    public async Task SaveAsync(InputBindingProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string directory = Path.GetDirectoryName(targetPath)
                ?? throw new InvalidOperationException("Input binding path requires a directory.");
            Directory.CreateDirectory(directory);
            string temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (FileStream stream = new(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        profile,
                        SerializerOptions,
                        cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, targetPath, overwrite: true);
                InputBindingStoreLog.SaveCompleted(logger, profile.SchemaVersion, profile.Bindings.Length);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            accessGate.Release();
        }
    }
}

internal static partial class InputBindingStoreLog
{
    [LoggerMessage(2010, LogLevel.Information,
        "Input bindings loaded with schema {SchemaVersion} and {BindingCount} active bindings")]
    internal static partial void LoadCompleted(ILogger logger, int schemaVersion, int bindingCount);

    [LoggerMessage(2011, LogLevel.Warning,
        "Input binding load failed with {ErrorType}; defaults restored")]
    internal static partial void LoadFailed(ILogger logger, string errorType);

    [LoggerMessage(2012, LogLevel.Debug,
        "Input bindings saved with schema {SchemaVersion} and {BindingCount} active bindings")]
    internal static partial void SaveCompleted(ILogger logger, int schemaVersion, int bindingCount);
}
