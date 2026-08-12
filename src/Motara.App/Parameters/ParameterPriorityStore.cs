using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Motara.App.Parameters;

internal sealed class ParameterPriorityStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string path;
    private readonly ILogger<ParameterPriorityStore> logger;
    private readonly SemaphoreSlim gate = new(1, 1);

    internal ParameterPriorityStore(
        string path,
        ILogger<ParameterPriorityStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
        this.logger = logger ?? NullLogger<ParameterPriorityStore>.Instance;
    }

    internal async Task<ParameterPriorityProfile> LoadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return ParameterPriorityProfile.Default;
            }

            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                ParameterPriorityProfile profile = await JsonSerializer.DeserializeAsync<ParameterPriorityProfile>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new JsonException("Parameter priority profile is empty.");
                profile.Validate();
                ParameterPriorityLog.Loaded(logger, profile.Order.Length);
                return profile;
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                ParameterPriorityLog.LoadFailed(logger, exception.GetType().Name);
                return ParameterPriorityProfile.Default;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task SaveAsync(
        ParameterPriorityProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("Parameter priority path requires a directory.");
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, $".priority-{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, profile, JsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, path, overwrite: true);
                ParameterPriorityLog.Saved(logger, profile.Order.Length);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();
}

internal static partial class ParameterPriorityLog
{
    [LoggerMessage(6700, LogLevel.Information, "Parameter priority profile loaded with {ProviderCount} providers")]
    internal static partial void Loaded(ILogger logger, int providerCount);

    [LoggerMessage(6701, LogLevel.Warning, "Parameter priority profile load failed with {ErrorType}; defaults restored")]
    internal static partial void LoadFailed(ILogger logger, string errorType);

    [LoggerMessage(6702, LogLevel.Information, "Parameter priority profile saved with {ProviderCount} providers")]
    internal static partial void Saved(ILogger logger, int providerCount);
}
