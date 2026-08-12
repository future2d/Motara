namespace Motara.Tracking.Abstractions;

/// <summary>Describes whether a source natively detected its tracked subject.</summary>
public enum TrackingPresence
{
    /// <summary>The source does not expose or has not yet established detection state.</summary>
    Unknown,

    /// <summary>The source reports that its tracked subject is present.</summary>
    Tracked,

    /// <summary>The source reports that its tracked subject is absent.</summary>
    Lost,
}
