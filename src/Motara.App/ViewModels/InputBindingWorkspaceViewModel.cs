using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Shortcuts;
using Motara.App.Shell;
using Motara.Persistence;

namespace Motara.App.ViewModels;

internal enum ShortcutEditorError
{
    None,
    NameRequired,
    ActionRequired,
    TargetRequired,
    GestureRequired,
    GestureConflict,
    GlobalUnavailable,
    SaveFailed,
}

internal sealed record ShortcutRowViewModel(
    Guid Id,
    string Name,
    string GestureText,
    string ActionName,
    string? TargetName,
    bool IsSelected,
    bool IsSuppressed);

internal sealed record ShortcutSectionViewModel(
    ShortcutOwnerKind Owner,
    int TotalCount,
    bool IsExpanded,
    ImmutableArray<ShortcutRowViewModel> Rows);

public sealed class InputBindingWorkspaceViewModel : INotifyPropertyChanged, IWorkspaceCloseGuard
{
    private static readonly ShortcutOwnerKind[] SectionOrder =
        [ShortcutOwnerKind.Model, ShortcutOwnerKind.Scene, ShortcutOwnerKind.Software];

    private readonly ILayeredShortcutStore store;
    private readonly ImmutableArray<ShortcutActionDefinition> actions;
    private readonly Func<ShortcutActionDefinition, ImmutableArray<ShortcutTargetOption>> targetProvider;
    private readonly Func<string, string> localize;
    private readonly ILogger<InputBindingWorkspaceViewModel> logger;
    private readonly Dictionary<ShortcutOwnerKind, bool> expanded = new()
    {
        [ShortcutOwnerKind.Model] = true,
        [ShortcutOwnerKind.Scene] = true,
        [ShortcutOwnerKind.Software] = true,
    };
    private ImmutableArray<ShortcutEntry> allEntries;
    private ImmutableDictionary<Guid, ShortcutConflict> conflicts;
    private string searchQuery = string.Empty;
    private Guid? selectedEntryId;
    private EditorDraft? editor;
    private ShortcutEditorError editorError;
    private bool isCloseConfirmationVisible;

    internal InputBindingWorkspaceViewModel(
        LayeredShortcutSnapshot snapshot,
        ILayeredShortcutStore store,
        ImmutableArray<ShortcutActionDefinition> actions,
        Func<ShortcutActionDefinition, ImmutableArray<ShortcutTargetOption>> targetProvider,
        Func<string, string>? localize = null,
        ILogger<InputBindingWorkspaceViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(targetProvider);
        allEntries = snapshot.AllEntries;
        conflicts = snapshot.Conflicts.ToImmutableDictionary(
            static conflict => conflict.Suppressed.Id);
        this.store = store;
        this.actions = actions;
        this.targetProvider = targetProvider;
        this.localize = localize ?? (static key => key);
        this.logger = logger ?? NullLogger<InputBindingWorkspaceViewModel>.Instance;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal event Action<ShortcutProfile>? Applied;

    internal ImmutableArray<ShortcutSectionViewModel> Sections => SectionOrder
        .Select(CreateSection)
        .ToImmutableArray();

    internal string SearchQuery => searchQuery;

    internal ImmutableArray<ShortcutActionDefinition> EditorActions => editor is null
        ? []
        : actions.Where(action => action.Owner == editor.Owner).ToImmutableArray();

    internal ImmutableArray<ShortcutTargetOption> EditorTargets
    {
        get
        {
            ShortcutActionDefinition? action = FindEditorAction();
            return action is null ? [] : targetProvider(action);
        }
    }

    internal Guid? SelectedEntryId => selectedEntryId;
    internal bool IsEditorVisible => editor is not null;
    internal bool IsCreating => editor?.IsNew == true;
    internal ShortcutOwnerKind? EditorOwner => editor?.Owner;
    internal string EditorName => editor?.Name ?? string.Empty;
    internal string? EditorActionKind => editor?.ActionKind;
    internal string? EditorTargetId => editor?.TargetId;
    internal InputGesture? EditorGesture => editor?.Gesture;
    internal bool EditorGlobal => editor?.IsGlobalEnabled == true;
    internal bool EditorIsSuppressed => editor is not null && conflicts.ContainsKey(editor.Id);
    internal ShortcutEditorError EditorError
    {
        get => editorError;
        private set
        {
            if (editorError == value) return;
            editorError = value;
            Raise();
        }
    }
    internal bool IsCloseConfirmationVisible
    {
        get => isCloseConfirmationVisible;
        private set => Set(ref isCloseConfirmationVisible, value);
    }

    internal void SetSearchQuery(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (StringComparer.Ordinal.Equals(searchQuery, normalized)) return;
        searchQuery = normalized;
        Raise(nameof(Sections));
    }

    internal void ToggleSection(ShortcutOwnerKind owner)
    {
        expanded[owner] = !expanded[owner];
        Raise(nameof(Sections));
    }

    internal void Create(ShortcutOwnerKind owner, string defaultName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultName);
        expanded[owner] = true;
        selectedEntryId = null;
        editor = new EditorDraft(
            Guid.NewGuid(), owner, defaultName.Trim(), null, null, null, false, IsNew: true,
            IsNameAutomatic: true);
        EditorError = ShortcutEditorError.None;
        InputBindingWorkspaceLog.Created(logger, owner.ToString());
        RaiseEditorState();
    }

