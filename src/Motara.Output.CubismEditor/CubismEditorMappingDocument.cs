using System.Collections.Immutable;

namespace Motara.Output.CubismEditor;

/// <summary>Persistable independent parameter mapping for Cubism Editor output.</summary>
public sealed record CubismEditorMappingDocument(
    int SchemaVersion,
    ImmutableArray<CubismEditorParameterBinding> Bindings)
{
    public const int CurrentSchemaVersion = 1;

    public static CubismEditorMappingDocument Default { get; } = new(
        CurrentSchemaVersion,
        CubismEditorParameterMapping.Default.Bindings);

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(SchemaVersion, CurrentSchemaVersion);
        _ = new CubismEditorParameterMapping(Bindings);
    }

    public CubismEditorParameterMapping ToMapping()
    {
        Validate();
        return new CubismEditorParameterMapping(Bindings);
    }
}
