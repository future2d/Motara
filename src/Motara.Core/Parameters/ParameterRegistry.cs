using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Motara.Core.Parameters;

/// <summary>Maps stable parameter identifiers to deterministic integer slots.</summary>
public sealed class ParameterRegistry
{
    private readonly FrozenDictionary<string, int> slotsById;

    private ParameterRegistry(
        ImmutableArray<ParameterDefinition> definitions,
        FrozenDictionary<string, int> slotsById)
    {
        Definitions = definitions;
        this.slotsById = slotsById;
    }

    /// <summary>Gets the number of registered slots.</summary>
    public int Count => Definitions.Length;

    /// <summary>Gets definitions in their stable slot order.</summary>
    public ImmutableArray<ParameterDefinition> Definitions { get; }

    /// <summary>Builds a registry and rejects blank or duplicate identifiers.</summary>
    public static ParameterRegistry Create(IEnumerable<ParameterDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var ordered = ImmutableArray.CreateBuilder<ParameterDefinition>();
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (ParameterDefinition definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (!GlobalParameterId.IsValid(definition.Id))
            {
                throw new ArgumentException(
                    $"Invalid global parameter identifier: {definition.Id}",
                    nameof(definitions));
            }

            if (!double.IsFinite(definition.NeutralValue)
                || !double.IsFinite(definition.SuggestedMinimum)
                || !double.IsFinite(definition.SuggestedMaximum)
                || definition.SuggestedMinimum > definition.SuggestedMaximum
                || definition.NeutralValue < definition.SuggestedMinimum
                || definition.NeutralValue > definition.SuggestedMaximum)
            {
                throw new ArgumentException(
                    $"Invalid suggested range for parameter: {definition.Id}",
                    nameof(definitions));
            }

            if (!Enum.IsDefined(definition.Origin))
            {
                throw new ArgumentException(
                    $"Invalid metadata for parameter: {definition.Id}",
                    nameof(definitions));
            }

            int slot = ordered.Count;
            if (!slots.TryAdd(definition.Id, slot))
            {
                throw new ArgumentException($"Duplicate parameter identifier: {definition.Id}", nameof(definitions));
            }

            ordered.Add(definition);
        }

        return new ParameterRegistry(
            ordered.ToImmutable(),
            slots.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>Attempts to resolve a stable identifier to its slot.</summary>
    public bool TryGetSlot(string id, out int slot)
    {
        ArgumentNullException.ThrowIfNull(id);
        return slotsById.TryGetValue(id, out slot);
    }

    /// <summary>Resolves an identifier or throws when it is not registered.</summary>
    public int GetRequiredSlot(string id)
    {
        if (TryGetSlot(id, out int slot))
        {
            return slot;
        }

        throw new KeyNotFoundException($"Unknown parameter identifier: {id}");
    }
}
