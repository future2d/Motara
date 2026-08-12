using System.Collections.Immutable;
using Motara.Collaboration.Identity;

namespace Motara.Collaboration.Models;

public enum ModelPublicationStatus { Idle, Publishing, Ready, Withdrawn, Failed }

public sealed record ModelPublicationState(
    ModelPublicationStatus Status,
    ModelGeneration? Generation,
    ImmutableDictionary<DeviceId, ModelPublicationStatus> Peers)
{
    public static ModelPublicationState Empty { get; } = new(
        ModelPublicationStatus.Idle,
        null,
        ImmutableDictionary<DeviceId, ModelPublicationStatus>.Empty);
}
