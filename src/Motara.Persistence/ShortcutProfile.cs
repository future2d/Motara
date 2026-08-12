using System.Collections.Immutable;

namespace Motara.Persistence;

public enum ShortcutOwnerKind
{
    Software = 0,
    Scene = 1,
    Model = 2,
}

public sealed record ShortcutEntry
{
    public ShortcutEntry(
        Guid id,
        ShortcutOwnerKind owner,
        string name,
        string actionKind,
        string? targetId,
        InputGesture gesture,
        bool isGlobalEnabled)
    {
        if (id == Guid.Empty) throw new ArgumentException("Shortcut ID cannot be empty.", nameof(id));
        if (!Enum.IsDefined(owner)) throw new ArgumentOutOfRangeException(nameof(owner));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKind);
        ArgumentNullException.ThrowIfNull(gesture);

        Id = id;
        Owner = owner;
        Name = name.Trim();
        ActionKind = actionKind.Trim().TrimEnd('/');
        TargetId = string.IsNullOrWhiteSpace(targetId) ? null : targetId.Trim().TrimStart('/');
        Gesture = gesture;
        IsGlobalEnabled = isGlobalEnabled;
    }

    public Guid Id { get; }

    public ShortcutOwnerKind Owner { get; }

    public string Name { get; }

    public string ActionKind { get; }

    public string? TargetId { get; }

    public InputGesture Gesture { get; }

    public bool IsGlobalEnabled { get; }

    public string RuntimeActionId => TargetId is null ? ActionKind : $"{ActionKind}/{TargetId}";

    public InputBinding ToInputBinding() => new(
        RuntimeActionId,
        IsGlobalEnabled ? InputBindingScope.Global : InputBindingScope.Application,
        Gesture,
        IsGlobalEnabled,
        displayName: Name);
}

public sealed record ShortcutProfile
{
    public const int CurrentSchemaVersion = 1;

    public static ShortcutProfile Default { get; } = Create([]);

    public ShortcutProfile(int schemaVersion, ImmutableArray<ShortcutEntry> entries)
    {
        SchemaVersion = schemaVersion;
        Entries = entries;
        Validate();
    }

    public int SchemaVersion { get; }

    public ImmutableArray<ShortcutEntry> Entries { get; }

    public static ShortcutProfile Create(IEnumerable<ShortcutEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return new ShortcutProfile(CurrentSchemaVersion, entries.ToImmutableArray());
    }

    public InputBindingProfile ToInputBindingProfile() =>
        InputBindingProfile.Create(Entries.Select(static entry => entry.ToInputBinding()));

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new ArgumentException("Unsupported shortcut schema version.");
        if (Entries.IsDefault)
            throw new ArgumentException("Shortcut entries must be initialized.");
        if (Entries.Select(static entry => entry.Id).Distinct().Count() != Entries.Length)
            throw new ArgumentException("Shortcut instance IDs must be unique.");
        if (Entries.Select(static entry => entry.Gesture.CanonicalText)
            .Distinct(StringComparer.Ordinal).Count() != Entries.Length)
            throw new ArgumentException("A gesture can be assigned to only one shortcut.");
    }

    public bool Equals(ShortcutProfile? other) =>
        other is not null
        && SchemaVersion == other.SchemaVersion
        && Entries.Length == other.Entries.Length
        && Entries.Zip(other.Entries).All(static pair => EntryEquals(pair.First, pair.Second));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        foreach (ShortcutEntry entry in Entries)
        {
            hash.Add(entry.Id);
            hash.Add(entry.Owner);
            hash.Add(entry.Name, StringComparer.Ordinal);
            hash.Add(entry.ActionKind, StringComparer.Ordinal);
            hash.Add(entry.TargetId, StringComparer.Ordinal);
            hash.Add(entry.Gesture.CanonicalText, StringComparer.Ordinal);
            hash.Add(entry.IsGlobalEnabled);
        }
        return hash.ToHashCode();
    }

    private static bool EntryEquals(ShortcutEntry left, ShortcutEntry right) =>
        left.Id == right.Id
        && left.Owner == right.Owner
        && StringComparer.Ordinal.Equals(left.Name, right.Name)
        && StringComparer.Ordinal.Equals(left.ActionKind, right.ActionKind)
        && StringComparer.Ordinal.Equals(left.TargetId, right.TargetId)
        && StringComparer.Ordinal.Equals(left.Gesture.CanonicalText, right.Gesture.CanonicalText)
        && left.IsGlobalEnabled == right.IsGlobalEnabled;
}
