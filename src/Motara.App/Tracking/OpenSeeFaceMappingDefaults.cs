using Motara.Core.Formulas;
using Motara.Core.Parameters;
using Motara.Tracking.Abstractions;

namespace Motara.App.Tracking;

internal static class OpenSeeFaceMappingDefaults
{
    internal static IReadOnlyList<TrackingInputDefinition> Inputs => OpenSeeFaceInputSchema.Definitions;

    internal static SourceMappingProfileDocument CreateProfile() =>
        SourceMappingProfileDocument.Create(
            "openseeface-standard-v2",
            "openseeface",
            "openseeface",
            "openseeface",
            "face",
            OpenSeeFaceInputSchema.InputIds,
            [
                Rotation("AngleX", "Head.EulerYDegrees", 30),
                Rotation("AngleY", "Head.EulerXDegrees", 30),
                Rotation("AngleZ", "-Head.EulerZDegrees", 30),
                Scalar("EyeLOpen", "clamp(Eye.Left.Open, 0, 1)", 1, 0, 1),
                Scalar("EyeROpen", "clamp(Eye.Right.Open, 0, 1)", 1, 0, 1),
                Scalar("EyeLSmile", "clamp(-Feature.EyeLeft, 0, 1)", 0, 0, 1),
                Scalar("EyeRSmile", "clamp(-Feature.EyeRight, 0, 1)", 0, 0, 1),
                Scalar("EyeLSquint", "clamp(1 - Eye.Left.Open, 0, 1)", 0, 0, 1),
                Scalar("EyeRSquint", "clamp(1 - Eye.Right.Open, 0, 1)", 0, 0, 1),
                Scalar("BrowLY", "clamp(Feature.EyebrowUpDownLeft, -1, 1)", 0, -1, 1),
                Scalar("BrowRY", "clamp(Feature.EyebrowUpDownRight, -1, 1)", 0, -1, 1),
                Scalar("BrowLX", "clamp(Feature.EyebrowQuirkLeft, -1, 1)", 0, -1, 1),
                Scalar("BrowRX", "clamp(Feature.EyebrowQuirkRight, -1, 1)", 0, -1, 1),
                Scalar("BrowLAngle", "clamp(Feature.EyebrowSteepnessLeft, -1, 1)", 0, -1, 1),
                Scalar("BrowRAngle", "clamp(Feature.EyebrowSteepnessRight, -1, 1)", 0, -1, 1),
                Scalar("MouthForm", "clamp((Feature.MouthCornerUpDownLeft + Feature.MouthCornerUpDownRight) / 2, -1, 1)", 0, -1, 1),
                Scalar("MouthOpenY", "clamp(Feature.MouthOpen, 0, 1)", 0, 0, 1),
                Scalar("MouthFunnel", "clamp(-Feature.MouthWide, 0, 1)", 0, 0, 1),
                Scalar("JawOpen", "clamp(Feature.MouthOpen, 0, 1)", 0, 0, 1),
                Scalar("MouthPressLipOpen", "clamp(Feature.MouthWide, -1, 1)", 0, -1, 1),
                Rotation("BodyAngleX", "-Head.EulerYDegrees * 0.5", 10),
                Rotation("BodyAngleY", "Head.EulerXDegrees * 0.5", 10),
                Rotation("BodyAngleZ", "-Head.EulerZDegrees * 0.5", 10),
            ]);

    private static SourceMappingOutputDocument Rotation(
        string parameterId,
        string formula,
        double maximumDegrees) => Output(
        parameterId,
        formula,
        neutralValue: 0,
        suggestedMinimum: -maximumDegrees,
        suggestedMaximum: maximumDegrees);

    private static SourceMappingOutputDocument Scalar(
        string parameterId,
        string formula,
        double neutralValue,
        double suggestedMinimum,
        double suggestedMaximum) => Output(
        parameterId,
        formula,
        neutralValue,
        suggestedMinimum,
        suggestedMaximum);

    private static SourceMappingOutputDocument Output(
        string parameterId,
        string formula,
        double neutralValue,
        double suggestedMinimum,
        double suggestedMaximum) => new(
        parameterId,
        Subtitle: null,
        formula,
        neutralValue,
        suggestedMinimum,
        suggestedMaximum,
        Smoothing(parameterId));

    private static double Smoothing(string parameterId) => parameterId switch
    {
        "AngleX" => 0.41,
        "AngleY" => 0.49,
        "AngleZ" => 0.48,
        "MouthOpenY" or "JawOpen" => 0.66,
        "MouthForm" or "MouthFunnel" or "MouthPressLipOpen" => 0.64,
        "EyeLOpen" or "EyeROpen" or "EyeLSmile" or "EyeRSmile" or "EyeLSquint" or "EyeRSquint" => 0.32,
        "BodyAngleX" => 0.21,
        "BodyAngleY" => 0.15,
        "BodyAngleZ" => 0.17,
        _ => 0,
    };
}
