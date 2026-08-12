using System.ComponentModel;
using Avalonia.Automation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.VisualTree;
using Motara.App.Collaboration;
using Motara.App.ViewModels;
using Motara.Collaboration.Identity;
using Motara.Collaboration.Sessions;

namespace Motara.App.Controls;

public sealed partial class CollaborationMenu : UserControl
{
    private readonly TextBlock titleText;
    private readonly TextBlock identityHeading;
    private readonly TextBlock identityValue;
    private readonly TextBlock identityHint;
    private readonly TextBlock sessionHeading;
    private readonly TextBlock sessionStatus;
    private readonly TextBlock sessionMembers;
    private readonly TextBlock sessionTransfer;
    private readonly Border identityCard;
    private readonly TextBlock invitationsHeading;
    private readonly TextBlock contactsHeading;
    private readonly TextBlock emptyContactsText;
    private readonly StackPanel contactsPanel;
    private readonly Button generateInviteButton;
    private readonly Button acceptFriendInviteButton;
    private readonly Button generateSessionInviteButton;
    private readonly Button acceptSessionInviteButton;
    private readonly Button leaveSessionButton;
    private readonly MenuActionVisual generateInviteVisual;
    private readonly MenuActionVisual acceptFriendInviteVisual;
    private readonly MenuActionVisual generateSessionInviteVisual;
    private readonly MenuActionVisual acceptSessionInviteVisual;
    private readonly MenuActionVisual leaveSessionVisual;
    private readonly Grid contentGrid;
    private readonly Border contentDivider;
    private readonly ScrollViewer identityScroll;
    private readonly ScrollViewer contactsScroll;
    private MainWindowViewModel? shell;
    private CollaborationWorkspaceViewModel? workspace;

