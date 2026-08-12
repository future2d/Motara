namespace Motara.Core.Parameters;

/// <summary>Defines one stable Motara parameter slot.</summary>
public sealed record ParameterDefinition(
    string Id,
    double NeutralValue,
    double SuggestedMinimum,
    double SuggestedMaximum,
    string? DisplayNameResourceKey = null,
    ParameterDefinitionOrigin Origin = ParameterDefinitionOrigin.BuiltIn,
    bool IsWritable = true);
