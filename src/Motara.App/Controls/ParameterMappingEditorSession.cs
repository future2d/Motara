using System.ComponentModel;

namespace Motara.App.Controls;

internal sealed record ParameterMappingEditorFeedback(
    bool IsSuccess,
    string Title,
    string Message);

internal sealed class ParameterMappingEditorSession
{
    internal ParameterMappingEditorSession(
        INotifyPropertyChanged state,
        Func<bool> isCloseConfirmationVisible,
        Func<CancellationToken, Task<bool>> requestCloseAsync,
        Func<CancellationToken, Task<ParameterMappingEditorFeedback>> applyAsync,
        Action cancelClose,
        Action discardChanges,
        string unsavedChangesResourceKey,
        Action? acknowledgeApplyResult = null)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        IsCloseConfirmationVisible = isCloseConfirmationVisible
            ?? throw new ArgumentNullException(nameof(isCloseConfirmationVisible));
        RequestCloseAsync = requestCloseAsync ?? throw new ArgumentNullException(nameof(requestCloseAsync));
        ApplyAsync = applyAsync ?? throw new ArgumentNullException(nameof(applyAsync));
        CancelClose = cancelClose ?? throw new ArgumentNullException(nameof(cancelClose));
        DiscardChanges = discardChanges ?? throw new ArgumentNullException(nameof(discardChanges));
        ArgumentException.ThrowIfNullOrWhiteSpace(unsavedChangesResourceKey);
        UnsavedChangesResourceKey = unsavedChangesResourceKey;
        AcknowledgeApplyResult = acknowledgeApplyResult ?? (() => { });
    }

    internal INotifyPropertyChanged State { get; }

    internal Func<bool> IsCloseConfirmationVisible { get; }

    internal Func<CancellationToken, Task<bool>> RequestCloseAsync { get; }

    internal Func<CancellationToken, Task<ParameterMappingEditorFeedback>> ApplyAsync { get; }

    internal Action CancelClose { get; }

    internal Action DiscardChanges { get; }

    internal string UnsavedChangesResourceKey { get; }

    internal Action AcknowledgeApplyResult { get; }
}
