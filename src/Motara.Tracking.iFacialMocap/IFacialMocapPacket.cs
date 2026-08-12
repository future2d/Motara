using System.Collections.Immutable;

namespace Motara.Tracking.iFacialMocap;

/// <summary>Contains one three-axis Euler-angle sample in degrees.</summary>
public sealed record IFacialMocapEulerAngles(double X, double Y, double Z);

/// <summary>Contains the iFacialMocap head rotation and translation values.</summary>
public sealed record IFacialMocapHeadPose(
    double EulerX,
    double EulerY,
    double EulerZ,
    double PositionX,
    double PositionY,
    double PositionZ);

/// <summary>Contains one validated protocol packet without applying model-specific mapping.</summary>
public sealed record IFacialMocapPacket(
    ImmutableDictionary<string, double> BlendShapes,
    IFacialMocapHeadPose? Head,
    IFacialMocapEulerAngles? RightEye,
    IFacialMocapEulerAngles? LeftEye);
