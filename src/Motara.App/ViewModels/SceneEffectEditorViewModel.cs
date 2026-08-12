using System.ComponentModel;
using System.Runtime.CompilerServices;
using Motara.App.Shell;
using Motara.Scene;

namespace Motara.App.ViewModels;

public sealed class SceneEffectEditorViewModel : INotifyPropertyChanged, IWorkspaceCloseGuard
{
    private readonly Func<SceneEffectInstance?, CancellationToken, Task> saveAsync;
    private SceneEffectInstance? baseline;
    private double baselineRadius;
    private bool baselineEnabled;
    private double radius;
    private bool isEnabled;
    private bool isCloseConfirmationVisible;
    private bool isDeleteConfirmationVisible;

    internal SceneEffectEditorViewModel(
        SceneEffectInstance? effect,
        Func<SceneEffectInstance?, CancellationToken, Task> saveAsync)
    {
        baseline = effect;
        this.saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
        radius = effect?.Blur?.Radius ?? 8;
        isEnabled = effect?.IsEnabled ?? true;
        baselineRadius = radius;
        baselineEnabled = isEnabled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public double Radius
    {
        get => radius;
        set
        {
            if (!double.IsFinite(value) || value < 0 || value > 40)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            Set(ref radius, value);
            Raise(nameof(IsDirty));
        }
    }

    public bool IsEnabled
    {
        get => isEnabled;
        set
        {
            Set(ref isEnabled, value);
            Raise(nameof(IsDirty));
        }
    }

    public bool IsDirty => baselineRadius != Radius || baselineEnabled != IsEnabled;

    public bool IsCloseConfirmationVisible
    {
        get => isCloseConfirmationVisible;
        private set => Set(ref isCloseConfirmationVisible, value);
    }

    public bool IsDeleteConfirmationVisible
    {
        get => isDeleteConfirmationVisible;
        private set => Set(ref isDeleteConfirmationVisible, value);
    }

    public bool CanDelete => baseline is not null;

    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        SceneEffectInstance effect = baseline is null
            ? SceneEffectInstance.CreateBlur(Radius, IsEnabled)
            : baseline.SetBlurRadius(Radius).SetEnabled(IsEnabled);
        await saveAsync(effect, cancellationToken).ConfigureAwait(false);
        baseline = effect;
        baselineRadius = Radius;
        baselineEnabled = IsEnabled;
        Raise(nameof(IsDirty));
        Raise(nameof(CanDelete));
    }

    public void RequestDelete()
    {
        if (CanDelete) IsDeleteConfirmationVisible = true;
    }

    public void CancelDelete() => IsDeleteConfirmationVisible = false;

    public async Task<bool> ConfirmDeleteAsync(CancellationToken cancellationToken)
    {
        if (!IsDeleteConfirmationVisible) return false;
        await saveAsync(null, cancellationToken).ConfigureAwait(false);
        baseline = null;
        baselineRadius = 8;
        baselineEnabled = true;
        IsDeleteConfirmationVisible = false;
        Raise(nameof(CanDelete));
        Raise(nameof(IsDirty));
        return true;
    }

    public Task<bool> RequestCloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsDirty) return Task.FromResult(true);
        IsCloseConfirmationVisible = true;
        return Task.FromResult(false);
    }

    public void CancelClose() => IsCloseConfirmationVisible = false;

    public void DiscardChanges()
    {
        Radius = baselineRadius;
        IsEnabled = baselineEnabled;
        IsCloseConfirmationVisible = false;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(propertyName);
    }

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
