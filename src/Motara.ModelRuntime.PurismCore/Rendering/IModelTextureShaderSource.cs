using SkiaSharp;

namespace Motara.ModelRuntime.PurismCore;

internal interface IModelTextureShaderSource
{
    SKShader GetShader(int index);
}
