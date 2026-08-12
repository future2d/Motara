using System.Text.Json.Serialization;

namespace Motara.Collaboration.Invites;

public readonly record struct CollaborationSessionId
{
    [JsonConstructor]
    public CollaborationSessionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Collaboration session ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static CollaborationSessionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}
