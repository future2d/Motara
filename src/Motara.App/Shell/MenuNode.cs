using System.Collections.Immutable;
using System.Windows.Input;
using Motara.Persistence;
using Motara.Scene;

namespace Motara.App.Shell;

public enum MenuNodeKind
{
    Command = 0,
    Submenu = 1,
    Toggle = 2,
    RadioChoice = 3,
    Status = 4,
    Separator = 5,
    InformationBlock = 6,
    SectionHeading = 7,
    TextInput = 8,
    Choice = 9,
    InputCapture = 10,
}

public sealed record MenuChoiceOption(string Id, string Label);

public sealed record MenuSectionActions(
    int Count,
    bool IsExpanded,
    bool CanCreate,
    Action Toggle,
    Action Create);

public enum MenuInformationState
{
    Neutral = 0,
    Positive = 1,
    Warning = 2,
    Error = 3,
}

public sealed record MenuStatusField
{
    public MenuStatusField(string labelResourceKey, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(labelResourceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        LabelResourceKey = labelResourceKey;
        Value = value;
    }

    public string LabelResourceKey { get; }

    public string Value { get; }
}

public sealed record MenuSourceActions(
    bool IsVisible,
    bool IsLocked,
    Func<bool, Task<bool>> SetVisibilityAsync,
    Func<bool, Task<bool>> SetLockAsync)
{
    public Guid SourceId { get; init; }

    public bool IsMainModel { get; init; }

    public AttachmentPlacement Placement { get; init; } = AttachmentPlacement.AfterMainModel;

    public int? OrderIndex { get; init; }

    public Func<int, Task<bool>>? MoveAsync { get; init; }

    public Func<AttachmentPlacement, int, Task<bool>>? MoveToAsync { get; init; }

    public Func<int, Task<bool>>? MoveMainToAsync { get; init; }

    public string? DisplayName { get; init; }

    public Func<string, Task<bool>>? SetDisplayNameAsync { get; init; }

    public Func<Task<bool>>? DeleteAsync { get; init; }
}

/// <summary>Describes one immutable, resource-independent business menu node.</summary>
public sealed record MenuNode
{
    public MenuNode(
        string id,
        string labelResourceKey,
        IEnumerable<MenuNode>? children = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(labelResourceKey);
        ImmutableArray<MenuNode> materializedChildren = children?.ToImmutableArray() ?? [];
        Id = id;
        LabelResourceKey = labelResourceKey;
        IconResourceKey = string.Empty;
        Children = materializedChildren;
        Kind = materializedChildren.IsEmpty ? MenuNodeKind.Command : MenuNodeKind.Submenu;
    }

    private MenuNode(
        MenuNodeKind kind,
        string id,
        string labelResourceKey,
        string iconResourceKey,
        ImmutableArray<MenuNode> children,
        ICommand? command,
        object? commandParameter,
        bool isEnabled,
        bool isSelected,
        bool isLiteralLabel,
        string? automationName,
        string? helpTextResourceKey,
        bool toggleValue,
        Func<bool, Task<bool>>? toggleChangeAsync)
    {
        Kind = kind;
        Id = id;
        LabelResourceKey = labelResourceKey;
        IconResourceKey = iconResourceKey;
        Children = children;
        Command = command;
        CommandParameter = commandParameter;
        IsEnabled = isEnabled;
        IsSelected = isSelected;
        IsLiteralLabel = isLiteralLabel;
        AutomationName = automationName;
        HelpTextResourceKey = helpTextResourceKey;
        ToggleValue = toggleValue;
        ToggleChangeAsync = toggleChangeAsync;
    }

    public MenuNodeKind Kind { get; }

    public string Id { get; }

    public string LabelResourceKey { get; }

    public string IconResourceKey { get; }

    public ImmutableArray<MenuNode> Children { get; init; }

    public ICommand? Command { get; }

    public object? CommandParameter { get; }

    public bool IsEnabled { get; }

    public bool IsSelected { get; }

    public bool IsLiteralLabel { get; init; }

    public string? AutomationName { get; }

    public string? HelpTextResourceKey { get; }

    public bool ToggleValue { get; }

    public Func<bool, Task<bool>>? ToggleChangeAsync { get; }

    public string? SecondaryText { get; init; }

    public string? PlaceholderResourceKey { get; init; }

    public string TextValue { get; init; } = string.Empty;

    public Action<string?>? TextChanged { get; init; }

    public ImmutableArray<MenuChoiceOption> ChoiceOptions { get; init; } = [];

    public string? SelectedChoiceId { get; init; }

