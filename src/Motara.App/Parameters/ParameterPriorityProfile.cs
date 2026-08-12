using System.Collections.Immutable;

namespace Motara.App.Parameters;

internal sealed record ParameterPriorityProfile(
    int SchemaVersion,
    ImmutableArray<ParameterProviderKind> Order)
{
    internal const int CurrentSchemaVersion = 1;

    internal static ParameterPriorityProfile Default { get; } = Create(
    [
        ParameterProviderKind.Default,
        ParameterProviderKind.AutoBreath,
        ParameterProviderKind.AutoBlink,
        ParameterProviderKind.IdleAnimation,
        ParameterProviderKind.Tracking,
        ParameterProviderKind.OneShotAnimation,
        ParameterProviderKind.Expression,
        ParameterProviderKind.Physics,
    ]);

    internal static ParameterPriorityProfile Create(
        IEnumerable<ParameterProviderKind> order)
    {
        ArgumentNullException.ThrowIfNull(order);
        var profile = new ParameterPriorityProfile(CurrentSchemaVersion, order.ToImmutableArray());
        profile.Validate();
        return profile;
    }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(SchemaVersion, CurrentSchemaVersion);
        if (Order.IsDefault || Order.Length != Enum.GetValues<ParameterProviderKind>().Length)
        {
            throw new ArgumentException("Parameter priority order must contain every provider exactly once.");
        }

        if (Order.Distinct().Count() != Order.Length
            || Order.Any(provider => !Enum.IsDefined(provider)))
        {
            throw new ArgumentException("Parameter priority order contains duplicate or unknown providers.");
        }
    }
}
