namespace Motara.Scene;

public static class SceneStorageLayout
{
    public static string GetSceneDirectory(string scenesRoot, SceneId sceneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenesRoot);
        return Path.Combine(
            Path.GetFullPath(scenesRoot),
            sceneId.Value.ToString("N"));
    }

    public static string GetMappingsDirectory(string scenesRoot, SceneId sceneId) =>
        Path.Combine(GetSceneDirectory(scenesRoot, sceneId), "motara", "mappings");

    public static string GetManifestPath(string scenesRoot, SceneId sceneId) =>
        Path.Combine(GetSceneDirectory(scenesRoot, sceneId), "motara", "scene.motara.json");

    public static string GetAssetsDirectory(string scenesRoot, SceneId sceneId) =>
        Path.Combine(GetSceneDirectory(scenesRoot, sceneId), "motara", "assets");

    public static string GetEffectsDirectory(string scenesRoot, SceneId sceneId) =>
        Path.Combine(GetSceneDirectory(scenesRoot, sceneId), "motara", "effects");

    public static string GetInputBindingsPath(string scenesRoot, SceneId sceneId) =>
        Path.Combine(GetSceneDirectory(scenesRoot, sceneId), "motara", "input-bindings.motara.json");
}
