using System.Collections.Immutable;
using System.Numerics;
using Motara.App.Parameters;
using Motara.ModelRuntime.Abstractions;

namespace Motara.App.Physics;

internal sealed class CubismPhysicsEvaluator
{
    private static readonly TimeSpan MaximumElapsed = TimeSpan.FromMilliseconds(250);

    private readonly CubismPhysicsDefinition definition;
    private readonly ImmutableArray<ModelParameter> parameters;
    private readonly CompiledSetting[] settings;
    private readonly double[] parameterCache;
    private readonly double[] baselineParameterCache;
    private readonly bool[] changedOutputs;
    private readonly double[] latestOutputValues;
    private readonly bool[] hasLatestOutputValues;
    private TimeSpan remainder;
    private Vector2 pendingDragParameterOffset;
    private int calculationFramesPerSecond;
    private TimeSpan fixedStep;

    internal CubismPhysicsEvaluator(
        CubismPhysicsDefinition definition,
        ModelCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(capabilities);
        this.definition = definition;
        parameters = capabilities.Parameters;
        var parameterIndexes = parameters
            .Select(static (parameter, index) => (parameter.Id, index))
            .ToDictionary(static pair => pair.Id, static pair => pair.index, StringComparer.Ordinal);
        settings = definition.Settings
            .Select(setting => CompiledSetting.Create(setting, parameterIndexes))
            .ToArray();
        parameterCache = new double[parameters.Length];
        baselineParameterCache = new double[parameters.Length];
        changedOutputs = new bool[parameters.Length];
        latestOutputValues = new double[parameters.Length];
        hasLatestOutputValues = new bool[parameters.Length];
        calculationFramesPerSecond = 60;
        fixedStep = TimeSpan.FromSeconds(1d / calculationFramesPerSecond);
    }

    internal ImmutableArray<ParameterContribution> Evaluate(
        ReadOnlySpan<double> values,
        TimeSpan elapsed,
        Vector2 userWind,
        double outputStrength,
        int calculationFramesPerSecond,
        Vector2 dragParameterOffset = default)
    {
        if (values.Length != parameters.Length)
        {
            throw new ArgumentException("Model parameter layout changed.", nameof(values));
        }

        if (!IsFinite(userWind))
        {
            throw new ArgumentOutOfRangeException(nameof(userWind));
        }

        if (!IsFinite(dragParameterOffset))
        {
            throw new ArgumentOutOfRangeException(nameof(dragParameterOffset));
        }

        if (!double.IsFinite(outputStrength) || outputStrength is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(outputStrength));
        }

        if (calculationFramesPerSecond is not (30 or 60 or 120))
        {
            throw new ArgumentOutOfRangeException(nameof(calculationFramesPerSecond));
        }

        if (this.calculationFramesPerSecond != calculationFramesPerSecond)
        {
            this.calculationFramesPerSecond = calculationFramesPerSecond;
            fixedStep = TimeSpan.FromSeconds(1d / calculationFramesPerSecond);
            remainder = TimeSpan.Zero;
        }

        values.CopyTo(parameterCache);
        values.CopyTo(baselineParameterCache);
        Array.Clear(changedOutputs);
        pendingDragParameterOffset += dragParameterOffset;
        if (!IsFinite(pendingDragParameterOffset))
        {
            pendingDragParameterOffset = Vector2.Zero;
            throw new ArithmeticException("Accumulated model drag parameter offset is not finite.");
        }
        if (elapsed > TimeSpan.Zero)
        {
            remainder += elapsed > MaximumElapsed ? MaximumElapsed : elapsed;
            while (remainder >= fixedStep)
            {
                Vector2 stepDragParameterOffset = pendingDragParameterOffset;
                pendingDragParameterOffset = Vector2.Zero;
                EvaluateStep(
                    fixedStep.TotalSeconds,
                    userWind,
                    outputStrength,
                    stepDragParameterOffset);
                remainder -= fixedStep;
            }
        }

        for (int index = 0; index < changedOutputs.Length; index++)
        {
            if (changedOutputs[index] && double.IsFinite(parameterCache[index]))
            {
                latestOutputValues[index] = parameterCache[index];
                hasLatestOutputValues[index] = true;
            }
        }

        var output = ImmutableArray.CreateBuilder<ParameterContribution>();
        for (int index = 0; index < hasLatestOutputValues.Length; index++)
        {
            if (hasLatestOutputValues[index])
            {
                output.Add(new ParameterContribution(
                    index,
                    latestOutputValues[index],
                    ParameterProviderKind.Physics));
            }
        }

