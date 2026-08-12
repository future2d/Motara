using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Motara.App.Shell;

public sealed record TopLevelWorkspaceContent
{
    public TopLevelWorkspaceContent(string workspaceId, object payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(payload);
        WorkspaceId = workspaceId;
        Payload = payload;
    }

    public string WorkspaceId { get; }

    public object Payload { get; }
}

public sealed class TopLevelWorkspaceState : INotifyPropertyChanged
{
    private ILogger<TopLevelWorkspaceState> logger = NullLogger<TopLevelWorkspaceState>.Instance;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action<string>? Closed;

    public TopLevelWorkspaceContent? Content { get; private set; }

    public string? ReturnFocusKey { get; private set; }

    public bool IsOpen => Content is not null;

    internal void AttachLogger(ILogger<TopLevelWorkspaceState>? value) =>
        logger = value ?? NullLogger<TopLevelWorkspaceState>.Instance;

    public void Open(TopLevelWorkspaceContent content, string returnFocusKey)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(returnFocusKey);
        Content = content;
        ReturnFocusKey = returnFocusKey;
        OnPropertyChanged(nameof(Content));
        OnPropertyChanged(nameof(ReturnFocusKey));
        OnPropertyChanged(nameof(IsOpen));
        TopLevelWorkspaceLog.Opened(logger, content.WorkspaceId);
    }

    public void Close()
    {
        if (Content is null)
        {
            return;
        }

        string workspaceId = Content.WorkspaceId;
        string returnFocusKey = ReturnFocusKey
            ?? throw new InvalidOperationException("An open workspace requires a focus return target.");
        Content = null;
        ReturnFocusKey = null;
        OnPropertyChanged(nameof(Content));
        OnPropertyChanged(nameof(ReturnFocusKey));
        OnPropertyChanged(nameof(IsOpen));
        TopLevelWorkspaceLog.Closed(logger, workspaceId);
        Closed?.Invoke(returnFocusKey);
    }

    public async Task<bool> RequestCloseAsync(CancellationToken cancellationToken)
    {
        if (Content is null)
        {
            return true;
        }

        if (Content.Payload is IWorkspaceCloseGuard guard
            && !await guard.RequestCloseAsync(cancellationToken))
        {
            return false;
        }

        Close();
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal static partial class TopLevelWorkspaceLog
{
    [LoggerMessage(6100, LogLevel.Information, "Top-level workspace opened: {WorkspaceId}")]
    internal static partial void Opened(ILogger logger, string workspaceId);

    [LoggerMessage(6101, LogLevel.Information, "Top-level workspace closed: {WorkspaceId}")]
    internal static partial void Closed(ILogger logger, string workspaceId);
}
