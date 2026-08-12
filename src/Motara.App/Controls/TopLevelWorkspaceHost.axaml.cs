using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Motara.App.Shell;
using Motara.App.ViewModels;

namespace Motara.App.Controls;

public sealed partial class TopLevelWorkspaceHost : UserControl
{
    private readonly TextBlock workspaceTitle;
    private readonly Button closeButton;
    private readonly Border workspacePanel;
    private readonly ContentControl workspaceHeader;
    private readonly ScrollViewer workspaceScroll;
    private readonly ContentControl workspaceContent;
    private readonly ContentControl workspaceFooter;
    private MainWindowViewModel? viewModel;
    private object? attachedPayload;
    private WorkspaceContentDescriptor? attachedContent;

    public TopLevelWorkspaceHost()
    {
        AvaloniaXamlLoader.Load(this);
        workspaceTitle = this.FindControl<TextBlock>("WorkspaceTitle")!;
        closeButton = this.FindControl<Button>("CloseButton")!;
        workspacePanel = this.FindControl<Border>("WorkspacePanel")!;
        workspaceHeader = this.FindControl<ContentControl>("WorkspaceHeader")!;
        workspaceScroll = this.FindControl<ScrollViewer>("WorkspaceScroll")!;
        workspaceContent = this.FindControl<ContentControl>("WorkspaceContent")!;
        workspaceFooter = this.FindControl<ContentControl>("WorkspaceFooter")!;
        closeButton.Click += async (_, _) => await RequestWorkspaceCloseAsync();
        KeyDown += OnKeyDown;
        AutomationProperties.SetAutomationId(this, "workspace.host");
        AutomationProperties.SetAutomationId(workspaceTitle, "workspace.title");
        AutomationProperties.SetAutomationId(closeButton, "workspace.close");
    }

    public void Attach(MainWindowViewModel value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (viewModel is not null)
        {
            viewModel.TopLevelWorkspace.PropertyChanged -= OnWorkspaceStateChanged;
            viewModel.TopLevelWorkspace.Closed -= RestoreFocus;
        }

        viewModel = value;
        value.TopLevelWorkspace.PropertyChanged += OnWorkspaceStateChanged;
        value.TopLevelWorkspace.Closed += RestoreFocus;
        Refresh();
    }

