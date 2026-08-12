using Microsoft.Extensions.Logging;

namespace Motara.Collaboration.Friends;

internal static partial class RelationshipSecretEvents
{
    [LoggerMessage(8040, LogLevel.Information, "Collaboration relationship secret saved")]
    internal static partial void Saved(ILogger logger);

    [LoggerMessage(8041, LogLevel.Debug, "Collaboration relationship secret loaded; found={Found}")]
    internal static partial void Loaded(ILogger logger, bool found);

    [LoggerMessage(8042, LogLevel.Information, "Collaboration relationship secret removed; existed={Existed}")]
    internal static partial void Removed(ILogger logger, bool existed);

    [LoggerMessage(8043, LogLevel.Warning,
        "Collaboration relationship secret operation failed; operation={Operation}; error={ErrorType}")]
    internal static partial void Failed(ILogger logger, string operation, string errorType);
}
