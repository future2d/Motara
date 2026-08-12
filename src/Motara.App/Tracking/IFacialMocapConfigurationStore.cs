using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Motara.App.Tracking;

internal interface IIFacialMocapConfigurationStore
{
    Task<IFacialMocapConfiguration?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(
        IFacialMocapConfiguration configuration,
        CancellationToken cancellationToken);
}

internal sealed class IFacialMocapConfigurationStore : IIFacialMocapConfigurationStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AccessGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string targetPath;
    private readonly SemaphoreSlim accessGate;
    private readonly ILogger<IFacialMocapConfigurationStore> logger;

    internal IFacialMocapConfigurationStore(
        string targetPath,
        ILogger<IFacialMocapConfigurationStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        this.targetPath = Path.GetFullPath(targetPath);
        this.logger = logger ?? NullLogger<IFacialMocapConfigurationStore>.Instance;
        accessGate = AccessGates.GetOrAdd(this.targetPath, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<IFacialMocapConfiguration?> LoadAsync(CancellationToken cancellationToken)
    {
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(targetPath))
            {
                IFacialMocapConfigurationLog.Loaded(logger, configured: false);
                return null;
            }

            try
            {
                await using FileStream stream = new(
                    targetPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                IFacialMocapConfiguration? configuration =
                    await JsonSerializer.DeserializeAsync<IFacialMocapConfiguration>(
                        stream,
                        SerializerOptions,
                        cancellationToken).ConfigureAwait(false);
                if (configuration is null)
                {
                    IFacialMocapConfigurationLog.LoadFailed(logger, "EmptyDocument");
                    return null;
                }

                IFacialMocapConfiguration.Validate(configuration);
                IFacialMocapConfigurationLog.Loaded(logger, configured: true);
                return configuration;
            }
            catch (JsonException exception)
            {
                IFacialMocapConfigurationLog.LoadFailed(logger, exception.GetType().Name);
                return null;
            }
            catch (ArgumentException exception)
            {
                IFacialMocapConfigurationLog.LoadFailed(logger, exception.GetType().Name);
                return null;
            }
        }
        finally
        {
            accessGate.Release();
        }
    }

    public async Task SaveAsync(
        IFacialMocapConfiguration configuration,
        CancellationToken cancellationToken)
    {
        IFacialMocapConfiguration.Validate(configuration);
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string directory = Path.GetDirectoryName(targetPath)
                ?? throw new InvalidOperationException("Configuration path requires a directory.");
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
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        configuration,
                        SerializerOptions,
                        cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, targetPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            IFacialMocapConfigurationLog.Saved(logger, configuration.SchemaVersion);
        }
        finally
        {
            accessGate.Release();
        }
    }
}

internal static partial class IFacialMocapConfigurationLog
{
    [LoggerMessage(6600, LogLevel.Information, "iFacialMocap configuration loaded; configured={Configured}")]
    internal static partial void Loaded(ILogger logger, bool configured);

    [LoggerMessage(6601, LogLevel.Warning, "iFacialMocap configuration load failed with {ErrorType}")]
    internal static partial void LoadFailed(ILogger logger, string errorType);

    [LoggerMessage(6602, LogLevel.Information, "iFacialMocap configuration saved with schema {SchemaVersion}")]
    internal static partial void Saved(ILogger logger, int schemaVersion);
}
