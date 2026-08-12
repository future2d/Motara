namespace Motara.Core.Diagnostics;

/// <summary>Describes a language-neutral processing diagnostic.</summary>
public sealed record DiagnosticEvent(
    string Code,
    string SourceId,
    long Sequence,
    int SourceSlot);
