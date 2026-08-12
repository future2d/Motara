using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Parameters;
using Motara.App.Shell;

namespace Motara.App.ViewModels;

internal enum ParameterPriorityApplyResult
{
    Success,
    StorageFailure,
}

internal sealed record ParameterPriorityItemViewModel(
    ParameterProviderKind Kind,
    string LabelResourceKey);

internal sealed class ParameterPriorityWorkspaceViewModel : INotifyPropertyChanged, IWorkspaceCloseGuard
{
    private readonly Func<ParameterPriorityProfile, CancellationToken, Task> saveAsync;
    private readonly Action<ParameterPriorityProfile> publish;
    private readonly ILogger logger;
    private ParameterPriorityProfile baseline;
    private ImmutableArray<ParameterPriorityItemViewModel> items;
    private int selectedIndex = -1;
    private bool isRestoreConfirmationVisible;
    private bool isCloseConfirmationVisible;
    private ParameterPriorityApplyResult? applyResult;

    internal ParameterPriorityWorkspaceViewModel(
        ParameterPriorityProfile profile,
        Func<ParameterPriorityProfile, CancellationToken, Task> saveAsync,
        Action<ParameterPriorityProfile> publish,
        ILogger? logger = null)
    {
        baseline = profile ?? throw new ArgumentNullException(nameof(profile));
        baseline.Validate();
        this.saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
        this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
        this.logger = logger ?? NullLogger.Instance;
        items = CreateItems(profile);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal ImmutableArray<ParameterPriorityItemViewModel> Items => items;

    internal int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            int normalized = value >= 0 && value < items.Length ? value : -1;
            if (selectedIndex == normalized) return;
            selectedIndex = normalized;
            Raise();
            Raise(nameof(CanMoveUp));
            Raise(nameof(CanMoveDown));
        }
    }

    internal bool CanMoveUp => selectedIndex > 0;

    internal bool CanMoveDown => selectedIndex >= 0 && selectedIndex < items.Length - 1;

    internal bool IsDirty => !CreateProfile().Order.SequenceEqual(baseline.Order);

    internal bool IsRestoreConfirmationVisible
    {
        get => isRestoreConfirmationVisible;
        private set => Set(ref isRestoreConfirmationVisible, value);
    }

    internal bool IsCloseConfirmationVisible
    {
        get => isCloseConfirmationVisible;
        private set => Set(ref isCloseConfirmationVisible, value);
    }

    internal ParameterPriorityApplyResult? ApplyResult
    {
        get => applyResult;
        private set => Set(ref applyResult, value);
    }

    internal bool MoveUp() => MoveSelected(-1);

    internal bool MoveDown() => MoveSelected(1);

    internal void RequestRestoreDefault() => IsRestoreConfirmationVisible = true;

    internal void CancelRestoreDefault() => IsRestoreConfirmationVisible = false;

    internal void ConfirmRestoreDefault()
    {
        items = CreateItems(ParameterPriorityProfile.Default);
        SelectedIndex = -1;
        IsRestoreConfirmationVisible = false;
        Raise(nameof(Items));
        Raise(nameof(IsDirty));
        ParameterPriorityWorkspaceLog.DefaultsRestored(logger);
    }

    internal async Task<ParameterPriorityApplyResult> ApplyAsync(CancellationToken cancellationToken)
    {
        ParameterPriorityProfile profile = CreateProfile();
        try
        {
            await saveAsync(profile, cancellationToken).ConfigureAwait(false);
            publish(profile);
            baseline = profile;
            ApplyResult = ParameterPriorityApplyResult.Success;
            Raise(nameof(IsDirty));
            ParameterPriorityWorkspaceLog.Applied(logger, profile.Order.Length);
            return ApplyResult.Value;
        }
        catch (IOException exception)
        {
            ApplyResult = ParameterPriorityApplyResult.StorageFailure;
            ParameterPriorityWorkspaceLog.ApplyFailed(logger, exception);
            return ApplyResult.Value;
        }
        catch (UnauthorizedAccessException exception)
        {
            ApplyResult = ParameterPriorityApplyResult.StorageFailure;
            ParameterPriorityWorkspaceLog.ApplyFailed(logger, exception);
            return ApplyResult.Value;
        }
    }

    public Task<bool> RequestCloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsDirty) return Task.FromResult(true);
        IsCloseConfirmationVisible = true;
        return Task.FromResult(false);
    }

    internal void CancelClose() => IsCloseConfirmationVisible = false;

    internal void DiscardAndClose()
    {
        items = CreateItems(baseline);
        SelectedIndex = -1;
        IsCloseConfirmationVisible = false;
        Raise(nameof(Items));
        Raise(nameof(IsDirty));
    }

    private bool MoveSelected(int offset)
    {
        int target = selectedIndex + offset;
        if (selectedIndex < 0 || target < 0 || target >= items.Length) return false;
        ParameterPriorityItemViewModel selected = items[selectedIndex];
        ParameterPriorityItemViewModel displaced = items[target];
        items = items.SetItem(target, selected).SetItem(selectedIndex, displaced);
        selectedIndex = target;
        Raise(nameof(Items));
        Raise(nameof(SelectedIndex));
        Raise(nameof(CanMoveUp));
        Raise(nameof(CanMoveDown));
        Raise(nameof(IsDirty));
        return true;
    }

    private ParameterPriorityProfile CreateProfile() => ParameterPriorityProfile.Create(
        items.Select(static item => item.Kind));

    private static ImmutableArray<ParameterPriorityItemViewModel> CreateItems(
        ParameterPriorityProfile profile) => profile.Order
        .Select(kind => new ParameterPriorityItemViewModel(kind, GetLabelKey(kind)))
        .ToImmutableArray();

    private static string GetLabelKey(ParameterProviderKind kind) => kind switch
    {
        ParameterProviderKind.Default => "Workspace.ParameterPriority.Provider.Default",
        ParameterProviderKind.AutoBreath => "Workspace.ParameterPriority.Provider.AutoBreath",
        ParameterProviderKind.AutoBlink => "Workspace.ParameterPriority.Provider.AutoBlink",
        ParameterProviderKind.IdleAnimation => "Workspace.ParameterPriority.Provider.IdleAnimation",
        ParameterProviderKind.Tracking => "Workspace.ParameterPriority.Provider.Tracking",
        ParameterProviderKind.OneShotAnimation => "Workspace.ParameterPriority.Provider.OneShotAnimation",
        ParameterProviderKind.Expression => "Workspace.ParameterPriority.Provider.Expression",
        ParameterProviderKind.Physics => "Workspace.ParameterPriority.Provider.Physics",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal static partial class ParameterPriorityWorkspaceLog
{
    [LoggerMessage(6710, LogLevel.Information, "Parameter priority workspace applied {ProviderCount} providers")]
    internal static partial void Applied(ILogger logger, int providerCount);

    [LoggerMessage(6711, LogLevel.Warning, "Parameter priority workspace apply failed")]
    internal static partial void ApplyFailed(ILogger logger, Exception exception);

    [LoggerMessage(6712, LogLevel.Information, "Parameter priority workspace restored default draft")]
    internal static partial void DefaultsRestored(ILogger logger);
}
