using System.Collections.Immutable;
using Motara.Tracking.Abstractions;

namespace Motara.Core.Formulas;

public sealed class CompiledSourceFormulaProgram
{
    private readonly ImmutableArray<CompiledFormula> formulas;
    private readonly ImmutableArray<int> evaluationOrder;

    internal CompiledSourceFormulaProgram(
        string sourceId,
        ImmutableArray<string> inputIds,
        ImmutableArray<SourceFormulaDefinition> outputDefinitions,
        ImmutableArray<CompiledFormula> formulas,
        ImmutableArray<int> evaluationOrder)
    {
        SourceId = sourceId;
        InputIds = inputIds;
        OutputDefinitions = outputDefinitions;
        this.formulas = formulas;
        this.evaluationOrder = evaluationOrder;
    }

    public string SourceId { get; }

    public ImmutableArray<string> InputIds { get; }

    public ImmutableArray<SourceFormulaDefinition> OutputDefinitions { get; }

    public SourceFormulaEvaluation Evaluate(
        ReadOnlySpan<double> inputValues,
        ReadOnlySpan<ParameterValidity> inputValidity)
    {
        if (inputValues.Length != InputIds.Length
            || inputValidity.Length != InputIds.Length)
        {
            throw new ArgumentException("Input buffers must match the compiled source layout.");
        }

        var outputValues = new double[formulas.Length];
        var outputValidity = new ParameterValidity[formulas.Length];
        int programMaximumStackDepth = formulas.IsDefaultOrEmpty
            ? 1
            : formulas.Max(static formula => formula.MaximumStackDepth);
        Span<double> values = stackalloc double[programMaximumStackDepth];
        Span<ParameterValidity> validity = stackalloc ParameterValidity[programMaximumStackDepth];
        for (int index = 0; index < formulas.Length; index++)
        {
            outputValues[index] = OutputDefinitions[index].NeutralValue;
            outputValidity[index] = ParameterValidity.Missing;
        }

        foreach (int outputSlot in evaluationOrder)
        {
            CompiledFormula formula = formulas[outputSlot];
            int stackCount = 0;

            foreach (FormulaInstruction instruction in formula.Instructions)
            {
                switch (instruction.Operation)
                {
                    case FormulaOperation.Constant:
                        values[stackCount] = instruction.Constant;
                        validity[stackCount++] = ParameterValidity.Valid;
                        break;
                    case FormulaOperation.Input:
                        values[stackCount] = inputValues[instruction.Slot];
                        validity[stackCount++] = inputValidity[instruction.Slot];
                        break;
                    case FormulaOperation.Output:
                        values[stackCount] = outputValues[instruction.Slot];
                        validity[stackCount++] = outputValidity[instruction.Slot];
                        break;
                    case FormulaOperation.Negate:
                        ApplyUnary(values, validity, stackCount, static value => -value);
                        break;
                    case FormulaOperation.Absolute:
                        ApplyUnary(values, validity, stackCount, Math.Abs);
                        break;
                    case FormulaOperation.DegreesToRadians:
                        ApplyUnary(values, validity, stackCount, static value => value * Math.PI / 180d);
                        break;
                    case FormulaOperation.Add:
                        stackCount = ApplyBinary(values, validity, stackCount, static (left, right) => left + right);
                        break;
                    case FormulaOperation.Subtract:
                        stackCount = ApplyBinary(values, validity, stackCount, static (left, right) => left - right);
                        break;
                    case FormulaOperation.Multiply:
                        stackCount = ApplyBinary(values, validity, stackCount, static (left, right) => left * right);
                        break;
                    case FormulaOperation.Divide:
                        stackCount = ApplyBinary(
                            values,
                            validity,
                            stackCount,
                            static (left, right) => right == 0 ? double.NaN : left / right);
                        break;
                    case FormulaOperation.Minimum:
                        stackCount = ApplyBinary(values, validity, stackCount, Math.Min);
                        break;
                    case FormulaOperation.Maximum:
                        stackCount = ApplyBinary(values, validity, stackCount, Math.Max);
                        break;
                    case FormulaOperation.Clamp:
                        stackCount = ApplyClamp(values, validity, stackCount);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown compiled formula operation.");
                }
            }

            ParameterValidity resultValidity = validity[0];
            double resultValue = values[0];
            if (resultValidity == ParameterValidity.Valid && !double.IsFinite(resultValue))
            {
                resultValidity = ParameterValidity.Invalid;
            }

            if (resultValidity == ParameterValidity.Valid)
            {
                SourceFormulaDefinition definition = OutputDefinitions[outputSlot];
                outputValues[outputSlot] = Math.Clamp(
                    resultValue,
                    definition.SuggestedMinimum,
                    definition.SuggestedMaximum);
            }

            outputValidity[outputSlot] = resultValidity;
        }

        return new SourceFormulaEvaluation(
            ImmutableArray.CreateRange(outputValues),
            ImmutableArray.CreateRange(outputValidity));
    }

