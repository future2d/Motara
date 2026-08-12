using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Motara.App.Localization;

namespace Motara.App.Controls;

internal sealed partial class ParameterMappingEditorShell : UserControl
{
    internal static readonly StyledProperty<object?> HeaderContentProperty =
        AvaloniaProperty.Register<ParameterMappingEditorShell, object?>(nameof(HeaderContent));
    internal static readonly StyledProperty<object?> EditorContentProperty =
        AvaloniaProperty.Register<ParameterMappingEditorShell, object?>(nameof(EditorContent));
    internal static readonly StyledProperty<object?> FooterContentProperty =
        AvaloniaProperty.Register<ParameterMappingEditorShell, object?>(nameof(FooterContent));
    internal static readonly StyledProperty<object?> OverlayContentProperty =
        AvaloniaProperty.Register<ParameterMappingEditorShell, object?>(nameof(OverlayContent));
    internal static readonly StyledProperty<bool> IsActionBarVisibleProperty =
        AvaloniaProperty.Register<ParameterMappingEditorShell, bool>(nameof(IsActionBarVisible), true);

    private readonly ContentControl headerPresenter;
    private readonly ContentControl editorPresenter;
    private readonly ContentControl footerPresenter;
    private readonly ContentControl overlayPresenter;
    private readonly Border closeConfirmationOverlay;
    private readonly Border applyResultOverlay;
    private readonly Grid actionBar;
    private ParameterMappingEditorSession? session;

    static ParameterMappingEditorShell()
    {
        HeaderContentProperty.Changed.AddClassHandler<ParameterMappingEditorShell>(
            static (shell, args) => SetPresenterContent(shell.headerPresenter, args.NewValue));
        EditorContentProperty.Changed.AddClassHandler<ParameterMappingEditorShell>(
            static (shell, args) => SetPresenterContent(shell.editorPresenter, args.NewValue));
        FooterContentProperty.Changed.AddClassHandler<ParameterMappingEditorShell>(
            static (shell, args) => SetPresenterContent(shell.footerPresenter, args.NewValue));
        OverlayContentProperty.Changed.AddClassHandler<ParameterMappingEditorShell>(
            static (shell, args) => SetPresenterContent(shell.overlayPresenter, args.NewValue));
        IsActionBarVisibleProperty.Changed.AddClassHandler<ParameterMappingEditorShell>(
            static (shell, args) => shell.actionBar.IsVisible = args.NewValue is true);
    }

    public ParameterMappingEditorShell()
    {
        AvaloniaXamlLoader.Load(this);
        headerPresenter = this.FindControl<ContentControl>("HeaderPresenter")!;
        editorPresenter = this.FindControl<ContentControl>("EditorPresenter")!;
        footerPresenter = this.FindControl<ContentControl>("FooterPresenter")!;
        overlayPresenter = this.FindControl<ContentControl>("OverlayPresenter")!;
        closeConfirmationOverlay = this.FindControl<Border>("CloseConfirmationOverlay")!;
        applyResultOverlay = this.FindControl<Border>("ApplyResultOverlay")!;
        actionBar = this.FindControl<Grid>("ActionBar")!;
        this.FindControl<Button>("ApplyButton")!.Click += async (_, _) => await ApplyAsync();
        this.FindControl<Button>("CancelButton")!.Click += async (_, _) => await RequestCloseAsync();
        this.FindControl<Button>("CancelCloseButton")!.Click += (_, _) => session?.CancelClose();
        this.FindControl<Button>("DiscardCloseButton")!.Click += (_, _) => DiscardAndClose();
        this.FindControl<Button>("SaveAndCloseButton")!.Click += async (_, _) => await SaveAndCloseAsync();
        this.FindControl<Button>("AcknowledgeApplyResultButton")!.Click += (_, _) =>
        {
            session?.AcknowledgeApplyResult();
            applyResultOverlay.IsVisible = false;
        };
        AutomationProperties.SetAutomationId(this.FindControl<Button>("ApplyButton")!, "workspace.parameter-mapping.apply");
        AutomationProperties.SetAutomationId(this.FindControl<Button>("CancelButton")!, "workspace.parameter-mapping.cancel");
        AutomationProperties.SetAutomationId(this.FindControl<Button>("CancelCloseButton")!, "workspace.parameter-mapping.close.cancel");
        AutomationProperties.SetAutomationId(this.FindControl<Button>("DiscardCloseButton")!, "workspace.parameter-mapping.close.discard");
        AutomationProperties.SetAutomationId(this.FindControl<Button>("SaveAndCloseButton")!, "workspace.parameter-mapping.close.save");
        AutomationProperties.SetAutomationId(this.FindControl<Button>("AcknowledgeApplyResultButton")!, "workspace.parameter-mapping.apply-result.acknowledge");
    }

