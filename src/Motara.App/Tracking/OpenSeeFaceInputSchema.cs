using System.Collections.Immutable;
using Motara.Tracking.Abstractions;

namespace Motara.App.Tracking;

internal static class OpenSeeFaceInputSchema
{
    internal const int Landmark2DCount = 68;
    internal const int Landmark3DCount = 70;
    internal const int FeatureCount = 14;
    internal const int InputCount = 28;

    private static readonly (string Id, string Category, double Minimum, double Maximum)[] Features =
    [
        ("EyeLeft", "Eyes", 0, 1),
        ("EyeRight", "Eyes", 0, 1),
        ("EyebrowSteepnessLeft", "Brows", -1, 1),
        ("EyebrowUpDownLeft", "Brows", -1, 1),
        ("EyebrowQuirkLeft", "Brows", -1, 1),
        ("EyebrowSteepnessRight", "Brows", -1, 1),
        ("EyebrowUpDownRight", "Brows", -1, 1),
        ("EyebrowQuirkRight", "Brows", -1, 1),
        ("MouthCornerUpDownLeft", "Mouth", -1, 1),
        ("MouthCornerInOutLeft", "Mouth", -1, 1),
        ("MouthCornerUpDownRight", "Mouth", -1, 1),
        ("MouthCornerInOutRight", "Mouth", -1, 1),
        ("MouthOpen", "Mouth", 0, 1),
        ("MouthWide", "Mouth", -1, 1),
    ];

    internal static ImmutableArray<TrackingInputDefinition> Definitions { get; } = CreateDefinitions();

    internal static ImmutableArray<string> InputIds { get; } =
        Definitions.Select(static definition => definition.Id).ToImmutableArray();

    private static ImmutableDictionary<string, int> Slots { get; } = InputIds
        .Select(static (id, slot) => (id, slot))
        .ToImmutableDictionary(static item => item.id, static item => item.slot, StringComparer.Ordinal);

    internal static int GetRequiredSlot(string id) => Slots[id];

    private static ImmutableArray<TrackingInputDefinition> CreateDefinitions()
    {
        var definitions = ImmutableArray.CreateBuilder<TrackingInputDefinition>(InputCount);
        Add("Eye.Right.Open", "Eyes", "Eye.Open", TrackingInputUnit.Unitless, 0, 1);
        Add("Eye.Left.Open", "Eyes", "Eye.Open", TrackingInputUnit.Unitless, 0, 1);
        Add("Tracking.Success", "Tracking", "Tracking.Success", TrackingInputUnit.Unitless, 0, 1);
        Add("Tracking.PnpError", "Tracking", "Tracking.PnpError", TrackingInputUnit.Unitless, 0, 100);
        foreach (string axis in new[] { "X", "Y", "Z", "W" })
        {
            Add($"Head.Quaternion.{axis}", "Head", "Head.Quaternion", TrackingInputUnit.Unitless, -1, 1);
        }

        foreach (string axis in new[] { "X", "Y", "Z" })
        {
            Add($"Head.Euler{axis}Degrees", "Head", "Head.Euler", TrackingInputUnit.Degrees, -180, 180);
        }

        foreach (string axis in new[] { "X", "Y", "Z" })
        {
            Add($"Head.Translation.{axis}", "Head", "Head.Translation", TrackingInputUnit.Position, -1000, 1000);
        }

        foreach ((string id, string category, double minimum, double maximum) in Features)
        {
            Add($"Feature.{id}", category, $"Feature.{id}", TrackingInputUnit.Unitless, minimum, maximum);
        }

        if (definitions.Count != InputCount)
        {
            throw new InvalidOperationException("OpenSeeFace input schema count does not match the UDP layout.");
        }

        return definitions.MoveToImmutable();

        void Add(
            string id,
            string category,
            string displayName,
            TrackingInputUnit unit,
            double minimum,
            double maximum) => definitions.Add(new TrackingInputDefinition(
                id,
                category,
                $"Tracking.Input.OpenSeeFace.{displayName}",
                unit,
                minimum,
                maximum));
    }
}
