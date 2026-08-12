using System.Collections.Immutable;
using Motara.Core.Diagnostics;
using Motara.Tracking.Abstractions;

namespace Motara.Core.Sessions;

/// <summary>Contains one immutable canonical parameter sample for UI projection.</summary>
public sealed record ParameterSample(
    string Id,
    double Value,
    ParameterValidity Validity);

/// <summary>Contains a complete immutable session state publication.</summary>
public sealed record SessionSnapshot(
    long Revision,
    ModuleState TrackingState,
    ImmutableArray<ParameterSample> Parameters,
    long DroppedInputFrames,
    TimeSpan LastInputAge,
    ImmutableArray<DiagnosticEvent> Diagnostics,
    TrackingPresence TrackingPresence = TrackingPresence.Unknown);
