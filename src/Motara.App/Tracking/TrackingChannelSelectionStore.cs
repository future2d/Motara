using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Motara.App.Tracking;

internal interface ITrackingChannelSelectionStore
{
    Task<TrackingChannelSelections> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(TrackingChannelSelections selections, CancellationToken cancellationToken);
}

internal sealed class TrackingChannelSelectionStore : ITrackingChannelSelectionStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AccessGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string targetPath;
    private readonly SemaphoreSlim accessGate;
    private readonly ILogger<TrackingChannelSelectionStore> logger;

    internal TrackingChannelSelectionStore(
        string targetPath,
        ILogger<TrackingChannelSelectionStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        this.targetPath = Path.GetFullPath(targetPath);
        this.logger = logger ?? NullLogger<TrackingChannelSelectionStore>.Instance;
        accessGate = AccessGates.GetOrAdd(this.targetPath, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<TrackingChannelSelections> LoadAsync(CancellationToken cancellationToken)
    {
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(targetPath))
            {
                TrackingChannelSelectionLog.Loaded(logger, configuredChannelCount: 0);
                return TrackingChannelSelections.Default;
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
                TrackingChannelSelections? selections =
                    await JsonSerializer.DeserializeAsync<TrackingChannelSelections>(
                        stream,
                        SerializerOptions,
                        cancellationToken).ConfigureAwait(false);
                if (selections is null)
                {
                    TrackingChannelSelectionLog.LoadFailed(logger, "EmptyDocument");
                    return TrackingChannelSelections.Default;
                }

                TrackingChannelSelections.Validate(selections);
                TrackingChannelSelectionLog.Loaded(logger, selections.ConfiguredChannelCount);
                return selections;
            }
            catch (JsonException exception)
            {
                TrackingChannelSelectionLog.LoadFailed(logger, exception.GetType().Name);
                return TrackingChannelSelections.Default;
            }
            catch (ArgumentException exception)
            {
                TrackingChannelSelectionLog.LoadFailed(logger, exception.GetType().Name);
                return TrackingChannelSelections.Default;
            }
        }
        finally
        {
            accessGate.Release();
        }
    }

    public async Task SaveAsync(
        TrackingChannelSelections selections,
        CancellationToken cancellationToken)
    {
        TrackingChannelSelections.Validate(selections);
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string directory = Path.GetDirectoryName(targetPath)
                ?? throw new InvalidOperationException("Tracking selection path requires a directory.");
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
                        selections,
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

            TrackingChannelSelectionLog.Saved(logger, selections.ConfiguredChannelCount);
        }
        finally
        {
            accessGate.Release();
        }
    }
}

internal static partial class TrackingChannelSelectionLog
{
    [LoggerMessage(6610, LogLevel.Information, "Tracking channel selections loaded; {ConfiguredChannelCount} configured channels")]
    internal static partial void Loaded(ILogger logger, int configuredChannelCount);

    [LoggerMessage(6611, LogLevel.Warning, "Tracking channel selections load failed with {ErrorType}; defaults restored")]
    internal static partial void LoadFailed(ILogger logger, string errorType);

    [LoggerMessage(6612, LogLevel.Debug, "Tracking channel selections saved; {ConfiguredChannelCount} configured channels")]
    internal static partial void Saved(ILogger logger, int configuredChannelCount);
}
