using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using System.ComponentModel;
using Motara.App.Localization;
using Motara.App.Tracking;
using Motara.App.ViewModels;
using Motara.Core.Formulas;

namespace Motara.App.Controls;

public sealed partial class SourceMappingEditor : UserControl
{
    private readonly ParameterMappingEditorShell editorShell;
    private readonly TextBlock inputsTitle;
    private readonly TextBlock formulaTitle;
    private readonly TextBlock outputsTitle;
    private readonly TextBox inputSearch;
    private readonly ListBox inputList;
    private readonly ListBox outputList;
    private readonly FormulaEditorControl formulaInput;
    private readonly Button formatButton;
    private readonly Button applyButton;
    private readonly Button addParameterButton;
    private readonly TextBlock minimumLabel;
    private readonly TextBlock neutralLabel;
    private readonly TextBlock maximumLabel;
    private readonly TextBlock smoothingLabel;
    private readonly TextBlock parameterIdLabel;
    private readonly TextBlock subtitleLabel;
    private readonly TextBox selectedSubtitleInput;
    private readonly Button deleteButton;
    private readonly TextBlock deleteConfirmationText;
    private readonly Button cancelDeleteButton;
    private readonly Button confirmDeleteButton;
    private readonly Button importButton;
    private readonly Button saveAsButton;
    private readonly Button restoreDefaultButton;
    private readonly ComboBox sourceSelector;
    private readonly Button openConfigurationFolderButton;
    private readonly TextBlock restoreDefaultConfirmationText;
    private readonly Button cancelRestoreDefaultButton;
    private readonly Button confirmRestoreDefaultButton;
    private SourceMappingEditorViewModel? attachedEditor;
    private SourceMappingEditorHostViewModel? attachedHost;
    private LocalizationManager? localization;
    private readonly MenuItem copyInputIdMenuItem;
    private bool updatingSourceSelector;

    public event EventHandler? CloseApproved;