    public Action<string?>? ChoiceChanged { get; init; }

    public InputGesture? CapturedGesture { get; init; }

    public Action<InputGesture>? GestureChanged { get; init; }

    public MenuSectionActions? SectionActions { get; init; }

    public Action? BeforeOpen { get; init; }

    public Func<Task<bool>>? ActionAsync { get; init; }

    public ImmutableArray<MenuStatusField> StatusFields { get; init; } = [];

    public MenuInformationState InformationState { get; init; }

    public string? UnavailableReasonResourceKey { get; init; }

    public string? EmptyValueResourceKey { get; init; }

    public MenuSourceActions? SourceActions { get; init; }

    public static MenuNode CreateCommand(
        string id,
        string labelResourceKey,
        string iconResourceKey,
        ICommand? command = null,
        object? commandParameter = null,
        bool isEnabled = true,
        bool isSelected = false,
        bool isLiteralLabel = false,
        string? automationName = null,
        string? helpTextResourceKey = null,
        MenuSourceActions? sourceActions = null) =>
        CreateLeaf(
            MenuNodeKind.Command,
            id,
            labelResourceKey,
            iconResourceKey,
            command,
            commandParameter,
            isEnabled,
            isSelected,
            isLiteralLabel,
            automationName,
            helpTextResourceKey) with
        {
            SourceActions = sourceActions,
        };

    public static MenuNode CreateSubmenu(
        string id,
        string labelResourceKey,
        string iconResourceKey,
        IEnumerable<MenuNode> children,
        bool isEnabled = true,
        bool isSelected = false,
        string? automationName = null,
        string? helpTextResourceKey = null)
    {
        ValidatePresentation(id, labelResourceKey, iconResourceKey);
        ArgumentNullException.ThrowIfNull(children);
        ImmutableArray<MenuNode> materializedChildren = children.ToImmutableArray();
        if (materializedChildren.IsEmpty)
        {
            throw new ArgumentException("A submenu requires at least one child.", nameof(children));
        }

        return new MenuNode(
            MenuNodeKind.Submenu,
            id,
            labelResourceKey,
            iconResourceKey,
            materializedChildren,
            command: null,
            commandParameter: null,
            isEnabled,
            isSelected,
            isLiteralLabel: false,
            automationName,
            helpTextResourceKey,
            toggleValue: false,
            toggleChangeAsync: null);
    }

    public static MenuNode CreateToggle(
        string id,
        string labelResourceKey,
        string iconResourceKey,
        bool isChecked,
        Func<bool, Task<bool>> changeAsync,
        bool isEnabled = true,
        string? automationName = null,
        string? helpTextResourceKey = null)
    {
        ValidatePresentation(id, labelResourceKey, iconResourceKey);
        ArgumentNullException.ThrowIfNull(changeAsync);
        return new MenuNode(
            MenuNodeKind.Toggle,
            id,
            labelResourceKey,
            iconResourceKey,
            [],
            command: null,
            commandParameter: null,
            isEnabled,
            isSelected: false,
            isLiteralLabel: false,
            automationName,
            helpTextResourceKey,
            isChecked,
            changeAsync);
    }

    public static MenuNode CreateRadioChoice(
        string id,
        string labelResourceKey,
        string iconResourceKey,
        ICommand? command,
        object? commandParameter = null,
        bool isSelected = false,
        bool isEnabled = true,
        string? automationName = null,
        string? helpTextResourceKey = null,
        bool isLiteralLabel = false)
    {
        if (command is null && isEnabled)
        {
            throw new ArgumentNullException(nameof(command));
        }
        return CreateLeaf(
            MenuNodeKind.RadioChoice,
            id,
            labelResourceKey,
            iconResourceKey,
            command,
            commandParameter,
            isEnabled,
            isSelected,
            isLiteralLabel,
            automationName,
            helpTextResourceKey);
    }

    public static MenuNode CreateStatus(
        string id,
        string labelResourceKey,
        string iconResourceKey,
        bool isLiteralLabel = false,
        string? automationName = null,
        string? helpTextResourceKey = null) =>
        CreateLeaf(
            MenuNodeKind.Status,
            id,
            labelResourceKey,
            iconResourceKey,
            command: null,
            commandParameter: null,
            isEnabled: false,
            isSelected: false,
            isLiteralLabel,
            automationName,
            helpTextResourceKey);

