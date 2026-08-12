using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Motara.App.ViewModels;
using Motara.ModelLibrary;
using Motara.Persistence;

namespace Motara.App.Controls;

public sealed partial class ModelLibraryMenu : UserControl
{
    private readonly TextBlock titleText;
    private readonly TextBlock statusText;
    private readonly ToggleSwitch layoutToggle;
    private readonly Panel modelsPanel;
    private readonly TextBlock detailsHeading;
    private readonly TextBlock selectedName;
    private readonly TextBlock emptyDetailsText;
    private readonly Grid detailsFields;
    private readonly TextBlock compatibilityLabel;
    private readonly TextBlock compatibilityValue;
    private readonly TextBlock formatLabel;
    private readonly TextBlock formatValue;
    private readonly TextBlock versionLabel;
    private readonly TextBlock versionValue;
    private readonly TextBlock roleLabel;
    private readonly TextBlock roleValue;
    private readonly Border configurationReview;
    private readonly TextBlock configurationReviewMessage;
    private readonly Button configurationCancelButton;
    private readonly Button configurationRepairButton;
    private readonly Button configurationConfirmButton;
    private readonly Border mappingReview;
    private readonly TextBlock mappingReviewMessage;
    private readonly Button mappingDeclineButton;
    private readonly Button mappingAcceptButton;
    private readonly Button assignButton;
    private readonly Button basicSettingsButton;
    private readonly Button editButton;
    private readonly Button physicsButton;
    private readonly Button advancedSettingsButton;
    private readonly Button importButton;
    private readonly Button refreshButton;
    private readonly Button organizeButton;
    private readonly Button openFolderButton;
    private MainWindowViewModel? viewModel;
    private ModelSourceMappingReviewViewModel? attachedMappingReview;
    private ModelConfigurationReviewViewModel? attachedConfigurationReview;
    private readonly Dictionary<string, Task<Bitmap?>> thumbnailLoads = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private Task? thumbnailPreload;
    private bool isDetached;

