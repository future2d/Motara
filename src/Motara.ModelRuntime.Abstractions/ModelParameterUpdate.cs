using System.Collections.Immutable;

namespace Motara.ModelRuntime.Abstractions;

public readonly record struct ModelParameterValue
{
    public ModelParameterValue(int parameterIndex, double value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(parameterIndex);
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        ParameterIndex = parameterIndex;
        Value = value;
    }

    public int ParameterIndex { get; }

    public double Value { get; }
}

public sealed class ModelParameterUpdate
{
    public ModelParameterUpdate(long sequence, ReadOnlySpan<ModelParameterValue> values)
        : this(sequence, values, ReadOnlySpan<ModelPartOpacity>.Empty)
    {
    }

    public ModelParameterUpdate(
        long sequence,
        ReadOnlySpan<ModelParameterValue> values,
        ReadOnlySpan<ModelPartOpacity> partOpacities)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        Sequence = sequence;
        Values = ImmutableArray.CreateRange(values.ToArray());
        PartOpacities = ImmutableArray.CreateRange(partOpacities.ToArray());
    }

    public long Sequence { get; }

    public ImmutableArray<ModelParameterValue> Values { get; }

    public ImmutableArray<ModelPartOpacity> PartOpacities { get; }
}
