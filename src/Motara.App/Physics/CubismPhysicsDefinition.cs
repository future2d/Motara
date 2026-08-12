using System.Collections.Immutable;
using System.Numerics;

namespace Motara.App.Physics;

internal sealed record CubismPhysicsDefinition(
    ImmutableArray<CubismPhysicsSettingDefinition> Settings,
    Vector2 Gravity,
    Vector2 Wind);

internal sealed record CubismPhysicsSettingDefinition(
    CubismPhysicsNormalization PositionNormalization,
    CubismPhysicsNormalization AngleNormalization,
    ImmutableArray<CubismPhysicsInputDefinition> Inputs,
    ImmutableArray<CubismPhysicsOutputDefinition> Outputs,
    ImmutableArray<CubismPhysicsParticleDefinition> Vertices);

internal sealed record CubismPhysicsNormalization(
    double Minimum,
    double Default,
    double Maximum);

internal sealed record CubismPhysicsInputDefinition(
    string SourceId,
    double Weight,
    CubismPhysicsValueType Type,
    bool Reflect);

internal sealed record CubismPhysicsOutputDefinition(
    string DestinationId,
    int VertexIndex,
    double Scale,
    double Weight,
    CubismPhysicsValueType Type,
    bool Reflect);

internal sealed record CubismPhysicsParticleDefinition(
    Vector2 Position,
    double Mobility,
    double Delay,
    double Acceleration,
    double Radius);

internal enum CubismPhysicsValueType
{
    X,
    Y,
    Angle,
}
