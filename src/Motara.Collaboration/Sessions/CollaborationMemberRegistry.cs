using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Collaboration.Identity;

namespace Motara.Collaboration.Sessions;

/// <summary>
/// Local admission boundary for a collaboration session. Network discovery and
/// transport remain outside this type; it only records authenticated members.
/// </summary>
public sealed class CollaborationMemberRegistry
{
    public const int MaximumSupportedMembers = 8;

    private readonly object gate = new();
    private readonly HashSet<DeviceId> members = [];
    private readonly int maximumMembers;
    private readonly ILogger<CollaborationMemberRegistry> logger;

    public CollaborationMemberRegistry(
        int maximumMembers = 4,
        ILogger<CollaborationMemberRegistry>? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMembers);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumMembers, MaximumSupportedMembers);
        this.maximumMembers = maximumMembers;
        this.logger = logger ?? NullLogger<CollaborationMemberRegistry>.Instance;
    }

    public int Count
    {
        get
        {
            lock (gate)
            {
                return members.Count;
            }
        }
    }

    public bool TryAdd(DeviceId member)
    {
        lock (gate)
        {
            if (members.Contains(member))
            {
                return true;
            }

            if (members.Count >= maximumMembers)
            {
                CollaborationSessionEvents.MemberRejected(logger, members.Count);
                return false;
            }

            bool added = members.Add(member);
            if (added)
            {
                CollaborationSessionEvents.MemberAdmitted(logger, members.Count);
            }

            return added;
        }
    }

    public bool Remove(DeviceId member)
    {
        lock (gate)
        {
            bool removed = members.Remove(member);
            if (removed)
            {
                CollaborationSessionEvents.MemberRemoved(logger, members.Count);
            }

            return removed;
        }
    }
}
