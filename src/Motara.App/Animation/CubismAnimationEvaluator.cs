using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Parameters;
using Motara.App.Models;
using Motara.ModelRuntime.Abstractions;
using Motara.Tracking.Abstractions;

namespace Motara.App.Animation;

internal sealed record CubismAnimationFrame(
    ImmutableArray<ParameterContribution> Contributions,
    ImmutableArray<ModelPartOpacity> PartOpacities,
    bool IsActive);

internal sealed class CubismAnimationEvaluator
{
    private readonly CubismAnimationSet definitions;
    private CubismMotionClip? idle;
    private CubismMotionClip? regularIdle;
    private CubismMotionClip? lostTrackingIdle;
    private readonly ILogger logger;
    private CubismMotionClip? activeMotion;
    private CubismExpression? activeExpression;
    private TimeSpan motionElapsed;
    private TimeSpan expressionElapsed;
    private TrackingPresence trackingPresence;
    private bool activeMotionIsExplicit;

    internal CubismAnimationEvaluator(
        CubismAnimationSet definitions,
        ILogger? logger = null)
    {
        this.definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        this.logger = logger ?? NullLogger.Instance;
        regularIdle = definitions.Clips.FirstOrDefault(static clip => string.Equals(
            clip.Asset.Group,
            "Idle",
            StringComparison.OrdinalIgnoreCase));
        lostTrackingIdle = regularIdle;
        idle = regularIdle;
        activeMotion = idle;
        if (idle is not null)
        {
            CubismAnimationLog.IdleSelected(this.logger, idle.Asset.Name);
        }
    }

    internal void ConfigureIdle(
        ModelIdleMotionSelection regularSelection,
        ModelLostTrackingIdleMotionSelection lostSelection)
    {
        ArgumentNullException.ThrowIfNull(regularSelection);
        ArgumentNullException.ThrowIfNull(lostSelection);
        regularSelection.Validate();
        lostSelection.Validate();
        regularIdle = ResolveRegularIdle(regularSelection);
        lostTrackingIdle = lostSelection.Mode switch
        {
            ModelLostTrackingIdleMotionMode.UseRegularIdle => regularIdle,
            ModelLostTrackingIdleMotionMode.None => null,
            ModelLostTrackingIdleMotionMode.Asset => FindClip(lostSelection.AssetId!),
            _ => throw new ArgumentOutOfRangeException(nameof(lostSelection)),
        };
        idle = ResolveIdleForPresence();
        if (!activeMotionIsExplicit)
        {
            activeMotion = idle;
            motionElapsed = TimeSpan.Zero;
        }
    }

    internal void SetTrackingPresence(TrackingPresence presence)
    {
        if (presence == TrackingPresence.Unknown || presence == trackingPresence)
        {
            return;
        }

        trackingPresence = presence;
        idle = ResolveIdleForPresence();
        if (!activeMotionIsExplicit)
        {
            activeMotion = idle;
            motionElapsed = TimeSpan.Zero;
        }
        CubismAnimationLog.TrackingPresenceChanged(logger, presence, idle?.Asset.Name ?? string.Empty);
    }

