using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.ModelLibrary;
using Motara.Persistence;
using Motara.Scene;

namespace Motara.App.Shortcuts;

internal enum ShortcutProfileOwnerKind
{
    Software,
    Scene,
    Model,
}

internal sealed record ShortcutProfileOwner(
    ShortcutProfileOwnerKind Kind,
    string TargetPath,
    string StableId)
{
    internal static ShortcutProfileOwner Software(string targetPath) => new(
        ShortcutProfileOwnerKind.Software,
        Path.GetFullPath(targetPath),
        "software");

    internal static ShortcutProfileOwner Scene(SceneId sceneId, string sceneRoot) => new(
        ShortcutProfileOwnerKind.Scene,
        SceneStorageLayout.GetInputBindingsPath(sceneRoot, sceneId),
        sceneId.Value.ToString("N"));

    internal static ShortcutProfileOwner Model(ModelId modelId, string modelRoot) => new(
        ShortcutProfileOwnerKind.Model,
        Path.Combine(
            Path.GetFullPath(modelRoot),
            "motara",
            "input-bindings.motara.json"),
        modelId.Value);
}

internal sealed class ShortcutProfileStore
{
    private readonly string softwarePath;
    private readonly ILogger<ShortcutProfileStore> logger;

    internal ShortcutProfileStore(
        string softwarePath,
        ILogger<ShortcutProfileStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(softwarePath);
        this.softwarePath = Path.GetFullPath(softwarePath);
        this.logger = logger ?? NullLogger<ShortcutProfileStore>.Instance;
    }

    internal Task<ShortcutProfile> LoadAsync(
        ShortcutProfileOwner owner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return LoadCoreAsync(owner, cancellationToken);
    }

    internal Task SaveAsync(
        ShortcutProfileOwner owner,
        ShortcutProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(profile);
        return SaveCoreAsync(owner, profile, cancellationToken);
    }

    private async Task<ShortcutProfile> LoadCoreAsync(
        ShortcutProfileOwner owner,
        CancellationToken cancellationToken)
    {
        string path = ResolvePath(owner);
        try
        {
            ShortcutProfile profile = await new ShortcutStore(path)
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            ShortcutOwnerKind expectedOwner = MapOwner(owner.Kind);
            if (profile.Entries.Any(entry => entry.Owner != expectedOwner))
                throw new ArgumentException("Shortcut profile contains an entry owned by another layer.");
            ShortcutProfileStoreLog.Loaded(
                logger,
                owner.Kind.ToString(),
                owner.StableId,
                profile.Entries.Length);
            return profile;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ShortcutProfileStoreLog.LoadFailed(
                logger,
                owner.Kind.ToString(),
                owner.StableId,
                exception.GetType().Name);
            return ShortcutProfile.Default;
        }
    }

    private async Task SaveCoreAsync(
        ShortcutProfileOwner owner,
        ShortcutProfile profile,
        CancellationToken cancellationToken)
    {
        string path = ResolvePath(owner);
        ShortcutOwnerKind expectedOwner = MapOwner(owner.Kind);
        if (profile.Entries.Any(entry => entry.Owner != expectedOwner))
            throw new ArgumentException("Shortcut profile contains an entry owned by another layer.", nameof(profile));
        await new ShortcutStore(path)
            .SaveAsync(profile, cancellationToken)
            .ConfigureAwait(false);
        ShortcutProfileStoreLog.Saved(
            logger,
            owner.Kind.ToString(),
            owner.StableId,
            profile.Entries.Length);
    }

    private string ResolvePath(ShortcutProfileOwner owner) =>
        owner.Kind == ShortcutProfileOwnerKind.Software
            ? softwarePath
            : owner.TargetPath;

    private static ShortcutOwnerKind MapOwner(ShortcutProfileOwnerKind owner) => owner switch
    {
        ShortcutProfileOwnerKind.Software => ShortcutOwnerKind.Software,
        ShortcutProfileOwnerKind.Scene => ShortcutOwnerKind.Scene,
        ShortcutProfileOwnerKind.Model => ShortcutOwnerKind.Model,
        _ => throw new ArgumentOutOfRangeException(nameof(owner)),
    };
}

internal static partial class ShortcutProfileStoreLog
{
    [LoggerMessage(2040, LogLevel.Information,
        "Shortcut profile loaded for {OwnerKind}:{OwnerId} with {BindingCount} bindings")]
    internal static partial void Loaded(
        ILogger logger,
        string ownerKind,
        string ownerId,
        int bindingCount);

    [LoggerMessage(2041, LogLevel.Warning,
        "Shortcut profile load failed for {OwnerKind}:{OwnerId}: {ErrorType}")]
    internal static partial void LoadFailed(
        ILogger logger,
        string ownerKind,
        string ownerId,
        string errorType);

    [LoggerMessage(2042, LogLevel.Information,
        "Shortcut profile saved for {OwnerKind}:{OwnerId} with {BindingCount} bindings")]
    internal static partial void Saved(
        ILogger logger,
        string ownerKind,
        string ownerId,
        int bindingCount);
}
