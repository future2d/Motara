using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Motara.App.Localization;
using Motara.App.Models;
using Motara.App.ViewModels;

namespace Motara.App.Controls;

internal sealed partial class ModelPhysicsSettingsControl : UserControl
{
    private readonly ParameterMappingEditorShell editorShell;
    private readonly ComboBox calculationFrameRateSelector;
    private ModelPhysicsSettingsViewModel? viewModel;
    private bool isUpdating;

    public ModelPhysicsSettingsControl()
    {
        AvaloniaXamlLoader.Load(this);
        editorShell = this.FindControl<ParameterMappingEditorShell>("EditorShell")!;
        calculationFrameRateSelector = this.FindControl<ComboBox>("CalculationFrameRateSelector")!;
        calculationFrameRateSelector.SelectionChanged += OnCalculationFrameRateSelectionChanged;
        AutomationProperties.SetAutomationId(this, "workspace.model-physics");
        AutomationProperties.SetAutomationId(
            this.FindControl<ToggleSwitch>("Enabled")!,
            "workspace.model-physics.enabled");
    }

    internal void Attach(
        ModelPhysicsSettingsViewModel value,
        LocalizationManager localization,
        Action close)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(close);
        Detach();
        viewModel = value;
        DataContext = value;
        this.FindControl<ToggleSwitch>("Enabled")!.Content =
            localization.GetString("Workspace.ModelPhysics.Enabled");
        this.FindControl<TextBlock>("StrengthLabel")!.Text =
            localization.GetString("Workspace.ModelPhysics.Strength");
        this.FindControl<TextBlock>("WindSimulationLabel")!.Text =
            localization.GetString("Workspace.ModelPhysics.WindSimulation");
        this.FindControl<TextBlock>("DragPhysicsLabel")!.Text =
            localization.GetString("Workspace.ModelPhysics.DragPhysics");
        this.FindControl<TextBlock>("CalculationFrameRateLabel")!.Text =
            localization.GetString("Workspace.ModelPhysics.CalculationFrameRate");
        this.FindControl<ToggleSwitch>("MotionExpansionEnabled")!.Content =
            localization.GetString("Workspace.ModelPhysics.MotionExpansionEnabled");
        this.FindControl<TextBlock>("MotionExpansionXLabel")!.Text =
            localization.GetString("Workspace.ModelPhysics.MotionExpansionX");
        this.FindControl<TextBlock>("MotionExpansionYLabel")!.Text =
            localization.GetString("Workspace.ModelPhysics.MotionExpansionY");
        this.FindControl<TextBlock>("MotionExpansionZLabel")!.Text =
            localization.GetString("Workspace.ModelPhysics.MotionExpansionZ");
        isUpdating = true;
        calculationFrameRateSelector.ItemsSource = new[]
        {
            new PhysicsFrameRateOption(localization.GetString("Workspace.ModelPhysics.CalculationFrameRateFollowApplication"), PhysicsCalculationFrameRate.FollowApplication),
            new PhysicsFrameRateOption("30 FPS", PhysicsCalculationFrameRate.FramesPerSecond30),
            new PhysicsFrameRateOption("60 FPS", PhysicsCalculationFrameRate.FramesPerSecond60),
            new PhysicsFrameRateOption("120 FPS", PhysicsCalculationFrameRate.FramesPerSecond120),
        };
        calculationFrameRateSelector.SelectedItem = calculationFrameRateSelector.Items
            .OfType<PhysicsFrameRateOption>()
            .Single(option => option.Value == value.CalculationFrameRate);
        isUpdating = false;
        editorShell.Attach(new ParameterMappingEditorSession(
            value,
            () => value.IsCloseConfirmationVisible,
            value.RequestCloseAsync,
            token => ApplyAsync(value, localization, token),
            value.CancelClose,
            value.DiscardAndClose,
            "Workspace.ModelPhysics.UnsavedChanges"), localization);
        editorShell.CloseApproved += OnCloseApproved;
        CloseRequested = close;
    }

    internal Action? CloseRequested { get; private set; }

    internal void Detach()
    {
        editorShell.CloseApproved -= OnCloseApproved;
        editorShell.Detach();
        CloseRequested = null;
        viewModel = null;
        DataContext = null;
    }

    private void OnCalculationFrameRateSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!isUpdating && calculationFrameRateSelector.SelectedItem is PhysicsFrameRateOption option
            && viewModel is not null)
        {
            viewModel.CalculationFrameRate = option.Value;
        }
    }

    private void OnCloseApproved(object? sender, EventArgs args) => CloseRequested?.Invoke();

    private static async Task<ParameterMappingEditorFeedback> ApplyAsync(
        ModelPhysicsSettingsViewModel settings,
        LocalizationManager localization,
        CancellationToken cancellationToken)
    {
        ModelPhysicsSettingsApplyResult result = await settings.ApplyAsync(cancellationToken);
        string messageKey = result switch
        {
            ModelPhysicsSettingsApplyResult.Success => "Workspace.ModelPhysics.Applied",
            ModelPhysicsSettingsApplyResult.ValidationFailed => "Workspace.ModelPhysics.ApplyValidationFailed",
            _ => "Workspace.ModelPhysics.ApplyFailed",
        };
        return new ParameterMappingEditorFeedback(
            result == ModelPhysicsSettingsApplyResult.Success,
            localization.GetString("Workspace.ModelPhysics.Title"),
            localization.GetString(messageKey));
    }

    private sealed record PhysicsFrameRateOption(string Label, PhysicsCalculationFrameRate Value)
    {
        public override string ToString() => Label;
    }
}