    public static MenuNode CreateInformationBlock(
        string id,
        string titleResourceKey,
        string? iconResourceKey,
        IEnumerable<MenuStatusField> fields,
        MenuInformationState informationState = MenuInformationState.Neutral,
        string? unavailableReasonResourceKey = null,
        string? emptyValueResourceKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(titleResourceKey);
        ArgumentNullException.ThrowIfNull(fields);
        ImmutableArray<MenuStatusField> materializedFields = fields.ToImmutableArray();
        if (!Enum.IsDefined(informationState))
        {
            throw new ArgumentOutOfRangeException(nameof(informationState));
        }

        return new MenuNode(
            MenuNodeKind.InformationBlock,
            id,
            titleResourceKey,
            iconResourceKey ?? string.Empty,
            [],
            command: null,
            commandParameter: null,
            isEnabled: false,
            isSelected: false,
            isLiteralLabel: false,
            automationName: null,
            helpTextResourceKey: null,
            toggleValue: false,
            toggleChangeAsync: null) with
        {
            StatusFields = materializedFields,
            InformationState = informationState,
            UnavailableReasonResourceKey = unavailableReasonResourceKey,
            EmptyValueResourceKey = emptyValueResourceKey,
        };
    }

    public static MenuNode CreateSeparator(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return new MenuNode(
            MenuNodeKind.Separator,
            id,
            string.Empty,
            string.Empty,
            [],
            command: null,
            commandParameter: null,
            isEnabled: false,
            isSelected: false,
            isLiteralLabel: false,
            automationName: null,
            helpTextResourceKey: null,
            toggleValue: false,
            toggleChangeAsync: null);
    }

    public static MenuNode CreateSectionHeading(string id, string labelResourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(labelResourceKey);
        return new MenuNode(
            MenuNodeKind.SectionHeading,
            id,
            labelResourceKey,
            string.Empty,
            [],
            command: null,
            commandParameter: null,
            isEnabled: false,
            isSelected: false,
            isLiteralLabel: false,
            automationName: null,
            helpTextResourceKey: null,
            toggleValue: false,
            toggleChangeAsync: null);
    }

    public static MenuNode CreateTextInput(
        string id,
        string labelResourceKey,
        string value,
        string placeholderResourceKey,
        Action<string?> changed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(placeholderResourceKey);
        ArgumentNullException.ThrowIfNull(changed);
        return CreateEditorNode(MenuNodeKind.TextInput, id, labelResourceKey) with
        {
            TextValue = value ?? string.Empty,
            PlaceholderResourceKey = placeholderResourceKey,
            TextChanged = changed,
        };
    }

    public static MenuNode CreateChoice(
        string id,
        string labelResourceKey,
        IEnumerable<MenuChoiceOption> options,
        string? selectedId,
        Action<string?> changed)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(changed);
        return CreateEditorNode(MenuNodeKind.Choice, id, labelResourceKey) with
        {
            ChoiceOptions = options.ToImmutableArray(),
            SelectedChoiceId = selectedId,
            ChoiceChanged = changed,
        };
    }

    public static MenuNode CreateInputCapture(
        string id,
        string labelResourceKey,
        InputGesture? gesture,
        Action<InputGesture> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        return CreateEditorNode(MenuNodeKind.InputCapture, id, labelResourceKey) with
        {
            CapturedGesture = gesture,
            GestureChanged = changed,
        };
    }

    private static MenuNode CreateEditorNode(
        MenuNodeKind kind,
        string id,
        string labelResourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(labelResourceKey);
        return new MenuNode(
            kind,
            id,
            labelResourceKey,
            string.Empty,
            [],
            command: null,
            commandParameter: null,
            isEnabled: true,
            isSelected: false,
            isLiteralLabel: false,
            automationName: null,
            helpTextResourceKey: null,
            toggleValue: false,
            toggleChangeAsync: null);
    }

    private static MenuNode CreateLeaf(
        MenuNodeKind kind,
        string id,
        string labelResourceKey,
        string iconResourceKey,
        ICommand? command,
        object? commandParameter,
        bool isEnabled,
        bool isSelected,
        bool isLiteralLabel,
        string? automationName,
        string? helpTextResourceKey)
    {
        ValidatePresentation(id, labelResourceKey, iconResourceKey);
        return new MenuNode(
            kind,
            id,
            labelResourceKey,
            iconResourceKey,
            [],
            command,
            commandParameter,
            isEnabled,
            isSelected,
            isLiteralLabel,
            automationName,
            helpTextResourceKey,
            toggleValue: false,
            toggleChangeAsync: null);
    }

    private static void ValidatePresentation(
        string id,
        string labelResourceKey,
        string iconResourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(labelResourceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(iconResourceKey);
    }
}
