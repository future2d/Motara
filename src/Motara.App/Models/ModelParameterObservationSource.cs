using System.Collections.Immutable;
using Motara.ModelLibrary;

namespace Motara.App.Models;

internal readonly record struct ModelParameterObservation(
    string ModelParameterId,
    string? GlobalParameterId,
    double? InputValue,
    double? OutputValue);

internal sealed class ModelParameterObservationSource
{
    private Snapshot current = Snapshot.Empty;

    internal void Publish(
        ModelId modelId,
        IEnumerable<ModelParameterObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var byTarget = observations.ToImmutableDictionary(
            static observation => observation.ModelParameterId,
            StringComparer.Ordinal);
        Volatile.Write(ref current, new Snapshot(modelId, byTarget));
    }

    internal bool TryGet(
        ModelId modelId,
        string modelParameterId,
        out ModelParameterObservation observation)
    {
        Snapshot snapshot = Volatile.Read(ref current);
        if (snapshot.ModelId == modelId
            && snapshot.ByTarget.TryGetValue(modelParameterId, out observation))
        {
            return true;
        }

        observation = default;
        return false;
    }

    private sealed record Snapshot(
        ModelId? ModelId,
        ImmutableDictionary<string, ModelParameterObservation> ByTarget)
    {
        internal static Snapshot Empty { get; } = new(
            null,
            ImmutableDictionary.Create<string, ModelParameterObservation>(
                StringComparer.Ordinal));
    }
}
