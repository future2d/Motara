using System.Collections.Immutable;
using Motara.Core.Parameters;
using Motara.Core.Sessions;
using Motara.Output.Abstractions;
using Motara.Tracking.Abstractions;

namespace Motara.Output.CubismEditor;

/// <summary>Maps Motara's canonical session parameters directly to Cubism Editor parameters.</summary>
public sealed class CubismEditorParameterMapping
{
    public static CubismEditorParameterMapping Default { get; } = new(
        StandardParameterCatalog.Definitions.Select(static definition =>
            new CubismEditorParameterBinding(definition.Id, $"Param{definition.Id}")));

    public CubismEditorParameterMapping(IEnumerable<CubismEditorParameterBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        Bindings = bindings.ToImmutableArray();
        if (Bindings.Any(static binding => string.IsNullOrWhiteSpace(binding.SoftwareParameterId)
            || string.IsNullOrWhiteSpace(binding.CubismParameterId)))
        {
            throw new ArgumentException("Cubism Editor parameter bindings require both identifiers.", nameof(bindings));
        }

        if (Bindings.Select(static binding => binding.CubismParameterId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != Bindings.Length)
        {
            throw new ArgumentException("Cubism Editor parameter identifiers must be unique.", nameof(bindings));
        }
    }

    public ImmutableArray<CubismEditorParameterBinding> Bindings { get; }

    /// <summary>Creates a frame from valid canonical session values without consulting a local model.</summary>
    public OutputParameterFrame? CreateFrame(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var samples = snapshot.Parameters.ToDictionary(static sample => sample.Id, StringComparer.Ordinal);
        var values = new List<OutputParameterValue>(Bindings.Length);
        foreach (CubismEditorParameterBinding binding in Bindings)
        {
            if (samples.TryGetValue(binding.SoftwareParameterId, out ParameterSample? sample)
                && sample.Validity == ParameterValidity.Valid
                && double.IsFinite(sample.Value))
            {
                values.Add(new OutputParameterValue(binding.CubismParameterId, sample.Value));
            }
        }

        return values.Count == 0 ? null : new OutputParameterFrame(snapshot.Revision, values);
    }
}

/// <summary>Identifies one one-to-one canonical-to-Cubism Editor parameter route.</summary>
public sealed record CubismEditorParameterBinding(string SoftwareParameterId, string CubismParameterId);
