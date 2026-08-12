namespace Motara.Output.CubismEditor;

/// <summary>Describes the lifecycle state of the Cubism Editor parameter output target.</summary>
public enum CubismEditorOutputState
{
    Stopped,
    Connecting,
    EditorUnavailable,
    WaitingForApproval,
    ModelUnavailable,
    Connected,
    Reconnecting,
    ProtocolError,
}

/// <summary>Provides an immutable diagnostics snapshot without requiring callers to parse logs.</summary>
public sealed record CubismEditorOutputStatus(
    CubismEditorOutputState State,
    Uri Endpoint,
    string? ModelUid,
    string? Detail)
{
    public static CubismEditorOutputStatus Stopped(CubismEditorConnectionOptions options) =>
        new(CubismEditorOutputState.Stopped, options.Endpoint, null, null);
}
