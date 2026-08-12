using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System.ComponentModel;
using Motara.App.Localization;
using Motara.App.ViewModels;

namespace Motara.App.Controls;

internal sealed partial class ModelParameterMappingEditor : UserControl
{
    private readonly ParameterMappingEditorShell editorShell;
    private ModelParameterMappingEditorViewModel? viewModel;
    private LocalizationManager? localization;
    private Action? close;
    private ListBox globalList = null!;
    private ListBox modelList = null!;
    private TextBox globalSearch = null!;
    private TextBox modelSearch = null!;
    private IdentifierCompletionEditor bindingInput = null!;
    private PropertyChangedEventHandler? viewModelPropertyChanged;
    private readonly DispatcherTimer observationTimer;

    public ModelParameterMappingEditor()
    {
        AvaloniaXamlLoader.Load(this);
        editorShell = this.FindControl<ParameterMappingEditorShell>("EditorShell")!;
        editorShell.CloseApproved += (_, _) => close?.Invoke();
        globalList = this.FindControl<ListBox>("GlobalList")!;
        modelList = this.FindControl<ListBox>("ModelList")!;
        globalSearch = this.FindControl<TextBox>("GlobalSearch")!;
        modelSearch = this.FindControl<TextBox>("ModelSearch")!;
        bindingInput = this.FindControl<IdentifierCompletionEditor>("BindingInput")!;
        observationTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) =>
        {
            viewModel?.RefreshCurrentValues();
            RefreshCurrentValueLabels();
        });
        globalSearch.TextChanged += (_, _) => { if (viewModel is not null) viewModel.GlobalSearchText = globalSearch.Text ?? string.Empty; RefreshLists(); };
        modelSearch.TextChanged += (_, _) => { if (viewModel is not null) viewModel.ModelSearchText = modelSearch.Text ?? string.Empty; RefreshLists(); };
        globalList.SelectionChanged += (_, _) => viewModel!.SelectedGlobalParameter = globalList.SelectedItem as GlobalParameterEditorItem;
        globalList.DoubleTapped += (_, _) => { if (viewModel?.BindSelectedGlobalParameter() == true) RefreshSelection(); };
        modelList.SelectionChanged += (_, _) => { viewModel!.SelectedModelParameter = modelList.SelectedItem as ModelParameterEditorItem; RefreshSelection(); };
        bindingInput.TextChanged += (_, _) =>
        {
            if (viewModel is not null)
            {
                viewModel.BindingInputText = bindingInput.Text;
                RefreshBindingValidation();
            }
        };
        bindingInput.Submitted += (_, id) => BindGlobalParameter(id);
        bindingInput.CompletionAccepted += (_, id) => BindGlobalParameter(id);
        this.FindControl<Button>("ClearBinding")!.Click += (_, _) =>
        {
            if (viewModel?.ClearSelectedBinding() == true)
            {
                RefreshSelection();
            }
        };
        this.FindControl<Button>("AutoMatch")!.Click += (_, _) => { viewModel?.AutoMatchStandardParameters(); RefreshLists(); RefreshSelection(); };
        AutomationProperties.SetAutomationId(globalList, "workspace.model-mapping.globals");
        AutomationProperties.SetAutomationId(modelList, "workspace.model-mapping.model");
        AutomationProperties.SetAutomationId(bindingInput, "workspace.model-mapping.binding");
        AutomationProperties.SetAutomationId(globalSearch, "workspace.model-mapping.global-search");
        AutomationProperties.SetAutomationId(modelSearch, "workspace.model-mapping.model-search");
        AutomationProperties.SetAutomationId(
            this.FindControl<Button>("ClearBinding")!,
            "workspace.model-mapping.clear-binding");
        AutomationProperties.SetAutomationId(editorShell.FindControl<Button>("ApplyButton")!, "workspace.model-mapping.apply");
        AutomationProperties.SetAutomationId(editorShell.FindControl<Button>("AcknowledgeApplyResultButton")!, "workspace.model-mapping.apply-result.acknowledge");
        AutomationProperties.SetAutomationId(editorShell.FindControl<Button>("DiscardCloseButton")!, "workspace.model-mapping.discard");
        AutomationProperties.SetAutomationId(editorShell.FindControl<Button>("SaveAndCloseButton")!, "workspace.model-mapping.save-and-close");
    }

    public void Attach(ModelParameterMappingEditorViewModel value, LocalizationManager localization, Action close)
    {
        viewModel = value;
        this.localization = localization;
        this.close = close;
        DataContext = value;
        viewModelPropertyChanged = OnViewModelPropertyChanged;
        value.PropertyChanged += viewModelPropertyChanged;
        SetText("GlobalTitle", "Workspace.ModelMapping.GlobalParameters");
        SetText(
            "ModelTitle",
            value.IsExternalOutputMapping
                ? "Workspace.CubismMapping.EditorParameters"
                : "Workspace.ModelMapping.ModelParameters");
        SetText("SettingsTitle", "Workspace.ModelMapping.Settings");
        SetText("NoSelection", "Workspace.ModelMapping.NoSelection");
        SetText("BindingLabel", "Workspace.ModelMapping.Binding");
        globalSearch.PlaceholderText = localization.GetString("Workspace.ModelMapping.Search");
        modelSearch.PlaceholderText = localization.GetString("Workspace.ModelMapping.Search");
        bindingInput.PlaceholderText = localization.GetString("Workspace.ModelMapping.BindingPlaceholder");
        this.FindControl<Button>("ClearBinding")!.Content =
            localization.GetString("Workspace.ModelMapping.ClearBinding");
        bindingInput.SetCompletions(value.GlobalParameters.Select(item => new FormulaCompletionItem(
            item.Id,
            item.Id,
            localization.GetString("Workspace.SourceMapping.Completion.Output"),
            item.Subtitle ?? string.Empty,
            null,
            FormulaCompletionKind.Output)));
        this.FindControl<Button>("AutoMatch")!.Content =
            localization.GetString("Workspace.ModelMapping.AutoMatch");
        editorShell.Attach(CreateSession(value, localization), localization);
        this.FindControl<ToggleSwitch>("ClampInput")!.Content = localization.GetString("Workspace.ModelMapping.ClampInput");
        this.FindControl<ToggleSwitch>("ClampOutput")!.Content = localization.GetString("Workspace.ModelMapping.ClampOutput");
        this.FindControl<ToggleSwitch>("AutoBlink")!.Content = localization.GetString("Workspace.ModelMapping.AutoBlink");
        this.FindControl<ToggleSwitch>("AutoBreath")!.Content = localization.GetString("Workspace.ModelMapping.AutoBreath");
        this.FindControl<Grid>("RangeMappingPanel")!.IsVisible = !value.IsExternalOutputMapping;
        this.FindControl<StackPanel>("ModelBehaviorSettings")!.IsVisible = !value.IsExternalOutputMapping;
        AutomationProperties.SetName(globalSearch, localization.GetString("Workspace.ModelMapping.GlobalParameters"));
        AutomationProperties.SetName(modelSearch, localization.GetString("Workspace.ModelMapping.ModelParameters"));
        AutomationProperties.SetName(bindingInput, localization.GetString("Workspace.ModelMapping.Binding"));
        RefreshLists();
        RefreshSelection();
        observationTimer.Start();
    }

    internal void Detach()
    {
        observationTimer.Stop();
        if (viewModel is not null && viewModelPropertyChanged is not null)
        {
            viewModel.PropertyChanged -= viewModelPropertyChanged;
        }

        editorShell.Detach();
        viewModelPropertyChanged = null;
        viewModel = null;
        DataContext = null;
    }

    private void BindGlobalParameter(string id)
    {
        if (viewModel?.TryBindGlobalParameterId(id) == true)
        {
            RefreshSelection();
        }
    }

    private static ParameterMappingEditorSession CreateSession(
        ModelParameterMappingEditorViewModel editor,
        LocalizationManager localization) => new(
            editor,
            () => editor.IsCloseConfirmationVisible,
            editor.RequestCloseAsync,
            async cancellationToken =>
            {
                ModelParameterMappingApplyResult result = await editor.ApplyAsync(cancellationToken);
                string titleKey = result == ModelParameterMappingApplyResult.Success
            ? "Workspace.SourceMapping.ApplyResult.SuccessTitle"
            : "Workspace.SourceMapping.ApplyResult.FailureTitle";
                string messageKey = result switch
                {
                    ModelParameterMappingApplyResult.Success => "Workspace.ModelMapping.Applied",
                    ModelParameterMappingApplyResult.ValidationFailed => "Workspace.ModelMapping.ApplyValidationFailed",
                    _ => "Workspace.ModelMapping.ApplyFailed",
                };
                return new ParameterMappingEditorFeedback(
                    result == ModelParameterMappingApplyResult.Success,
                    localization.GetString(titleKey),
                    localization.GetString(messageKey));
            },
            editor.CancelClose,
            editor.DiscardAndClose,
            "Workspace.ModelMapping.UnsavedChanges");

    private void RefreshLists()
    {
        if (viewModel is null) return;
        globalList.ItemsSource = viewModel.FilteredGlobalParameters.ToArray();
        modelList.ItemsSource = viewModel.FilteredModelParameters.ToArray();
    }

    private void RefreshSelection()
    {
        if (viewModel is null) return;
        this.FindControl<StackPanel>("SettingsContent")!.IsVisible = viewModel.HasSelection;
        this.FindControl<TextBlock>("NoSelection")!.IsVisible = !viewModel.HasSelection;
        if (bindingInput.Text != viewModel.BindingInputText) bindingInput.Text = viewModel.BindingInputText;
        RefreshBindingValidation();
        RefreshCurrentValueLabels();
        this.FindControl<TextBlock>("InputDeclaredRange")!.Text = FormatDeclaredRange(
            viewModel.InputDeclaredMinimum,
            viewModel.InputDeclaredDefault,
            viewModel.InputDeclaredMaximum);
        this.FindControl<TextBlock>("OutputDeclaredRange")!.Text = FormatDeclaredRange(
            viewModel.OutputDeclaredMinimum,
            viewModel.OutputDeclaredDefault,
            viewModel.OutputDeclaredMaximum);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args) => RefreshSelection();

    private void RefreshCurrentValueLabels()
    {
        if (viewModel is null || localization is null) return;
        this.FindControl<TextBlock>("CurrentInput")!.Text = $"{localization.GetString("Workspace.ModelMapping.CurrentInput")}: {viewModel.CurrentInputValueText}";
        this.FindControl<TextBlock>("CurrentOutput")!.Text = $"{localization.GetString("Workspace.ModelMapping.CurrentOutput")}: {viewModel.CurrentOutputValueText}";
    }

    private void RefreshBindingValidation()
    {
        if (viewModel is null || localization is null) return;
        TextBlock error = this.FindControl<TextBlock>("MappingError")!;
        string? key = viewModel.BindingInputError switch
        {
            ModelParameterBindingInputError.InvalidSyntax => "Workspace.ModelMapping.BindingInvalidSyntax",
            ModelParameterBindingInputError.UnknownGlobalParameter => "Workspace.ModelMapping.BindingUnknown",
            _ => null,
        };
        if (key is null && viewModel.HasRangeInputError)
        {
            key = "Workspace.ModelMapping.RangeIncomplete";
        }

        error.Text = key is null ? string.Empty : localization.GetString(key);
    }

    private string FormatDeclaredRange(double? minimum, double? defaultValue, double? maximum)
    {
        string Format(double? value) => value?.ToString("0.###", localization!.Culture) ?? "--";
        return string.Format(
            localization!.Culture,
            localization.GetString("Workspace.ModelMapping.RangeSummary"),
            Format(minimum),
            Format(defaultValue),
            Format(maximum));
    }

    private void SetText(string name, string key) => this.FindControl<TextBlock>(name)!.Text = localization!.GetString(key);
}
