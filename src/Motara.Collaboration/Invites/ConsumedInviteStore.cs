using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Motara.Collaboration.Invites;

internal sealed record ConsumedInviteSnapshot(string Nonce, DateTimeOffset ExpiresAtUtc);

public sealed class ConsumedInviteStore : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly string path;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ConsumedInviteStore> logger;
    private readonly SemaphoreSlim accessGate = new(1, 1);

    public ConsumedInviteStore(
        string collaborationRoot,
        TimeProvider? timeProvider = null,
        ILogger<ConsumedInviteStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collaborationRoot);
        path = Path.Combine(Path.GetFullPath(collaborationRoot), "invites", "consumed-invites.motara.json");
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<ConsumedInviteStore>.Instance;
    }

    public async Task<bool> TryConsumeAsync(
        string nonce,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        if (!Base64Url.TryDecode(nonce, 16, out byte[] decoded) || decoded.Length != 16)
        {
            throw new ArgumentException("The invitation nonce is invalid.", nameof(nonce));
        }

        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            ConsumedDocument document = await LoadAsync(cancellationToken).ConfigureAwait(false);
            List<ConsumedEntry> entries = document.Entries
                .Where(entry => entry.ExpiresAtUtc > now)
                .ToList();
            if (entries.Any(entry => string.Equals(entry.Nonce, nonce, StringComparison.Ordinal)))
            {
                InviteEvents.Duplicate(logger);
                return false;
            }

            entries.Add(new ConsumedEntry(nonce, expiresAtUtc));
            await SaveAsync(new ConsumedDocument(1, entries), cancellationToken).ConfigureAwait(false);
            InviteEvents.Consumed(logger);
            return true;
        }
        finally
        {
            accessGate.Release();
        }
    }

    public async Task<bool> IsConsumedAsync(string nonce, CancellationToken cancellationToken)
    {
        ValidateNonce(nonce);
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            ConsumedDocument document = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return document.Entries.Any(entry =>
                entry.ExpiresAtUtc > now
                && string.Equals(entry.Nonce, nonce, StringComparison.Ordinal));
        }
        finally
        {
            accessGate.Release();
        }
    }

    internal async Task<IReadOnlyList<ConsumedInviteSnapshot>> ExportAsync(
        CancellationToken cancellationToken)
    {
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            ConsumedDocument document = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return document.Entries
                .Where(entry => entry.ExpiresAtUtc > now)
                .Select(entry => new ConsumedInviteSnapshot(entry.Nonce, entry.ExpiresAtUtc))
                .ToArray();
        }
        finally
        {
            accessGate.Release();
        }
    }

    internal async Task RestoreAsync(
        IReadOnlyList<ConsumedInviteSnapshot> entries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (ConsumedInviteSnapshot entry in entries)
        {
            ValidateNonce(entry.Nonce);
        }

        if (entries.Select(entry => entry.Nonce).Distinct(StringComparer.Ordinal).Count() != entries.Count)
        {
            throw new InvalidDataException("Consumed invitation entries contain duplicate nonces.");
        }

        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveAsync(
                new ConsumedDocument(
                    1,
                    entries.Select(entry => new ConsumedEntry(entry.Nonce, entry.ExpiresAtUtc)).ToList()),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            accessGate.Release();
        }
    }

    public void Dispose() => accessGate.Dispose();

    private static void ValidateNonce(string nonce)
    {
        if (!Base64Url.TryDecode(nonce, 16, out byte[] decoded) || decoded.Length != 16)
        {
            throw new ArgumentException("The invitation nonce is invalid.", nameof(nonce));
        }
    }

    private async Task<ConsumedDocument> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new ConsumedDocument(1, []);
        }

        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        ConsumedDocument document = await JsonSerializer.DeserializeAsync<ConsumedDocument>(
            stream, SerializerOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Consumed invitation document is empty.");
        if (document.SchemaVersion != 1)
        {
            throw new InvalidDataException("Consumed invitation schema is unsupported.");
        }

        return document;
    }

    private async Task SaveAsync(ConsumedDocument document, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private sealed record ConsumedDocument(int SchemaVersion, List<ConsumedEntry> Entries);
    private sealed record ConsumedEntry(string Nonce, DateTimeOffset ExpiresAtUtc);
}