    private static void ApplyUnary(
        Span<double> values,
        Span<ParameterValidity> validity,
        int stackCount,
        Func<double, double> operation)
    {
        int slot = stackCount - 1;
        if (validity[slot] != ParameterValidity.Valid)
        {
            return;
        }

        double result = operation(values[slot]);
        values[slot] = result;
        if (!double.IsFinite(result))
        {
            validity[slot] = ParameterValidity.Invalid;
        }
    }

    private static int ApplyBinary(
        Span<double> values,
        Span<ParameterValidity> validity,
        int stackCount,
        Func<double, double, double> operation)
    {
        int rightSlot = stackCount - 1;
        int leftSlot = rightSlot - 1;
        ParameterValidity merged = Merge(validity[leftSlot], validity[rightSlot]);
        validity[leftSlot] = merged;
        if (merged == ParameterValidity.Valid)
        {
            double result = operation(values[leftSlot], values[rightSlot]);
            values[leftSlot] = result;
            if (!double.IsFinite(result))
            {
                validity[leftSlot] = ParameterValidity.Invalid;
            }
        }

        return stackCount - 1;
    }

    private static int ApplyClamp(
        Span<double> values,
        Span<ParameterValidity> validity,
        int stackCount)
    {
        int maximumSlot = stackCount - 1;
        int minimumSlot = maximumSlot - 1;
        int valueSlot = minimumSlot - 1;
        ParameterValidity merged = Merge(
            validity[valueSlot],
            Merge(validity[minimumSlot], validity[maximumSlot]));
        validity[valueSlot] = merged;
        if (merged == ParameterValidity.Valid)
        {
            if (values[minimumSlot] > values[maximumSlot])
            {
                validity[valueSlot] = ParameterValidity.Invalid;
            }
            else
            {
                values[valueSlot] = Math.Clamp(
                    values[valueSlot],
                    values[minimumSlot],
                    values[maximumSlot]);
            }
        }

        return stackCount - 2;
    }

    private static ParameterValidity Merge(ParameterValidity left, ParameterValidity right)
    {
        if (left == ParameterValidity.Invalid || right == ParameterValidity.Invalid)
        {
            return ParameterValidity.Invalid;
        }

        return left == ParameterValidity.Missing || right == ParameterValidity.Missing
            ? ParameterValidity.Missing
            : ParameterValidity.Valid;
    }
}

internal sealed record CompiledFormula(
    ImmutableArray<FormulaInstruction> Instructions,
    int MaximumStackDepth);

internal readonly record struct FormulaInstruction(
    FormulaOperation Operation,
    int Slot = 0,
    double Constant = 0);

internal enum FormulaOperation
{
    Constant,
    Input,
    Output,
    Add,
    Subtract,
    Multiply,
    Divide,
    Negate,
    Absolute,
    Minimum,
    Maximum,
    Clamp,
    DegreesToRadians,
}
