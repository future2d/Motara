using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Motara.Persistence;

namespace Motara.App.Diagnostics;

internal sealed class PlatformLogOperations : ILogOperations
{
    private readonly MotaraLogHost logHost;
    private readonly Func<CancellationToken, Task<string?>> selectExportDestination;
    private readonly Action<ProcessStartInfo> startProcess;

    internal PlatformLogOperations(
        MotaraLogHost logHost,
        Func<CancellationToken, Task<string?>> selectExportDestination)
        : this(logHost, selectExportDestination, static startInfo => Process.Start(startInfo))
    {
    }

    internal PlatformLogOperations(
        MotaraLogHost logHost,
        Func<CancellationToken, Task<string?>> selectExportDestination,
        Action<ProcessStartInfo> startProcess)
    {
        ArgumentNullException.ThrowIfNull(logHost);
        ArgumentNullException.ThrowIfNull(selectExportDestination);
        ArgumentNullException.ThrowIfNull(startProcess);
        this.logHost = logHost;
        this.selectExportDestination = selectExportDestination;
        this.startProcess = startProcess;
    }

    public DiagnosticLogLevel MinimumLevel
    {
        get => logHost.MinimumLevel;
        set => logHost.MinimumLevel = value;
    }

    public Task OpenLogsFolderAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string logsRoot = RequireLogsRoot();
        Directory.CreateDirectory(logsRoot);
        startProcess(new ProcessStartInfo
        {
            FileName = logsRoot,
            UseShellExecute = true,
        });
        return Task.CompletedTask;
    }

    public async Task ExportDiagnosticLogsAsync(CancellationToken cancellationToken)
    {
        await logHost.RetentionCompleted.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? selectedPath = await selectExportDestination(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        string destinationPath = Path.GetFullPath(selectedPath);
        if (!destinationPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            destinationPath += ".zip";
        }

        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        string logsRoot = RequireLogsRoot();
        string[] logFiles = Directory.Exists(logsRoot)
            ? Directory.GetFiles(logsRoot, "motara-*.jsonl", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        string temporaryPath = Path.Combine(
            destinationDirectory ?? Directory.GetCurrentDirectory(),
            $".motara-diagnostics-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                81920,
                FileOptions.Asynchronous))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (string logFile in logFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ZipArchiveEntry entry = archive.CreateEntry(
                        $"logs/{Path.GetFileName(logFile)}",
                        CompressionLevel.Fastest);
                    await using Stream entryStream = entry.Open();
                    await using FileStream source = new(
                        logFile,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        81920,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await source.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
                }

                ZipArchiveEntry manifestEntry = archive.CreateEntry(
                    "manifest.json",
                    CompressionLevel.Fastest);
                await using Stream manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(
                    manifestStream,
                    CreateManifest(),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string RequireLogsRoot()
    {
        if (string.IsNullOrWhiteSpace(logHost.LogsRoot))
        {
            throw new InvalidOperationException("The log directory is unavailable.");
        }

        return Path.GetFullPath(logHost.LogsRoot);
    }

    private static object CreateManifest()
    {
        Assembly assembly = typeof(PlatformLogOperations).Assembly;
        string version = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        string platform = OperatingSystem.IsWindows()
            ? "Windows"
            : OperatingSystem.IsLinux()
                ? "Linux"
                : OperatingSystem.IsMacOS()
                    ? "macOS"
                    : "Other";
        return new
        {
            FormatVersion = 1,
            Application = "Motara",
            ApplicationVersion = version,
            RuntimeVersion = Environment.Version.ToString(),
            Platform = platform,
            ExportedAtUtc = DateTimeOffset.UtcNow,
        };
    }
}