    internal event EventHandler? CloseApproved;

    internal object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    internal object? EditorContent
    {
        get => GetValue(EditorContentProperty);
        set => SetValue(EditorContentProperty, value);
    }

    internal object? FooterContent
    {
        get => GetValue(FooterContentProperty);
        set => SetValue(FooterContentProperty, value);
    }

    internal object? OverlayContent
    {
        get => GetValue(OverlayContentProperty);
        set => SetValue(OverlayContentProperty, value);
    }

    internal bool IsActionBarVisible
    {
        get => GetValue(IsActionBarVisibleProperty);
        set => SetValue(IsActionBarVisibleProperty, value);
    }

    internal void Attach(ParameterMappingEditorSession value, LocalizationManager localization)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(localization);
        Detach();
        session = value;
        value.State.PropertyChanged += OnSessionStateChanged;
        this.FindControl<Button>("ApplyButton")!.Content = localization.GetString("Command.Apply");
        this.FindControl<Button>("CancelButton")!.Content = localization.GetString("Command.Cancel");
        this.FindControl<Button>("CancelCloseButton")!.Content = localization.GetString("Command.Cancel");
        this.FindControl<Button>("DiscardCloseButton")!.Content = localization.GetString("Command.Discard");
        this.FindControl<Button>("SaveAndCloseButton")!.Content = localization.GetString("Command.SaveAndClose");
        this.FindControl<Button>("AcknowledgeApplyResultButton")!.Content = localization.GetString("Command.OK");
        this.FindControl<TextBlock>("CloseConfirmationText")!.Text =
            localization.GetString(value.UnsavedChangesResourceKey);
        RefreshCloseConfirmation();
    }

    internal void Detach()
    {
        if (session is not null)
        {
            session.State.PropertyChanged -= OnSessionStateChanged;
        }

        session = null;
        closeConfirmationOverlay.IsVisible = false;
        applyResultOverlay.IsVisible = false;
    }

    internal async Task ApplyAsync()
    {
        if (session is null) return;
        ShowFeedback(await session.ApplyAsync(CancellationToken.None));
    }

    internal async Task RequestCloseAsync()
    {
        if (session is null) return;
        if (await session.RequestCloseAsync(CancellationToken.None))
        {
            CloseApproved?.Invoke(this, EventArgs.Empty);
            return;
        }

        RefreshCloseConfirmation();
    }

    private void DiscardAndClose()
    {
        if (session is null) return;
        session.DiscardChanges();
        CloseApproved?.Invoke(this, EventArgs.Empty);
    }

    internal async Task SaveAndCloseAsync()
    {
        if (session is null) return;
        ParameterMappingEditorFeedback feedback = await session.ApplyAsync(CancellationToken.None);
        if (feedback.IsSuccess)
        {
            session.CancelClose();
            CloseApproved?.Invoke(this, EventArgs.Empty);
            return;
        }

        ShowFeedback(feedback);
    }

    private void ShowFeedback(ParameterMappingEditorFeedback feedback)
    {
        this.FindControl<TextBlock>("ApplyResultTitle")!.Text = feedback.Title;
        this.FindControl<TextBlock>("ApplyResultMessage")!.Text = feedback.Message;
        applyResultOverlay.IsVisible = true;
    }

    private void OnSessionStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshCloseConfirmation();
        }
        else
        {
            Dispatcher.UIThread.Post(RefreshCloseConfirmation);
        }
    }

    private void RefreshCloseConfirmation()
    {
        closeConfirmationOverlay.IsVisible = session?.IsCloseConfirmationVisible() == true;
    }

    private static void SetPresenterContent(ContentControl presenter, object? content)
    {
        presenter.Content = content;
        presenter.IsVisible = content is not null;
    }
}
