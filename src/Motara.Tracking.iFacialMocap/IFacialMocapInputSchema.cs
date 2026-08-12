using System.Collections.Immutable;
using Motara.Tracking.Abstractions;

namespace Motara.Tracking.iFacialMocap;

internal static class IFacialMocapInputSchema
{
    private static readonly string[] BlendShapes =
    [
        "jawOpen", "browDown_L", "browDown_R", "browInnerUp", "browOuterUp_L", "browOuterUp_R",
        "cheekPuff", "cheekSquint_L", "cheekSquint_R", "eyeBlink_L", "eyeBlink_R",
        "eyeLookDown_L", "eyeLookDown_R", "eyeLookIn_L", "eyeLookIn_R", "eyeLookOut_L",
        "eyeLookOut_R", "eyeLookUp_L", "eyeLookUp_R", "eyeSquint_L", "eyeSquint_R",
        "eyeWide_L", "eyeWide_R", "jawForward", "jawLeft", "jawRight", "mouthClose",
        "mouthDimple_L", "mouthDimple_R", "mouthFrown_L", "mouthFrown_R", "mouthFunnel",
        "mouthLeft", "mouthLowerDown_L", "mouthLowerDown_R", "mouthPress_L", "mouthPress_R",
        "mouthPucker", "mouthRight", "mouthRollLower", "mouthRollUpper", "mouthShrugLower",
        "mouthShrugUpper", "mouthSmile_L", "mouthSmile_R", "mouthStretch_L", "mouthStretch_R",
        "mouthUpperUp_L", "mouthUpperUp_R", "noseSneer_L", "noseSneer_R", "tongueOut",
    ];

    internal static ImmutableArray<TrackingInputDefinition> Definitions { get; } = CreateDefinitions();
    internal static ImmutableArray<string> InputIds { get; } = Definitions.Select(x => x.Id).ToImmutableArray();
    private static readonly ImmutableDictionary<string, int> Slots = InputIds
        .Select((id, slot) => (id, slot))
        .ToImmutableDictionary(x => x.id, x => x.slot, StringComparer.Ordinal);

    internal static int GetRequiredSlot(string id) => Slots[id];

    internal static bool TryGetBlendShapeSlot(string name, out int slot) =>
        Slots.TryGetValue($"BlendShape.{name}Percent", out slot);

    private static ImmutableArray<TrackingInputDefinition> CreateDefinitions()
    {
        var result = ImmutableArray.CreateBuilder<TrackingInputDefinition>(64);
        Add("Head.EulerYDegrees", "Head", TrackingInputUnit.Degrees, -180, 180);
        Add("Head.EulerXDegrees", "Head", TrackingInputUnit.Degrees, -180, 180);
        Add("BlendShape.jawOpenPercent", "Mouth", TrackingInputUnit.Percent, 0, 100);
        Add("Head.EulerZDegrees", "Head", TrackingInputUnit.Degrees, -180, 180);
        Add("Head.PositionX", "Head", TrackingInputUnit.Position, -100, 100);
        Add("Head.PositionY", "Head", TrackingInputUnit.Position, -100, 100);
        Add("Head.PositionZ", "Head", TrackingInputUnit.Position, -100, 100);
        foreach (string side in new[] { "Left", "Right" })
        {
            Add($"Eye.{side}.EulerXDegrees", "Eyes", TrackingInputUnit.Degrees, -180, 180);
            Add($"Eye.{side}.EulerYDegrees", "Eyes", TrackingInputUnit.Degrees, -180, 180);
            Add($"Eye.{side}.EulerZDegrees", "Eyes", TrackingInputUnit.Degrees, -180, 180);
        }

        foreach (string name in BlendShapes.Skip(1))
        {
            Add($"BlendShape.{name}Percent", Category(name), TrackingInputUnit.Percent, 0, 100);
        }

        return result.MoveToImmutable();

        void Add(string id, string category, TrackingInputUnit unit, double minimum, double maximum) =>
            result.Add(new TrackingInputDefinition(
                id, category, $"Tracking.Input.{id}", unit, minimum, maximum));
    }

    private static string Category(string name) => name.StartsWith("eye", StringComparison.Ordinal)
        ? "Eyes"
        : name.StartsWith("brow", StringComparison.Ordinal)
            ? "Brows"
            : name.StartsWith("mouth", StringComparison.Ordinal) || name.StartsWith("jaw", StringComparison.Ordinal)
                ? "Mouth"
                : "Face";
}