    internal void Select(Guid id)
    {
        ShortcutEntry entry = allEntries.Single(item => item.Id == id);
        selectedEntryId = id;
        editor = new EditorDraft(
            entry.Id,
            entry.Owner,
            entry.Name,
            entry.ActionKind,
            entry.TargetId,
            entry.Gesture,
            entry.IsGlobalEnabled,
            IsNew: false,
            IsNameAutomatic: false);
        EditorError = ShortcutEditorError.None;
        InputBindingWorkspaceLog.Selected(logger, id);
        RaiseEditorState();
    }

    internal void RestoreSelection(Guid? id)
    {
        if (id is Guid selectedId && allEntries.Any(entry => entry.Id == selectedId))
            Select(selectedId);
    }

    internal void SetEditorName(string? value)
    {
        if (editor is null) return;
        string normalized = value?.Trim() ?? string.Empty;
        if (StringComparer.Ordinal.Equals(editor.Name, normalized)) return;
        editor = editor with { Name = normalized, IsNameAutomatic = false };
        EditorError = ShortcutEditorError.None;
        Raise(nameof(EditorName));
    }

    internal void SelectEditorAction(string? actionKind)
    {
        if (editor is null) return;
        if (StringComparer.Ordinal.Equals(editor.ActionKind, actionKind)) return;
        ShortcutActionDefinition? action = actions.FirstOrDefault(candidate =>
            candidate.Owner == editor.Owner
            && StringComparer.Ordinal.Equals(candidate.ActionKind, actionKind));
        editor = editor with
        {
            ActionKind = action?.ActionKind,
            TargetId = action?.TargetKind == ShortcutTargetKind.None ? null : editor.TargetId,
            IsGlobalEnabled = action?.AllowsGlobalRegistration == true && editor.IsGlobalEnabled,
        };
        if (action is not null
            && action.TargetKind != ShortcutTargetKind.None
            && !targetProvider(action).Any(target => StringComparer.Ordinal.Equals(target.Id, editor.TargetId)))
            editor = editor with { TargetId = null };
        UpdateAutomaticEditorName();
        EditorError = ShortcutEditorError.None;
        RaiseEditorState();
    }

    internal void SelectEditorTarget(string? targetId)
    {
        if (editor is null) return;
        string? normalized = string.IsNullOrWhiteSpace(targetId) ? null : targetId.Trim();
        if (StringComparer.Ordinal.Equals(editor.TargetId, normalized)) return;
        editor = editor with { TargetId = normalized };
        UpdateAutomaticEditorName();
        EditorError = ShortcutEditorError.None;
        Raise(nameof(EditorTargetId));
        Raise(nameof(EditorName));
    }

    internal void SetEditorGesture(InputGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        if (editor is null) return;
        if (StringComparer.Ordinal.Equals(editor.Gesture?.CanonicalText, gesture.CanonicalText)) return;
        editor = editor with { Gesture = gesture };
        EditorError = ShortcutEditorError.None;
        Raise(nameof(EditorGesture));
    }

    internal void SetEditorGlobal(bool enabled)
    {
        if (editor is null) return;
        if (editor.IsGlobalEnabled == enabled) return;
        editor = editor with { IsGlobalEnabled = enabled };
        EditorError = ShortcutEditorError.None;
        Raise(nameof(EditorGlobal));
    }