    public ModelLibraryMenu()
    {
        AvaloniaXamlLoader.Load(this);
        titleText = this.FindControl<TextBlock>("TitleText")!;
        statusText = this.FindControl<TextBlock>("StatusText")!;
        layoutToggle = this.FindControl<ToggleSwitch>("LayoutToggle")!;
        modelsPanel = this.FindControl<Panel>("ModelsPanel")!;
        detailsHeading = this.FindControl<TextBlock>("DetailsHeading")!;
        selectedName = this.FindControl<TextBlock>("SelectedName")!;
        emptyDetailsText = this.FindControl<TextBlock>("EmptyDetailsText")!;
        detailsFields = this.FindControl<Grid>("DetailsFields")!;
        compatibilityLabel = this.FindControl<TextBlock>("CompatibilityLabel")!;
        compatibilityValue = this.FindControl<TextBlock>("CompatibilityValue")!;
        formatLabel = this.FindControl<TextBlock>("FormatLabel")!;
        formatValue = this.FindControl<TextBlock>("FormatValue")!;
        versionLabel = this.FindControl<TextBlock>("VersionLabel")!;
        versionValue = this.FindControl<TextBlock>("VersionValue")!;
        roleLabel = this.FindControl<TextBlock>("RoleLabel")!;
        roleValue = this.FindControl<TextBlock>("RoleValue")!;
        configurationReview = this.FindControl<Border>("ConfigurationReview")!;
        configurationReviewMessage = this.FindControl<TextBlock>("ConfigurationReviewMessage")!;
        configurationCancelButton = this.FindControl<Button>("ConfigurationCancelButton")!;
        configurationRepairButton = this.FindControl<Button>("ConfigurationRepairButton")!;
        configurationConfirmButton = this.FindControl<Button>("ConfigurationConfirmButton")!;
        mappingReview = this.FindControl<Border>("MappingReview")!;
        mappingReviewMessage = this.FindControl<TextBlock>("MappingReviewMessage")!;
        mappingDeclineButton = this.FindControl<Button>("MappingDeclineButton")!;
        mappingAcceptButton = this.FindControl<Button>("MappingAcceptButton")!;
        assignButton = this.FindControl<Button>("AssignButton")!;
        basicSettingsButton = this.FindControl<Button>("BasicSettingsButton")!;
        editButton = this.FindControl<Button>("EditButton")!;
        physicsButton = this.FindControl<Button>("PhysicsButton")!;
        advancedSettingsButton = this.FindControl<Button>("AdvancedSettingsButton")!;
        importButton = this.FindControl<Button>("ImportButton")!;
        refreshButton = this.FindControl<Button>("RefreshButton")!;
        organizeButton = this.FindControl<Button>("OrganizeButton")!;
        openFolderButton = this.FindControl<Button>("OpenFolderButton")!;
        layoutToggle.IsCheckedChanged += (_, _) => SetLayout(
            layoutToggle.IsChecked == true
                ? ModelCatalogLayoutMode.Grid
                : ModelCatalogLayoutMode.List);
        AutomationProperties.SetAutomationId(this, "model-library");
        AutomationProperties.SetAutomationId(layoutToggle, "model-library.layout");
        AutomationProperties.SetAutomationId(selectedName, "model-library.details.name");
        AutomationProperties.SetAutomationId(assignButton, "model-library.assign-main");
        AutomationProperties.SetAutomationId(basicSettingsButton, "model-library.basic-settings");
        AutomationProperties.SetAutomationId(editButton, "model-library.edit");
        AutomationProperties.SetAutomationId(physicsButton, "model-library.physics-settings");
        AutomationProperties.SetAutomationId(advancedSettingsButton, "model-library.advanced-settings");
        AutomationProperties.SetAutomationId(importButton, "model-library.import");
        AutomationProperties.SetAutomationId(refreshButton, "model-library.refresh");
        AutomationProperties.SetAutomationId(organizeButton, "model-library.organize");
        AutomationProperties.SetAutomationId(openFolderButton, "model-library.open-folder");
        AutomationProperties.SetAutomationId(
            this.FindControl<Panel>("ModelListActions")!,
            "model-library.list-actions");
        AutomationProperties.SetAutomationId(mappingReview, "model-library.mapping-review");
        AutomationProperties.SetAutomationId(configurationReview, "model-library.configuration-review");
    }

    public void Attach(MainWindowViewModel value)
    {
        ArgumentNullException.ThrowIfNull(value);
        isDetached = false;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.ModelCatalog.PropertyChanged -= OnCatalogPropertyChanged;
        }

