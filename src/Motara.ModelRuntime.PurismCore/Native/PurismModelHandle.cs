using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Motara.ModelRuntime.Abstractions;

namespace Motara.ModelRuntime.PurismCore;

internal interface IPurismModelLoader
{
    ValueTask<IPurismModelSession> LoadAsync(byte[] bytes, CancellationToken cancellationToken);
}

internal sealed class NativeLibraryUnavailableException : Exception
{
    internal NativeLibraryUnavailableException()
        : base("The PurismCore native library is unavailable.")
    {
    }

    internal NativeLibraryUnavailableException(Exception innerException)
        : base("The PurismCore native library is unavailable.", innerException)
    {
    }
}

internal sealed class PurismModelLoader : IPurismModelLoader
{
    internal const long MaximumNativeModelBytes = 256L * 1024 * 1024;

    public async ValueTask<IPurismModelSession> LoadAsync(
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || bytes.LongLength > MaximumNativeModelBytes)
        {
            throw new InvalidDataException("The native model file size is invalid.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await ValueTask.FromResult<IPurismModelSession>(PurismModelHandle.Create(bytes));
    }
}

internal sealed class PurismModelHandle : IPurismModelSession
{
    private const byte InvertedMaskConstantFlag = 0x08;
    private AlignedNativeBuffer? _mocBuffer;
    private AlignedNativeBuffer? _modelBuffer;
    private nint _model;
    private readonly float[] _parameterValues;
    private readonly float[] _partOpacityValues;
    private readonly Dictionary<string, int> _partIndexes;

    private PurismModelHandle(
        AlignedNativeBuffer mocBuffer,
        AlignedNativeBuffer modelBuffer,
        nint model,
        NativeCanvasData canvas,
        ImmutableArray<NativeParameterData> parameters,
        ImmutableArray<NativePartData> parts,
        ImmutableArray<NativeDrawableData> drawables)
    {
        _mocBuffer = mocBuffer;
        _modelBuffer = modelBuffer;
        _model = model;
        Canvas = canvas;
        Parameters = parameters;
        Drawables = drawables;
        _parameterValues = parameters.Select(static parameter => parameter.Default).ToArray();
        _partOpacityValues = parts.Select(static part => part.Opacity).ToArray();
        _partIndexes = CreatePartIndexes(parts);
    }

    public NativeCanvasData Canvas { get; }

    public ImmutableArray<NativeParameterData> Parameters { get; }

    public ImmutableArray<NativeDrawableData> Drawables { get; private set; }

    internal static PurismModelHandle Create(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || bytes.LongLength > PurismModelLoader.MaximumNativeModelBytes)
        {
            throw new InvalidDataException("The native model data size is invalid.");
        }

        AlignedNativeBuffer? mocBuffer = null;
        AlignedNativeBuffer? modelBuffer = null;
        try
        {
            mocBuffer = AlignedNativeBuffer.Allocate((nuint)bytes.Length, alignment: 64);
            Marshal.Copy(bytes, 0, mocBuffer.Pointer, bytes.Length);
            uint byteCount = checked((uint)bytes.Length);
            if (InvokeNative(() => PurismCoreNative.HasMocConsistency(mocBuffer.Pointer, byteCount)) == 0)
            {
                throw new InvalidDataException("The native model data is inconsistent.");
            }

            nint moc = InvokeNative(() => PurismCoreNative.ReviveMocInPlace(mocBuffer.Pointer, byteCount));
            if (moc == 0)
            {
                throw new InvalidDataException("The native model could not be revived.");
            }

            uint modelByteCount = InvokeNative(() => PurismCoreNative.GetSizeofModel(moc));
            if (modelByteCount == 0 || modelByteCount > PurismModelLoader.MaximumNativeModelBytes)
            {
                throw new InvalidDataException("The native model memory size is invalid.");
            }

            modelBuffer = AlignedNativeBuffer.Allocate(modelByteCount, alignment: 16);
            nint model = InvokeNative(() => PurismCoreNative.InitializeModelInPlace(
                moc,
                modelBuffer.Pointer,
                modelByteCount));
            if (model == 0)
            {
                throw new InvalidDataException("The native model could not be initialized.");
            }

            InvokeNative(() =>
            {
                PurismCoreNative.ResetDrawableDynamicFlags(model);
                PurismCoreNative.UpdateModel(model);
                return true;
            });
            ReadNativeData(model, out NativeCanvasData canvas, out var parameters, out var parts, out var drawables);
            return new PurismModelHandle(mocBuffer, modelBuffer, model, canvas, parameters, parts, drawables);
        }
        catch
        {
            modelBuffer?.Dispose();
            mocBuffer?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _model = 0;
        Interlocked.Exchange(ref _modelBuffer, null)?.Dispose();
        Interlocked.Exchange(ref _mocBuffer, null)?.Dispose();
    }

