using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Motara.Persistence;

public interface IUiSettingsStore
{
    Task<UiSettings> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(UiSettings settings, CancellationToken cancellationToken);
}

/// <summary>Loads and atomically replaces one versioned UI settings file.</summary>
public sealed class UiSettingsStore : IUiSettingsStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AccessGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string targetPath;
    private readonly SemaphoreSlim accessGate;
    private readonly ILogger<UiSettingsStore> logger;

    public UiSettingsStore(string targetPath)
        : this(targetPath, NullLogger<UiSettingsStore>.Instance)
    {
    }

    public UiSettingsStore(string targetPath, ILogger<UiSettingsStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(logger);
        this.targetPath = Path.GetFullPath(targetPath);
        this.logger = logger;
        accessGate = AccessGates.GetOrAdd(this.targetPath, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<UiSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UiSettings settings = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            UiSettingsLog.LoadCompleted(logger, settings.SchemaVersion);
            return settings;
        }
        finally
        {
            accessGate.Release();
        }
    }

    public async Task SaveAsync(UiSettings settings, CancellationToken cancellationToken)
    {
        UiSettings.Validate(settings);
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
            UiSettingsLog.SaveCompleted(
                logger,
                settings.SchemaVersion,
                settings.WindowWidthPixels,
                settings.WindowHeightPixels,
                settings.ContentScaleMode,
                settings.ContentScale,
                settings.FrameRateMode,
                settings.IsWindowSizeLocked,
                settings.Screenshot.CountdownSeconds,
                settings.Screenshot.UseCustomResolution,
                settings.Screenshot.WidthPixels,
                settings.Screenshot.HeightPixels,
                settings.Screenshot.FramingMode,
                settings.RememberFaceTrackingOnStartup,
                settings.ApplicationLanguage);
        }
        finally
        {
            accessGate.Release();
        }
    }

    private async Task<UiSettings> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(targetPath))
        {
            return UiSettings.Default;
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
            UiSettings? settings = await JsonSerializer.DeserializeAsync<UiSettings>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            if (settings is null)
            {
                return UiSettings.Default;
            }

            UiSettings.Validate(settings);
            return settings;
        }
        catch (JsonException exception)
        {
            UiSettingsLog.LoadFailed(logger, exception.GetType().Name);
            return UiSettings.Default;
        }
        catch (ArgumentException exception)
        {
            UiSettingsLog.LoadFailed(logger, exception.GetType().Name);
            return UiSettings.Default;
        }
    }

    private async Task SaveCoreAsync(UiSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = Path.Combine(
            directory ?? Directory.GetCurrentDirectory(),
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
                    settings,
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
    }
}

internal static partial class UiSettingsLog
{
    [LoggerMessage(2001, LogLevel.Information, "UI settings loaded with schema {SchemaVersion}")]
    internal static partial void LoadCompleted(ILogger logger, int schemaVersion);

    [LoggerMessage(2002, LogLevel.Warning, "UI settings load failed with {ErrorType}; defaults restored")]
    internal static partial void LoadFailed(ILogger logger, string errorType);

    [LoggerMessage(
        2003,
        LogLevel.Debug,
        "UI settings saved with schema {SchemaVersion}, window {WindowWidthPixels}x{WindowHeightPixels} px, content scale {ContentScaleMode}:{ContentScale}, frame rate {FrameRateMode}, size locked {IsWindowSizeLocked}, screenshot {ScreenshotCountdownSeconds}s custom {UseCustomScreenshotResolution} {ScreenshotWidthPixels}x{ScreenshotHeightPixels} {ScreenshotFramingMode}, remember face tracking {RememberFaceTrackingOnStartup}, language {ApplicationLanguage}")]
    internal static partial void SaveCompleted(
        ILogger logger,
        int schemaVersion,
        int windowWidthPixels,
        int windowHeightPixels,
        ContentScaleMode contentScaleMode,
        double contentScale,
        FrameRateMode frameRateMode,
        bool isWindowSizeLocked,
        int screenshotCountdownSeconds,
        bool useCustomScreenshotResolution,
        int screenshotWidthPixels,
        int screenshotHeightPixels,
        ScreenshotFramingMode screenshotFramingMode,
        bool rememberFaceTrackingOnStartup,
        ApplicationLanguage applicationLanguage);
}
