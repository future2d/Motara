using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Motara.App.ViewModels;

internal sealed record SourceMappingEditorAdapterItem(
    string AdapterId,
    string DisplayNameResourceKey);

internal sealed class SourceMappingEditorHostViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ImmutableDictionary<string, Func<CancellationToken, Task<SourceMappingEditorViewModel>>> factories;
    private readonly Dictionary<string, SourceMappingEditorViewModel> editors =
        new(StringComparer.Ordinal);
    private SourceMappingEditorViewModel currentEditor;
    private string selectedAdapterId;
    private bool isCloseConfirmationVisible;
    private int disposed;

    internal SourceMappingEditorHostViewModel(
        SourceMappingEditorViewModel initialEditor,
        string initialAdapterId,
        IEnumerable<SourceMappingEditorAdapterItem> adapters,
        IEnumerable<KeyValuePair<string, Func<CancellationToken, Task<SourceMappingEditorViewModel>>>> factories)
    {
        currentEditor = initialEditor ?? throw new ArgumentNullException(nameof(initialEditor));
        ArgumentException.ThrowIfNullOrWhiteSpace(initialAdapterId);
        AvailableAdapters = adapters?.ToImmutableArray()
            ?? throw new ArgumentNullException(nameof(adapters));
        if (AvailableAdapters.IsDefaultOrEmpty
            || !AvailableAdapters.Any(item => StringComparer.Ordinal.Equals(item.AdapterId, initialAdapterId))
            || AvailableAdapters.Select(item => item.AdapterId).Distinct(StringComparer.Ordinal).Count()
                != AvailableAdapters.Length)
        {
            throw new ArgumentException("Source mapping adapter definitions are invalid.", nameof(adapters));
        }

        this.factories = factories?.ToImmutableDictionary(
                static item => item.Key,
                static item => item.Value,
                StringComparer.Ordinal)
            ?? throw new ArgumentNullException(nameof(factories));
        if (!this.factories.Keys.ToHashSet(StringComparer.Ordinal)
                .SetEquals(AvailableAdapters.Select(item => item.AdapterId)))
        {
            throw new ArgumentException("Source mapping adapter factories are invalid.", nameof(factories));
        }

        selectedAdapterId = initialAdapterId;
        editors.Add(initialAdapterId, currentEditor);
        currentEditor.PropertyChanged += OnEditorPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal ImmutableArray<SourceMappingEditorAdapterItem> AvailableAdapters { get; }

    internal SourceMappingEditorViewModel CurrentEditor => currentEditor;

    internal string SelectedAdapterId => selectedAdapterId;

    internal bool IsDirty => editors.Values.Any(static editor => editor.IsDirty);

    internal bool IsCloseConfirmationVisible
    {
        get => isCloseConfirmationVisible;
        private set
        {
            if (isCloseConfirmationVisible == value)
            {
                return;
            }

            isCloseConfirmationVisible = value;
            Raise();
        }
    }

    internal Task<bool> RequestCloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsDirty)
        {
            return Task.FromResult(true);
        }

        IsCloseConfirmationVisible = true;
        return Task.FromResult(false);
    }

    internal void CancelClose() => IsCloseConfirmationVisible = false;

    internal void DiscardAndConfirmClose() => IsCloseConfirmationVisible = false;

    internal async Task<bool> ApplyAllAsync(CancellationToken cancellationToken)
    {
        foreach (SourceMappingEditorViewModel editor in editors.Values)
        {
            if (!editor.IsDirty
                || await editor.ApplyAsync(cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    internal async Task<bool> SelectAdapterAsync(
        string adapterId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!factories.TryGetValue(adapterId, out Func<CancellationToken, Task<SourceMappingEditorViewModel>>? createEditor))
        {
            return false;
        }

        if (StringComparer.Ordinal.Equals(selectedAdapterId, adapterId))
        {
            return true;
        }

        SourceMappingEditorViewModel editor;
        if (!editors.TryGetValue(adapterId, out editor!))
        {
            editor = await createEditor(cancellationToken).ConfigureAwait(true);
            editors.Add(adapterId, editor);
            editor.PropertyChanged += OnEditorPropertyChanged;
        }

        currentEditor = editor;
        selectedAdapterId = adapterId;
        Raise(nameof(CurrentEditor));
        Raise(nameof(SelectedAdapterId));
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        foreach (SourceMappingEditorViewModel editor in editors.Values)
        {
            editor.PropertyChanged -= OnEditorPropertyChanged;
            editor.Dispose();
        }

        editors.Clear();
    }

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SourceMappingEditorViewModel.IsDirty))
        {
            Raise(nameof(IsDirty));
        }
    }
}