    public void ApplyParameters(
        ReadOnlySpan<ModelParameterValue> values,
        ReadOnlySpan<ModelPartOpacity> partOpacities)
    {
        nint model = _model;
        ObjectDisposedException.ThrowIf(model == 0, this);
        foreach (ModelParameterValue value in values)
        {
            if ((uint)value.ParameterIndex >= (uint)_parameterValues.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(values));
            }

            _parameterValues[value.ParameterIndex] = (float)value.Value;
        }

        foreach (ModelPartOpacity partOpacity in partOpacities)
        {
            if (string.IsNullOrWhiteSpace(partOpacity.PartId)
                || !float.IsFinite(partOpacity.Opacity))
            {
                throw new ArgumentOutOfRangeException(nameof(partOpacities));
            }

            if (_partIndexes.TryGetValue(partOpacity.PartId, out int partIndex))
            {
                _partOpacityValues[partIndex] = Math.Clamp(partOpacity.Opacity, 0, 1);
            }
        }

        nint parameterValues = InvokeNative(() => PurismCoreNative.GetParameterValues(model));
        EnsurePointer(parameterValues, _parameterValues.Length);
        if (_parameterValues.Length > 0)
        {
            Marshal.Copy(_parameterValues, 0, parameterValues, _parameterValues.Length);
        }

        nint partOpacitiesPointer = InvokeNative(() => PurismCoreNative.GetPartOpacities(model));
        EnsurePointer(partOpacitiesPointer, _partOpacityValues.Length);
        if (_partOpacityValues.Length > 0)
        {
            Marshal.Copy(_partOpacityValues, 0, partOpacitiesPointer, _partOpacityValues.Length);
        }

        InvokeNative(() =>
        {
            PurismCoreNative.ResetDrawableDynamicFlags(model);
            PurismCoreNative.UpdateModel(model);
            return true;
        });
        Drawables = ReadDynamicDrawables(model, Drawables);
    }

