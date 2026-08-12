using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Motara.Persistence;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Motara.App.Diagnostics;

internal sealed class LogFilePolicy
{
    internal const long DefaultFileSizeLimitBytes = 10L * 1024 * 1024;
    internal const int DefaultRetainedFileCountLimit = 5;
    internal const long DefaultMaximumTotalSizeBytes = 100L * 1024 * 1024;
    internal static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromDays(14);

    internal LogFilePolicy(
        long fileSizeLimitBytes = DefaultFileSizeLimitBytes,
        int retainedFileCountLimit = DefaultRetainedFileCountLimit,
        TimeSpan? maximumAge = null,
        long maximumTotalSizeBytes = DefaultMaximumTotalSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileSizeLimitBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedFileCountLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTotalSizeBytes);
        TimeSpan resolvedMaximumAge = maximumAge ?? DefaultMaximumAge;
        if (resolvedMaximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        }

        FileSizeLimitBytes = fileSizeLimitBytes;
        RetainedFileCountLimit = retainedFileCountLimit;
        MaximumAge = resolvedMaximumAge;
        MaximumTotalSizeBytes = maximumTotalSizeBytes;
    }

    internal long FileSizeLimitBytes { get; }

    internal int RetainedFileCountLimit { get; }

    internal TimeSpan MaximumAge { get; }

    internal long MaximumTotalSizeBytes { get; }
}

internal sealed class MotaraLogHost : IDisposable
{
    private readonly LoggingLevelSwitch? levelSwitch;
    private DiagnosticLogLevel minimumLevel;
    private int disposed;

    private MotaraLogHost(
        ILoggerFactory loggerFactory,
        string logsRoot,
        DiagnosticLogLevel minimumLevel,
        LoggingLevelSwitch? levelSwitch,
        bool isFileLoggingEnabled,
        Task retentionCompleted)
    {
        LoggerFactory = loggerFactory;
        LogsRoot = logsRoot;
        this.minimumLevel = minimumLevel;
        this.levelSwitch = levelSwitch;
        IsFileLoggingEnabled = isFileLoggingEnabled;
        RetentionCompleted = retentionCompleted;
    }

    internal ILoggerFactory LoggerFactory { get; }

    internal string LogsRoot { get; }

    internal bool IsFileLoggingEnabled { get; }

    internal Task RetentionCompleted { get; }

    internal DiagnosticLogLevel MinimumLevel
    {
        get => minimumLevel;
        set
        {
            ValidateLevel(value);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            minimumLevel = value;
            if (levelSwitch is not null)
            {
                levelSwitch.MinimumLevel = ToSerilogLevel(value);
            }
        }
    }

    internal static MotaraLogHost Create(
        ILogStoragePathProvider pathProvider,
        DiagnosticLogLevel minimumLevel,
        LogFilePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(pathProvider);
        ValidateLevel(minimumLevel);
        policy ??= new LogFilePolicy();
        string logsRoot = string.Empty;
        Task retentionCompleted = Task.CompletedTask;
        try
        {
            logsRoot = Path.GetFullPath(pathProvider.GetLogsRoot());
            Directory.CreateDirectory(logsRoot);
            string processId = Guid.NewGuid().ToString("N");
            string sessionId = Guid.NewGuid().ToString("N");
            string filePath = Path.Combine(
                logsRoot,
                $"motara-{DateTime.UtcNow:yyyyMMdd}-{processId[..8]}.jsonl");
            var levelSwitch = new LoggingLevelSwitch(ToSerilogLevel(minimumLevel));
            Serilog.ILogger logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(levelSwitch)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("ProcessInstanceId", processId)
                .Enrich.WithProperty("SessionId", sessionId)
                .WriteTo.File(
                    new MotaraJsonLogFormatter(),
                    filePath,
                    fileSizeLimitBytes: policy.FileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: policy.RetainedFileCountLimit,
                    shared: false,
                    flushToDiskInterval: TimeSpan.FromSeconds(1))
                .CreateLogger();
            ILoggerFactory factory = Microsoft.Extensions.Logging.LoggerFactory.Create(
                builder => builder.AddSerilog(logger, dispose: true));
            retentionCompleted = CleanAndLogRetentionAsync(
                logsRoot,
                policy,
                factory.CreateLogger<MotaraLogHost>());
            return new MotaraLogHost(
                factory,
                logsRoot,
                minimumLevel,
                levelSwitch,
                isFileLoggingEnabled: true,
                retentionCompleted);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            Trace.WriteLine($"Motara logging file sink unavailable: {LogSanitizer.Sanitize(exception.Message)}");
            ILoggerFactory fallback = Microsoft.Extensions.Logging.LoggerFactory.Create(
                builder => builder.AddProvider(new TraceLoggerProvider(minimumLevel)));
            return new MotaraLogHost(
                fallback,
                logsRoot,
                minimumLevel,
                levelSwitch: null,
                isFileLoggingEnabled: false,
                retentionCompleted);
        }
    }

    private static async Task CleanAndLogRetentionAsync(
        string logsRoot,
        LogFilePolicy policy,
        ILogger<MotaraLogHost> logger)
    {
        LogFileRetentionSummary summary = await LogFileRetentionCleaner.CleanAsync(
                logsRoot,
                policy,
                DateTime.UtcNow)
            .ConfigureAwait(false);
        LogFileRetentionEvents.Completed(
            logger,
            summary.DeletedFileCount,
            summary.FreedBytes,
            summary.SkippedFileCount,
            summary.DurationMilliseconds);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        LoggerFactory.Dispose();
    }

    private static void ValidateLevel(DiagnosticLogLevel level)
    {
        if (!Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }
    }

    private static LogEventLevel ToSerilogLevel(DiagnosticLogLevel level) => level switch
    {
        DiagnosticLogLevel.Information => LogEventLevel.Information,
        DiagnosticLogLevel.Debug => LogEventLevel.Debug,
        DiagnosticLogLevel.Trace => LogEventLevel.Verbose,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    private sealed class TraceLoggerProvider(DiagnosticLogLevel minimumLevel) : ILoggerProvider
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) =>
            new TraceLogger(categoryName, minimumLevel);

        public void Dispose()
        {
        }
    }

    private sealed class TraceLogger(string categoryName, DiagnosticLogLevel minimumLevel)
        : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= ToMicrosoftLevel(minimumLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            Trace.WriteLine(
                $"{logLevel} {categoryName} {eventId.Id}:{eventId.Name} "
                + LogSanitizer.Sanitize(formatter(state, exception)));
        }

        private static LogLevel ToMicrosoftLevel(DiagnosticLogLevel level) => level switch
        {
            DiagnosticLogLevel.Information => LogLevel.Information,
            DiagnosticLogLevel.Debug => LogLevel.Debug,
            DiagnosticLogLevel.Trace => LogLevel.Trace,
            _ => throw new ArgumentOutOfRangeException(nameof(level)),
        };
    }
}
