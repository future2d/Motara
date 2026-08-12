using Motara.Collaboration.Models;
namespace Motara.Collaboration.Drive;
public enum ModelDriveEventKind { Action, Expression, State }
public sealed record ModelDriveEvent
{
    public const int MaximumPayloadBytes = 4 * 1024;

    public ModelDriveEvent(ModelGeneration generation, ulong sequence, ModelDriveEventKind kind, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length > MaximumPayloadBytes)
        {
            throw new ArgumentException("A model drive event payload is too large.", nameof(payload));
        }

        Generation = generation;
        Sequence = sequence;
        Kind = kind;
        Payload = payload.ToArray();
    }

    public ModelGeneration Generation { get; }
    public ulong Sequence { get; }
    public ModelDriveEventKind Kind { get; }
    public byte[] Payload { get; }
}
