using Microsoft.Extensions.Logging;

namespace Motara.App.Collaboration;

internal sealed class StartupInvitationErrorViewModel(Action close)
{
    private readonly Action close = close ?? throw new ArgumentNullException(nameof(close));

    internal void Close() => close();
}

internal static partial class StartupInvitationEvents
{
    [LoggerMessage(8064, LogLevel.Information, "Startup invitation dispatch classified input as {Status}")]
    internal static partial void Classified(ILogger logger, StartupInvitationStatus status);

    [LoggerMessage(8065, LogLevel.Information, "Startup invitation opened confirmation for {Kind}")]
    internal static partial void Dispatched(ILogger logger, Motara.Collaboration.Invites.InvitationKind kind);
}
