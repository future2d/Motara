namespace Motara.App.Models;

internal sealed record ModelParameterMapping
{
    internal ModelParameterMapping(string sourceParameterId, string modelParameterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceParameterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelParameterId);
        SourceParameterId = sourceParameterId;
        ModelParameterId = modelParameterId;
    }

    internal string SourceParameterId { get; }

    internal string ModelParameterId { get; }
}

internal enum ModelParameterMappingIssueCode
{
    MissingSoftwareParameter = 0,
    MissingModelParameter = 1,
}

internal sealed record ModelParameterMappingIssue(
    ModelParameterMappingIssueCode Code,
    string SourceParameterId,
    string ModelParameterId);