    public SourceMappingEditor()
    {
        AvaloniaXamlLoader.Load(this);
        editorShell = this.FindControl<ParameterMappingEditorShell>("EditorShell")!;
        editorShell.CloseApproved += (_, _) => CloseApproved?.Invoke(this, EventArgs.Empty);
        Loaded += (_, _) => ApplyListScrollBarSettings();
        inputsTitle = this.FindControl<TextBlock>("InputsTitle")!;
        formulaTitle = this.FindControl<TextBlock>("FormulaTitle")!;
        outputsTitle = this.FindControl<TextBlock>("OutputsTitle")!;
        inputSearch = this.FindControl<TextBox>("InputSearch")!;
        inputList = this.FindControl<ListBox>("InputList")!;
        outputList = this.FindControl<ListBox>("OutputList")!;
        outputList.ContainerPrepared += (_, args) =>
            UpdateOutputValidationClass(args.Container as ListBoxItem, args.Index);
        formulaInput = this.FindControl<FormulaEditorControl>("FormulaInput")!;
        formatButton = this.FindControl<Button>("FormatButton")!;
        copyInputIdMenuItem = new MenuItem();
        inputList.ContextMenu = new ContextMenu
        {
            ItemsSource = new[] { copyInputIdMenuItem },
        };
        applyButton = this.FindControl<Button>("InlineApplyButton")!;
        addParameterButton = this.FindControl<Button>("AddParameterButton")!;
        minimumLabel = this.FindControl<TextBlock>("MinimumLabel")!;
        neutralLabel = this.FindControl<TextBlock>("NeutralLabel")!;
        maximumLabel = this.FindControl<TextBlock>("MaximumLabel")!;
        smoothingLabel = this.FindControl<TextBlock>("SmoothingLabel")!;
        parameterIdLabel = this.FindControl<TextBlock>("ParameterIdLabel")!;
        subtitleLabel = this.FindControl<TextBlock>("SubtitleLabel")!;
        selectedSubtitleInput = this.FindControl<TextBox>("SelectedSubtitleInput")!;
        deleteButton = this.FindControl<Button>("DeleteButton")!;
        deleteConfirmationText = this.FindControl<TextBlock>("DeleteConfirmationText")!;
        cancelDeleteButton = this.FindControl<Button>("CancelDeleteButton")!;
        confirmDeleteButton = this.FindControl<Button>("ConfirmDeleteButton")!;
        importButton = this.FindControl<Button>("ImportButton")!;
        saveAsButton = this.FindControl<Button>("SaveAsButton")!;
        restoreDefaultButton = this.FindControl<Button>("RestoreDefaultButton")!;
        sourceSelector = this.FindControl<ComboBox>("SourceSelector")!;
        sourceSelector.SelectionChanged += OnSourceSelectorSelectionChanged;
        openConfigurationFolderButton = this.FindControl<Button>("OpenConfigurationFolderButton")!;
        restoreDefaultConfirmationText = this.FindControl<TextBlock>("RestoreDefaultConfirmationText")!;
        cancelRestoreDefaultButton = this.FindControl<Button>("CancelRestoreDefaultButton")!;
        confirmRestoreDefaultButton = this.FindControl<Button>("ConfirmRestoreDefaultButton")!;
        this.FindControl<Button>("SkipReferenceSyncButton")!.Click += (_, _) =>
            (DataContext as SourceMappingEditorViewModel)?.SkipReferenceSync();
        this.FindControl<Button>("ConfirmReferenceSyncButton")!.Click += async (_, _) =>
        {
            if (DataContext is SourceMappingEditorViewModel editor)
                await editor.ConfirmReferenceSyncAsync(CancellationToken.None);
        };
        this.FindControl<Button>("InlineApplyButton")!.Click += async (_, _) =>
            await editorShell.ApplyAsync();
        this.FindControl<Button>("InlineCancelButton")!.Click += async (_, _) =>
            await editorShell.RequestCloseAsync();
        formatButton.Click += (_, _) =>
            (DataContext as SourceMappingEditorViewModel)?.FormatSelectedFormula();
        addParameterButton.Click += (_, _) =>
            (DataContext as SourceMappingEditorViewModel)?.TryAddGlobalParameter();
        deleteButton.Click += (_, _) =>
            (DataContext as SourceMappingEditorViewModel)?.RequestDeleteSelected();
        cancelDeleteButton.Click += (_, _) =>
            (DataContext as SourceMappingEditorViewModel)?.CancelDeleteSelected();
        confirmDeleteButton.Click += (_, _) =>
            (DataContext as SourceMappingEditorViewModel)?.ConfirmDeleteSelected();
        importButton.Click += async (_, _) => await ImportAsync();
        saveAsButton.Click += async (_, _) => await SaveAsAsync();
        openConfigurationFolderButton.Click += async (_, _) =>
        {
            if (DataContext is SourceMappingEditorViewModel editor)
            {
                _ = await editor.OpenConfigurationFolderAsync(CancellationToken.None);
            }
        };
        restoreDefaultButton.Click += (_, _) =>
            (DataContext as SourceMappingEditorViewModel)?.RequestRestoreDefault();
        cancelRestoreDefaultButton.Click += (_, _) =>
            (DataContext as SourceMappingEditorViewModel)?.CancelRestoreDefault();
        confirmRestoreDefaultButton.Click += async (_, _) =>
        {
            if (DataContext is SourceMappingEditorViewModel editor)
            {
                _ = await editor.ConfirmRestoreDefaultAsync(CancellationToken.None);
            }
        };
        inputList.DoubleTapped += (_, _) =>
        {
            if (inputList.SelectedItem is SourceMappingInputItem input)
            {
                InsertInput(input.Id);
            }
        };
        inputList.KeyDown += async (_, args) =>
        {
            if (args.Key == Key.C && args.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                args.Handled = await CopySelectedInputIdAsync();
            }
        };
        copyInputIdMenuItem.Click += async (_, _) => _ = await CopySelectedInputIdAsync();
        AutomationProperties.SetAutomationId(this, "workspace.source-mapping");
        AutomationProperties.SetAutomationId(inputSearch, "workspace.source-mapping.search");
        AutomationProperties.SetAutomationId(inputList, "workspace.source-mapping.inputs");
        AutomationProperties.SetAutomationId(formulaInput, "workspace.source-mapping.formula");
        AutomationProperties.SetAutomationId(formatButton, "workspace.source-mapping.format");
        AutomationProperties.SetAutomationId(applyButton, "workspace.source-mapping.apply");
        AutomationProperties.SetAutomationId(
            this.FindControl<Button>("InlineCancelButton")!,
            "workspace.source-mapping.cancel");
        AutomationProperties.SetAutomationId(
            editorShell.FindControl<Button>("AcknowledgeApplyResultButton")!,
            "workspace.source-mapping.apply-result.acknowledge");
        AutomationProperties.SetAutomationId(addParameterButton, "workspace.source-mapping.add");
        AutomationProperties.SetAutomationId(deleteButton, "workspace.source-mapping.delete");
        AutomationProperties.SetAutomationId(cancelDeleteButton, "workspace.source-mapping.delete.cancel");
        AutomationProperties.SetAutomationId(confirmDeleteButton, "workspace.source-mapping.delete.confirm");
        AutomationProperties.SetAutomationId(editorShell.FindControl<Button>("CancelCloseButton")!, "workspace.source-mapping.close.cancel");
        AutomationProperties.SetAutomationId(editorShell.FindControl<Button>("DiscardCloseButton")!, "workspace.source-mapping.close.discard");
        AutomationProperties.SetAutomationId(editorShell.FindControl<Button>("SaveAndCloseButton")!, "workspace.source-mapping.close.apply");
        AutomationProperties.SetAutomationId(importButton, "workspace.source-mapping.import");
        AutomationProperties.SetAutomationId(saveAsButton, "workspace.source-mapping.save-as");
        AutomationProperties.SetAutomationId(restoreDefaultButton, "workspace.source-mapping.restore-default");
        AutomationProperties.SetAutomationId(
            cancelRestoreDefaultButton,
            "workspace.source-mapping.restore-default.cancel");
        AutomationProperties.SetAutomationId(
            confirmRestoreDefaultButton,
            "workspace.source-mapping.restore-default.confirm");
        AutomationProperties.SetAutomationId(sourceSelector, "workspace.source-mapping.source");
        AutomationProperties.SetAutomationId(
            openConfigurationFolderButton,
            "workspace.source-mapping.open-configuration-folder");
    }