        return output.ToImmutable();
    }

    private void EvaluateStep(
        double deltaSeconds,
        Vector2 userWind,
        double outputStrength,
        Vector2 dragParameterOffset)
    {
        Vector2 wind = definition.Wind + userWind;
        foreach (CompiledSetting setting in settings)
        {
            try
            {
                Vector2 translation = Vector2.Zero;
                double angle = 0;
                foreach (CompiledInput input in setting.Inputs)
                {
                    if (input.ParameterIndex < 0)
                    {
                        continue;
                    }

                    ModelParameter parameter = parameters[input.ParameterIndex];
                    double value = parameterCache[input.ParameterIndex]
                        + (input.DragParameterAxis switch
                        {
                            DragParameterAxis.AngleX => dragParameterOffset.X,
                            DragParameterAxis.AngleY => dragParameterOffset.Y,
                            _ => 0,
                        });
                    double normalized = Normalize(
                        value,
                        parameter.Minimum,
                        parameter.Maximum,
                        input.Type is CubismPhysicsValueType.Angle
                            ? setting.Definition.AngleNormalization
                            : setting.Definition.PositionNormalization,
                        input.Reflect) * (input.Weight / 100d);
                    switch (input.Type)
                    {
                        case CubismPhysicsValueType.X:
                            translation.X += (float)normalized;
                            break;
                        case CubismPhysicsValueType.Y:
                            translation.Y += (float)normalized;
                            break;
                        case CubismPhysicsValueType.Angle:
                            angle += normalized;
                            break;
                    }
                }

                float inverseAngleRadians = (float)(-angle * Math.PI / 180d);
                translation = Rotate(translation, inverseAngleRadians);
                UpdateParticles(setting, translation, angle, wind, deltaSeconds);
                ApplyOutputs(setting, outputStrength);
            }
            catch (ArithmeticException)
            {
                setting.Reset();
            }
        }
    }

    private static void UpdateParticles(
        CompiledSetting setting,
        Vector2 translation,
        double angle,
        Vector2 wind,
        double deltaSeconds)
    {
        ParticleState[] particles = setting.Particles;
        particles[0].Position = translation;
        Vector2 gravity = Normalize(new Vector2(
            (float)Math.Sin(angle * Math.PI / 180d),
            (float)Math.Cos(angle * Math.PI / 180d)));
        float threshold = (float)(0.001d * Math.Max(
            Math.Abs(setting.Definition.PositionNormalization.Minimum),
            Math.Abs(setting.Definition.PositionNormalization.Maximum)));

        for (int index = 1; index < particles.Length; index++)
        {
            ref ParticleState particle = ref particles[index];
            ParticleState previous = particles[index - 1];
            particle.LastPosition = particle.Position;
            Vector2 force = gravity * (float)particle.Definition.Acceleration + wind;
            double delay = particle.Definition.Delay * deltaSeconds * 30d;
            Vector2 direction = particle.Position - previous.Position;
            float rotation = DirectionToRadians(particle.LastGravity, gravity) / 5f;
            direction = Rotate(direction, rotation);
            Vector2 next = previous.Position + direction;
            next += particle.Velocity * (float)delay;
            next += force * (float)(delay * delay);
            Vector2 nextDirection = Normalize(next - previous.Position);
            if (nextDirection == Vector2.Zero)
            {
                nextDirection = Normalize(direction);
            }

            next = previous.Position + nextDirection * (float)particle.Definition.Radius;
            if (Math.Abs(next.X) < threshold)
            {
                next.X = 0;
            }

            if (delay > 0)
            {
                particle.Velocity = (next - particle.LastPosition) / (float)delay
                    * (float)particle.Definition.Mobility;
            }

            particle.Position = next;
            particle.LastGravity = gravity;
            if (!IsFinite(particle.Position) || !IsFinite(particle.Velocity))
            {
                throw new ArithmeticException("Physics particle state is not finite.");
            }
        }
    }

    private void ApplyOutputs(CompiledSetting setting, double outputStrength)
    {
        foreach (CompiledOutput output in setting.Outputs)
        {
            if (output.ParameterIndex < 0)
            {
                continue;
            }

            ParticleState current = setting.Particles[output.VertexIndex];
            ParticleState previous = setting.Particles[output.VertexIndex - 1];
            Vector2 translation = current.Position - previous.Position;
            double value = output.Type switch
            {
                CubismPhysicsValueType.X => translation.X,
                CubismPhysicsValueType.Y => translation.Y,
                CubismPhysicsValueType.Angle => OutputAngle(setting, output.VertexIndex, translation),
                _ => throw new ArithmeticException("Unsupported Cubism physics output type."),
            };
            if (output.Reflect)
            {
                value = -value;
            }

            ModelParameter parameter = parameters[output.ParameterIndex];
            value = Math.Clamp(value * output.Scale, parameter.Minimum, parameter.Maximum);
            value = baselineParameterCache[output.ParameterIndex]
                + (value - baselineParameterCache[output.ParameterIndex]) * outputStrength;
            value = Math.Clamp(value, parameter.Minimum, parameter.Maximum);
            double weight = Math.Clamp(output.Weight / 100d, 0, 1);
            parameterCache[output.ParameterIndex] = parameterCache[output.ParameterIndex] * (1d - weight)
                + value * weight;
            changedOutputs[output.ParameterIndex] = true;
        }
    }

    private double OutputAngle(CompiledSetting setting, int vertexIndex, Vector2 translation)
    {
        Vector2 parentGravity = vertexIndex >= 2
            ? setting.Particles[vertexIndex - 1].Position - setting.Particles[vertexIndex - 2].Position
            : -definition.Gravity;
        return DirectionToRadians(parentGravity, translation);
    }

    private static double Normalize(
        double value,
        double parameterMinimum,
        double parameterMaximum,
        CubismPhysicsNormalization normalization,
        bool reflect)
    {
        if (!double.IsFinite(value))
        {
            throw new ArithmeticException("Physics input is not finite.");
        }

        double minimum = Math.Min(parameterMinimum, parameterMaximum);
        double maximum = Math.Max(parameterMinimum, parameterMaximum);
        double middle = minimum + (maximum - minimum) / 2d;
        double bounded = Math.Clamp(value, minimum, maximum);
        double normalized;
        if (bounded > middle && maximum > middle)
        {
            normalized = (bounded - middle)
                * ((normalization.Maximum - normalization.Default) / (maximum - middle))
                + normalization.Default;
        }
        else if (bounded < middle && minimum < middle)
        {
            normalized = (bounded - middle)
                * ((normalization.Minimum - normalization.Default) / (minimum - middle))
                + normalization.Default;
        }
        else
        {
            normalized = normalization.Default;
        }

        return reflect ? normalized : -normalized;
    }

    private static Vector2 Rotate(Vector2 value, float radians) => new(
        MathF.Cos(radians) * value.X - MathF.Sin(radians) * value.Y,
        MathF.Sin(radians) * value.X + MathF.Cos(radians) * value.Y);

    private static float DirectionToRadians(Vector2 from, Vector2 to)
    {
        float angle = MathF.Atan2(to.Y, to.X) - MathF.Atan2(from.Y, from.X);
        if (angle < -MathF.PI) angle += MathF.PI * 2;
        if (angle > MathF.PI) angle -= MathF.PI * 2;
        return angle;
    }

    private static Vector2 Normalize(Vector2 value)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > 0 && float.IsFinite(lengthSquared)
            ? Vector2.Normalize(value)
            : Vector2.Zero;
    }

    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private sealed class CompiledSetting
    {
        private readonly ImmutableArray<CubismPhysicsParticleDefinition> particles;

        private CompiledSetting(
            CubismPhysicsSettingDefinition definition,
            CompiledInput[] inputs,
            CompiledOutput[] outputs)
        {
            Definition = definition;
            Inputs = inputs;
            Outputs = outputs;
            particles = definition.Vertices;
            Particles = CreateInitialParticles();
        }

        internal CubismPhysicsSettingDefinition Definition { get; }

        internal CompiledInput[] Inputs { get; }

        internal CompiledOutput[] Outputs { get; }

        internal ParticleState[] Particles { get; private set; }

        internal static CompiledSetting Create(
            CubismPhysicsSettingDefinition definition,
            IReadOnlyDictionary<string, int> parameterIndexes) => new(
                definition,
                definition.Inputs.Select(input => new CompiledInput(
                    parameterIndexes.GetValueOrDefault(input.SourceId, -1),
                    input.Weight,
                    input.Type,
                    input.Reflect,
                    ResolveDragParameterAxis(input.SourceId))).ToArray(),
                definition.Outputs.Select(output => new CompiledOutput(
                    parameterIndexes.GetValueOrDefault(output.DestinationId, -1),
                    output.VertexIndex,
                    output.Scale,
                    output.Weight,
                    output.Type,
                    output.Reflect)).ToArray());

        internal void Reset() => Particles = CreateInitialParticles();

        private ParticleState[] CreateInitialParticles() => particles.Select(particle => new ParticleState(
            particle,
            particle.Position,
            particle.Position,
            Vector2.Zero,
            new Vector2(0, 1))).ToArray();
    }

    private readonly record struct CompiledInput(
        int ParameterIndex,
        double Weight,
        CubismPhysicsValueType Type,
        bool Reflect,
        DragParameterAxis DragParameterAxis);

    private static DragParameterAxis ResolveDragParameterAxis(string sourceId) => sourceId switch
    {
        "ParamAngleX" => DragParameterAxis.AngleX,
        "ParamAngleY" => DragParameterAxis.AngleY,
        _ => DragParameterAxis.None,
    };

    private enum DragParameterAxis
    {
        None,
        AngleX,
        AngleY,
    }

    private readonly record struct CompiledOutput(
        int ParameterIndex,
        int VertexIndex,
        double Scale,
        double Weight,
        CubismPhysicsValueType Type,
        bool Reflect);

    private struct ParticleState(
        CubismPhysicsParticleDefinition definition,
        Vector2 position,
        Vector2 lastPosition,
        Vector2 velocity,
        Vector2 lastGravity)
    {
        internal CubismPhysicsParticleDefinition Definition = definition;
        internal Vector2 Position = position;
        internal Vector2 LastPosition = lastPosition;
        internal Vector2 Velocity = velocity;
        internal Vector2 LastGravity = lastGravity;
    }
}
