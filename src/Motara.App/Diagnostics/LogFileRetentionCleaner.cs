using System.Diagnostics;

namespace Motara.App.Diagnostics;

internal readonly record struct LogFileRetentionSummary(
    int DeletedFileCount,
    long FreedBytes,
    int SkippedFileCount,
    long DurationMilliseconds);

internal static class LogFileRetentionCleaner
{
    internal static Task<LogFileRetentionSummary> CleanAsync(
        string logsRoot,
        LogFilePolicy policy,
        DateTime utcNow) =>
        Task.Run(() => Clean(logsRoot, policy, utcNow));

    internal static LogFileRetentionSummary Clean(
        string logsRoot,
        LogFilePolicy policy,
        DateTime utcNow)
    {
        long startedAt = Stopwatch.GetTimestamp();
        ArgumentException.ThrowIfNullOrWhiteSpace(logsRoot);
        ArgumentNullException.ThrowIfNull(policy);

        (List<LogFileCandidate> candidates, int skippedFileCount) = GetCandidates(logsRoot);
        DateTime cutoff = utcNow - policy.MaximumAge;
        int deletedFileCount = 0;
        long freedBytes = 0;
        foreach (LogFileCandidate candidate in candidates.Where(candidate => candidate.LastWriteTimeUtc < cutoff))
        {
            if (TryDelete(candidate.Path))
            {
                candidate.IsDeleted = true;
                deletedFileCount++;
                freedBytes += candidate.Length;
            }
            else
            {
                candidate.IsSkipped = true;
                skippedFileCount++;
            }
        }

        List<LogFileCandidate> remaining = candidates
            .Where(static candidate => !candidate.IsDeleted)
            .OrderBy(static candidate => candidate.LastWriteTimeUtc)
            .ThenBy(static candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        long totalSize = remaining.Sum(static candidate => candidate.Length);
        foreach (LogFileCandidate candidate in remaining)
        {
            if (totalSize <= policy.MaximumTotalSizeBytes)
            {
                break;
            }

            if (TryDelete(candidate.Path))
            {
                candidate.IsDeleted = true;
                totalSize -= candidate.Length;
                deletedFileCount++;
                freedBytes += candidate.Length;
            }
            else if (!candidate.IsSkipped)
            {
                candidate.IsSkipped = true;
                skippedFileCount++;
            }
        }

        return new LogFileRetentionSummary(
            deletedFileCount,
            freedBytes,
            skippedFileCount,
            (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
    }

    private static (List<LogFileCandidate> Candidates, int SkippedFileCount) GetCandidates(string logsRoot)
    {
        var candidates = new List<LogFileCandidate>();
        int skippedFileCount = 0;
        try
        {
            foreach (string path in Directory.EnumerateFiles(
                         logsRoot,
                         "motara-*.jsonl",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var file = new FileInfo(path);
                    candidates.Add(new LogFileCandidate(path, file.LastWriteTimeUtc, file.Length));
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    skippedFileCount++;
                }
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            skippedFileCount++;
        }

        return (candidates, skippedFileCount);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return false;
        }
    }

    private static bool IsRecoverable(Exception exception) => exception is IOException
        or UnauthorizedAccessException
        or ArgumentException
        or NotSupportedException;

    private sealed class LogFileCandidate(
        string path,
        DateTime lastWriteTimeUtc,
        long length)
    {
        internal string Path { get; } = path;

        internal DateTime LastWriteTimeUtc { get; } = lastWriteTimeUtc;

        internal long Length { get; } = length;

        internal bool IsDeleted { get; set; }

        internal bool IsSkipped { get; set; }
    }
}
