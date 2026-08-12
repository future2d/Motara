using System.Collections.Immutable;
namespace Motara.Core.Formulas;

public sealed record SourceFormulaDefinition(
    string OutputId,
    string Expression,
    double NeutralValue,
    double SuggestedMinimum,
    double SuggestedMaximum,
    double Smoothing = 0);

public sealed class SourceFormulaProfile
{
    private SourceFormulaProfile(
        string sourceId,
        ImmutableArray<string> inputIds,
        ImmutableArray<SourceFormulaDefinition> outputs)
    {
        SourceId = sourceId;
        InputIds = inputIds;
        Outputs = outputs;
    }

    public string SourceId { get; }

    public ImmutableArray<string> InputIds { get; }

    public ImmutableArray<SourceFormulaDefinition> Outputs { get; }

    public static SourceFormulaProfile Create(
        string sourceId,
        IEnumerable<string> inputIds,
        IEnumerable<SourceFormulaDefinition> outputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(inputIds);
        ArgumentNullException.ThrowIfNull(outputs);
        return new SourceFormulaProfile(
            sourceId,
            ImmutableArray.CreateRange(inputIds),
            ImmutableArray.CreateRange(outputs));
    }
}

public enum SourceFormulaErrorCode
{
    InvalidDefinition = 0,
    DuplicateIdentifier = 1,
    UnknownReference = 2,
    CyclicDependency = 3,
    Syntax = 4,
    UnsupportedFunction = 5,
    ComplexityLimit = 6,
}

public sealed class SourceFormulaCompilationException : Exception
{
    internal SourceFormulaCompilationException(
        SourceFormulaErrorCode code,
        string message,
        int start,
        int length)
        : base(message)
    {
        Code = code;
        Start = start;
        Length = length;
    }

    public SourceFormulaErrorCode Code { get; }

    public int Start { get; }

    public int Length { get; }
}

public sealed record SourceFormulaEvaluation(
    ImmutableArray<double> Values,
    ImmutableArray<Motara.Tracking.Abstractions.ParameterValidity> Validity);
