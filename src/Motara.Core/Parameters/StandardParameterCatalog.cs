using System.Collections.Immutable;

namespace Motara.Core.Parameters;

/// <summary>Defines Motara's stable built-in parameters derived from the Cubism standard list.</summary>
public static class StandardParameterCatalog
{
    /// <summary>Gets built-in definitions in deterministic slot order.</summary>
    public static ImmutableArray<ParameterDefinition> Definitions { get; } =
    [
        Rotation("AngleX", 30),
        Rotation("AngleY", 30),
        Rotation("AngleZ", 30),
        Scalar("EyeLOpen", 1, 0, 1),
        Scalar("EyeLSmile", 0, 0, 1),
        Scalar("EyeROpen", 1, 0, 1),
        Scalar("EyeRSmile", 0, 0, 1),
        Scalar("EyeLSquint", 0, 0, 1),
        Scalar("EyeRSquint", 0, 0, 1),
        Scalar("EyeBallX", 0, -1, 1),
        Scalar("EyeBallY", 0, -1, 1),
        Scalar("EyeBallForm", 0, -1, 1),
        Scalar("BrowLY", 0, -1, 1),
        Scalar("BrowRY", 0, -1, 1),
        Scalar("BrowLX", 0, -1, 1),
        Scalar("BrowRX", 0, -1, 1),
        Scalar("BrowLAngle", 0, -1, 1),
        Scalar("BrowRAngle", 0, -1, 1),
        Scalar("BrowLForm", 0, -1, 1),
        Scalar("BrowRForm", 0, -1, 1),
        Scalar("BrowInnerUp", 0, 0, 1),
        Scalar("MouthForm", 0, -1, 1),
        Scalar("MouthOpenY", 0, 0, 1),
        Scalar("TongueOut", 0, 0, 1),
        Scalar("MouthShrug", 0, 0, 1),
        Scalar("MouthFunnel", 0, 0, 1),
        Scalar("CheekPuff", 0, 0, 1),
        Scalar("JawOpen", 0, 0, 1),
        Scalar("MouthX", 0, -1, 1),
        Scalar("MouthPressLipOpen", 0, -1, 1),
        Scalar("Cheek", 0, 0, 1),
        Rotation("BodyAngleX", 10),
        Rotation("BodyAngleY", 10),
        Rotation("BodyAngleZ", 10),
        Scalar("Breath", 0, 0, 1),
        Rotation("ArmLA", 30),
        Rotation("ArmRA", 30),
        Rotation("ArmLB", 30),
        Rotation("ArmRB", 30),
        Scalar("HandL", 0, -10, 10),
        Scalar("HandR", 0, -10, 10),
        Scalar("HairFront", 0, -1, 1),
        Scalar("HairSide", 0, -1, 1),
        Scalar("HairBack", 0, -1, 1),
        Scalar("HairFluffy", 0, -1, 1),
        Scalar("ShoulderY", 0, -10, 10),
        Scalar("BustX", 0, -1, 1),
        Scalar("BustY", 0, -1, 1),
        Scalar("BaseX", 0, -10, 10),
        Scalar("BaseY", 0, -10, 10),
    ];

    /// <summary>Gets the immutable built-in registry shared by standard sessions.</summary>
    public static ParameterRegistry Registry { get; } = ParameterRegistry.Create(Definitions);

    private static ParameterDefinition Rotation(string id, double maximumDegrees) => new(
        id,
        NeutralValue: 0,
        SuggestedMinimum: -maximumDegrees,
        SuggestedMaximum: maximumDegrees,
        DisplayNameResourceKey: $"Parameter.Standard.{id}");

    private static ParameterDefinition Scalar(
        string id,
        double neutral,
        double minimum,
        double maximum) => new(
            id,
            neutral,
            minimum,
            maximum,
            DisplayNameResourceKey: $"Parameter.Standard.{id}");
}
