using Motara.Core.Sessions;
using Motara.ModelLibrary;
using Motara.Tracking.Abstractions;

namespace Motara.App.Models;

internal sealed record ModelMotionExpansionSnapshot(
    ModelId ModelId,
    double X,
    double Y,
    double Z);

internal sealed class ActiveModelMotionExpansionSource
{
    private readonly object gate = new();
    private ModelMotionExpansionSnapshot? current;

    internal event EventHandler? Changed;

    internal void Publish(
        ModelId modelId,
        ModelPhysicsConfiguration configuration,
        SessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(session);
        ModelMotionExpansionSnapshot next = configuration.MotionExpansionEnabled
            ? new ModelMotionExpansionSnapshot(
                modelId,
                Project(session, "AngleX", configuration.MotionExpansionX),
                Project(session, "AngleY", configuration.MotionExpansionY),
                Project(session, "AngleZ", configuration.MotionExpansionZ))
            : new ModelMotionExpansionSnapshot(modelId, 0, 0, 0);
        bool changed;
        lock (gate)
        {
            changed = current != next;
            current = next;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    internal bool TryGet(ModelId modelId, out ModelMotionExpansionSnapshot snapshot)
    {
        ModelMotionExpansionSnapshot? candidate = Volatile.Read(ref current);
        if (candidate is not null && candidate.ModelId == modelId)
        {
            snapshot = candidate;
            return true;
        }

        snapshot = null!;
        return false;
    }

    private static double Project(SessionSnapshot session, string parameterId, double extent)
    {
        ParameterSample? sample = session.Parameters.FirstOrDefault(parameter =>
            parameter.Validity == ParameterValidity.Valid
            && string.Equals(parameter.Id, parameterId, StringComparison.OrdinalIgnoreCase));
        return sample is { Value: var value } && double.IsFinite(value)
            ? Math.Clamp(value / 30d, -1, 1) * extent
            : 0;
    }
}