    internal async Task<bool> ConfirmEditorAsync(CancellationToken cancellationToken)
    {
        if (editor is null) return false;
        ShortcutActionDefinition? action = FindEditorAction();
        EditorError = Validate(editor, action);
        if (EditorError != ShortcutEditorError.None)
        {
            InputBindingWorkspaceLog.ValidationFailed(logger, editor.Id, EditorError.ToString());
            return false;
        }

        var entry = new ShortcutEntry(
            editor.Id,
            editor.Owner,
            editor.Name,
            editor.ActionKind!,
            editor.TargetId,
            editor.Gesture!,
            editor.IsGlobalEnabled);
        ImmutableArray<ShortcutEntry> next = allEntries
            .Where(existing => existing.Id != entry.Id)
            .Append(entry)
            .ToImmutableArray();
        try
        {
            await store.SaveEntriesAsync(next, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            EditorError = ShortcutEditorError.SaveFailed;
            InputBindingWorkspaceLog.SaveFailed(logger, exception, editor.Id);
            return false;
        }

        ApplyEntries(next);
        selectedEntryId = entry.Id;
        editor = editor with { IsNew = false };
        InputBindingWorkspaceLog.Saved(logger, entry.Id, entry.Owner.ToString(), entry.ActionKind);
        Applied?.Invoke(LayeredShortcutSnapshot.Resolve(allEntries).ActiveProfile);
        RaiseEditorState();
        return true;
    }

    internal void CancelEditor()
    {
        editor = null;
        selectedEntryId = null;
        EditorError = ShortcutEditorError.None;
        RaiseEditorState();
    }

    internal async Task DeleteSelectedAsync(CancellationToken cancellationToken)
    {
        if (selectedEntryId is not Guid id) return;
        ShortcutEntry removed = allEntries.Single(entry => entry.Id == id);
        ShortcutEntry? adjacent = allEntries
            .Where(entry => entry.Owner == removed.Owner && entry.Id != id)
            .FirstOrDefault();
        ImmutableArray<ShortcutEntry> next = allEntries
            .Where(entry => entry.Id != id)
            .ToImmutableArray();
        await store.SaveEntriesAsync(next, cancellationToken).ConfigureAwait(false);
        ApplyEntries(next);
        InputBindingWorkspaceLog.Deleted(logger, id, removed.Owner.ToString());
        Applied?.Invoke(LayeredShortcutSnapshot.Resolve(allEntries).ActiveProfile);
        if (adjacent is null) CancelEditor();
        else Select(adjacent.Id);
        Raise(nameof(Sections));
    }

    public Task<bool> RequestCloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (editor is null) return Task.FromResult(true);
        IsCloseConfirmationVisible = true;
        return Task.FromResult(false);
    }

    internal bool DiscardAndConfirmClose()
    {
        if (!IsCloseConfirmationVisible) return false;
        CancelEditor();
        IsCloseConfirmationVisible = false;
        return true;
    }

    internal void CancelClose() => IsCloseConfirmationVisible = false;

    private ShortcutSectionViewModel CreateSection(ShortcutOwnerKind owner)
    {
        ShortcutEntry[] all = allEntries.Where(entry => entry.Owner == owner).ToArray();
        ImmutableArray<ShortcutRowViewModel> rows = all
            .Select(CreateRow)
            .Where(RowMatchesSearch)
            .ToImmutableArray();
        bool isExpanded = searchQuery.Length > 0 ? !rows.IsEmpty : expanded[owner];
        return new ShortcutSectionViewModel(owner, all.Length, isExpanded, rows);
    }

    private ShortcutRowViewModel CreateRow(ShortcutEntry entry)
    {
        ShortcutActionDefinition? action = actions.FirstOrDefault(candidate =>
            candidate.Owner == entry.Owner
            && StringComparer.Ordinal.Equals(candidate.ActionKind, entry.ActionKind));
        string actionName = action is null ? entry.ActionKind : localize(action.NameResourceKey);
        string? targetName = action is null
            ? entry.TargetId
            : targetProvider(action).FirstOrDefault(target =>
                StringComparer.Ordinal.Equals(target.Id, entry.TargetId))?.DisplayName ?? entry.TargetId;
        return new ShortcutRowViewModel(
            entry.Id,
            entry.Name,
            ShortcutGestureFormatter.Format(entry.Gesture),
            actionName,
            targetName,
            entry.Id == selectedEntryId,
            conflicts.ContainsKey(entry.Id));
    }

    private bool RowMatchesSearch(ShortcutRowViewModel row) =>
        searchQuery.Length == 0
        || Contains(row.Name)
        || Contains(row.GestureText)
        || Contains(row.ActionName)
        || Contains(row.TargetName);

    private bool Contains(string? value) => value?.Contains(searchQuery, StringComparison.OrdinalIgnoreCase) == true;

    private ShortcutActionDefinition? FindEditorAction() => editor is null
        ? null
        : actions.FirstOrDefault(action =>
            action.Owner == editor.Owner
            && StringComparer.Ordinal.Equals(action.ActionKind, editor.ActionKind));

