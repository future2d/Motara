namespace Motara.App.Parameters;

internal enum ParameterProviderKind
{
    Default,
    AutoBreath,
    AutoBlink,
    IdleAnimation,
    Tracking,
    OneShotAnimation,
    Expression,
    Physics,
}

internal readonly record struct ParameterContribution(
    int ParameterIndex,
    double Value,
    ParameterProviderKind Provider);

internal readonly record struct ResolvedParameterValue(
    double Value,
    ParameterProviderKind Provider);
