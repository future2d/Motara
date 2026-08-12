using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace Motara.App.Diagnostics;

internal sealed class ApplicationExceptionHooks : IDisposable
{
    private readonly ILogger<ApplicationExceptionHooks> logger;
    private int disposed;

    internal ApplicationExceptionHooks(ILogger<ApplicationExceptionHooks> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        this.logger = logger;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
    }

    internal void ReportUnhandledException(Exception exception, bool isTerminating)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ApplicationExceptionLog.Unhandled(
            logger,
            isTerminating,
            exception.GetType().Name,
            exception);
    }

    internal void ReportUnobservedTaskException(AggregateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ApplicationExceptionLog.UnobservedTask(
            logger,
            exception.GetType().Name,
            exception);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        Dispatcher.UIThread.UnhandledException -= OnDispatcherUnhandledException;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            ReportUnhandledException(exception, args.IsTerminating);
        }
        else
        {
            ApplicationExceptionLog.NonExceptionUnhandled(logger, args.IsTerminating);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args) =>
        ReportUnobservedTaskException(args.Exception);

    private void OnDispatcherUnhandledException(
        object? sender,
        DispatcherUnhandledExceptionEventArgs args) =>
        ApplicationExceptionLog.DispatcherUnhandled(
            logger,
            args.Exception.GetType().Name,
            args.Exception);
}

internal static partial class ApplicationExceptionLog
{
    [LoggerMessage(1003, LogLevel.Critical,
        "Unhandled process exception {ExceptionType}; terminating: {IsTerminating}")]
    internal static partial void Unhandled(
        ILogger logger,
        bool isTerminating,
        string exceptionType,
        Exception exception);

    [LoggerMessage(1004, LogLevel.Error, "Unobserved task exception {ExceptionType}")]
    internal static partial void UnobservedTask(
        ILogger logger,
        string exceptionType,
        Exception exception);

    [LoggerMessage(1005, LogLevel.Critical, "Unhandled non-exception process failure; terminating: {IsTerminating}")]
    internal static partial void NonExceptionUnhandled(ILogger logger, bool isTerminating);

    [LoggerMessage(1006, LogLevel.Critical, "Unhandled UI dispatcher exception {ExceptionType}")]
    internal static partial void DispatcherUnhandled(
        ILogger logger,
        string exceptionType,
        Exception exception);
}
