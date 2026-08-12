using System.Numerics;
using Motara.ModelRuntime.Abstractions;
using SkiaSharp;

namespace Motara.ModelRuntime.PurismCore;

internal sealed class GpuModelShader : IDisposable
{
    internal const string Source = """
        uniform shader source;
        uniform shader maskAtlas;
        uniform half3 multiply;
        uniform half3 screen;
        uniform half opacity;
        uniform half invertedMask;
        uniform float4 maskUvTransform;

        half4 main(float2 coord)
        {
            half4 sampled = source.eval(coord);
            half alpha = sampled.a;
            half3 rgb = alpha > 0.0 ? sampled.rgb / alpha : half3(0.0);
            rgb = rgb * multiply * (half3(1.0) - screen) + screen;
            float2 maskCoord = coord * maskUvTransform.xy + maskUvTransform.zw;
            half mask = maskAtlas.eval(maskCoord).a;
            mask = mix(mask, half(1.0) - mask, invertedMask);
            alpha *= opacity * mask;
            return half4(rgb * alpha, alpha);
        }
        """;

    private readonly SKRuntimeEffect effect;
    private int disposed;

    internal GpuModelShader()
    {
        string? errors;
        effect = SKRuntimeEffect.CreateShader(Source, out errors)
            ?? throw new InvalidOperationException(
                $"GPU model shader compilation failed: {errors}");
    }

    internal ModelRasterTransform Transform { get; private set; } = ModelRasterTransform.Identity;

    internal SKRuntimeShaderBuilder CreateBuilder()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return new SKRuntimeShaderBuilder(effect);
    }

    internal void SetTransform(ModelRasterTransform transform)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!transform.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(transform));
        }

        Transform = transform;
    }

    internal static Vector4 EvaluateForTest(Vector4 source, float maskAlpha, bool inverted)
    {
        float mask = inverted ? 1 - maskAlpha : maskAlpha;
        return new Vector4(source.X, source.Y, source.Z, source.W * mask);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            effect.Dispose();
        }
    }
}