    private static void ReadNativeData(
        nint model,
        out NativeCanvasData canvas,
        out ImmutableArray<NativeParameterData> parameters,
        out ImmutableArray<NativePartData> parts,
        out ImmutableArray<NativeDrawableData> drawables)
    {
        canvas = InvokeNative(() =>
        {
            PurismCoreNative.ReadCanvasInfo(model, out NativeVector2Data size, out _, out float pixelsPerUnit);
            return new NativeCanvasData(size.X, size.Y, pixelsPerUnit);
        });

        int parameterCount = ValidateCount(InvokeNative(() => PurismCoreNative.GetParameterCount(model)), 100_000);
        string[] parameterIds = ReadStrings(PurismCoreNative.GetParameterIds(model), parameterCount);
        float[] minimums = ReadFloats(PurismCoreNative.GetParameterMinimumValues(model), parameterCount);
        float[] defaults = ReadFloats(PurismCoreNative.GetParameterDefaultValues(model), parameterCount);
        float[] maximums = ReadFloats(PurismCoreNative.GetParameterMaximumValues(model), parameterCount);
        parameters = Enumerable.Range(0, parameterCount)
            .Select(index => new NativeParameterData(
                parameterIds[index],
                minimums[index],
                defaults[index],
                maximums[index]))
            .ToImmutableArray();

        int partCount = ValidateCount(InvokeNative(() => PurismCoreNative.GetPartCount(model)), 100_000);
        string[] partIds = ReadStrings(PurismCoreNative.GetPartIds(model), partCount);
        float[] partOpacities = ReadFloats(PurismCoreNative.GetPartOpacities(model), partCount);
        parts = Enumerable.Range(0, partCount)
            .Select(index => new NativePartData(partIds[index], partOpacities[index]))
            .ToImmutableArray();

        int drawableCount = ValidateCount(InvokeNative(() => PurismCoreNative.GetDrawableCount(model)), 100_000);
        string[] drawableIds = ReadStrings(PurismCoreNative.GetDrawableIds(model), drawableCount);
        int[] textureIndices = ReadIntegers(PurismCoreNative.GetDrawableTextureIndices(model), drawableCount);
        int[] renderOrders = ReadIntegers(PurismCoreNative.GetRenderOrders(model), drawableCount);
        float[] opacities = ReadFloats(PurismCoreNative.GetDrawableOpacities(model), drawableCount);
        int[] maskCounts = ReadIntegers(PurismCoreNative.GetDrawableMaskCounts(model), drawableCount);
        nint[] maskPointers = ReadPointers(PurismCoreNative.GetDrawableMasks(model), drawableCount);
        int[] vertexCounts = ReadIntegers(PurismCoreNative.GetDrawableVertexCounts(model), drawableCount);
        nint[] positionPointers = ReadPointers(PurismCoreNative.GetDrawableVertexPositions(model), drawableCount);
        nint[] uvPointers = ReadPointers(PurismCoreNative.GetDrawableVertexUvs(model), drawableCount);
        int[] indexCounts = ReadIntegers(PurismCoreNative.GetDrawableIndexCounts(model), drawableCount);
        nint[] indexPointers = ReadPointers(PurismCoreNative.GetDrawableIndices(model), drawableCount);
        int[] blendModes = ReadIntegers(PurismCoreNative.GetDrawableBlendModes(model), drawableCount);
        byte[] constantFlags = ReadBytes(PurismCoreNative.GetDrawableConstantFlags(model), drawableCount);
        NativeColorData[] multiplyColors = ReadColors(
            PurismCoreNative.GetDrawableMultiplyColors(model),
            drawableCount);
        NativeColorData[] screenColors = ReadColors(
            PurismCoreNative.GetDrawableScreenColors(model),
            drawableCount);

        var builder = ImmutableArray.CreateBuilder<NativeDrawableData>(drawableCount);
        for (int index = 0; index < drawableCount; index++)
        {
            int vertexCount = ValidateCount(vertexCounts[index], 1_000_000);
            int indexCount = ValidateCount(indexCounts[index], 3_000_000);
            int maskCount = ValidateCount(maskCounts[index], drawableCount);
            builder.Add(new NativeDrawableData(
                drawableIds[index],
                textureIndices[index],
                renderOrders[index],
                opacities[index],
                blendModes[index],
                ReadVectors(positionPointers[index], vertexCount),
                ReadVectors(uvPointers[index], vertexCount),
                ReadUnsignedShorts(indexPointers[index], indexCount),
                ReadIntegers(maskPointers[index], maskCount).Where(static value => value >= 0).ToArray(),
                HasInvertedMaskFlag(constantFlags[index]),
                multiplyColors[index],
                screenColors[index]));
        }

        drawables = builder.MoveToImmutable();
    }

    private static int ValidateCount(int count, int maximum)
    {
        if (count < 0 || count > maximum)
        {
            throw new InvalidDataException("A native array count is invalid.");
        }

        return count;
    }

    internal static bool HasInvertedMaskFlag(byte constantFlags) =>
        (constantFlags & InvertedMaskConstantFlag) != 0;

    private static Dictionary<string, int> CreatePartIndexes(ImmutableArray<NativePartData> parts)
    {
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < parts.Length; index++)
        {
            NativePartData part = parts[index];
            if (string.IsNullOrWhiteSpace(part.Id)
                || !float.IsFinite(part.Opacity)
                || !indexes.TryAdd(part.Id, index))
            {
                throw new InvalidDataException("Native part data is invalid.");
            }
        }

