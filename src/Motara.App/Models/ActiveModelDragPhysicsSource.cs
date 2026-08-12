using System.Numerics;
using Motara.ModelLibrary;

namespace Motara.App.Models;

internal sealed class ActiveModelDragPhysicsSource
{
    private readonly object gate = new();
    private ModelId? modelId;
    private Vector2 pendingDisplacement;

    internal event EventHandler? Changed;

    internal void Publish(ModelId value, Vector2 normalizedDisplacement)
    {
        if (!float.IsFinite(normalizedDisplacement.X)
            || !float.IsFinite(normalizedDisplacement.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(normalizedDisplacement));
        }

        if (normalizedDisplacement == Vector2.Zero)
        {
            return;
        }

        lock (gate)
        {
            if (modelId != value)
            {
                modelId = value;
                pendingDisplacement = Vector2.Zero;
            }

            pendingDisplacement += normalizedDisplacement;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal bool TryConsume(ModelId value, out Vector2 normalizedDisplacement)
    {
        lock (gate)
        {
            if (modelId != value || pendingDisplacement == Vector2.Zero)
            {
                normalizedDisplacement = Vector2.Zero;
                return false;
            }

            normalizedDisplacement = pendingDisplacement;
            pendingDisplacement = Vector2.Zero;
            return true;
        }
    }
}