    public void Attach(SourceMappingEditorViewModel editor, LocalizationManager localization)
    {
        if (attachedHost is not null)
        {
            attachedHost.PropertyChanged -= OnHostPropertyChanged;
            attachedHost = null;
        }

        if (attachedEditor is not null)
        {
            attachedEditor.PropertyChanged -= OnEditorPropertyChanged;
        }

        attachedEditor = editor;
        this.localization = localization;
        IsVisible = true;
        attachedEditor.PropertyChanged += OnEditorPropertyChanged;
        DataContext = editor;
        editorShell.Attach(CreateSession(editor, localization), localization);
        editor.SetInputLocalizer(localization.GetString);
        editor.SetParameterLocalizer(localization.GetString);
        ApplyListScrollBarSettings();
        formulaInput.SetCompletions(LocalizeCompletions(editor.Completions, localization));
        UpdateFormulaState(editor.EditorState, localization);
        UpdateOutputValidationClasses();
        inputsTitle.Text = localization.GetString("Workspace.SourceMapping.Inputs");
        formulaTitle.Text = localization.GetString("Workspace.SourceMapping.Formula");
        outputsTitle.Text = localization.GetString("Workspace.SourceMapping.Outputs");
        inputSearch.PlaceholderText = localization.GetString("Workspace.SourceMapping.Search");
        copyInputIdMenuItem.Header = localization.GetString("Workspace.SourceMapping.CopyId");
        AutomationProperties.SetName(inputList, localization.GetString("Accessibility.SourceMapping.Inputs"));
        AutomationProperties.SetName(formulaInput, localization.GetString("Accessibility.SourceMapping.Formula"));
        formatButton.Content = localization.GetString("Command.Format");
        applyButton.Content = localization.GetString("Command.Apply");
        this.FindControl<Button>("InlineCancelButton")!.Content =
            localization.GetString("Command.Cancel");
        addParameterButton.Content = localization.GetString("Command.Add");
        minimumLabel.Text = localization.GetString("Workspace.SourceMapping.Minimum");
        neutralLabel.Text = localization.GetString("Workspace.SourceMapping.Neutral");
        maximumLabel.Text = localization.GetString("Workspace.SourceMapping.Maximum");
        smoothingLabel.Text = localization.GetString("Workspace.SourceMapping.Smoothing");
        parameterIdLabel.Text = localization.GetString("Workspace.SourceMapping.ParameterId");
        subtitleLabel.Text = localization.GetString("Workspace.SourceMapping.Subtitle");
        selectedSubtitleInput.PlaceholderText = localization.GetString(
            "Workspace.SourceMapping.SubtitlePlaceholder");
        deleteButton.Content = localization.GetString("Command.Delete");
        deleteConfirmationText.Text = localization.GetString("Workspace.SourceMapping.DeleteConfirmation");
        cancelDeleteButton.Content = localization.GetString("Command.Cancel");
        confirmDeleteButton.Content = localization.GetString("Command.Delete");
        importButton.Content = localization.GetString("Workspace.SourceMapping.Import");
        saveAsButton.Content = localization.GetString("Workspace.SourceMapping.SaveAs");
        restoreDefaultButton.Content = localization.GetString("Workspace.SourceMapping.RestoreDefault");
        restoreDefaultConfirmationText.Text = localization.GetString(
            "Workspace.SourceMapping.RestoreDefaultConfirmation");
        cancelRestoreDefaultButton.Content = localization.GetString("Command.Cancel");
        confirmRestoreDefaultButton.Content = localization.GetString(
            "Workspace.SourceMapping.RestoreDefault");
        openConfigurationFolderButton.Content = localization.GetString(
            "Workspace.SourceMapping.OpenConfigurationFolder");
        openConfigurationFolderButton.IsEnabled = editor.CanOpenConfigurationFolder;
        sourceSelector.Items.Clear();
        sourceSelector.Items.Add(new ComboBoxItem
        {
            Content = localization.GetString("Menu.Tracking.Source.IFacialMocap"),
            IsEnabled = true,
        });
        foreach (string sourceKey in new[]
        {
            "Menu.Tracking.Source.FaceMotion3D",
            "Menu.Tracking.Source.Maxine",
            "Menu.Tracking.Source.MediaPipe",
            "Menu.Tracking.Source.OpenSeeFace",
        })
        {
            sourceSelector.Items.Add(new ComboBoxItem
            {
                Content = string.Format(
                    localization.Culture,
                    localization.GetString("Workspace.SourceMapping.SourceUnavailableFormat"),
                    localization.GetString(sourceKey)),
                IsEnabled = false,
            });
        }

        sourceSelector.SelectedIndex = 0;
        AutomationProperties.SetName(
            sourceSelector,
            localization.GetString("Accessibility.SourceMapping.Source"));
    }