        return indexes;
    }

    private static ImmutableArray<NativeDrawableData> ReadDynamicDrawables(
        nint model,
        ImmutableArray<NativeDrawableData> current)
    {
        int count = current.Length;
        int[] renderOrders = ReadIntegers(PurismCoreNative.GetRenderOrders(model), count);
        float[] opacities = ReadFloats(PurismCoreNative.GetDrawableOpacities(model), count);
        nint[] positionPointers = ReadPointers(
            PurismCoreNative.GetDrawableVertexPositions(model),
            count);
        NativeColorData[] multiplyColors = ReadColors(
            PurismCoreNative.GetDrawableMultiplyColors(model),
            count);
        NativeColorData[] screenColors = ReadColors(
            PurismCoreNative.GetDrawableScreenColors(model),
            count);
        var drawables = ImmutableArray.CreateBuilder<NativeDrawableData>(count);
        for (int index = 0; index < count; index++)
        {
            NativeDrawableData drawable = current[index];
            drawables.Add(new NativeDrawableData(
                drawable.Id,
                drawable.TextureIndex,
                renderOrders[index],
                opacities[index],
                drawable.BlendMode,
                ReadVectors(positionPointers[index], drawable.Positions.Length),
                drawable.Uvs,
                drawable.Indices,
                drawable.Masks,
                drawable.IsInvertedMask,
                multiplyColors[index],
                screenColors[index]));
        }

        return drawables.MoveToImmutable();
    }

    private static string[] ReadStrings(nint pointer, int count)
    {
        nint[] pointers = ReadPointers(pointer, count);
        return pointers.Select(static value => Marshal.PtrToStringUTF8(value)
            ?? throw new InvalidDataException("A native identifier is invalid.")).ToArray();
    }

    private static int[] ReadIntegers(nint pointer, int count)
    {
        EnsurePointer(pointer, count);
        var values = new int[count];
        if (count > 0)
        {
            Marshal.Copy(pointer, values, 0, count);
        }

        return values;
    }

    private static byte[] ReadBytes(nint pointer, int count)
    {
        EnsurePointer(pointer, count);
        var values = new byte[count];
        for (int index = 0; index < count; index++)
        {
            values[index] = Marshal.ReadByte(pointer, index);
        }

        return values;
    }

    private static float[] ReadFloats(nint pointer, int count)
    {
        EnsurePointer(pointer, count);
        var values = new float[count];
        if (count > 0)
        {
            Marshal.Copy(pointer, values, 0, count);
        }

        return values;
    }

    private static nint[] ReadPointers(nint pointer, int count)
    {
        EnsurePointer(pointer, count);
        var values = new nint[count];
        for (int index = 0; index < count; index++)
        {
            values[index] = Marshal.ReadIntPtr(pointer, index * IntPtr.Size);
        }

        return values;
    }

    private static NativeVector2Data[] ReadVectors(nint pointer, int count)
    {
        EnsurePointer(pointer, count);
        var values = new NativeVector2Data[count];
        int stride = Marshal.SizeOf<NativeVector2Data>();
        for (int index = 0; index < count; index++)
        {
            values[index] = Marshal.PtrToStructure<NativeVector2Data>(pointer + (index * stride));
        }

        return values;
    }

    private static NativeColorData[] ReadColors(nint pointer, int count)
    {
        EnsurePointer(pointer, count);
        var values = new NativeColorData[count];
        int stride = Marshal.SizeOf<NativeColorData>();
        for (int index = 0; index < count; index++)
        {
            values[index] = Marshal.PtrToStructure<NativeColorData>(pointer + (index * stride));
        }

        return values;
    }

    private static ushort[] ReadUnsignedShorts(nint pointer, int count)
    {
        EnsurePointer(pointer, count);
        var values = new ushort[count];
        for (int index = 0; index < count; index++)
        {
            values[index] = unchecked((ushort)Marshal.ReadInt16(pointer, index * sizeof(ushort)));
        }

        return values;
    }

    private static void EnsurePointer(nint pointer, int count)
    {
        if (count > 0 && pointer == 0)
        {
            throw new InvalidDataException("A native array pointer is null.");
        }
    }

    private static T InvokeNative<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or BadImageFormatException
            or EntryPointNotFoundException)
        {
            throw new NativeLibraryUnavailableException(exception);
        }
    }

}
