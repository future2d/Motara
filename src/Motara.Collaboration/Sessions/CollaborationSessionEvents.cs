using Microsoft.Extensions.Logging;

namespace Motara.Collaboration.Sessions;

internal static partial class CollaborationSessionEvents
{
    [LoggerMessage(8143, LogLevel.Information, "Collaboration host session prepared")]
    internal static partial void HostPrepared(ILogger logger);

    [LoggerMessage(8144, LogLevel.Information, "Collaboration participant session prepared")]
    internal static partial void ParticipantPrepared(ILogger logger);

    [LoggerMessage(8145, LogLevel.Information, "Collaboration model-distribution consent confirmed")]
    internal static partial void ConsentConfirmed(ILogger logger);

    [LoggerMessage(8146, LogLevel.Information,
        "Collaboration local model publication changed; modelPresent={ModelPresent}")]
    internal static partial void LocalModelChanged(ILogger logger, bool modelPresent);

    [LoggerMessage(8147, LogLevel.Information, "Collaboration session left")]
    internal static partial void Left(ILogger logger);

    [LoggerMessage(8140, LogLevel.Information,
        "Collaboration member admitted; memberCount={MemberCount}")]
    internal static partial void MemberAdmitted(ILogger logger, int memberCount);

    [LoggerMessage(8141, LogLevel.Warning,
        "Collaboration member admission rejected because the configured limit was reached; memberCount={MemberCount}")]
    internal static partial void MemberRejected(ILogger logger, int memberCount);

    [LoggerMessage(8142, LogLevel.Information,
        "Collaboration member removed; memberCount={MemberCount}")]
    internal static partial void MemberRemoved(ILogger logger, int memberCount);
}
