using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Motara.Persistence;

public interface IShortcutStore
{
    Task<ShortcutProfile> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(ShortcutProfile profile, CancellationToken cancellationToken);
}

public sealed class ShortcutStore : IShortcutStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AccessGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string targetPath;
    private readonly SemaphoreSlim accessGate;
    private readonly ILogger<ShortcutStore> logger;

    public ShortcutStore(string targetPath, ILogger<ShortcutStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        this.targetPath = Path.GetFullPath(targetPath);
        this.logger = logger ?? NullLogger<ShortcutStore>.Instance;
        accessGate = AccessGates.GetOrAdd(this.targetPath, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<ShortcutProfile> LoadAsync(CancellationToken cancellationToken)
    {
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(targetPath)) return ShortcutProfile.Default;
            try
            {
                await using var stream = new FileStream(
                    targetPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                ShortcutProfile profile = await JsonSerializer.DeserializeAsync<ShortcutProfile>(
                    stream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false) ?? ShortcutProfile.Default;
                profile.Validate();
                ShortcutStoreLog.Loaded(logger, profile.SchemaVersion, profile.Entries.Length);
                return profile;
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                ShortcutStoreLog.LoadFailed(logger, exception.GetType().Name);
                return ShortcutProfile.Default;
            }
        }
        finally
        {
            accessGate.Release();
        }
    }

    public async Task SaveAsync(ShortcutProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string directory = Path.GetDirectoryName(targetPath)
                ?? throw new InvalidOperationException("Shortcut path requires a directory.");
            Directory.CreateDirectory(directory);
            string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, profile, SerializerOptions, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, targetPath, overwrite: true);
                ShortcutStoreLog.Saved(logger, profile.SchemaVersion, profile.Entries.Length);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        finally
        {
            accessGate.Release();
        }
    }
}

internal static partial class ShortcutStoreLog
{
    [LoggerMessage(2050, LogLevel.Information,
        "Shortcut profile loaded with schema {SchemaVersion} and {ShortcutCount} entries")]
    internal static partial void Loaded(ILogger logger, int schemaVersion, int shortcutCount);

    [LoggerMessage(2051, LogLevel.Warning,
        "Shortcut profile load failed with {ErrorType}; an empty profile was loaded")]
    internal static partial void LoadFailed(ILogger logger, string errorType);

    [LoggerMessage(2052, LogLevel.Debug,
        "Shortcut profile saved with schema {SchemaVersion} and {ShortcutCount} entries")]
    internal static partial void Saved(ILogger logger, int schemaVersion, int shortcutCount);
}