        viewModel = value;
        value.PropertyChanged += OnViewModelPropertyChanged;
        value.ModelCatalog.PropertyChanged += OnCatalogPropertyChanged;
        assignButton.Command = value.AssignViewedModelCommand;
        basicSettingsButton.Command = value.OpenModelBasicSettingsCommand;
        editButton.Command = value.OpenModelParameterMappingCommand;
        physicsButton.Command = value.OpenModelPhysicsSettingsCommand;
        advancedSettingsButton.Command = value.OpenModelAdvancedSettingsCommand;
        importButton.Command = value.ModelCatalog.ImportCommand;
        refreshButton.Command = value.ModelCatalog.RefreshCommand;
        organizeButton.Command = value.ModelCatalog.OrganizeViewedModelCommand;
        openFolderButton.Command = value.ModelCatalog.OpenModelsFolderCommand;
        ApplyLocalization();
        Refresh();
    }

    public void Refresh()
    {
        if (viewModel is null)
        {
            return;
        }

        statusText.Text = viewModel.ModelCatalog.StatusText;
        ModelCatalogLayoutMode mode = viewModel.ModelCatalogLayoutMode;
        layoutToggle.IsChecked = mode == ModelCatalogLayoutMode.Grid;
        UpdateLayoutTogglePresentation(mode);
        Task<Bitmap?>[] pendingThumbnails = viewModel.ModelCatalog.Entries
            .Where(static entry => entry.ThumbnailPath is not null)
            .Select(entry => GetOrStartThumbnailLoad(entry.ThumbnailPath!))
            .Where(static load => !load.IsCompleted)
            .ToArray();
        if (pendingThumbnails.Length > 0)
        {
            if (thumbnailPreload is null || thumbnailPreload.IsCompleted)
            {
                thumbnailPreload = RefreshAfterThumbnailPreloadAsync(pendingThumbnails);
            }

            return;
        }

        Panel entries = mode == ModelCatalogLayoutMode.Grid
            ? new WrapPanel { Orientation = Orientation.Horizontal, ItemWidth = 132, ItemHeight = 112 }
            : new StackPanel { Spacing = 4 };
        for (int index = 0; index < viewModel.ModelCatalog.Entries.Length; index++)
        {
            entries.Children.Add(CreateModelButton(viewModel.ModelCatalog.Entries[index], index, mode));
        }
        modelsPanel.Children.Clear();
        modelsPanel.Children.Add(entries);
        UpdateDetails();
    }

    private Button CreateModelButton(
        ModelCatalogViewModel.ModelCatalogEntryViewModel entry,
        int index,
        ModelCatalogLayoutMode mode)
    {
        var content = new StackPanel
        {
            Orientation = mode == ModelCatalogLayoutMode.List
                ? Orientation.Horizontal
                : Orientation.Vertical,
            HorizontalAlignment = mode == ModelCatalogLayoutMode.List
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = mode == ModelCatalogLayoutMode.List ? 10 : 7,
        };
        content.Children.Add(CreateThumbnail(entry, mode));
        content.Children.Add(new TextBlock
        {
            Text = entry.DisplayName,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = mode == ModelCatalogLayoutMode.Grid ? 112 : double.PositiveInfinity,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var button = new Button
        {
            Theme = (ControlTheme)this.FindResource("MenuRowButtonTheme")!,
            Content = content,
            Command = viewModel!.ModelCatalog.SelectModelCommand,
            CommandParameter = entry.Id,
            IsEnabled = entry.IsSelectable,
            MinHeight = mode == ModelCatalogLayoutMode.List ? 44 : 104,
            HorizontalContentAlignment = mode == ModelCatalogLayoutMode.List
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Center,
            FontSize = 14,
        };
        button.Classes.Set("selected", entry.IsViewed);
        button.Classes.Set("viewed", entry.IsViewed);
        button.Classes.Set("main-model", entry.IsCurrentMainModel);
        AutomationProperties.SetAutomationId(button, $"model-library.entry.{index}");
        AutomationProperties.SetName(button, string.Format(
            viewModel.Localization.Culture,
            viewModel.Localization.GetString("Accessibility.ModelEntryFormat"),
            entry.DisplayName));
        return button;
    }

    private void UpdateDetails()
    {
        ModelCatalogViewModel.ModelCatalogEntryViewModel? entry = viewModel!.ModelCatalog.ViewedEntry;
        AttachConfigurationReview(viewModel.ModelConfigurationReview);
        AttachMappingReview(viewModel.ModelMappingReview);
        bool hasEntry = entry is not null;
        selectedName.Text = entry?.DisplayName ?? string.Empty;
        selectedName.IsVisible = hasEntry;
        emptyDetailsText.IsVisible = !hasEntry;
        detailsFields.IsVisible = hasEntry;
        assignButton.IsVisible = hasEntry;
        basicSettingsButton.IsVisible = hasEntry;
        editButton.IsVisible = hasEntry;
        physicsButton.IsVisible = hasEntry;
        advancedSettingsButton.IsVisible = hasEntry;
        organizeButton.IsVisible = hasEntry;
        configurationReview.IsVisible = hasEntry && attachedConfigurationReview?.IsVisible == true;
        mappingReview.IsVisible = hasEntry && attachedMappingReview?.IsReviewVisible == true;
        assignButton.IsEnabled = entry is { IsSelectable: true }
            && (attachedConfigurationReview is null
                || attachedConfigurationReview.State == ModelConfigurationReviewState.Ready)
            && (attachedMappingReview?.CanAssignModel ?? true);
        editButton.CommandParameter = entry?.Id;
        basicSettingsButton.CommandParameter = entry?.Id;
        basicSettingsButton.IsEnabled = entry is { IsSelectable: true }
            && (attachedConfigurationReview is null
                || attachedConfigurationReview.State == ModelConfigurationReviewState.Ready);
        editButton.IsEnabled = entry is { IsSelectable: true }
            && (attachedConfigurationReview is null
                || attachedConfigurationReview.State == ModelConfigurationReviewState.Ready);
        physicsButton.CommandParameter = entry?.Id;
        physicsButton.IsEnabled = entry is { IsSelectable: true }
            && (attachedConfigurationReview is null
                || attachedConfigurationReview.State == ModelConfigurationReviewState.Ready);
        advancedSettingsButton.CommandParameter = entry?.Id;
        advancedSettingsButton.IsEnabled = entry is { IsSelectable: true }
            && (attachedConfigurationReview is null
                || attachedConfigurationReview.State == ModelConfigurationReviewState.Ready);
        organizeButton.IsEnabled = entry is not null;
        if (entry is null)
        {
            return;
        }


        if (attachedConfigurationReview is { IsVisible: true } configuration)
        {
            configurationReviewMessage.Text = viewModel.Localization.GetString(
                configuration.IsBusy
                    ? configuration.State == ModelConfigurationReviewState.NonCanonical
                        ? "ModelLibrary.Configuration.Organizing"
                        : configuration.State == ModelConfigurationReviewState.Invalid
                            ? "ModelLibrary.Configuration.Repairing"
                            : "ModelLibrary.Configuration.Creating"
                    : configuration.State switch
                    {
                        ModelConfigurationReviewState.Missing => "ModelLibrary.Configuration.Missing",
                        ModelConfigurationReviewState.NonCanonical => "ModelLibrary.Configuration.NonCanonical",
                        ModelConfigurationReviewState.Conflict => "ModelLibrary.Configuration.Conflict",
                        ModelConfigurationReviewState.Invalid => configuration.RepairFailed
                            ? "ModelLibrary.Configuration.RepairFailed"
                            : "ModelLibrary.Configuration.Invalid",
                        _ => "ModelLibrary.Configuration.Failed",
                    });
            configurationCancelButton.Command = configuration.CancelCommand;
            configurationCancelButton.IsEnabled = !configuration.IsBusy;
            configurationRepairButton.Command = configuration.RepairCommand;
            configurationRepairButton.IsVisible = configuration.State == ModelConfigurationReviewState.Invalid;
            configurationRepairButton.IsEnabled = configuration.CanRepair;
            configurationConfirmButton.Command = configuration.ConfirmCommand;
            configurationConfirmButton.IsVisible = configuration.CanConfirm || configuration.IsBusy;
            configurationConfirmButton.IsEnabled = configuration.CanConfirm;
            configurationConfirmButton.Content = viewModel.Localization.GetString(
                configuration.State == ModelConfigurationReviewState.NonCanonical
                    ? "Menu.Model.Organize"
                    : "ModelLibrary.Configuration.CreateDefault");
        }

        compatibilityValue.Text = viewModel.Localization.GetString(entry.IsSelectable
            ? "ModelLibrary.Compatible"
            : "ModelLibrary.Unavailable");
        formatValue.Text = string.Format(
            viewModel.Localization.Culture,
            viewModel.Localization.GetString("ModelLibrary.FormatSummary"),
            entry.FormatSummary,
            entry.TextureCount);
        versionValue.Text = FormatVersion(entry.FormatVersion);
        roleValue.Text = viewModel.Localization.GetString(entry.IsCurrentMainModel
            ? "ModelLibrary.CurrentMainModel"
            : "ModelLibrary.NotCurrentMainModel");
        if (attachedMappingReview?.PendingCandidate is { } candidate)
        {
            mappingReviewMessage.Text = string.Format(
                viewModel.Localization.Culture,
                viewModel.Localization.GetString("ModelLibrary.MappingReviewFormat"),
                candidate.AdapterId,
                candidate.ProfileId);
            mappingDeclineButton.Command = attachedMappingReview.DeclineCommand;
            mappingAcceptButton.Command = attachedMappingReview.AcceptCommand;
        }
    }

    private void ApplyLocalization()
    {
        var localization = viewModel!.Localization;
        titleText.Text = localization.GetString("ModelLibrary.Title");
        detailsHeading.Text = localization.GetString("ModelLibrary.CurrentResource");
        emptyDetailsText.Text = localization.GetString("ModelLibrary.NoSelection");
        compatibilityLabel.Text = localization.GetString("ModelLibrary.Compatibility");
        formatLabel.Text = localization.GetString("ModelLibrary.Format");
        versionLabel.Text = localization.GetString("ModelLibrary.Version");
        roleLabel.Text = localization.GetString("ModelLibrary.SceneRole");
        SetMenuActionContent(assignButton, "ModelLibrary.SetMainModel", "Icon.Lucide.User");
        SetMenuActionContent(basicSettingsButton, "ModelLibrary.BasicSettings", "Icon.Lucide.Settings");
        SetMenuActionContent(editButton, "ModelLibrary.Edit", "Icon.Lucide.SlidersHorizontal");
        SetMenuActionContent(physicsButton, "ModelLibrary.PhysicsSettings", "Icon.Lucide.Activity");
        SetMenuActionContent(advancedSettingsButton, "ModelLibrary.AdvancedSettings", "Icon.Lucide.Wrench");
        mappingDeclineButton.Content = localization.GetString("Command.Decline");
        mappingAcceptButton.Content = localization.GetString("Command.Accept");
        configurationCancelButton.Content = localization.GetString("Command.Cancel");
        configurationRepairButton.Content = localization.GetString("Command.Repair");
        importButton.Content = localization.GetString("Menu.Model.Import");
        refreshButton.Content = localization.GetString("Menu.Model.Refresh");
        SetMenuActionContent(organizeButton, "Menu.Model.Organize", "Icon.Lucide.WandSparkles");
        openFolderButton.Content = localization.GetString("Menu.Model.OpenFolder");
        UpdateLayoutTogglePresentation(viewModel.ModelCatalogLayoutMode);
    }

    private void UpdateLayoutTogglePresentation(ModelCatalogLayoutMode mode)
    {
        string label = viewModel!.Localization.GetString(mode == ModelCatalogLayoutMode.Grid
            ? "ModelLibrary.Layout.Grid"
            : "ModelLibrary.Layout.List");
        layoutToggle.Content = label;
        ToolTip.SetTip(layoutToggle, label);
        AutomationProperties.SetName(layoutToggle, label);
    }

    private void SetMenuActionContent(Button button, string labelResourceKey, string iconResourceKey)
    {
        string label = viewModel!.Localization.GetString(labelResourceKey);
        button.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new LucideIcon
                {
                    Width = 20,
                    Height = 20,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Data = (Geometry)this.FindResource(iconResourceKey)!,
                    Stroke = (IBrush)this.FindResource("TextPrimary")!,
                },
                new TextBlock
                {
                    Text = label,
                    FontSize = 14,
                    Foreground = (IBrush)this.FindResource("TextPrimary")!,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };
        AutomationProperties.SetName(button, label);
    }

    private Border CreateThumbnail(
        ModelCatalogViewModel.ModelCatalogEntryViewModel entry,
        ModelCatalogLayoutMode mode)
    {
        double size = mode == ModelCatalogLayoutMode.List ? 28 : 56;
        var fallback = new LucideIcon
        {
            Width = mode == ModelCatalogLayoutMode.List ? 20 : 34,
            Height = mode == ModelCatalogLayoutMode.List ? 20 : 34,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Data = (Geometry)this.FindResource("Icon.Lucide.User")!,
            Stroke = (IBrush)this.FindResource("TextPrimary")!,
        };
        var image = new Image
        {
            Width = size,
            Height = size,
            Stretch = Stretch.UniformToFill,
            IsVisible = false,
        };
        var container = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = new Grid
            {
                Children = { fallback, image },
            },
        };
        if (entry.ThumbnailPath is string thumbnailPath)
        {
            Task<Bitmap?> load = GetOrStartThumbnailLoad(thumbnailPath);
            if (load.IsCompletedSuccessfully && load.Result is Bitmap bitmap)
            {
                image.Source = bitmap;
                image.IsVisible = true;
                fallback.IsVisible = false;
            }
        }

        return container;
    }

    private Task<Bitmap?> GetOrStartThumbnailLoad(string path)
    {
        lock (thumbnailLoads)
        {
            if (!thumbnailLoads.TryGetValue(path, out Task<Bitmap?>? load))
            {
                load = Task.Run(() => DecodeThumbnail(path));
                thumbnailLoads.Add(path, load);
            }

            return load;
        }
    }

    private async Task RefreshAfterThumbnailPreloadAsync(Task<Bitmap?>[] loads)
    {
        await Task.WhenAll(loads).ConfigureAwait(false);
        if (isDetached)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
    }

    private static Bitmap? DecodeThumbnail(string path)
    {
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            return Bitmap.DecodeToWidth(stream, 256);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return null;
        }
    }

    private string FormatVersion(Moc3FormatVersion version) => version switch
    {
        Moc3FormatVersion.Version30 => "MOC3 3.0",
        Moc3FormatVersion.Version33 => "MOC3 3.3",
        Moc3FormatVersion.Version40 => "MOC3 4.0",
        Moc3FormatVersion.Version42 => "MOC3 4.2",
        Moc3FormatVersion.Version50 => "MOC3 5.0",
        Moc3FormatVersion.Version53 => "MOC3 5.3+",
        _ => viewModel!.Localization.GetString("ModelLibrary.Version.Unknown"),
    };

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        isDetached = true;
        Task<Bitmap?>[] loads;
        lock (thumbnailLoads)
        {
            loads = thumbnailLoads.Values.ToArray();
            thumbnailLoads.Clear();
        }

        foreach (Task<Bitmap?> load in loads)
        {
            _ = DisposeThumbnailAsync(load);
        }

        base.OnDetachedFromVisualTree(e);
    }

    private static async Task DisposeThumbnailAsync(Task<Bitmap?> load)
    {
        Bitmap? bitmap = await load.ConfigureAwait(false);
        bitmap?.Dispose();
    }

    private void SetLayout(ModelCatalogLayoutMode mode)
    {
        if (viewModel is not null && viewModel.ModelCatalogLayoutMode != mode)
        {
            viewModel.SetModelCatalogLayoutModeCommand.Execute(mode);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.Localization))
        {
            ApplyLocalization();
            Refresh();
            return;
        }

        if (args.PropertyName is nameof(MainWindowViewModel.ModelCatalogLayoutMode)
            or nameof(MainWindowViewModel.CurrentMainModelId)
            or nameof(MainWindowViewModel.ModelConfigurationReview)
            or nameof(MainWindowViewModel.ModelMappingReview))
        {
            Refresh();
        }
    }

    private void OnCatalogPropertyChanged(object? sender, PropertyChangedEventArgs args) => Refresh();

    private void AttachMappingReview(ModelSourceMappingReviewViewModel? review)
    {
        if (ReferenceEquals(attachedMappingReview, review))
        {
            return;
        }

        if (attachedMappingReview is not null)
        {
            attachedMappingReview.PropertyChanged -= OnMappingReviewPropertyChanged;
        }

        attachedMappingReview = review;
        if (review is not null)
        {
            review.PropertyChanged += OnMappingReviewPropertyChanged;
        }
    }

    private void OnMappingReviewPropertyChanged(object? sender, PropertyChangedEventArgs args) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);

    private void AttachConfigurationReview(ModelConfigurationReviewViewModel? review)
    {
        if (ReferenceEquals(attachedConfigurationReview, review))
        {
            return;
        }

        if (attachedConfigurationReview is not null)
        {
            attachedConfigurationReview.PropertyChanged -= OnConfigurationReviewPropertyChanged;
        }

        attachedConfigurationReview = review;
        if (review is not null)
        {
            review.PropertyChanged += OnConfigurationReviewPropertyChanged;
        }
    }

    private void OnConfigurationReviewPropertyChanged(object? sender, PropertyChangedEventArgs args) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
}
