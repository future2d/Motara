namespace Motara.Core.Sessions;

/// <summary>Describes the shared lifecycle state of a Motara module.</summary>
public enum ModuleState
{
    /// <summary>The module is stopped and owns no active operation.</summary>
    Disconnected,

    /// <summary>The module is starting but has not produced usable data.</summary>
    Connecting,

    /// <summary>The module is operating with current or held-valid data.</summary>
    Connected,

    /// <summary>The module is operating with reduced input quality or neutral return.</summary>
    Degraded,

    /// <summary>The module stopped because of a structured failure.</summary>
    Faulted,
}
