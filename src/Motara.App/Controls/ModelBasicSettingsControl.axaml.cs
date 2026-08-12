using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Motara.App.Localization;
using Motara.App.Models;
using Motara.App.ViewModels;

namespace Motara.App.Controls;

internal sealed partial class ModelBasicSettingsControl : UserControl
{
    private readonly ParameterMappingEditorShell editorShell;
    private readonly ComboBox idleSelector;
    private readonly ComboBox lostIdleSelector;
    private readonly Image previewImage;
    private ModelBasicSettingsViewModel? viewModel;
    private LocalizationManager? localization;
    private bool updating;

    public ModelBasicSettingsControl()
    {
        AvaloniaXamlLoader.Load(this);
        editorShell = this.FindControl<ParameterMappingEditorShell>("EditorShell")!;
        idleSelector = this.FindControl<ComboBox>("IdleMotionSelector")!;
        lostIdleSelector = this.FindControl<ComboBox>("LostIdleMotionSelector")!;
        previewImage = this.FindControl<Image>("PreviewImage")!;
        idleSelector.SelectionChanged += OnIdleSelectionChanged;
        lostIdleSelector.SelectionChanged += OnLostIdleSelectionChanged;
        this.FindControl<Button>("ChoosePreviewButton")!.Click += async (_, _) => await ChoosePreviewAsync();
        this.FindControl<Button>("RestoreDefaultsButton")!.Click += (_, _) => RestoreDefaults();
        AutomationProperties.SetAutomationId(this, "workspace.model-basic");
        AutomationProperties.SetAutomationId(this.FindControl<TextBox>("NicknameInput")!, "workspace.model-basic.nickname");
        AutomationProperties.SetAutomationId(this.FindControl<Button>("ChoosePreviewButton")!, "workspace.model-basic.choose-preview");
    }

    internal void Attach(
        ModelBasicSettingsViewModel value,
        LocalizationManager manager,
        Action close)
    {
        Detach();
        viewModel = value ?? throw new ArgumentNullException(nameof(value));
        localization = manager ?? throw new ArgumentNullException(nameof(manager));
        DataContext = value;
        SetText("PreviewLabel", "Workspace.ModelBasic.Preview");
        SetText("NicknameLabel", "Workspace.ModelBasic.Nickname");
        SetText("IdleMotionLabel", "Workspace.ModelBasic.IdleMotion");
        SetText("LostIdleMotionLabel", "Workspace.ModelBasic.LostIdleMotion");
        this.FindControl<Button>("ChoosePreviewButton")!.Content = manager.GetString("Workspace.ModelBasic.ChoosePreview");
        this.FindControl<Button>("RestoreDefaultsButton")!.Content = manager.GetString("Command.RestoreDefaults");
        PopulateSelectors();
        UpdatePreviewPath();
        _ = LoadPreviewAsync(value.PreviewPath);
        editorShell.Attach(new ParameterMappingEditorSession(
            value,
            () => value.IsCloseConfirmationVisible,
            value.RequestCloseAsync,
            token => ApplyAsync(value, manager, token),
            value.CancelClose,
            value.DiscardAndClose,
            "Workspace.ModelBasic.UnsavedChanges"), manager);
        editorShell.CloseApproved += OnCloseApproved;
        CloseRequested = close;
    }

    internal Action? CloseRequested { get; private set; }

    internal void Detach()
    {
        editorShell.CloseApproved -= OnCloseApproved;
        editorShell.Detach();
        if (previewImage.Source is IDisposable preview)
        {
            preview.Dispose();
        }
        previewImage.Source = null;
        viewModel = null;
        localization = null;
        DataContext = null;
        CloseRequested = null;
    }