    internal void Attach(SourceMappingEditorHostViewModel host, LocalizationManager localization)
    {
        ArgumentNullException.ThrowIfNull(host);
        Attach(host.CurrentEditor, localization);
        attachedHost = host;
        attachedHost.PropertyChanged += OnHostPropertyChanged;
        PopulateSourceSelector(host, localization);
        AttachHostSession(host, localization);
    }

    public void Detach()
    {
        if (attachedHost is not null)
        {
            attachedHost.PropertyChanged -= OnHostPropertyChanged;
            attachedHost = null;
        }

        if (attachedEditor is not null)
        {
            attachedEditor.PropertyChanged -= OnEditorPropertyChanged;
            attachedEditor = null;
        }

        localization = null;
        editorShell.Detach();
        DataContext = null;
    }

    private async void OnSourceSelectorSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (updatingSourceSelector
            || attachedHost is null
            || sourceSelector.SelectedItem is not ComboBoxItem { Tag: string adapterId })
        {
            return;
        }

        try
        {
            await attachedHost.SelectAdapterAsync(adapterId, CancellationToken.None);
        }
        catch (Exception)
        {
            PopulateSourceSelector(attachedHost, localization!);
        }
    }

    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(SourceMappingEditorHostViewModel.CurrentEditor)
            || attachedHost is null
            || localization is null)
        {
            return;
        }

        SourceMappingEditorHostViewModel host = attachedHost;
        Attach(host.CurrentEditor, localization);
        attachedHost = host;
        attachedHost.PropertyChanged += OnHostPropertyChanged;
        PopulateSourceSelector(host, localization);
        AttachHostSession(host, localization);
    }

    private void PopulateSourceSelector(
        SourceMappingEditorHostViewModel host,
        LocalizationManager activeLocalization)
    {
        updatingSourceSelector = true;
        try
        {
            sourceSelector.Items.Clear();
            foreach (SourceMappingEditorAdapterItem adapter in host.AvailableAdapters)
            {
                sourceSelector.Items.Add(new ComboBoxItem
                {
                    Content = activeLocalization.GetString(adapter.DisplayNameResourceKey),
                    Tag = adapter.AdapterId,
                });
            }

            int selectedIndex = -1;
            for (int index = 0; index < host.AvailableAdapters.Length; index++)
            {
                if (StringComparer.Ordinal.Equals(
                    host.AvailableAdapters[index].AdapterId,
                    host.SelectedAdapterId))
                {
                    selectedIndex = index;
                    break;
                }
            }

            sourceSelector.SelectedIndex = selectedIndex;
        }
        finally
        {
            updatingSourceSelector = false;
        }
    }

    private void AttachHostSession(
        SourceMappingEditorHostViewModel host,
        LocalizationManager activeLocalization)
    {
        editorShell.Attach(
            new ParameterMappingEditorSession(
                host,
                () => host.IsCloseConfirmationVisible,
                host.RequestCloseAsync,
                async cancellationToken =>
                {
                    bool success = await host.ApplyAllAsync(cancellationToken);
                    return new ParameterMappingEditorFeedback(
                        success,
                        activeLocalization.GetString(success
                            ? "Workspace.SourceMapping.ApplyResult.SuccessTitle"
                            : "Workspace.SourceMapping.ApplyResult.FailureTitle"),
                        activeLocalization.GetString(success
                            ? "Workspace.SourceMapping.ApplyResult.Success"
                            : "Workspace.SourceMapping.ApplyResult.UnexpectedFailure"));
                },
                host.CancelClose,
                host.DiscardAndConfirmClose,
                "Workspace.SourceMapping.UnsavedPrompt"),
            activeLocalization);
    }

    public void InsertInput(string inputId) => formulaInput.InsertIdentifier(inputId);

    private void ApplyListScrollBarSettings()
    {
        foreach (ListBox list in new[] { inputList, outputList })
        {
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            list.ApplyTemplate();
            foreach (ScrollViewer scroll in list.GetVisualDescendants().OfType<ScrollViewer>())
            {
                ScrollViewer.SetHorizontalScrollBarVisibility(
                    scroll,
                    ScrollBarVisibility.Disabled);
                ScrollViewer.SetVerticalScrollBarVisibility(
                    scroll,
                    ScrollBarVisibility.Auto);
                scroll.ApplyTemplate();
                foreach (ScrollBar scrollBar in scroll
                    .GetVisualDescendants()
                    .OfType<ScrollBar>())
                {
                    if (!scrollBar.Classes.Contains("motara-scrollbar"))
                    {
                        scrollBar.Classes.Add("motara-scrollbar");
                    }
                }
            }
        }
    }

    public async Task<bool> CopySelectedInputIdAsync()
    {
        if (inputList.SelectedItem is not SourceMappingInputItem input
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return false;
        }

        await clipboard.SetTextAsync(input.Id);
        return true;
    }

    private async Task ImportAsync()
    {
        if (DataContext is not SourceMappingEditorViewModel editor
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storageProvider)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await storageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = importButton.Content?.ToString(),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Motara mapping")
                    {
                        Patterns = ["*.mapping.motara.json", "*.json"],
                    },
                ],
            });
        string? path = files.Count == 0 ? null : files[0].TryGetLocalPath();
        if (path is not null)
        {
            _ = await editor.ImportAsDraftAsync(path, CancellationToken.None);
        }
    }

    private async Task SaveAsAsync()
    {
        if (DataContext is not SourceMappingEditorViewModel editor
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storageProvider)
        {
            return;
        }

        IStorageFile? file = await storageProvider.SaveFilePickerAsync(
            CreateSaveFilePickerOptions(
                editor.Document.AdapterId,
                saveAsButton.Content?.ToString()));
        string? path = file?.TryGetLocalPath();
        if (path is not null)
        {
            _ = await editor.SaveAsAsync(
                Path.GetFileName(path),
                CancellationToken.None);
        }
    }

    internal static FilePickerSaveOptions CreateSaveFilePickerOptions(
        string adapterId,
        string? title)
    {
        string suffix = SourceMappingPaths.GetFileSuffix(adapterId);
        return new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = $"mapping-profile{suffix}",
            DefaultExtension = suffix.TrimStart('.'),
            FileTypeChoices =
            [
                new FilePickerFileType($"{adapterId} mapping")
                {
                    Patterns = [$"*{suffix}"],
                },
            ],
        };
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (attachedEditor is null)
        {
            return;
        }

        if (e.PropertyName == nameof(SourceMappingEditorViewModel.Completions))
        {
            formulaInput.SetCompletions(LocalizeCompletions(
                attachedEditor.Completions,
                localization!));
        }
        else if (e.PropertyName == nameof(SourceMappingEditorViewModel.EditorState))
        {
            formulaInput.SetDiagnostic(LocalizeDiagnostic(
                attachedEditor.EditorState.Diagnostic,
                localization!));
            formulaInput.SetPreview(FormatPreview(attachedEditor.EditorState));
        }
        else if (e.PropertyName == nameof(SourceMappingEditorViewModel.ValidationReport))
        {
            UpdateOutputValidationClasses();
        }
        else if (e.PropertyName == nameof(SourceMappingEditorViewModel.Outputs))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateOutputValidationClasses);
        }
    }

    private static ParameterMappingEditorSession CreateSession(
        SourceMappingEditorViewModel editor,
        LocalizationManager localization) => new(
            editor,
            () => editor.IsCloseConfirmationVisible,
            editor.RequestCloseAsync,
            cancellationToken => ApplyFromShellAsync(editor, localization, cancellationToken),
            editor.CancelClose,
            () => _ = editor.DiscardAndConfirmClose(),
            "Workspace.SourceMapping.UnsavedPrompt",
            editor.AcknowledgeApplyResult);

    private static async Task<ParameterMappingEditorFeedback> ApplyFromShellAsync(
        SourceMappingEditorViewModel editor,
        LocalizationManager localization,
        CancellationToken cancellationToken)
    {
        _ = await editor.ApplyAsync(cancellationToken);
        SourceMappingApplyResult result = editor.ApplyResult
            ?? SourceMappingApplyResult.UnexpectedFailure;
        string title = localization.GetString(result == SourceMappingApplyResult.Success
            ? "Workspace.SourceMapping.ApplyResult.SuccessTitle"
            : "Workspace.SourceMapping.ApplyResult.FailureTitle");
        string message;
        if (result == SourceMappingApplyResult.ValidationFailed
            && !editor.ApplyValidationErrors.IsEmpty)
        {
            message = string.Join(
                Environment.NewLine,
                editor.ApplyValidationErrors.Select(error =>
                    $"{error.ParameterId}: {localization.GetString(
                        $"Workspace.SourceMapping.Error.{error.Diagnostic.Code}")}"));
        }
        else
        {
            message = localization.GetString(
                $"Workspace.SourceMapping.ApplyResult.{result}");
        }

        return new ParameterMappingEditorFeedback(
            result == SourceMappingApplyResult.Success,
            title,
            message);
    }

    private void UpdateOutputValidationClasses()
    {
        if (attachedEditor is null)
        {
            return;
        }

        foreach (Control container in outputList.GetRealizedContainers())
        {
            if (container is ListBoxItem item)
            {
                int index = item.DataContext is SourceMappingOutputItem output
                    ? attachedEditor.OutputItems.IndexOf(output)
                    : -1;
                UpdateOutputValidationClass(item, index);
            }
        }
    }

    internal void UpdateOutputValidationClass(ListBoxItem? item, int index)
    {
        if (item is null || attachedEditor is null)
        {
            return;
        }

        bool hasError = index >= 0
            && index < attachedEditor.ValidationReport.OutputStates.Length
            && attachedEditor.ValidationReport.OutputStates[index].Diagnostic is not null;
        item.Classes.Set("validation-error", hasError);
    }

    private void UpdateFormulaState(FormulaEditorState state, LocalizationManager localization)
    {
        formulaInput.SetDiagnostic(LocalizeDiagnostic(state.Diagnostic, localization));
        formulaInput.SetPreview(state.PreviewValue?.ToString("G", localization.Culture)
            ?? localization.GetString("Workspace.SourceMapping.PreviewUnavailable"));
    }

    private string? FormatPreview(FormulaEditorState state) => state.PreviewValue?.ToString(
            "G",
            localization?.Culture ?? System.Globalization.CultureInfo.InvariantCulture)
        ?? localization?.GetString("Workspace.SourceMapping.PreviewUnavailable");

    private static IEnumerable<FormulaCompletionItem> LocalizeCompletions(
        IEnumerable<FormulaCompletionItem> completions,
        LocalizationManager localization) => completions.Select(item => item with
    {
        Category = localization.GetString(
            $"Workspace.SourceMapping.Completion.{item.Kind}"),
        Description = item.Description,
    });

    private static Motara.Core.Formulas.SourceFormulaDiagnostic? LocalizeDiagnostic(
        Motara.Core.Formulas.SourceFormulaDiagnostic? diagnostic,
        LocalizationManager localization) => diagnostic is null
            ? null
            : diagnostic with
            {
                Message = localization.GetString(
                    $"Workspace.SourceMapping.Error.{diagnostic.Code}"),
            };
}
