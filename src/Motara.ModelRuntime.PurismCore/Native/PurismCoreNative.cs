using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Motara.ModelRuntime.PurismCore;

internal static partial class PurismCoreNative
{
    private const string LibraryName = "PurismCore";

    static PurismCoreNative()
    {
        NativeLibraryResolver.Register();
    }

    [LibraryImport(LibraryName, EntryPoint = "csmGetTrueVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial uint GetTrueVersion();

    [LibraryImport(LibraryName, EntryPoint = "csmGetLatestMocVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial uint GetLatestMocVersion();

    [LibraryImport(LibraryName, EntryPoint = "csmHasMocConsistency")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int HasMocConsistency(nint address, uint size);

    [LibraryImport(LibraryName, EntryPoint = "csmReviveMocInPlace")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint ReviveMocInPlace(nint address, uint size);

    [LibraryImport(LibraryName, EntryPoint = "csmGetSizeofModel")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial uint GetSizeofModel(nint moc);

    [LibraryImport(LibraryName, EntryPoint = "csmInitializeModelInPlace")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint InitializeModelInPlace(nint moc, nint address, uint size);

    [LibraryImport(LibraryName, EntryPoint = "csmReadCanvasInfo")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial void ReadCanvasInfo(
        nint model,
        out NativeVector2Data canvasSize,
        out NativeVector2Data canvasOrigin,
        out float pixelsPerUnit);

    [LibraryImport(LibraryName, EntryPoint = "csmGetParameterCount")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int GetParameterCount(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetParameterIds")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetParameterIds(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetParameterMinimumValues")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetParameterMinimumValues(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetParameterMaximumValues")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetParameterMaximumValues(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetParameterDefaultValues")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetParameterDefaultValues(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetParameterValues")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetParameterValues(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetPartCount")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int GetPartCount(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetPartIds")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetPartIds(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetPartOpacities")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetPartOpacities(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmResetDrawableDynamicFlags")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial void ResetDrawableDynamicFlags(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmUpdateModel")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial void UpdateModel(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableCount")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int GetDrawableCount(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableIds")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetDrawableIds(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableTextureIndices")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetDrawableTextureIndices(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetRenderOrders")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetRenderOrders(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableOpacities")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetDrawableOpacities(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableMaskCounts")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetDrawableMaskCounts(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableMasks")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetDrawableMasks(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableVertexCounts")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetDrawableVertexCounts(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableVertexPositions")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetDrawableVertexPositions(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableVertexUvs")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetDrawableVertexUvs(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableIndexCounts")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetDrawableIndexCounts(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableIndices")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetDrawableIndices(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableBlendModes")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetDrawableBlendModes(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableConstantFlags")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetDrawableConstantFlags(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableMultiplyColors")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetDrawableMultiplyColors(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableScreenColors")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint GetDrawableScreenColors(nint model);
}
