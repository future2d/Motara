using System.Collections.Immutable;

namespace Motara.Scene;

public enum SceneSourceRole
{
    MainModel = 0,
    Attachment = 1,
}

public sealed record SceneSourceDescriptor(
    string Id,
    SceneSourceRole Role,
    string DisplayNameResourceKey);

public sealed class SceneSourceRegistry
{
    private readonly ImmutableDictionary<string, SceneSourceDescriptor> byId;

    private SceneSourceRegistry(IEnumerable<SceneSourceDescriptor> descriptors)
    {
        Descriptors = descriptors.ToImmutableArray();
        byId = Descriptors.ToImmutableDictionary(
            static descriptor => descriptor.Id,
            StringComparer.Ordinal);
    }

    public static SceneSourceRegistry Default { get; } = new(
    [
        new("source.main-model", SceneSourceRole.MainModel, "Menu.Scene.MainModel"),
        new("attachment.image", SceneSourceRole.Attachment, "Menu.Scene.Attachment.Image"),
        new("attachment.video", SceneSourceRole.Attachment, "Menu.Scene.Attachment.Video"),
        new("attachment.live2d", SceneSourceRole.Attachment, "Menu.Scene.Attachment.Live2D"),
        new("attachment.spout2", SceneSourceRole.Attachment, "Menu.Scene.Attachment.Spout2"),
        new("attachment.ndi", SceneSourceRole.Attachment, "Menu.Scene.Attachment.Ndi"),
        new(
            "attachment.virtual-camera",
            SceneSourceRole.Attachment,
            "Menu.Scene.Attachment.VirtualCamera"),
    ]);

    public ImmutableArray<SceneSourceDescriptor> Descriptors { get; }

    public bool TryGet(string id, out SceneSourceDescriptor? descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return byId.TryGetValue(id, out descriptor);
    }
}
