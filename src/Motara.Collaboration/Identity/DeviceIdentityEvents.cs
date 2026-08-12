using Microsoft.Extensions.Logging;

namespace Motara.Collaboration.Identity;

internal static partial class DeviceIdentityEvents
{
    [LoggerMessage(8000, LogLevel.Information, "Collaboration device identity created")]
    internal static partial void Created(ILogger logger);

    [LoggerMessage(8001, LogLevel.Information, "Collaboration device identity loaded")]
    internal static partial void Loaded(ILogger logger);

    [LoggerMessage(8002, LogLevel.Warning, "Collaboration device identity load failed with {ErrorType}")]
    internal static partial void LoadFailed(ILogger logger, string errorType);
}
