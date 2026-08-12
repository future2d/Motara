using Motara.Core.Formulas;
namespace Motara.Tracking.iFacialMocap;

internal static class IFacialMocapFormulaProfile
{
    private static readonly SourceFormulaProfile Profile = SourceFormulaProfile.Create(
            IFacialMocapTrackingSource.SourceId,
            IFacialMocapInputSchema.InputIds,
            [
                new SourceFormulaDefinition(
                    "AngleX",
                    "Head.EulerYDegrees",
                    0,
                    -30,
                    30),
                new SourceFormulaDefinition(
                    "AngleY",
                    "-Head.EulerXDegrees",
                    0,
                    -30,
                    30),
                new SourceFormulaDefinition(
                    "MouthOpenY",
                    "clamp(BlendShape.jawOpenPercent / 100, 0, 1)",
                    0,
                    0,
                    1),
                Rotation("AngleZ", "-Head.EulerZDegrees", 30),
                Scalar("EyeLOpen", "clamp(1 - BlendShape.eyeBlink_LPercent / 100 + BlendShape.eyeWide_LPercent / 100, 0, 1)", 1, 0, 1),
                Scalar("EyeROpen", "clamp(1 - BlendShape.eyeBlink_RPercent / 100 + BlendShape.eyeWide_RPercent / 100, 0, 1)", 1, 0, 1),
                Scalar("EyeLSmile", "clamp(BlendShape.eyeSquint_LPercent / 100, 0, 1)", 0, 0, 1),
                Scalar("EyeRSmile", "clamp(BlendShape.eyeSquint_RPercent / 100, 0, 1)", 0, 0, 1),
                Scalar("EyeLSquint", "clamp(BlendShape.eyeSquint_LPercent / 100, 0, 1)", 0, 0, 1),
                Scalar("EyeRSquint", "clamp(BlendShape.eyeSquint_RPercent / 100, 0, 1)", 0, 0, 1),
                Scalar("EyeBallX", "clamp((BlendShape.eyeLookOut_LPercent - BlendShape.eyeLookIn_LPercent + BlendShape.eyeLookIn_RPercent - BlendShape.eyeLookOut_RPercent) / 200, -1, 1)", 0, -1, 1),
                Scalar("EyeBallY", "clamp((BlendShape.eyeLookUp_LPercent - BlendShape.eyeLookDown_LPercent + BlendShape.eyeLookUp_RPercent - BlendShape.eyeLookDown_RPercent) / 200, -1, 1)", 0, -1, 1),
                Scalar("BrowLY", "clamp((BlendShape.browOuterUp_LPercent - BlendShape.browDown_LPercent) / 100, -1, 1)", 0, -1, 1),
                Scalar("BrowRY", "clamp((BlendShape.browOuterUp_RPercent - BlendShape.browDown_RPercent) / 100, -1, 1)", 0, -1, 1),
                Scalar("BrowLX", "clamp((BlendShape.browOuterUp_LPercent - BlendShape.browInnerUpPercent) / 100, -1, 1)", 0, -1, 1),
                Scalar("BrowRX", "clamp((BlendShape.browInnerUpPercent - BlendShape.browOuterUp_RPercent) / 100, -1, 1)", 0, -1, 1),
                Scalar("BrowLAngle", "clamp((BlendShape.browOuterUp_LPercent - BlendShape.browInnerUpPercent) / 100, -1, 1)", 0, -1, 1),
                Scalar("BrowRAngle", "clamp((BlendShape.browInnerUpPercent - BlendShape.browOuterUp_RPercent) / 100, -1, 1)", 0, -1, 1),
                Scalar("BrowLForm", "clamp((BlendShape.browOuterUp_LPercent + BlendShape.browInnerUpPercent - 2 * BlendShape.browDown_LPercent) / 200, -1, 1)", 0, -1, 1),
                Scalar("BrowRForm", "clamp((BlendShape.browOuterUp_RPercent + BlendShape.browInnerUpPercent - 2 * BlendShape.browDown_RPercent) / 200, -1, 1)", 0, -1, 1),
                Scalar("BrowInnerUp", "clamp(BlendShape.browInnerUpPercent / 100, 0, 1)", 0, 0, 1),
                Scalar("MouthForm", "clamp((BlendShape.mouthSmile_LPercent + BlendShape.mouthSmile_RPercent - BlendShape.mouthFrown_LPercent - BlendShape.mouthFrown_RPercent - BlendShape.mouthPuckerPercent) / 200, -1, 1)", 0, -1, 1),
                Scalar("TongueOut", "clamp(BlendShape.tongueOutPercent / 100, 0, 1)", 0, 0, 1),
                Scalar("MouthShrug", "clamp((BlendShape.mouthShrugLowerPercent + BlendShape.mouthShrugUpperPercent) / 200, 0, 1)", 0, 0, 1),
                Scalar("MouthFunnel", "clamp(BlendShape.mouthFunnelPercent / 100, 0, 1)", 0, 0, 1),
                Scalar("CheekPuff", "clamp(BlendShape.cheekPuffPercent / 100, 0, 1)", 0, 0, 1),
                Scalar("JawOpen", "clamp(BlendShape.jawOpenPercent / 100, 0, 1)", 0, 0, 1),
                Scalar("MouthX", "clamp((BlendShape.mouthLeftPercent - BlendShape.mouthRightPercent) / 100, -1, 1)", 0, -1, 1),
                Scalar("MouthPressLipOpen", "clamp((BlendShape.mouthPress_LPercent + BlendShape.mouthPress_RPercent) / 200, 0, 1)", 0, -1, 1),
                Scalar("Cheek", "clamp(BlendShape.cheekPuffPercent / 100, 0, 1)", 0, 0, 1),
                Rotation("BodyAngleX", "-Head.EulerYDegrees * 0.5", 10),
                Rotation("BodyAngleY", "-Head.EulerXDegrees * 0.5", 10),
                Rotation("BodyAngleZ", "-Head.EulerZDegrees * 0.5", 10),
            ]);

    internal static CompiledSourceFormulaProgram Program { get; } =
        SourceFormulaCompiler.Compile(Profile);

    internal static IReadOnlyDictionary<string, string> Expressions { get; } =
        Profile.Outputs.ToDictionary(output => output.OutputId, output => output.Expression, StringComparer.Ordinal);

    private static SourceFormulaDefinition Rotation(string id, string formula, double maximumDegrees) =>
        new(id, formula, 0, -maximumDegrees, maximumDegrees);

    private static SourceFormulaDefinition Scalar(
        string id, string formula, double neutral, double minimum, double maximum) =>
        new(id, formula, neutral, minimum, maximum);
}