    internal void Play(string group, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        CubismMotionClip? selected = definitions.Clips.FirstOrDefault(clip =>
            string.Equals(clip.Asset.Group, group, StringComparison.OrdinalIgnoreCase)
            && string.Equals(clip.Asset.Name, name, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            throw new ArgumentException("The requested Cubism motion does not exist.", nameof(name));
        }

        activeMotion = selected;
        activeMotionIsExplicit = true;
        motionElapsed = TimeSpan.Zero;
        CubismAnimationLog.MotionStarted(logger, selected.Asset.Group!, selected.Asset.Name);
    }

    internal void PlayAsset(string assetId)
    {
        CubismMotionClip selected = definitions.Clips.FirstOrDefault(clip =>
            StringComparer.Ordinal.Equals(clip.Asset.AssetId, assetId))
            ?? throw new ArgumentException("The requested Cubism motion does not exist.", nameof(assetId));
        activeMotion = selected;
        activeMotionIsExplicit = true;
        motionElapsed = TimeSpan.Zero;
        CubismAnimationLog.MotionStarted(logger, selected.Asset.Group ?? string.Empty, selected.Asset.Name);
    }

    internal void SetIdleAsset(string assetId)
    {
        regularIdle = FindClip(assetId);
        if (trackingPresence != TrackingPresence.Lost)
        {
            idle = regularIdle;
        }
        activeMotion = idle;
        activeMotionIsExplicit = false;
        motionElapsed = TimeSpan.Zero;
        CubismAnimationLog.IdleSelected(logger, regularIdle.Asset.Name);
    }

    internal void ClearIdle()
    {
        regularIdle = null;
        if (trackingPresence != TrackingPresence.Lost)
        {
            idle = null;
        }
        activeMotion = idle;
        activeMotionIsExplicit = false;
        motionElapsed = TimeSpan.Zero;
        CubismAnimationLog.IdleSelected(logger, string.Empty);
    }

    internal void StopMotion()
    {
        if (activeMotion is null || ReferenceEquals(activeMotion, idle))
        {
            return;
        }

        activeMotion = idle;
        activeMotionIsExplicit = false;
        motionElapsed = TimeSpan.Zero;
        CubismAnimationLog.MotionStopped(logger);
    }

    internal void SetExpression(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        CubismExpression? selected = definitions.Expressions.FirstOrDefault(expression =>
            string.Equals(expression.Asset.Name, name, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            throw new ArgumentException("The requested Cubism expression does not exist.", nameof(name));
        }

        activeExpression = selected;
        expressionElapsed = TimeSpan.Zero;
        CubismAnimationLog.ExpressionSet(logger, selected.Asset.Name);
    }

    internal void SetExpressionAsset(string assetId)
    {
        CubismExpression selected = definitions.Expressions.FirstOrDefault(expression =>
            StringComparer.Ordinal.Equals(expression.Asset.AssetId, assetId))
            ?? throw new ArgumentException("The requested Cubism expression does not exist.", nameof(assetId));
        activeExpression = selected;
        expressionElapsed = TimeSpan.Zero;
        CubismAnimationLog.ExpressionSet(logger, selected.Asset.Name);
    }

    internal void ToggleExpressionAsset(string assetId)
    {
        CubismExpression selected = definitions.Expressions.FirstOrDefault(expression =>
            StringComparer.Ordinal.Equals(expression.Asset.AssetId, assetId))
            ?? throw new ArgumentException("The requested Cubism expression does not exist.", nameof(assetId));
        if (ReferenceEquals(activeExpression, selected))
        {
            ClearExpression();
            return;
        }
        activeExpression = selected;
        expressionElapsed = TimeSpan.Zero;
        CubismAnimationLog.ExpressionSet(logger, selected.Asset.Name);
    }

    internal void ClearExpression()
    {
        if (activeExpression is null)
        {
            return;
        }

        activeExpression = null;
        expressionElapsed = TimeSpan.Zero;
        CubismAnimationLog.ExpressionCleared(logger);
    }

    internal CubismAnimationFrame Advance(TimeSpan elapsed, ReadOnlySpan<double> baseline)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);

        foreach (double value in baseline)
        {
            if (!double.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(baseline));
            }
        }

        double[] values = baseline.ToArray();
        var motionContributions = new Dictionary<int, ParameterContribution>();
        var expressionContributions = new Dictionary<int, ParameterContribution>();
        var partOpacities = new Dictionary<string, ModelPartOpacity>(StringComparer.Ordinal);

        if (activeMotion is { } motion)
        {
            motionElapsed += elapsed;
            double sampleTime = GetSampleTime(motion, motionElapsed);
            double weight = GetMotionWeight(motion, sampleTime);
            ParameterProviderKind provider = ReferenceEquals(motion, idle)
                ? ParameterProviderKind.IdleAnimation
                : ParameterProviderKind.OneShotAnimation;
            foreach (CubismAnimationCurve curve in motion.Curves)
            {
                double sampled = curve.Evaluate(sampleTime);
                switch (curve.Target)
                {
                    case CubismAnimationCurveTarget.Parameter:
                        EnsureParameterIndex(curve.ParameterIndex, values.Length);
                        double value = Interpolate(values[curve.ParameterIndex], sampled, weight);
                        values[curve.ParameterIndex] = value;
                        motionContributions[curve.ParameterIndex] = new ParameterContribution(
                            curve.ParameterIndex,
                            value,
                            provider);
                        break;

                    case CubismAnimationCurveTarget.PartOpacity:
                        float opacity = (float)Math.Clamp(Interpolate(1, sampled, weight), 0, 1);
                        partOpacities[curve.TargetId] = new ModelPartOpacity(curve.TargetId, opacity);
                        break;

                    default:
                        throw new InvalidOperationException("The Cubism animation curve target is unsupported.");
                }
            }

            if (!motion.Loop && motionElapsed >= TimeSpan.FromSeconds(motion.Duration))
            {
                activeMotionIsExplicit = false;
                idle = ResolveIdleForPresence();
                activeMotion = idle;
                motionElapsed = TimeSpan.Zero;
                CubismAnimationLog.MotionCompleted(logger, motion.Asset.Name);
            }
        }

        if (activeExpression is { } expression)
        {
            expressionElapsed += elapsed;
            double weight = expression.FadeInTime <= 0
                ? 1
                : Math.Clamp(expressionElapsed.TotalSeconds / expression.FadeInTime, 0, 1);
            foreach (CubismExpressionParameter parameter in expression.Parameters)
            {
                EnsureParameterIndex(parameter.ParameterIndex, values.Length);
                double value = parameter.Blend switch
                {
                    CubismExpressionBlendMode.Add => values[parameter.ParameterIndex]
                        + parameter.Value * weight,
                    CubismExpressionBlendMode.Multiply => values[parameter.ParameterIndex]
                        * Interpolate(1, parameter.Value, weight),
                    CubismExpressionBlendMode.Overwrite => Interpolate(
                        values[parameter.ParameterIndex],
                        parameter.Value,
                        weight),
                    _ => throw new InvalidOperationException(
                        "The Cubism expression blend mode is unsupported."),
                };
                values[parameter.ParameterIndex] = value;
                expressionContributions[parameter.ParameterIndex] = new ParameterContribution(
                    parameter.ParameterIndex,
                    value,
                    ParameterProviderKind.Expression);
            }
        }

