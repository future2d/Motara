using Microsoft.Extensions.Logging;

namespace Motara.App.Diagnostics;

internal static partial class ApplicationLifecycleLog
{
    [LoggerMessage(1000, LogLevel.Information, "Application started")]
    internal static partial void Started(ILogger logger);

    [LoggerMessage(1001, LogLevel.Information, "Application stopped")]
    internal static partial void Stopped(ILogger logger);

    [LoggerMessage(1002, LogLevel.Critical, "Application failed with {ExceptionType}")]
    internal static partial void Fatal(
        ILogger logger,
        string exceptionType,
        Exception exception);
}