    private void OnWorkspaceStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(TopLevelWorkspaceState.Content) or nameof(TopLevelWorkspaceState.IsOpen))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        TopLevelWorkspaceContent? content = viewModel?.TopLevelWorkspace.Content;
        if (content is null)
        {
            UnmountContent();
            IsVisible = false;
            return;
        }

        if (ReferenceEquals(attachedPayload, content.Payload))
        {
            IsVisible = true;
            return;
        }

        UnmountContent();
        WorkspaceContentDescriptor? descriptor = WorkspaceContentFactory.Create(
            content.Payload,
            viewModel!.Localization,
            viewModel.TopLevelWorkspace.Close);
        if (descriptor is null)
        {
            IsVisible = false;
            return;
        }

        attachedPayload = content.Payload;
        attachedContent = descriptor;
        workspaceTitle.Text = viewModel.Localization.GetString(descriptor.TitleResourceKey);
        workspacePanel.Width = CalculatePanelWidth(
            descriptor.Width,
            descriptor.MaxWidth,
            Bounds.Width,
            descriptor.ExpandToAvailableWidth);
        workspacePanel.MaxWidth = descriptor.MaxWidth;
        workspacePanel.MaxHeight = CalculatePanelMaxHeight(Bounds.Height);
        workspaceScroll.VerticalScrollBarVisibility = descriptor.ScrollMode == WorkspaceScrollMode.ContentManaged
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
        workspaceContent.VerticalContentAlignment = descriptor.ScrollMode == WorkspaceScrollMode.ContentManaged
            ? Avalonia.Layout.VerticalAlignment.Stretch
            : Avalonia.Layout.VerticalAlignment.Top;
        workspaceContent.Content = descriptor.Content;
        if (descriptor.ScrollMode == WorkspaceScrollMode.HostManaged)
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (ReferenceEquals(attachedContent, descriptor) && IsVisible)
                    {
                        ExtractFixedSections(descriptor.Content);
                    }
                },
                DispatcherPriority.Loaded);
        }
        AutomationProperties.SetName(
            closeButton,
            viewModel.Localization.GetString("Accessibility.CloseWorkspace"));
        IsVisible = true;
        Dispatcher.UIThread.Post(() => (descriptor.InitialFocus ?? closeButton).Focus());
    }

    internal static double CalculatePanelWidth(
        double preferredWidth,
        double maximumWidth,
        double availableWidth,
        bool expandToAvailableWidth = true)
    {
        if (preferredWidth <= 0 || maximumWidth < preferredWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(preferredWidth));
        }

        if (availableWidth <= 0)
        {
            return preferredWidth;
        }

        const double floatingMargin = 64;
        double availablePanelWidth = Math.Max(0, availableWidth - floatingMargin);
        double desiredWidth = expandToAvailableWidth ? maximumWidth : preferredWidth;
        return Math.Min(desiredWidth, availablePanelWidth);
    }

    internal static double CalculatePanelMaxHeight(double availableHeight)
    {
        if (availableHeight <= 0)
        {
            return double.PositiveInfinity;
        }

        const double floatingMargin = 64;
        return Math.Max(0, availableHeight - floatingMargin);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (attachedContent is not null)
        {
            workspacePanel.Width = CalculatePanelWidth(
                attachedContent.Width,
                attachedContent.MaxWidth,
                Bounds.Width,
                attachedContent.ExpandToAvailableWidth);
            workspacePanel.MaxHeight = CalculatePanelMaxHeight(Bounds.Height);
        }
    }

    private void UnmountContent()
    {
        if (attachedContent is not null)
        {
            attachedContent.Detach();
        }

        workspaceContent.Content = null;
        workspaceHeader.Content = null;
        workspaceHeader.IsVisible = false;
        workspaceFooter.Content = null;
        workspaceFooter.IsVisible = false;
        workspaceScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        workspaceContent.VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Top;
        attachedContent = null;
        attachedPayload = null;
    }

    private void ExtractFixedSections(Control content)
    {
        Control[] headers = content.GetVisualDescendants()
            .Where(static control => control.Classes.Contains("workspace-header"))
            .OfType<Control>()
            .ToArray();
        Control[] footers = content.GetVisualDescendants()
            .Where(static control => control.Classes.Contains("workspace-footer"))
            .OfType<Control>()
            .ToArray();
        workspaceHeader.Content = DetachSectionControls(headers);
        workspaceHeader.IsVisible = workspaceHeader.Content is not null;
        workspaceFooter.Content = DetachSectionControls(footers);
        workspaceFooter.IsVisible = workspaceFooter.Content is not null;
    }

    private static Control? DetachSectionControls(Control[] controls)
    {
        if (controls.Length == 0)
        {
            return null;
        }

        foreach (Control control in controls)
        {
            if (control.Parent is Panel panel)
            {
                panel.Children.Remove(control);
            }
            else if (control.Parent is ContentControl contentControl)
            {
                contentControl.Content = null;
            }
        }

        if (controls.Length == 1)
        {
            return controls[0];
        }

        var container = new StackPanel { Spacing = 8 };
        foreach (Control control in controls)
        {
            container.Children.Add(control);
        }

        return container;
    }

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape && viewModel?.TopLevelWorkspace.IsOpen == true)
        {
            _ = RequestWorkspaceCloseAsync();
            args.Handled = true;
        }
    }

    private async Task RequestWorkspaceCloseAsync()
    {
        await (viewModel?.TopLevelWorkspace.RequestCloseAsync(CancellationToken.None)
            ?? Task.FromResult(true));
    }

    private void RestoreFocus(string automationId)
    {
        IsVisible = false;
        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        Control? target = topLevel?.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(control => StringComparer.Ordinal.Equals(
                AutomationProperties.GetAutomationId(control),
                automationId));
        Dispatcher.UIThread.Post(() => target?.Focus());
    }
}
