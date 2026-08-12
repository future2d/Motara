using System.Collections.Immutable;

namespace Motara.Persistence;

public enum InputGestureKind
{
    KeyChord = 0,
    KeySequence = 1,
    MouseButton = 2,
    Wheel = 3,
    TouchpadAxis = 4,
}

public enum InputBindingScope
{
    Global = 0,
    Application = 1,
    Canvas = 2,
    MenuWorkspace = 3,
    MenuColumn = 4,
    CurrentSource = 5,
}

public enum InputAxis
{
    Horizontal = 0,
    Vertical = 1,
}

public enum InputDirection
{
    Negative = 0,
    Positive = 1,
}

[Flags]
public enum InputModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Meta = 8,
}

public sealed record InputGesture
{
    public InputGesture(
        InputGestureKind kind,
        string? primary,
        ImmutableArray<string> sequence,
        InputAxis? axis,
        InputDirection? direction,
        InputModifiers modifiers,
        int sequenceTimeoutMilliseconds)
    {
        const InputModifiers KnownModifiers = InputModifiers.Control
            | InputModifiers.Alt
            | InputModifiers.Shift
            | InputModifiers.Meta;
        if (!Enum.IsDefined(kind)
            || (modifiers & ~KnownModifiers) != 0
            || (axis.HasValue && !Enum.IsDefined(axis.Value))
            || (direction.HasValue && !Enum.IsDefined(direction.Value)))
        {
            throw new ArgumentException("Input gesture contains an invalid enum value.");
        }

        if (sequence.IsDefault)
        {
            throw new ArgumentException("Input sequence must be initialized.", nameof(sequence));
        }

        Kind = kind;
        Primary = NormalizeOptional(primary);
        Sequence = sequence.Select(NormalizeRequired).ToImmutableArray();
        Axis = axis;
        Direction = direction;
        Modifiers = modifiers;
        SequenceTimeoutMilliseconds = sequenceTimeoutMilliseconds;
        Validate();
    }

    public InputGestureKind Kind { get; }

    public string? Primary { get; }

    public ImmutableArray<string> Sequence { get; }

    public InputAxis? Axis { get; }

    public InputDirection? Direction { get; }

    public InputModifiers Modifiers { get; }

    public int SequenceTimeoutMilliseconds { get; }

    public string CanonicalText => Kind switch
    {
        InputGestureKind.KeyChord => $"key:{Primary!.ToLowerInvariant()}:{ModifiersText()}",
        InputGestureKind.KeySequence => $"sequence:{string.Join(">", Sequence.Select(static value => value.ToLowerInvariant()))}:{SequenceTimeoutMilliseconds}",
        InputGestureKind.MouseButton => $"mouse:{Primary!.ToLowerInvariant()}:{ModifiersText()}",
        InputGestureKind.Wheel => $"wheel:{AxisText()}:{DirectionText()}:{ModifiersText()}",
        InputGestureKind.TouchpadAxis => $"touchpad:{AxisText()}:{DirectionText()}:{ModifiersText()}",
        _ => throw new InvalidOperationException(),
    };

    public static InputGesture KeyChord(string key, InputModifiers modifiers) => new(
        InputGestureKind.KeyChord,
        key,
        [],
        null,
        null,
        modifiers,
        0);

    public static InputGesture KeySequence(IEnumerable<string> steps, int timeoutMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return new InputGesture(
            InputGestureKind.KeySequence,
            null,
            steps.ToImmutableArray(),
            null,
            null,
            InputModifiers.None,
            timeoutMilliseconds);
    }

    public static InputGesture MouseButton(string button, InputModifiers modifiers) => new(
        InputGestureKind.MouseButton,
        button,
        [],
        null,
        null,
        modifiers,
        0);

    public static InputGesture Wheel(
        InputAxis axis,
        InputDirection direction,
        InputModifiers modifiers) => new(
            InputGestureKind.Wheel,
            null,
            [],
            axis,
            direction,
            modifiers,
            0);

    public static InputGesture TouchpadAxis(
        InputAxis axis,
        InputDirection direction,
        InputModifiers modifiers = InputModifiers.None) => new(
            InputGestureKind.TouchpadAxis,
            null,
            [],
            axis,
            direction,
            modifiers,
            0);

