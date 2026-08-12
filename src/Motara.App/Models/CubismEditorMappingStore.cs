using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Output.CubismEditor;

namespace Motara.App.Models;

/// <summary>Persists the independently editable Cubism Editor output mapping with atomic replacement.</summary>
internal sealed class CubismEditorMappingStore
{
    internal const string FileName = "cubism-editor.mapping.motara.json";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim gate;
    private readonly ILogger<CubismEditorMappingStore> logger;

    internal CubismEditorMappingStore(string path, ILogger<CubismEditorMappingStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        gate = Gates.GetOrAdd(Path, static _ => new SemaphoreSlim(1, 1));
        this.logger = logger ?? NullLogger<CubismEditorMappingStore>.Instance;
    }

    internal string Path { get; }

    internal async Task<CubismEditorMappingDocument> LoadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(Path))
            {
                CubismEditorMappingDocument defaultDocument = CubismEditorMappingDocument.Default;
                await WriteAtomicAsync(defaultDocument, cancellationToken).ConfigureAwait(false);
                CubismEditorMappingStoreLog.DefaultCreated(logger, Path);
                return defaultDocument;
            }

            await using var stream = new FileStream(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            CubismEditorMappingDocument document = await JsonSerializer.DeserializeAsync<CubismEditorMappingDocument>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("Cubism Editor mapping configuration is empty.");
            document.Validate();
            CubismEditorMappingStoreLog.Loaded(logger, document.Bindings.Length);
            return document;
        }
        catch (Exception exception) when (exception is JsonException or IOException or ArgumentException)
        {
            CubismEditorMappingStoreLog.LoadFailed(logger, exception);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task SaveAsync(CubismEditorMappingDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAtomicAsync(document, cancellationToken).ConfigureAwait(false);
            CubismEditorMappingStoreLog.Saved(logger, document.Bindings.Length);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task WriteAtomicAsync(CubismEditorMappingDocument document, CancellationToken cancellationToken)
    {
        string directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        string temporary = System.IO.Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
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
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}

internal static partial class CubismEditorMappingStoreLog
{
    [LoggerMessage(6730, LogLevel.Information, "Created default Cubism Editor mapping configuration at {Path}")]
    internal static partial void DefaultCreated(ILogger logger, string path);

    [LoggerMessage(6731, LogLevel.Information, "Loaded Cubism Editor mapping configuration with {BindingCount} bindings")]
    internal static partial void Loaded(ILogger logger, int bindingCount);

    [LoggerMessage(6732, LogLevel.Information, "Saved Cubism Editor mapping configuration with {BindingCount} bindings")]
    internal static partial void Saved(ILogger logger, int bindingCount);

    [LoggerMessage(6733, LogLevel.Warning, "Could not load Cubism Editor mapping configuration")]
    internal static partial void LoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(6734, LogLevel.Warning, "Could not initialize Cubism Editor mapping configuration")]
    internal static partial void InitializationFailed(ILogger logger, Exception exception);
}
