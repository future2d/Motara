using System.Collections.Immutable;
using Motara.Core.Configuration;
using Motara.Core.Diagnostics;
using Motara.Core.Frames;
using Motara.Core.Parameters;
using Motara.Core.Processing;
using Motara.Tracking.Abstractions;

namespace Motara.Core.Sessions;

/// <summary>Advances deterministic session state one explicit scheduler tick at a time.</summary>
public sealed class SessionEngine
{
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ReturnDuration = TimeSpan.FromMilliseconds(350);
    private readonly ParameterProcessor processor;
    private ParameterRegistry registry;
    private readonly object configurationGate = new();
    private readonly LatestFrameIngress ingress;
    private MotaraParameterFrame? lastFrame;
    private TimeSpan? lastInputAt;
    private TrackingPresence trackingPresence;
    private long revision;

    /// <summary>Creates a deterministic session engine.</summary>
    public SessionEngine(
        ParameterProcessor processor,
        ParameterRegistry registry,
        LatestFrameIngress ingress)
    {
        this.processor = processor ?? throw new ArgumentNullException(nameof(processor));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
    }

    /// <summary>Processes available input and publishes a complete snapshot for this tick.</summary>
    public SessionSnapshot Tick(TimeSpan monotonicTimestamp, DateTimeOffset utcNow)
    {
        lock (configurationGate)
        {
            return TickCore(monotonicTimestamp, utcNow);
        }
    }

    private SessionSnapshot TickCore(TimeSpan monotonicTimestamp, DateTimeOffset utcNow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(monotonicTimestamp, TimeSpan.Zero);

        ImmutableArray<DiagnosticEvent> diagnostics = [];
        if (ingress.TryRead(out RawTrackingFrame? sourceFrame) && sourceFrame is not null)
        {
            trackingPresence = sourceFrame.TrackingPresence;
            ProcessingResult result = processor.Process(sourceFrame);
            diagnostics = result.Diagnostics;
            if (result.Frame.Validity.Any(static validity => validity == ParameterValidity.Valid))
            {
                lastFrame = result.Frame;
                lastInputAt = monotonicTimestamp;
            }
        }

        return BuildSnapshot(monotonicTimestamp, diagnostics);
    }

    /// <summary>Replaces source routing and clears values produced by the previous layout.</summary>
    public int ReplaceConfiguration(PipelineConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lock (configurationGate)
        {
            processor.ReplaceConfiguration(configuration);
            registry = configuration.TargetRegistry;
            return ResetInputCore();
        }
    }

    /// <summary>Clears source input and the values retained from it.</summary>
    public int ResetInput()
    {
        lock (configurationGate)
        {
            return ResetInputCore();
        }
    }

    private int ResetInputCore()
    {
        int discardedFrameCount = ingress.Clear();
        lastFrame = null;
        lastInputAt = null;
        trackingPresence = TrackingPresence.Unknown;
        return discardedFrameCount;
    }

    private SessionSnapshot BuildSnapshot(
        TimeSpan monotonicTimestamp,
        ImmutableArray<DiagnosticEvent> diagnostics)
    {
        if (lastFrame is null || !lastInputAt.HasValue)
        {
            return new SessionSnapshot(
                ++revision,
                ModuleState.Disconnected,
                CreateNeutralParameters(ParameterValidity.Missing),
                ingress.DroppedFrameCount,
                TimeSpan.Zero,
                diagnostics,
                trackingPresence);
        }

        TimeSpan inputAge = monotonicTimestamp - lastInputAt.Value;
        if (inputAge < TimeSpan.Zero)
        {
            inputAge = TimeSpan.Zero;
        }

        double neutralProgress = inputAge <= HoldDuration
            ? 0
            : Math.Clamp(
                (inputAge - HoldDuration).TotalMilliseconds / ReturnDuration.TotalMilliseconds,
                0,
                1);
        var parameters = ImmutableArray.CreateBuilder<ParameterSample>(registry.Count);

        for (int slot = 0; slot < registry.Count; slot++)
        {
            ParameterDefinition definition = registry.Definitions[slot];
            double lastValue = lastFrame.Values[slot];
            double value = lastValue + ((definition.NeutralValue - lastValue) * neutralProgress);
            parameters.Add(new ParameterSample(definition.Id, value, lastFrame.Validity[slot]));
        }

        return new SessionSnapshot(
            ++revision,
            inputAge <= HoldDuration ? ModuleState.Connected : ModuleState.Degraded,
            parameters.MoveToImmutable(),
            ingress.DroppedFrameCount,
            inputAge,
            diagnostics,
            trackingPresence);
    }

    private ImmutableArray<ParameterSample> CreateNeutralParameters(ParameterValidity validity)
    {
        var parameters = ImmutableArray.CreateBuilder<ParameterSample>(registry.Count);
        foreach (ParameterDefinition definition in registry.Definitions)
        {
            parameters.Add(new ParameterSample(definition.Id, definition.NeutralValue, validity));
        }

        return parameters.MoveToImmutable();
    }
}