    private void PopulateSelectors()
    {
        updating = true;
        var regular = new List<IdleOption>
        {
            new(localization!.GetString("Workspace.ModelBasic.IdleAutomatic"), ModelIdleMotionSelection.Automatic),
            new(localization.GetString("Workspace.ModelBasic.NoAnimation"), ModelIdleMotionSelection.None),
        };
        regular.AddRange(viewModel!.Motions.Select(motion =>
            new IdleOption(motion.Name, ModelIdleMotionSelection.Asset(motion.AssetId))));
        idleSelector.ItemsSource = regular;
        idleSelector.SelectedItem = regular.First(option => option.Selection == viewModel.IdleMotion);

        var lost = new List<LostIdleOption>
        {
            new(localization.GetString("Workspace.ModelBasic.UseRegularIdle"), ModelLostTrackingIdleMotionSelection.UseRegularIdle),
            new(localization.GetString("Workspace.ModelBasic.NoAnimation"), ModelLostTrackingIdleMotionSelection.None),
        };
        lost.AddRange(viewModel.Motions.Select(motion =>
            new LostIdleOption(motion.Name, ModelLostTrackingIdleMotionSelection.Asset(motion.AssetId))));
        lostIdleSelector.ItemsSource = lost;
        lostIdleSelector.SelectedItem = lost.First(option => option.Selection == viewModel.LostTrackingIdleMotion);
        updating = false;
    }

    private async Task ChoosePreviewAsync()
    {
        if (viewModel is null || localization is null
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider) return;
        IReadOnlyList<IStorageFile> files = await provider.OpenFilePickerAsync(
            BackgroundEditorControl.CreateImagePickerOptions(
                localization.GetString("Workspace.ModelBasic.ChoosePreview")));
        string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is null) return;
        viewModel.SelectPreview(path);
        UpdatePreviewPath();
        await LoadPreviewAsync(path);
    }

    private async Task LoadPreviewAsync(string? path)
    {
        Bitmap? bitmap = path is not null && File.Exists(path)
            ? await Task.Run(() => new Bitmap(path)).ConfigureAwait(false)
            : null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (previewImage.Source is IDisposable previous)
            {
                previous.Dispose();
            }
            previewImage.Source = bitmap;
        });
    }

    private void RestoreDefaults()
    {
        if (viewModel is null) return;
        viewModel.RestoreDefaults();
        PopulateSelectors();
    }

    private void UpdatePreviewPath() => this.FindControl<TextBlock>("PreviewPathText")!.Text =
        viewModel?.PreviewPath is { } path ? Path.GetFileName(path) : localization?.GetString("Workspace.ModelBasic.NoPreview");

    private void OnIdleSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!updating && viewModel is not null && idleSelector.SelectedItem is IdleOption option)
            viewModel.IdleMotion = option.Selection;
    }

    private void OnLostIdleSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!updating && viewModel is not null && lostIdleSelector.SelectedItem is LostIdleOption option)
            viewModel.LostTrackingIdleMotion = option.Selection;
    }

    private void SetText(string name, string key) =>
        this.FindControl<TextBlock>(name)!.Text = localization!.GetString(key);

    private void OnCloseApproved(object? sender, EventArgs args) => CloseRequested?.Invoke();

    private static async Task<ParameterMappingEditorFeedback> ApplyAsync(
        ModelBasicSettingsViewModel settings,
        LocalizationManager localization,
        CancellationToken cancellationToken)
    {
        ModelBasicSettingsApplyResult result = await settings.ApplyAsync(cancellationToken);
        string key = result switch
        {
            ModelBasicSettingsApplyResult.Success => "Workspace.ModelBasic.Applied",
            ModelBasicSettingsApplyResult.ValidationFailed => "Workspace.ModelBasic.ValidationFailed",
            _ => "Workspace.ModelBasic.ApplyFailed",
        };
        return new(result == ModelBasicSettingsApplyResult.Success,
            localization.GetString("Workspace.ModelBasic.Title"), localization.GetString(key));
    }

    private sealed record IdleOption(string Label, ModelIdleMotionSelection Selection)
    {
        public override string ToString() => Label;
    }

    private sealed record LostIdleOption(string Label, ModelLostTrackingIdleMotionSelection Selection)
    {
        public override string ToString() => Label;
    }
}