    private void ApplyEntries(ImmutableArray<ShortcutEntry> entries)
    {
        LayeredShortcutSnapshot snapshot = LayeredShortcutSnapshot.Resolve(entries);
        allEntries = snapshot.AllEntries;
        conflicts = snapshot.Conflicts.ToImmutableDictionary(
            static conflict => conflict.Suppressed.Id);
    }

    private void UpdateAutomaticEditorName()
    {
        if (editor is not { IsNameAutomatic: true } draft) return;
        ShortcutActionDefinition? action = FindEditorAction();
        if (action is null) return;

        string baseName = localize(action.NameResourceKey);
        ShortcutTargetOption? target = targetProvider(action).FirstOrDefault(candidate =>
            StringComparer.Ordinal.Equals(candidate.Id, draft.TargetId));
        if (target is not null)
            baseName = $"{baseName} - {target.DisplayName}";

        string candidate = baseName;
        int suffix = 2;
        while (allEntries.Any(entry =>
            entry.Id != draft.Id
            && entry.Owner == draft.Owner
            && StringComparer.Ordinal.Equals(entry.Name, candidate)))
        {
            candidate = $"{baseName}{suffix++}";
        }

        editor = draft with { Name = candidate };
    }

    private ShortcutEditorError Validate(EditorDraft draft, ShortcutActionDefinition? action)
    {
        if (string.IsNullOrWhiteSpace(draft.Name)) return ShortcutEditorError.NameRequired;
        if (action is null) return ShortcutEditorError.ActionRequired;
        if (action.TargetPolicy is ShortcutTargetPolicy.Required or ShortcutTargetPolicy.RequiredWithNone
            && string.IsNullOrWhiteSpace(draft.TargetId))
            return ShortcutEditorError.TargetRequired;
        if (action.TargetPolicy == ShortcutTargetPolicy.None && !string.IsNullOrWhiteSpace(draft.TargetId))
            return ShortcutEditorError.TargetRequired;
        if (draft.Gesture is null) return ShortcutEditorError.GestureRequired;
        if (draft.IsGlobalEnabled && !action.AllowsGlobalRegistration)
            return ShortcutEditorError.GlobalUnavailable;
        if (allEntries.Any(entry =>
            entry.Id != draft.Id
            && entry.Owner == draft.Owner
            && StringComparer.Ordinal.Equals(entry.Gesture.CanonicalText, draft.Gesture.CanonicalText)))
            return ShortcutEditorError.GestureConflict;
        return ShortcutEditorError.None;
    }

    private void RaiseEditorState()
    {
        Raise(nameof(Sections));
        Raise(nameof(SelectedEntryId));
        Raise(nameof(IsEditorVisible));
        Raise(nameof(IsCreating));
        Raise(nameof(EditorOwner));
        Raise(nameof(EditorName));
        Raise(nameof(EditorActionKind));
        Raise(nameof(EditorTargetId));
        Raise(nameof(EditorGesture));
        Raise(nameof(EditorGlobal));
        Raise(nameof(EditorIsSuppressed));
        Raise(nameof(EditorActions));
        Raise(nameof(EditorTargets));
        Raise(nameof(EditorError));
    }

    private void Set(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value) return;
        field = value;
        Raise(propertyName);
    }

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed record EditorDraft(
        Guid Id,
        ShortcutOwnerKind Owner,
        string Name,
        string? ActionKind,
        string? TargetId,
        InputGesture? Gesture,
        bool IsGlobalEnabled,
        bool IsNew,
        bool IsNameAutomatic);
}

internal static partial class InputBindingWorkspaceLog
{
    [LoggerMessage(2060, LogLevel.Debug, "Shortcut draft created in {Owner} section")]
    internal static partial void Created(ILogger logger, string owner);

    [LoggerMessage(2061, LogLevel.Debug, "Shortcut selected: {ShortcutId}")]
    internal static partial void Selected(ILogger logger, Guid shortcutId);

    [LoggerMessage(2062, LogLevel.Warning,
        "Shortcut validation failed for {ShortcutId}: {ValidationError}")]
    internal static partial void ValidationFailed(ILogger logger, Guid shortcutId, string validationError);

    [LoggerMessage(2063, LogLevel.Information,
        "Shortcut saved: {ShortcutId}, owner {Owner}, action {ActionKind}")]
    internal static partial void Saved(ILogger logger, Guid shortcutId, string owner, string actionKind);

    [LoggerMessage(2064, LogLevel.Information,
        "Shortcut deleted: {ShortcutId}, owner {Owner}")]
    internal static partial void Deleted(ILogger logger, Guid shortcutId, string owner);

    [LoggerMessage(2065, LogLevel.Error, "Shortcut save failed: {ShortcutId}")]
    internal static partial void SaveFailed(ILogger logger, Exception exception, Guid shortcutId);
}