    public CollaborationMenu()
    {
        AvaloniaXamlLoader.Load(this);
        titleText = this.FindControl<TextBlock>("TitleText")!;
        identityHeading = this.FindControl<TextBlock>("IdentityHeading")!;
        identityValue = this.FindControl<TextBlock>("IdentityValue")!;
        identityHint = this.FindControl<TextBlock>("IdentityHint")!;
        sessionHeading = this.FindControl<TextBlock>("SessionHeading")!;
        sessionStatus = this.FindControl<TextBlock>("SessionStatus")!;
        sessionMembers = this.FindControl<TextBlock>("SessionMembers")!;
        sessionTransfer = this.FindControl<TextBlock>("SessionTransfer")!;
        identityCard = this.FindControl<Border>("IdentityCard")!;
        invitationsHeading = this.FindControl<TextBlock>("InvitationsHeading")!;
        contactsHeading = this.FindControl<TextBlock>("ContactsHeading")!;
        emptyContactsText = this.FindControl<TextBlock>("EmptyContactsText")!;
        contactsPanel = this.FindControl<StackPanel>("ContactsPanel")!;
        generateInviteButton = this.FindControl<Button>("GenerateInviteButton")!;
        acceptFriendInviteButton = this.FindControl<Button>("AcceptFriendInviteButton")!;
        generateSessionInviteButton = this.FindControl<Button>("GenerateSessionInviteButton")!;
        acceptSessionInviteButton = this.FindControl<Button>("AcceptSessionInviteButton")!;
        leaveSessionButton = this.FindControl<Button>("LeaveSessionButton")!;
        generateInviteVisual = ConfigureMenuActionButton(
            generateInviteButton,
            "Icon.Lucide.User");
        acceptFriendInviteVisual = ConfigureMenuActionButton(
            acceptFriendInviteButton,
            "Icon.Lucide.Plus");
        generateSessionInviteVisual = ConfigureMenuActionButton(
            generateSessionInviteButton,
            "Icon.Lucide.Users");
        acceptSessionInviteVisual = ConfigureMenuActionButton(
            acceptSessionInviteButton,
            "Icon.Lucide.Plus");
        leaveSessionVisual = ConfigureMenuActionButton(leaveSessionButton, "Icon.Lucide.X");
        contentGrid = this.FindControl<Grid>("ContentGrid")!;
        contentDivider = this.FindControl<Border>("ContentDivider")!;
        identityScroll = this.FindControl<ScrollViewer>("IdentityScroll")!;
        contactsScroll = this.FindControl<ScrollViewer>("ContactsScroll")!;
        SizeChanged += (_, _) => UpdateResponsiveLayout(Bounds.Width);
        generateInviteButton.Click += (_, _) => GenerateInviteRequested?.Invoke(this, EventArgs.Empty);
        acceptFriendInviteButton.Click += (_, _) => AcceptFriendInviteRequested?.Invoke(this, EventArgs.Empty);
        generateSessionInviteButton.Click += (_, _) => GenerateSessionInviteRequested?.Invoke(this, EventArgs.Empty);
        acceptSessionInviteButton.Click += (_, _) => AcceptSessionInviteRequested?.Invoke(this, EventArgs.Empty);
        leaveSessionButton.Click += (_, _) => LeaveSessionRequested?.Invoke(this, EventArgs.Empty);
        identityCard.PointerPressed += (_, args) =>
        {
            if (args.ClickCount == 2 && identityCard.IsEnabled)
            {
                LocalProfileRequested?.Invoke(this, EventArgs.Empty);
                args.Handled = true;
            }
        };
        identityCard.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter && identityCard.IsEnabled)
            {
                LocalProfileRequested?.Invoke(this, EventArgs.Empty);
                args.Handled = true;
            }
        };
    }

    internal event EventHandler? GenerateInviteRequested;
    internal event EventHandler? AcceptFriendInviteRequested;
    internal event EventHandler? GenerateSessionInviteRequested;
    internal event EventHandler? AcceptSessionInviteRequested;
    internal event EventHandler? LocalProfileRequested;
    internal event EventHandler? LeaveSessionRequested;
    internal event EventHandler<DeviceId>? ContactRequested;

    internal void Attach(MainWindowViewModel shellViewModel, CollaborationWorkspaceViewModel value)
    {
        if (shell is not null)
        {
            shell.PropertyChanged -= OnShellPropertyChanged;
        }

        if (workspace is not null)
        {
            workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        }

        shell = shellViewModel ?? throw new ArgumentNullException(nameof(shellViewModel));
        workspace = value ?? throw new ArgumentNullException(nameof(value));
        shell.PropertyChanged += OnShellPropertyChanged;
        workspace.PropertyChanged += OnWorkspacePropertyChanged;
        ApplyLocalization();
        Refresh();
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (workspace is null)
        {
            return;
        }

        await workspace.InitializeAsync(cancellationToken);
        Refresh();
    }

    internal void Refresh()
    {
        if (shell is null || workspace is null)
        {
            return;
        }

        identityValue.Text = workspace.LocalProfile?.DisplayName
            ?? (workspace.LocalIdentity is null
                ? shell.Localization.GetString("Menu.Collaboration.IdentityUnavailable")
                : shell.Localization.GetString("Menu.Collaboration.ProfileRequired"));
        identityHint.Text = workspace.LocalIdentity is { } identity
            ? ShortDeviceId(identity.DeviceId.Value)
            : workspace.IsBusy
                ? shell.Localization.GetString("Menu.Collaboration.Loading")
                : string.Empty;
        identityCard.IsEnabled = workspace.LocalIdentity is not null
            && !workspace.RequiresRestartAfterIdentityImport;
        bool canOperate = !workspace.RequiresRestartAfterIdentityImport;
        generateInviteButton.IsEnabled = canOperate && workspace.CanGenerateFriendInvite;
        acceptFriendInviteButton.IsEnabled = canOperate;
        generateSessionInviteButton.IsEnabled = canOperate;
        acceptSessionInviteButton.IsEnabled = canOperate;
        ApplySessionSnapshot(workspace.SessionSnapshot);
        contactsPanel.Children.Clear();
        foreach (CollaborationContactItem contact in workspace.Contacts)
        {
            contactsPanel.Children.Add(CreateContactRow(contact, canOperate));
        }

        emptyContactsText.IsVisible = workspace.Contacts.IsEmpty;
    }

    private Button CreateContactRow(CollaborationContactItem contact, bool isEnabled)
    {
        var name = new TextBlock
        {
            Text = contact.DisplayName,
            FontSize = 14,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = (Avalonia.Media.IBrush)this.FindResource("TextPrimary")!,
        };
        var status = new TextBlock
        {
            Text = shell!.Localization.GetString($"Menu.Collaboration.Status.{contact.Status}"),
            FontSize = 12,
            Foreground = (Avalonia.Media.IBrush)this.FindResource("TextSecondary")!,
        };
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(name);
        panel.Children.Add(status);
        var row = new Button
        {
            Theme = (ControlTheme)this.FindResource("MenuRowButtonTheme")!,
            MinHeight = 52,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Content = panel,
            IsEnabled = isEnabled,
        };
        row.Click += (_, _) => ContactRequested?.Invoke(this, contact.DeviceId);
        return row;
    }

    private void ApplyLocalization()
    {
        titleText.Text = shell!.Localization.GetString("Menu.Collaboration.Title");
        identityHeading.Text = shell.Localization.GetString("Menu.Collaboration.Identity");
        sessionHeading.Text = shell.Localization.GetString("Menu.Collaboration.Session");
        invitationsHeading.Text = shell.Localization.GetString("Menu.Collaboration.Invitations");
        contactsHeading.Text = shell.Localization.GetString("Menu.Collaboration.Contacts");
        emptyContactsText.Text = shell.Localization.GetString("Menu.Collaboration.NoContacts");
        AutomationProperties.SetName(
            identityCard,
            shell.Localization.GetString("Menu.Collaboration.Identity"));
        ApplyMenuAction(
            generateInviteButton,
            generateInviteVisual,
            shell.Localization.GetString("Command.GenerateFriendInvite"));
        ApplyMenuAction(
            acceptFriendInviteButton,
            acceptFriendInviteVisual,
            shell.Localization.GetString("Command.AcceptFriendInvite"));
        ApplyMenuAction(
            generateSessionInviteButton,
            generateSessionInviteVisual,
            shell.Localization.GetString("Command.GenerateSessionInvite"));
        ApplyMenuAction(
            acceptSessionInviteButton,
            acceptSessionInviteVisual,
            shell.Localization.GetString("Command.AcceptSessionInvite"));
        ApplyMenuAction(
            leaveSessionButton,
            leaveSessionVisual,
            shell.Localization.GetString("Command.LeaveSession"));
    }

    private void ApplySessionSnapshot(CollaborationSessionSnapshot snapshot)
    {
        sessionStatus.Text = shell!.Localization.GetString(snapshot.Phase switch
        {
            CollaborationSessionPhase.AwaitingHostConsent => "Menu.Collaboration.Session.AwaitingHostConsent",
            CollaborationSessionPhase.AwaitingParticipantConsent => "Menu.Collaboration.Session.AwaitingParticipantConsent",
            CollaborationSessionPhase.Active => "Menu.Collaboration.Session.Active",
            _ => "Menu.Collaboration.Session.Idle",
        });
        sessionMembers.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            shell.Localization.GetString("Menu.Collaboration.Session.Members"),
            snapshot.MemberCount);
        sessionTransfer.Text = shell.Localization.GetString(
            snapshot.LocalModelInstanceId is null
                ? "Menu.Collaboration.Session.ModelNotPublished"
                : "Menu.Collaboration.Session.ModelPublished");
        leaveSessionButton.IsEnabled = snapshot.Phase != CollaborationSessionPhase.Idle;
    }

    private static MenuActionVisual ConfigureMenuActionButton(Button button, string iconResourceKey)
    {
        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("17,9,*,14"),
        };
        var icon = new LucideIcon
        {
            Width = 17,
            Height = 17,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        icon.Bind(LucideIcon.StrokeProperty, new Binding(nameof(button.Foreground)) { Source = button });
        content.Children.Add(icon);
        var label = new TextBlock
        {
            FontSize = 14,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        label.Bind(TextBlock.ForegroundProperty, new Binding(nameof(button.Foreground)) { Source = button });
        Grid.SetColumn(label, 2);
        content.Children.Add(label);
        button.Content = content;
        return new MenuActionVisual(label, icon, iconResourceKey);
    }

    private void ApplyMenuAction(Button button, MenuActionVisual visual, string text)
    {
        visual.Label.Text = text;
        visual.Icon.Data = (Geometry)this.FindResource(visual.IconResourceKey)!;
        AutomationProperties.SetName(button, text);
    }

    private sealed record MenuActionVisual(
        TextBlock Label,
        LucideIcon Icon,
        string IconResourceKey);

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Refresh();
            return;
        }

        Dispatcher.UIThread.Post(Refresh);
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainWindowViewModel.Localization))
        {
            return;
        }

        void RefreshLocalizedContent()
        {
            ApplyLocalization();
            Refresh();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshLocalizedContent();
            return;
        }

        Dispatcher.UIThread.Post(RefreshLocalizedContent);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (shell is not null)
        {
            shell.PropertyChanged -= OnShellPropertyChanged;
        }

        if (workspace is not null)
        {
            workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateResponsiveLayout(double width)
    {
        bool compact = width > 0 && width < 560;
        contentGrid.ColumnDefinitions.Clear();
        contentGrid.RowDefinitions.Clear();
        if (compact)
        {
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            contentGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            contentGrid.RowDefinitions.Add(new RowDefinition(new GridLength(1)));
            contentGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            Grid.SetColumn(identityScroll, 0);
            Grid.SetRow(identityScroll, 0);
            Grid.SetColumn(contentDivider, 0);
            Grid.SetRow(contentDivider, 1);
            Grid.SetColumn(contactsScroll, 0);
            Grid.SetRow(contactsScroll, 2);
            contentDivider.Width = double.NaN;
            contentDivider.Height = 1;
        }
        else
        {
            contentGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition(0.75, GridUnitType.Star));
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1)));
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition(1.15, GridUnitType.Star));
            Grid.SetColumn(identityScroll, 0);
            Grid.SetRow(identityScroll, 0);
            Grid.SetColumn(contentDivider, 1);
            Grid.SetRow(contentDivider, 0);
            Grid.SetColumn(contactsScroll, 2);
            Grid.SetRow(contactsScroll, 0);
            contentDivider.Width = 1;
            contentDivider.Height = double.NaN;
        }
    }

    private static string ShortDeviceId(string value) => value.Length <= 20
        ? value
        : $"{value[..13]}...{value[^6..]}";
}
