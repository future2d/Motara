using Microsoft.Extensions.Logging;

namespace Motara.Collaboration.Models;

internal static partial class ModelPublicationEvents
{
    [LoggerMessage(8120, LogLevel.Information,
        "Model publication started; generation={Generation}; peerCount={PeerCount}")]
    internal static partial void Started(ILogger logger, ulong generation, int peerCount);

    [LoggerMessage(8121, LogLevel.Information,
        "Model publication completed; generation={Generation}; peerCount={PeerCount}")]
    internal static partial void Completed(ILogger logger, ulong generation, int peerCount);

    [LoggerMessage(8122, LogLevel.Debug,
        "Model publication cancelled; generation={Generation}")]
    internal static partial void Cancelled(ILogger logger, ulong generation);

    [LoggerMessage(8123, LogLevel.Warning,
        "Model publication failed; generation={Generation}; errorType={ErrorType}")]
    internal static partial void Failed(ILogger logger, ulong generation, string errorType);

    [LoggerMessage(8124, LogLevel.Information,
        "Model publication withdrawn; generation={Generation}; peerCount={PeerCount}")]
    internal static partial void Withdrawn(ILogger logger, ulong generation, int peerCount);
}
