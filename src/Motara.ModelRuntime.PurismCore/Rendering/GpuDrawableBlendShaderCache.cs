using Motara.ModelRuntime.Abstractions;
using SkiaSharp;

namespace Motara.ModelRuntime.PurismCore;

internal sealed class GpuDrawableBlendShaderCache : IDisposable
{
    private const string Source = """
        uniform shader source;
        uniform half3 multiply;
        uniform half3 screen;

        half4 main(float2 coord)
        {
            half4 sampled = source.eval(coord);
            half alpha = sampled.a;
            half3 unpremultiplied = half3(0.0);
            if (alpha > 0.0) {
                unpremultiplied = sampled.rgb / alpha;
            }
            half3 blended = unpremultiplied * multiply * (half3(1.0) - screen) + screen;
            return half4(blended * alpha, alpha);
        }
        """;

    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private bool disposed;

    internal object SyncRoot => gate;

    internal SKShader GetOrCreate(
        string drawableId,
        SKShader source,
        ModelColor multiplyColor,
        ModelColor screenColor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(drawableId);
        ArgumentNullException.ThrowIfNull(source);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (entries.TryGetValue(drawableId, out Entry? existing)
                && existing.MultiplyColor == multiplyColor
                && existing.ScreenColor == screenColor)
            {
                return existing.Shader;
            }

            string? errors;
            SKRuntimeEffect effect = SKRuntimeEffect.CreateShader(Source, out errors)
                ?? throw new InvalidOperationException(
                    $"GPU drawable blend shader compilation failed: {errors}");
            SKRuntimeShaderBuilder? builder = null;
            try
            {
                builder = new SKRuntimeShaderBuilder(effect);
                builder.Children.Add("source", source);
                builder.Uniforms["multiply"] = new[]
                {
                    multiplyColor.R,
                    multiplyColor.G,
                    multiplyColor.B,
                };
                builder.Uniforms["screen"] = new[]
                {
                    screenColor.R,
                    screenColor.G,
                    screenColor.B,
                };
                SKShader shader = builder.Build()
                    ?? throw new InvalidOperationException("GPU drawable blend shader could not be built.");
                existing?.Dispose();
                entries[drawableId] = new Entry(multiplyColor, screenColor, builder, shader);
                return shader;
            }
            catch
            {
                if (builder is not null)
                {
                    builder.Dispose();
                }
                else
                {
                    effect.Dispose();
                }

                throw;
            }
        }
    }

    internal void Clear()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ClearEntries();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            ClearEntries();
        }
    }

    private void ClearEntries()
    {
        foreach (Entry entry in entries.Values)
        {
            entry.Dispose();
        }

        entries.Clear();
    }

    private sealed class Entry(
        ModelColor multiplyColor,
        ModelColor screenColor,
        SKRuntimeShaderBuilder builder,
        SKShader shader) : IDisposable
    {
        internal ModelColor MultiplyColor { get; } = multiplyColor;

        internal ModelColor ScreenColor { get; } = screenColor;

        internal SKRuntimeShaderBuilder Builder { get; } = builder;

        internal SKShader Shader { get; } = shader;

        public void Dispose()
        {
            Shader.Dispose();
            Builder.Dispose();
        }
    }
}
