using Motara.Core.Formulas;
using Motara.Tracking.Abstractions;

namespace Motara.Tracking.iFacialMocap;

public static class IFacialMocapMappingDefaults
{
    public static IReadOnlyList<TrackingInputDefinition> Inputs => IFacialMocapInputSchema.Definitions;

    public static SourceMappingProfileDocument CreateProfile() =>
        SourceMappingProfileDocument.Create(
            "arkit-ifacialmocap",
            "apple",
            "arkit",
            "ifacialmocap",
            "face",
            IFacialMocapFormulaProfile.Program.InputIds,
            IFacialMocapFormulaProfile.Program.OutputDefinitions.Select(output =>
                new SourceMappingOutputDocument(
                    output.OutputId,
                    Subtitle: null,
                    IFacialMocapFormulaProfile.Expressions[output.OutputId],
                    output.NeutralValue,
                    output.SuggestedMinimum,
                    output.SuggestedMaximum,
                    DefaultSmoothing(output.OutputId))));

    private static double DefaultSmoothing(string id) => id switch
    {
        "AngleX" => 0.41,
        "AngleY" => 0.49,
        "AngleZ" => 0.48,
        "EyeBallX" or "EyeBallY" => 0.32,
        "MouthOpenY" => 0.66,
        "MouthForm" => 0.64,
        "BodyAngleX" => 0.21,
        "BodyAngleY" => 0.15,
        "BodyAngleZ" => 0.17,
        _ => 0,
    };

}