    private void Validate()
    {
        switch (Kind)
        {
            case InputGestureKind.KeyChord:
            case InputGestureKind.MouseButton:
                if (Primary is null || !Sequence.IsEmpty || Axis.HasValue || Direction.HasValue)
                {
                    throw new ArgumentException("Chord and mouse gestures require only a primary input.");
                }
                break;
            case InputGestureKind.KeySequence:
                if (Primary is not null || Sequence.IsEmpty || Axis.HasValue || Direction.HasValue)
                {
                    throw new ArgumentException("Key sequences require one or more sequence steps.");
                }

                if (SequenceTimeoutMilliseconds is < 100 or > 5000)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(SequenceTimeoutMilliseconds),
                        "Sequence timeout must be between 100 and 5000 milliseconds.");
                }

                if (Sequence.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Sequence.Length)
                {
                    throw new ArgumentException("Key sequence steps must not repeat.");
                }
                break;
            case InputGestureKind.Wheel:
            case InputGestureKind.TouchpadAxis:
                if (Primary is not null || !Sequence.IsEmpty || !Axis.HasValue || !Direction.HasValue)
                {
                    throw new ArgumentException("Axis gestures require an axis and direction.");
                }
                break;
        }
    }

    private string AxisText() => Axis!.Value.ToString().ToLowerInvariant();

    private string DirectionText() => Direction!.Value.ToString().ToLowerInvariant();

    private string ModifiersText()
    {
        if (Modifiers == InputModifiers.None)
        {
            return "none";
        }

        var values = new List<string>(4);
        Add(InputModifiers.Control, "control");
        Add(InputModifiers.Alt, "alt");
        Add(InputModifiers.Shift, "shift");
        Add(InputModifiers.Meta, "meta");
        return string.Join('+', values);

        void Add(InputModifiers value, string text)
        {
            if ((Modifiers & value) != 0)
            {
                values.Add(text);
            }
        }
    }

    private static string? NormalizeOptional(string? value) => value is null
        ? null
        : NormalizeRequired(value);

    private static string NormalizeRequired(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}

public sealed record InputBinding
{
    public InputBinding(
        string actionId,
        InputBindingScope scope,
        InputGesture gesture,
        bool isGlobalEnabled = false,
        bool isDefaultOverride = false,
        string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ArgumentNullException.ThrowIfNull(gesture);
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        ActionId = actionId;
        Scope = scope;
        Gesture = gesture;
        IsGlobalEnabled = isGlobalEnabled;
        IsDefaultOverride = isDefaultOverride;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
    }

    public string ActionId { get; }

    public InputBindingScope Scope { get; }

    public InputGesture Gesture { get; }

    public bool IsGlobalEnabled { get; }

    public bool IsDefaultOverride { get; }

    public string? DisplayName { get; }
}

public sealed record UnavailableBindingRecord(
    string ActionId,
    InputBindingScope Scope,
    InputGesture Gesture,
    string? DisplayName = null);

public sealed record InputBindingProfile
{
    public const int CurrentSchemaVersion = 1;

    public static InputBindingProfile Default { get; } = Create([]);

    public InputBindingProfile(
        int schemaVersion,
        ImmutableArray<InputBinding> bindings,
        ImmutableArray<UnavailableBindingRecord> unavailable)
    {
        SchemaVersion = schemaVersion;
        Bindings = bindings;
        Unavailable = unavailable;
        Validate();
    }

    public int SchemaVersion { get; }

    public ImmutableArray<InputBinding> Bindings { get; }

    public ImmutableArray<UnavailableBindingRecord> Unavailable { get; }

    public static InputBindingProfile Create(
        IEnumerable<InputBinding> bindings,
        IEnumerable<UnavailableBindingRecord>? unavailable = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        return new InputBindingProfile(
            CurrentSchemaVersion,
            bindings.ToImmutableArray(),
            unavailable?.ToImmutableArray() ?? []);
    }

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentException("Unsupported input binding schema version.");
        }

        if (Bindings.IsDefault || Unavailable.IsDefault)
        {
            throw new ArgumentException("Input binding collections must be initialized.");
        }

        foreach (IGrouping<(InputBindingScope Scope, string Canonical), InputBinding> duplicate in Bindings
            .GroupBy(static binding => (binding.Scope, binding.Gesture.CanonicalText)))
        {
            if (duplicate.Skip(1).Any())
            {
                throw new ArgumentException("A gesture can be assigned only once within a scope.");
            }
        }
    }
}