        foreach (CubismPoseGroup group in definitions.PoseGroups)
        {
            for (int index = 0; index < group.Parts.Length; index++)
            {
                float opacity = index == 0 ? 1 : 0;
                CubismPosePart part = group.Parts[index];
                partOpacities[part.PartId] = new ModelPartOpacity(part.PartId, opacity);
                foreach (string linkedPartId in part.Links)
                {
                    partOpacities[linkedPartId] = new ModelPartOpacity(linkedPartId, opacity);
                }
            }
        }

        ImmutableArray<ParameterContribution> contributions = motionContributions.Values
            .Concat(expressionContributions.Values)
            .ToImmutableArray();
        return new CubismAnimationFrame(
            contributions,
            partOpacities.Values.ToImmutableArray(),
            activeMotion is not null || activeExpression is not null || partOpacities.Count != 0);
    }

    private static double GetSampleTime(CubismMotionClip motion, TimeSpan elapsed)
    {
        double seconds = elapsed.TotalSeconds;
        return motion.Loop
            ? seconds % motion.Duration
            : Math.Min(seconds, motion.Duration);
    }

    private static double GetMotionWeight(CubismMotionClip motion, double sampleTime)
    {
        double fadeIn = motion.FadeInTime <= 0
            ? 1
            : Math.Clamp(sampleTime / motion.FadeInTime, 0, 1);
        if (motion.Loop || motion.FadeOutTime <= 0)
        {
            return fadeIn;
        }

        return Math.Min(
            fadeIn,
            Math.Clamp((motion.Duration - sampleTime) / motion.FadeOutTime, 0, 1));
    }

    private static void EnsureParameterIndex(int parameterIndex, int parameterCount)
    {
        if ((uint)parameterIndex >= (uint)parameterCount)
        {
            throw new ArgumentException("The Cubism animation parameter layout changed.", nameof(parameterIndex));
        }
    }

    private static double Interpolate(double start, double end, double weight) =>
        start + (end - start) * Math.Clamp(weight, 0, 1);

    private CubismMotionClip? ResolveRegularIdle(ModelIdleMotionSelection selection) => selection.Mode switch
    {
        ModelIdleMotionMode.Automatic => definitions.Clips.FirstOrDefault(static clip => string.Equals(
            clip.Asset.Group,
            "Idle",
            StringComparison.OrdinalIgnoreCase)),
        ModelIdleMotionMode.None => null,
        ModelIdleMotionMode.Asset => FindClip(selection.AssetId!),
        _ => throw new ArgumentOutOfRangeException(nameof(selection)),
    };

    private CubismMotionClip? ResolveIdleForPresence() =>
        trackingPresence == TrackingPresence.Lost ? lostTrackingIdle : regularIdle;

    private CubismMotionClip FindClip(string assetId) =>
        definitions.Clips.FirstOrDefault(clip => StringComparer.Ordinal.Equals(clip.Asset.AssetId, assetId))
        ?? throw new ArgumentException("The requested Cubism idle motion does not exist.", nameof(assetId));
}

internal static partial class CubismAnimationLog
{
    [LoggerMessage(6701, LogLevel.Information, "Cubism Idle motion selected: {MotionName}")]
    internal static partial void IdleSelected(ILogger logger, string motionName);

    [LoggerMessage(6702, LogLevel.Information, "Cubism motion started: {Group} / {MotionName}")]
    internal static partial void MotionStarted(ILogger logger, string group, string motionName);

    [LoggerMessage(6703, LogLevel.Information, "Cubism non-Idle motion stopped")]
    internal static partial void MotionStopped(ILogger logger);

    [LoggerMessage(6704, LogLevel.Information, "Cubism non-Idle motion completed: {MotionName}")]
    internal static partial void MotionCompleted(ILogger logger, string motionName);

    [LoggerMessage(6705, LogLevel.Information, "Cubism expression selected: {ExpressionName}")]
    internal static partial void ExpressionSet(ILogger logger, string expressionName);

    [LoggerMessage(6706, LogLevel.Information, "Cubism expression cleared")]
    internal static partial void ExpressionCleared(ILogger logger);

    [LoggerMessage(6707, LogLevel.Information,
        "Cubism tracking presence changed to {Presence}; active idle is {MotionName}")]
    internal static partial void TrackingPresenceChanged(
        ILogger logger,
        TrackingPresence presence,
        string motionName);
}
