using System.Collections.Immutable;

namespace Motara.Output.Abstractions;

/// <summary>Represents one immutable batch of resolved parameter values for an output target.</summary>
public sealed class OutputParameterFrame
{
    public OutputParameterFrame(long sequence, IEnumerable<OutputParameterValue> values)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentNullException.ThrowIfNull(values);
        Values = values.ToImmutableArray();
        if (Values.IsDefaultOrEmpty)
        {
            throw new ArgumentException("An output parameter frame must contain at least one value.", nameof(values));
        }

        if (Values.Select(static value => value.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Values.Length)
        {
            throw new ArgumentException("Output parameter identifiers must be unique.", nameof(values));
        }

        Sequence = sequence;
    }

    public long Sequence { get; }

    public ImmutableArray<OutputParameterValue> Values { get; }
}

/// <summary>Identifies one finite resolved parameter value by its target parameter identifier.</summary>
public readonly record struct OutputParameterValue
{
    public OutputParameterValue(string id, double value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Id = id;
        Value = value;
    }

    public string Id { get; }

    public double Value { get; }
}

/// <summary>Accepts the latest resolved model-parameter frame for one asynchronous output target.</summary>
public interface IOutputParameterPublisher
{
    bool IsActive { get; }

    event EventHandler? ActivityChanged;

    void PublishFrame(OutputParameterFrame frame);
}
