using Motara.Persistence;

namespace Motara.App.Backgrounds;

internal sealed record ResolvedBackground(
    BackgroundDefinition Definition,
    bool IsSceneOverride)
{
    internal static ResolvedBackground FromGlobal(BackgroundDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new ResolvedBackground(definition, IsSceneOverride: false);
    }

    internal static ResolvedBackground FromSceneOverride(BackgroundDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new ResolvedBackground(definition, IsSceneOverride: true);
    }
}

internal static class BackgroundResolver
{
    internal static ResolvedBackground Resolve(
        BackgroundDefinition global,
        BackgroundDefinition? sceneOverride,
        bool scenePresented)
    {
        ArgumentNullException.ThrowIfNull(global);
        return scenePresented && sceneOverride is not null
            ? ResolvedBackground.FromSceneOverride(sceneOverride)
            : ResolvedBackground.FromGlobal(global);
    }
}
