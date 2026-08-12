using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Motara.App.Tracking;

internal sealed class SourceMappingMutationTransaction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly ImmutableArray<Mutation> mutations;
    private readonly string transactionsRoot;
    private readonly Func<string, int, Exception?>? replaceFailureInjector;
    private readonly ILogger logger;

    internal SourceMappingMutationTransaction(
        IEnumerable<(string Path, byte[] Content)> mutations,
        string transactionsRoot,
        Func<string, int, Exception?>? replaceFailureInjector = null,
        ILogger? logger = null)
    {
        this.mutations = mutations
            .Select(static mutation => new Mutation(Path.GetFullPath(mutation.Path), mutation.Content))
            .ToImmutableArray();
        this.transactionsRoot = Path.GetFullPath(transactionsRoot);
        this.replaceFailureInjector = replaceFailureInjector;
        this.logger = logger ?? NullLogger.Instance;
    }

    internal async Task ApplyAsync(CancellationToken cancellationToken)
    {
        if (mutations.IsEmpty)
        {
            return;
        }

        string transactionDirectory = Path.Combine(transactionsRoot, Guid.NewGuid().ToString("N"));
        long started = Stopwatch.GetTimestamp();
        Directory.CreateDirectory(transactionDirectory);
        var entries = new List<JournalEntry>(mutations.Length);
        try
        {
            for (int index = 0; index < mutations.Length; index++)
            {
                Mutation mutation = mutations[index];
                string backupPath = Path.Combine(transactionDirectory, $"{index:D4}.bak");
                string temporaryPath = Path.Combine(
                    Path.GetDirectoryName(mutation.Path)!,
                    $".mapping-transaction-{Guid.NewGuid():N}.tmp");
                await CopyAsync(mutation.Path, backupPath, cancellationToken).ConfigureAwait(false);
                await WriteAsync(temporaryPath, mutation.Content, cancellationToken).ConfigureAwait(false);
                entries.Add(new JournalEntry(mutation.Path, backupPath, temporaryPath));
            }

            string journalPath = Path.Combine(transactionDirectory, "journal.json");
            await WriteJournalAsync(journalPath, entries, cancellationToken).ConfigureAwait(false);

            for (int index = 0; index < entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                JournalEntry entry = entries[index];
                Exception? injected = replaceFailureInjector?.Invoke(entry.TargetPath, index);
                if (injected is not null)
                {
                    throw injected;
                }

                File.Move(entry.TemporaryPath, entry.TargetPath, overwrite: true);
            }

            Directory.Delete(transactionDirectory, recursive: true);
            SourceMappingMutationLog.Completed(
                logger,
                mutations.Length,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception exception)
        {
            await RestoreAsync(entries, CancellationToken.None).ConfigureAwait(false);
            DeleteTransactionArtifacts(entries, transactionDirectory);
            SourceMappingMutationLog.RolledBack(
                logger,
                mutations.Length,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                exception.GetType().Name);
            throw;
        }
    }

    internal static async Task RecoverAsync(string transactionsRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionsRoot);
        string fullRoot = Path.GetFullPath(transactionsRoot);
        if (!Directory.Exists(fullRoot))
        {
            return;
        }

        foreach (string transactionDirectory in Directory.EnumerateDirectories(fullRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string journalPath = Path.Combine(transactionDirectory, "journal.json");
            if (!File.Exists(journalPath))
            {
                continue;
            }

            List<JournalEntry> entries;
            await using (FileStream stream = new(
                journalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                entries = await JsonSerializer.DeserializeAsync<List<JournalEntry>>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new JsonException("Mapping transaction journal is empty.");
            }

            await RestoreAsync(entries, cancellationToken).ConfigureAwait(false);
            DeleteTransactionArtifacts(entries, transactionDirectory);
        }
    }

    private static async Task RestoreAsync(
        IEnumerable<JournalEntry> entries,
        CancellationToken cancellationToken)
    {
        foreach (JournalEntry entry in entries)
        {
            if (File.Exists(entry.BackupPath))
            {
                await CopyAsync(entry.BackupPath, entry.TargetPath, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void DeleteTransactionArtifacts(
        IEnumerable<JournalEntry> entries,
        string transactionDirectory)
    {
        foreach (JournalEntry entry in entries)
        {
            if (File.Exists(entry.TemporaryPath))
            {
                File.Delete(entry.TemporaryPath);
            }
        }

        if (Directory.Exists(transactionDirectory))
        {
            Directory.Delete(transactionDirectory, recursive: true);
        }
    }

    private static async Task CopyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream destination = new(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }

    private static async Task WriteAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task WriteJournalAsync(
        string path,
        List<JournalEntry> entries,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, entries, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private sealed record Mutation(string Path, byte[] Content);

    private sealed record JournalEntry(
        string TargetPath,
        string BackupPath,
        string TemporaryPath);
}

internal static partial class SourceMappingMutationLog
{
    [LoggerMessage(6620, LogLevel.Information,
        "Source mapping mutation completed: files={FileCount}; elapsedMs={ElapsedMilliseconds}")]
    internal static partial void Completed(ILogger logger, int fileCount, double elapsedMilliseconds);

    [LoggerMessage(6621, LogLevel.Warning,
        "Source mapping mutation rolled back: files={FileCount}; elapsedMs={ElapsedMilliseconds}; error={ErrorType}")]
    internal static partial void RolledBack(
        ILogger logger,
        int fileCount,
        double elapsedMilliseconds,
        string errorType);
}
