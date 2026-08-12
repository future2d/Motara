using System.Numerics;
using Motara.Persistence;

namespace Motara.App.Models;

internal enum PhysicsCalculationFrameRate
{
    FollowApplication = 0,
    FramesPerSecond30 = 1,
    FramesPerSecond60 = 2,
    FramesPerSecond120 = 3,
}

internal sealed record ModelPhysicsConfiguration(
    bool Enabled = true,
    double Strength = 50,
    double WindSimulation = 0,
    double DragPhysics = 10,
    PhysicsCalculationFrameRate CalculationFrameRate = PhysicsCalculationFrameRate.FollowApplication,
    bool MotionExpansionEnabled = true,
    double MotionExpansionX = 5,
    double MotionExpansionY = 5,
    double MotionExpansionZ = 5)
{
    private const double WindPeriodSeconds = 8d;
    private const float MaximumWindForce = 0.3f;
    private const float MaximumDragAngleDegrees = 90f;

    internal static readonly ModelPhysicsConfiguration Default = new();

    internal static readonly ModelPhysicsConfiguration Disabled = new(false);

    internal Vector2 ResolveWind(TimeSpan elapsed)
    {
        double phase = elapsed.TotalSeconds / WindPeriodSeconds * Math.Tau;
        double direction = Math.Sin(phase);
        double gustEnvelope = 0.7d + 0.3d * Math.Pow(Math.Sin(phase * 3d), 2d);
        float force = (float)(direction * gustEnvelope * (WindSimulation / 100d))
            * MaximumWindForce;
        return new Vector2(force, 0);
    }

    internal Vector2 ResolveDragParameterOffset(Vector2 normalizedDisplacement)
    {
        if (!float.IsFinite(normalizedDisplacement.X)
            || !float.IsFinite(normalizedDisplacement.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(normalizedDisplacement));
        }

        Vector2 bounded = Vector2.Clamp(normalizedDisplacement, -Vector2.One, Vector2.One);
        float angleScale = (float)(DragPhysics / 100d) * MaximumDragAngleDegrees;
        return new Vector2(bounded.X, -bounded.Y) * angleScale;
    }

    internal double ResolveStrength() => Strength / 100d;

    internal int ResolveCalculationFramesPerSecond(FrameRateMode applicationFrameRate) =>
        CalculationFrameRate switch
        {
            PhysicsCalculationFrameRate.FramesPerSecond30 => 30,
            PhysicsCalculationFrameRate.FramesPerSecond60 => 60,
            PhysicsCalculationFrameRate.FramesPerSecond120 => 120,
            PhysicsCalculationFrameRate.FollowApplication => applicationFrameRate switch
            {
                FrameRateMode.FramesPerSecond30 or FrameRateMode.VSyncHalf => 30,
                _ => 60,
            },
            _ => throw new InvalidOperationException("Unknown physics calculation frame rate."),
        };

    internal void Validate()
    {
        if (!Enum.IsDefined(CalculationFrameRate)
            || !double.IsFinite(Strength) || !double.IsFinite(WindSimulation) || !double.IsFinite(DragPhysics)
            || !double.IsFinite(MotionExpansionX) || !double.IsFinite(MotionExpansionY)
            || !double.IsFinite(MotionExpansionZ)
            || Strength is < 0 or > 100
            || WindSimulation is < 0 or > 100
            || DragPhysics is < 0 or > 100
            || MotionExpansionX is < 0 or > 20
            || MotionExpansionY is < 0 or > 20
            || MotionExpansionZ is < 0 or > 20
            || !IsInteger(Strength)
            || !IsInteger(WindSimulation)
            || !IsInteger(DragPhysics)
            || !IsInteger(MotionExpansionX)
            || !IsInteger(MotionExpansionY)
            || !IsInteger(MotionExpansionZ))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Strength),
                "Physics controls must be finite and within [0, 100].");
        }
    }

    private static bool IsInteger(double value) => value == Math.Truncate(value);
}
