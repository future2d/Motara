using System.Collections.Immutable;
using Motara.Collaboration.Identity;
using Motara.Collaboration.Models;
namespace Motara.Collaboration.Drive;
public sealed record ModelDriveSnapshot
{
    public const int MaximumParameterCount = 256;
    public ModelDriveSnapshot(DeviceId memberDeviceId, ModelGeneration generation, ulong sequence, DateTimeOffset timestampUtc, ImmutableDictionary<string, float> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Count > MaximumParameterCount || parameters.Any(static pair => string.IsNullOrWhiteSpace(pair.Key) || !float.IsFinite(pair.Value)))
            throw new ArgumentException("The model drive snapshot parameters are invalid.", nameof(parameters));
        MemberDeviceId = memberDeviceId; Generation = generation; Sequence = sequence; TimestampUtc = timestampUtc; Parameters = parameters;
    }
    public DeviceId MemberDeviceId { get; }
    public ModelGeneration Generation { get; }
    public ulong Sequence { get; }
    public DateTimeOffset TimestampUtc { get; }
    public ImmutableDictionary<string, float> Parameters { get; }
}
