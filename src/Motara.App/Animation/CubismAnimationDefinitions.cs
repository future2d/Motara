using System.Collections.Immutable;
using Motara.ModelRuntime.Abstractions;

namespace Motara.App.Animation;

internal sealed record CubismAnimationSet(
    ImmutableArray<CubismMotionClip> Clips,
    ImmutableArray<CubismExpression> Expressions,
    ImmutableArray<CubismPoseGroup> PoseGroups,
    ImmutableArray<CubismAnimationDiagnostic> Diagnostics);

internal sealed record CubismAnimationDiagnostic(
    string AssetId,
    ModelAuxiliaryAssetKind Kind,
    string Reason);

internal sealed record CubismMotionClip(
    ModelAuxiliaryAsset Asset,
    double Duration,
    bool Loop,
    double FadeInTime,
    double FadeOutTime,
    ImmutableArray<CubismAnimationCurve> Curves);

internal enum CubismAnimationCurveTarget
{
    Parameter,
    PartOpacity,
}

internal sealed class CubismAnimationCurve
{
    internal CubismAnimationCurve(
        CubismAnimationCurveTarget target,
        string targetId,
        int parameterIndex,
        double? fadeInTime,
        double? fadeOutTime,
        ImmutableArray<CubismAnimationSegment> segments)
    {
        Target = target;
        TargetId = targetId;
        ParameterIndex = parameterIndex;
        FadeInTime = fadeInTime;
        FadeOutTime = fadeOutTime;
        Segments = segments;
    }

    internal CubismAnimationCurveTarget Target { get; }

    internal string TargetId { get; }

    internal int ParameterIndex { get; }

    internal double? FadeInTime { get; }

    internal double? FadeOutTime { get; }

    internal ImmutableArray<CubismAnimationSegment> Segments { get; }

    internal double Evaluate(double time)
    {
        if (!double.IsFinite(time))
        {
            throw new ArgumentOutOfRangeException(nameof(time));
        }

        if (Segments.IsDefaultOrEmpty || time <= Segments[0].StartTime)
        {
            return Segments.IsDefaultOrEmpty ? 0 : Segments[0].StartValue;
        }

        foreach (CubismAnimationSegment segment in Segments)
        {
            if (time <= segment.EndTime)
            {
                return segment.Evaluate(time);
            }
        }

        return Segments[^1].EndValue;
    }
}

internal enum CubismAnimationSegmentKind
{
    Linear,
    Bezier,
    Stepped,
    InverseStepped,
}

internal readonly record struct CubismAnimationSegment(
    CubismAnimationSegmentKind Kind,
    double StartTime,
    double StartValue,
    double ControlPoint1Time,
    double ControlPoint1Value,
    double ControlPoint2Time,
    double ControlPoint2Value,
    double EndTime,
    double EndValue)
{
    internal double Evaluate(double time)
    {
        double duration = EndTime - StartTime;
        if (duration <= 0)
        {
            return EndValue;
        }

        return Kind switch
        {
            CubismAnimationSegmentKind.Linear => Interpolate(
                StartValue,
                EndValue,
                (time - StartTime) / duration),
            CubismAnimationSegmentKind.Bezier => EvaluateBezier(time),
            CubismAnimationSegmentKind.Stepped => StartValue,
            CubismAnimationSegmentKind.InverseStepped => EndValue,
            _ => throw new InvalidOperationException("The Cubism animation segment kind is unsupported."),
        };
    }

    private double EvaluateBezier(double time)
    {
        double lower = 0;
        double upper = 1;
        for (int iteration = 0; iteration < 40; iteration++)
        {
            double candidate = (lower + upper) / 2d;
            if (Cubic(
                StartTime,
                ControlPoint1Time,
                ControlPoint2Time,
                EndTime,
                candidate) < time)
            {
                lower = candidate;
            }
            else
            {
                upper = candidate;
            }
        }

        return Cubic(
            StartValue,
            ControlPoint1Value,
            ControlPoint2Value,
            EndValue,
            (lower + upper) / 2d);
    }

    private static double Interpolate(double start, double end, double amount) =>
        start + (end - start) * Math.Clamp(amount, 0, 1);

    private static double Cubic(double p0, double p1, double p2, double p3, double time)
    {
        double inverse = 1d - time;
        return inverse * inverse * inverse * p0
            + 3d * inverse * inverse * time * p1
            + 3d * inverse * time * time * p2
            + time * time * time * p3;
    }
}

internal sealed record CubismExpression(
    ModelAuxiliaryAsset Asset,
    double FadeInTime,
    ImmutableArray<CubismExpressionParameter> Parameters);

internal sealed record CubismExpressionParameter(
    string ParameterId,
    int ParameterIndex,
    double Value,
    CubismExpressionBlendMode Blend);

internal enum CubismExpressionBlendMode
{
    Add,
    Multiply,
    Overwrite,
}

internal sealed record CubismPoseGroup(ImmutableArray<CubismPosePart> Parts);

internal sealed record CubismPosePart(string PartId, ImmutableArray<string> Links);
