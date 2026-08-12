using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Motara.Scene;

public readonly record struct SceneId
{
    [JsonConstructor]
    public SceneId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Scene ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static SceneId New() => new(Guid.NewGuid());
}

public sealed record SceneTransform
{
    public SceneTransform(double x, double y, double scale, double rotationDegrees)
    {
        if (!double.IsFinite(x)
            || !double.IsFinite(y)
            || !double.IsFinite(scale)
            || scale <= 0
            || !double.IsFinite(rotationDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Scene transform values must be finite and scale must be positive.");
        }

        X = x;
        Y = y;
        Scale = scale;
        RotationDegrees = rotationDegrees;
    }

    public static SceneTransform Default { get; } = new(0, 0, 1, 0);

    public double X { get; }

    public double Y { get; }

    public double Scale { get; }

    public double RotationDegrees { get; }
}

/// <summary>Combines screen-space and main-model-relative attachment transforms.</summary>
public static class AttachmentMountTransform
{
    public static SceneTransform Compose(SceneTransform parent, SceneTransform local)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(local);
        double radians = parent.RotationDegrees * Math.PI / 180d;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        return new SceneTransform(
            parent.X + ((cos * local.X) - (sin * local.Y)) * parent.Scale,
            parent.Y + ((sin * local.X) + (cos * local.Y)) * parent.Scale,
            parent.Scale * local.Scale,
            NormalizeDegrees(parent.RotationDegrees + local.RotationDegrees));
    }

    public static SceneTransform RelativeTo(SceneTransform world, SceneTransform parent)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(parent);
        if (parent.Scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parent), "Parent scale must be positive.");
        }

        double radians = -parent.RotationDegrees * Math.PI / 180d;
        double deltaX = world.X - parent.X;
        double deltaY = world.Y - parent.Y;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        return new SceneTransform(
            ((cos * deltaX) - (sin * deltaY)) / parent.Scale,
            ((sin * deltaX) + (cos * deltaY)) / parent.Scale,
            world.Scale / parent.Scale,
            NormalizeDegrees(world.RotationDegrees - parent.RotationDegrees));
    }

    public static SceneTransform ResolveWorld(
        AttachmentInstance attachment,
        MainModelInstance? mainModel)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (attachment.MountMode != AttachmentMountMode.MainModelAnchor
            || mainModel is null
            || !StringComparer.Ordinal.Equals(
                attachment.AnchorId,
                mainModel.SourceId.ToString("N")))
        {
            return attachment.Transform;
        }

        return Compose(mainModel.Transform, attachment.Transform);
    }

    private static double NormalizeDegrees(double value)
    {
        double normalized = (value + 180) % 360;
        if (normalized < 0)
        {
            normalized += 360;
        }

        return normalized - 180;
    }
}

public sealed record SceneSourceMappingOverride
{
    public const int CurrentSchemaVersion = 1;

    public SceneSourceMappingOverride(
        int schemaVersion,
        string vendorId,
        string technologyId,
        string adapterId,
        string profileId,
        string fileName)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(schemaVersion, CurrentSchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(vendorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(technologyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!StringComparer.Ordinal.Equals(fileName, Path.GetFileName(fileName)))
        {
            throw new ArgumentException("Mapping override must use a file name only.", nameof(fileName));
        }

        SchemaVersion = schemaVersion;
        VendorId = vendorId;
        TechnologyId = technologyId;
        AdapterId = adapterId;
        ProfileId = profileId;
        FileName = fileName;
    }

    public int SchemaVersion { get; }

    public string VendorId { get; }

    public string TechnologyId { get; }

    public string AdapterId { get; }

    public string ProfileId { get; }

    public string FileName { get; }
}

public sealed record MainModelInstance
{
    public MainModelInstance(
        Guid sourceId,
        string modelAssetId,
        bool isVisible,
        bool isLocked,
        SceneTransform transform,
        MainModelTrackingMode trackingMode = MainModelTrackingMode.SharedTracking,
        TrackingChannelBindings? trackingChannels = null,
        string? idleAnimationId = null)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("Source ID cannot be empty.", nameof(sourceId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(modelAssetId);
        ArgumentNullException.ThrowIfNull(transform);
        if (!Enum.IsDefined(trackingMode))
        {
            throw new ArgumentOutOfRangeException(nameof(trackingMode));
        }

        trackingChannels ??= TrackingChannelBindings.Default;
        if (trackingMode == MainModelTrackingMode.IdleAnimation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(idleAnimationId);
        }
        else if (idleAnimationId is not null)
        {
            throw new ArgumentException(
                "Only idle-animation mode can reference an idle animation.",
                nameof(idleAnimationId));
        }

        if (trackingMode != MainModelTrackingMode.SharedTracking && trackingChannels.HasAny)
        {
            throw new ArgumentException(
                "Non-tracking modes cannot subscribe to tracking channels.",
                nameof(trackingChannels));
        }

        SourceId = sourceId;
        ModelAssetId = modelAssetId;
        IsVisible = isVisible;
        IsLocked = isLocked;
        Transform = transform;
        TrackingMode = trackingMode;
        TrackingChannels = trackingChannels;
        IdleAnimationId = idleAnimationId;
    }

    public Guid SourceId { get; }

    public string ModelAssetId { get; }

    public bool IsVisible { get; }

    public bool IsLocked { get; }

    public SceneTransform Transform { get; }

    public MainModelTrackingMode TrackingMode { get; }

    public TrackingChannelBindings TrackingChannels { get; }

    public string? IdleAnimationId { get; }

    public MainModelInstance SetVisibility(bool isVisible) => IsVisible == isVisible
        ? this
        : new MainModelInstance(
            SourceId,
            ModelAssetId,
            isVisible,
            IsLocked,
            Transform,
            TrackingMode,
            TrackingChannels,
            IdleAnimationId);

    public MainModelInstance SetLock(bool isLocked) => IsLocked == isLocked
        ? this
        : new MainModelInstance(
            SourceId,
            ModelAssetId,
            IsVisible,
            isLocked,
            Transform,
            TrackingMode,
            TrackingChannels,
            IdleAnimationId);

    public MainModelInstance SetTransform(SceneTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        return Transform == transform
            ? this
            : new MainModelInstance(
                SourceId,
                ModelAssetId,
                IsVisible,
                IsLocked,
                transform,
                TrackingMode,
                TrackingChannels,
                IdleAnimationId);
    }

    public MainModelInstance SetTracking(
        MainModelTrackingMode trackingMode,
        TrackingChannelBindings trackingChannels,
        string? idleAnimationId) =>
        new(
            SourceId,
            ModelAssetId,
            IsVisible,
            IsLocked,
            Transform,
            trackingMode,
            trackingChannels,
            idleAnimationId);
}

public sealed record SceneDocument
{
    public const int CurrentSchemaVersion = 1;

    public SceneDocument(
        int schemaVersion,
        SceneId id,
        string displayName,
        double referenceHeight,
        MainModelInstance? mainModel,
        long revision,
        ImmutableArray<AttachmentInstance> attachments = default,
        SceneSourceMappingOverride? sourceMappingOverride = null,
        ImmutableArray<SceneEffectInstance> effects = default,
        Motara.Persistence.BackgroundDefinition? backgroundOverride = null)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(schemaVersion, CurrentSchemaVersion);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (!double.IsFinite(referenceHeight) || referenceHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(referenceHeight));
        }

        if (revision < 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(revision);
        }

        SchemaVersion = schemaVersion;
        Id = id;
        DisplayName = displayName;
        ReferenceHeight = referenceHeight;
        MainModel = mainModel;
        Revision = revision;
        Attachments = attachments.IsDefault ? [] : attachments;
        SourceMappingOverride = sourceMappingOverride;
        Effects = effects.IsDefault ? [] : effects;
        BackgroundOverride = backgroundOverride;
        if (Attachments.Select(static attachment => attachment.SourceId).Distinct().Count()
            != Attachments.Length)
        {
            throw new ArgumentException("Attachment source IDs must be unique.", nameof(attachments));
        }
        if (Effects.Select(static effect => effect.SourceId).Distinct().Count() != Effects.Length)
        {
            throw new ArgumentException("Effect source IDs must be unique.", nameof(effects));
        }
    }

    public int SchemaVersion { get; }

    public SceneId Id { get; }

    public string DisplayName { get; init; }

    public double ReferenceHeight { get; }

    public MainModelInstance? MainModel { get; init; }

    public long Revision { get; init; }

    public ImmutableArray<AttachmentInstance> Attachments { get; init; }

    public SceneSourceMappingOverride? SourceMappingOverride { get; init; }

    public ImmutableArray<SceneEffectInstance> Effects { get; init; }

    public Motara.Persistence.BackgroundDefinition? BackgroundOverride { get; init; }

    public static SceneDocument CreateDefault() => Create("Default");

    public static SceneDocument Create(string displayName) => new(
        CurrentSchemaVersion,
        SceneId.New(),
        displayName,
        1080,
        null,
        0);

    public SceneDocument Rename(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        return StringComparer.Ordinal.Equals(DisplayName, displayName)
            ? this
            : this with { DisplayName = displayName, Revision = Revision + 1 };
    }

    public SceneDocument SetBackgroundOverride(Motara.Persistence.BackgroundDefinition? backgroundOverride)
    {
        if (Equals(BackgroundOverride, backgroundOverride))
        {
            return this;
        }

        return this with
        {
            BackgroundOverride = backgroundOverride,
            Revision = Revision + 1,
        };
    }

    public SceneDocument AssignMainModel(string modelAssetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelAssetId);
        return this with
        {
            MainModel = new MainModelInstance(
                Guid.NewGuid(),
                modelAssetId,
                isVisible: true,
                isLocked: false,
                SceneTransform.Default),
            Revision = Revision + 1,
        };
    }

    public SceneDocument ClearMainModel() => MainModel is null
        ? this
        : this with { MainModel = null, Revision = Revision + 1 };

    public SceneDocument SetSourceMappingOverride(SceneSourceMappingOverride mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return mapping == SourceMappingOverride
            ? this
            : this with { SourceMappingOverride = mapping, Revision = Revision + 1 };
    }

    public SceneDocument ClearSourceMappingOverride() => SourceMappingOverride is null
        ? this
        : this with { SourceMappingOverride = null, Revision = Revision + 1 };

    public SceneDocument AddEffect(SceneEffectInstance effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (Effects.Any(candidate => candidate.SourceId == effect.SourceId))
        {
            throw new ArgumentException("Effect source ID already exists.", nameof(effect));
        }

        return this with { Effects = Effects.Add(effect), Revision = Revision + 1 };
    }

    public SceneDocument UpdateEffect(SceneEffectInstance effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        int index = FindEffectIndex(effect.SourceId);
        if (index < 0) throw new KeyNotFoundException("The requested effect does not exist.");
        return this with { Effects = Effects.SetItem(index, effect), Revision = Revision + 1 };
    }

    public SceneDocument RemoveEffect(Guid sourceId)
    {
        int index = FindEffectIndex(sourceId);
        if (index < 0) throw new KeyNotFoundException("The requested effect does not exist.");
        return this with { Effects = Effects.RemoveAt(index), Revision = Revision + 1 };
    }

    private int FindEffectIndex(Guid sourceId)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("Effect ID cannot be empty.", nameof(sourceId));
        }

        for (int index = 0; index < Effects.Length; index++)
        {
            if (Effects[index].SourceId == sourceId)
            {
                return index;
            }
        }

        return -1;
    }

    public SceneDocument SetMainModelVisibility(bool isVisible)
    {
        MainModelInstance current = MainModel
            ?? throw new InvalidOperationException("The scene has no main model.");
        MainModelInstance updated = current.SetVisibility(isVisible);
        return ReferenceEquals(current, updated)
            ? this
            : this with { MainModel = updated, Revision = Revision + 1 };
    }

    public SceneDocument SetMainModelLock(bool isLocked)
    {
        MainModelInstance current = MainModel
            ?? throw new InvalidOperationException("The scene has no main model.");
        MainModelInstance updated = current.SetLock(isLocked);
        return ReferenceEquals(current, updated)
            ? this
            : this with { MainModel = updated, Revision = Revision + 1 };
    }

    public SceneDocument SetMainModelTransform(SceneTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        MainModelInstance current = MainModel
            ?? throw new InvalidOperationException("The scene has no main model.");
        MainModelInstance updated = current.SetTransform(transform);
        return ReferenceEquals(current, updated)
            ? this
            : this with { MainModel = updated, Revision = Revision + 1 };
    }

    public SceneDocument SetMainModelTracking(
        MainModelTrackingMode trackingMode,
        TrackingChannelBindings trackingChannels,
        string? idleAnimationId)
    {
        MainModelInstance current = MainModel
            ?? throw new InvalidOperationException("The scene has no main model.");
        MainModelInstance updated = current.SetTracking(
            trackingMode,
            trackingChannels,
            idleAnimationId);
        return updated == current
            ? this
            : this with { MainModel = updated, Revision = Revision + 1 };
    }

    public SceneDocument AddAttachment(AttachmentInstance attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (Attachments.Any(candidate => candidate.SourceId == attachment.SourceId))
        {
            throw new ArgumentException("Attachment source ID already exists.", nameof(attachment));
        }

        return this with
        {
            Attachments = Attachments.Add(attachment),
            Revision = Revision + 1,
        };
    }

    public SceneDocument RemoveAttachment(Guid sourceId)
    {
        int index = FindAttachmentIndex(sourceId);
        return this with
        {
            Attachments = Attachments.RemoveAt(index),
            Revision = Revision + 1,
        };
    }

    public SceneDocument SetAttachmentVisibility(Guid sourceId, bool isVisible)
    {
        int index = FindAttachmentIndex(sourceId);
        AttachmentInstance current = Attachments[index];
        AttachmentInstance updated = current.SetVisibility(isVisible);
        return ReferenceEquals(current, updated)
            ? this
            : this with { Attachments = Attachments.SetItem(index, updated), Revision = Revision + 1 };
    }

    public SceneDocument SetAttachmentLock(Guid sourceId, bool isLocked)
    {
        int index = FindAttachmentIndex(sourceId);
        AttachmentInstance current = Attachments[index];
        AttachmentInstance updated = current.SetLock(isLocked);
        return ReferenceEquals(current, updated)
            ? this
            : this with { Attachments = Attachments.SetItem(index, updated), Revision = Revision + 1 };
    }

    public SceneDocument SetAttachmentTransform(Guid sourceId, SceneTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        int index = FindAttachmentIndex(sourceId);
        AttachmentInstance current = Attachments[index];
        AttachmentInstance updated = current.SetTransform(transform);
        return ReferenceEquals(current, updated)
            ? this
            : this with { Attachments = Attachments.SetItem(index, updated), Revision = Revision + 1 };
    }

    public SceneDocument SetAttachmentMountMode(
        Guid sourceId,
        AttachmentMountMode mountMode,
        string? anchorId)
    {
        int index = FindAttachmentIndex(sourceId);
        AttachmentInstance current = Attachments[index];
        AttachmentInstance updated = current.SetMountMode(mountMode, anchorId);
        return ReferenceEquals(current, updated)
            ? this
            : this with { Attachments = Attachments.SetItem(index, updated), Revision = Revision + 1 };
    }

    public SceneDocument SetAttachmentModelAnchor(
        Guid sourceId,
        AttachmentModelAnchor? anchor)
    {
        int index = FindAttachmentIndex(sourceId);
        AttachmentInstance current = Attachments[index];
        AttachmentInstance updated = current.SetModelAnchor(anchor);
        return ReferenceEquals(current, updated)
            ? this
            : this with { Attachments = Attachments.SetItem(index, updated), Revision = Revision + 1 };
    }

    public SceneDocument SetAttachmentDisplayName(Guid sourceId, string displayName)
    {
        int index = FindAttachmentIndex(sourceId);
        AttachmentInstance current = Attachments[index];
        AttachmentInstance updated = current.SetDisplayName(displayName);
        return ReferenceEquals(current, updated)
            ? this
            : this with { Attachments = Attachments.SetItem(index, updated), Revision = Revision + 1 };
    }

    public SceneDocument MoveAttachment(Guid sourceId, int destinationIndex)
    {
        int sourceIndex = FindAttachmentIndex(sourceId);
        if ((uint)destinationIndex >= (uint)Attachments.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationIndex));
        }

        if (sourceIndex == destinationIndex)
        {
            return this;
        }

        AttachmentInstance attachment = Attachments[sourceIndex];
        ImmutableArray<AttachmentInstance> withoutSource = Attachments.RemoveAt(sourceIndex);
        return this with
        {
            Attachments = withoutSource.Insert(destinationIndex, attachment),
            Revision = Revision + 1,
        };
    }

    public SceneDocument MoveAttachmentTo(
        Guid sourceId,
        AttachmentPlacement placement,
        int destinationIndex)
    {
        if (!Enum.IsDefined(placement))
        {
            throw new ArgumentOutOfRangeException(nameof(placement));
        }

        int sourceIndex = FindAttachmentIndex(sourceId);
        AttachmentInstance source = Attachments[sourceIndex];
        ImmutableArray<AttachmentInstance> remaining = Attachments.RemoveAt(sourceIndex);
        ImmutableArray<AttachmentInstance> destinationGroup = remaining
            .Where(attachment => attachment.Placement == placement)
            .ToImmutableArray();
        if ((uint)destinationIndex > (uint)destinationGroup.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationIndex));
        }

        AttachmentInstance moved = source.SetPlacement(placement);
        ImmutableArray<AttachmentInstance> reorderedGroup = destinationGroup.Insert(destinationIndex, moved);
        var result = ImmutableArray.CreateBuilder<AttachmentInstance>(remaining.Length + 1);
        int groupIndex = 0;
        foreach (AttachmentInstance attachment in remaining)
        {
            if (attachment.Placement == placement)
            {
                result.Add(reorderedGroup[groupIndex++]);
            }
            else
            {
                result.Add(attachment);
            }
        }

        if (groupIndex < reorderedGroup.Length)
        {
            result.Add(reorderedGroup[groupIndex]);
        }

        return this with
        {
            Attachments = result.ToImmutable(),
            Revision = Revision + 1,
        };
    }

    public SceneDocument MoveMainModelTo(int frontAttachmentCount)
    {
        if (MainModel is null)
        {
            throw new InvalidOperationException("A main model is required before it can be moved.");
        }

        AttachmentInstance[] visualAttachments = Attachments
            .Where(static attachment => attachment.Placement == AttachmentPlacement.AfterMainModel)
            .Reverse()
            .Concat(Attachments.Where(static attachment => attachment.Placement == AttachmentPlacement.BeforeMainModel).Reverse())
            .ToArray();
        if ((uint)frontAttachmentCount > (uint)visualAttachments.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(frontAttachmentCount));
        }

        ImmutableArray<AttachmentInstance> reordered =
        [
            .. visualAttachments
                .Take(frontAttachmentCount)
                .Select(static attachment => attachment.SetPlacement(AttachmentPlacement.AfterMainModel))
                .Reverse(),
            .. visualAttachments
                .Skip(frontAttachmentCount)
                .Select(static attachment => attachment.SetPlacement(AttachmentPlacement.BeforeMainModel))
                .Reverse(),
        ];
        return Attachments.SequenceEqual(reordered)
            ? this
            : this with { Attachments = reordered, Revision = Revision + 1 };
    }

    private int FindAttachmentIndex(Guid sourceId)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("Source ID cannot be empty.", nameof(sourceId));
        }

        for (int index = 0; index < Attachments.Length; index++)
        {
            if (Attachments[index].SourceId == sourceId)
            {
                return index;
            }
        }

        throw new KeyNotFoundException("The requested attachment does not exist.");
    }
}

public sealed record SceneWorkspace
{
    public const int CurrentSchemaVersion = 1;

    public SceneWorkspace(
        int schemaVersion,
        SceneId activeSceneId,
        ImmutableArray<SceneDocument> scenes)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(schemaVersion, CurrentSchemaVersion);
        }

        if (scenes.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one scene is required.", nameof(scenes));
        }

        if (scenes.Select(static scene => scene.Id).Distinct().Count() != scenes.Length)
        {
            throw new ArgumentException("Scene IDs must be unique.", nameof(scenes));
        }

        if (!scenes.Any(scene => scene.Id == activeSceneId))
        {
            throw new ArgumentException("The active scene must exist in the collection.", nameof(activeSceneId));
        }

        SchemaVersion = schemaVersion;
        ActiveSceneId = activeSceneId;
        Scenes = scenes;
    }

    public int SchemaVersion { get; }

    public SceneId ActiveSceneId { get; }

    public ImmutableArray<SceneDocument> Scenes { get; init; }

    public SceneDocument ActiveScene => Scenes.Single(scene => scene.Id == ActiveSceneId);

    public static SceneWorkspace CreateDefault()
    {
        SceneDocument scene = SceneDocument.CreateDefault();
        return new SceneWorkspace(CurrentSchemaVersion, scene.Id, [scene]);
    }

    public SceneWorkspace CreateScene(string displayName)
    {
        SceneDocument scene = SceneDocument.Create(displayName);
        return new SceneWorkspace(
            CurrentSchemaVersion,
            scene.Id,
            Scenes.Add(scene));
    }

    public SceneWorkspace Activate(SceneId sceneId)
    {
        EnsureSceneExists(sceneId);
        return sceneId == ActiveSceneId
            ? this
            : new SceneWorkspace(CurrentSchemaVersion, sceneId, Scenes);
    }

    public SceneWorkspace Rename(SceneId sceneId, string displayName)
    {
        EnsureSceneExists(sceneId);
        return this with
        {
            Scenes = Scenes
                .Select(scene => scene.Id == sceneId ? scene.Rename(displayName) : scene)
                .ToImmutableArray(),
        };
    }

    public SceneWorkspace RenameActive(string displayName) =>
        Rename(ActiveSceneId, displayName);

    public SceneWorkspace Delete(SceneId sceneId)
    {
        EnsureSceneExists(sceneId);
        if (Scenes.Length == 1)
        {
            throw new InvalidOperationException("The last scene cannot be deleted.");
        }

        ImmutableArray<SceneDocument> remaining = Scenes
            .Where(scene => scene.Id != sceneId)
            .ToImmutableArray();
        SceneId nextActiveId = sceneId == ActiveSceneId
            ? remaining[0].Id
            : ActiveSceneId;
        return new SceneWorkspace(CurrentSchemaVersion, nextActiveId, remaining);
    }

    public SceneWorkspace ReplaceActive(SceneDocument scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.Id != ActiveSceneId)
        {
            throw new ArgumentException("Replacement scene must be active.", nameof(scene));
        }

        return this with
        {
            Scenes = Scenes.Select(candidate => candidate.Id == scene.Id ? scene : candidate).ToImmutableArray(),
        };
    }

    public SceneWorkspace AssignMainModel(string modelAssetId) =>
        ReplaceActive(ActiveScene.AssignMainModel(modelAssetId));

    public SceneWorkspace ClearMainModel() => ReplaceActive(ActiveScene.ClearMainModel());

    public SceneWorkspace SetActiveMainModelVisibility(bool isVisible)
    {
        SceneDocument updated = ActiveScene.SetMainModelVisibility(isVisible);
        return ReferenceEquals(updated, ActiveScene) ? this : ReplaceActive(updated);
    }

    public SceneWorkspace SetActiveMainModelLock(bool isLocked)
    {
        SceneDocument updated = ActiveScene.SetMainModelLock(isLocked);
        return ReferenceEquals(updated, ActiveScene) ? this : ReplaceActive(updated);
    }

    public SceneWorkspace SetActiveMainModelTransform(SceneTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        SceneDocument updated = ActiveScene.SetMainModelTransform(transform);
        return ReferenceEquals(updated, ActiveScene) ? this : ReplaceActive(updated);
    }

    public SceneWorkspace SetActiveMainModelTracking(
        MainModelTrackingMode trackingMode,
        TrackingChannelBindings trackingChannels,
        string? idleAnimationId) =>
        ReplaceActive(ActiveScene.SetMainModelTracking(
            trackingMode,
            trackingChannels,
            idleAnimationId));

    public SceneWorkspace AddAttachment(AttachmentInstance attachment) =>
        ReplaceActive(ActiveScene.AddAttachment(attachment));

    public SceneWorkspace RemoveAttachment(Guid sourceId) =>
        ReplaceActive(ActiveScene.RemoveAttachment(sourceId));

    public SceneWorkspace SetActiveAttachmentVisibility(Guid sourceId, bool isVisible) =>
        ReplaceActive(ActiveScene.SetAttachmentVisibility(sourceId, isVisible));

    public SceneWorkspace SetActiveAttachmentLock(Guid sourceId, bool isLocked) =>
        ReplaceActive(ActiveScene.SetAttachmentLock(sourceId, isLocked));

    public SceneWorkspace SetActiveAttachmentTransform(Guid sourceId, SceneTransform transform) =>
        ReplaceActive(ActiveScene.SetAttachmentTransform(sourceId, transform));

    public SceneWorkspace SetActiveAttachmentMountMode(
        Guid sourceId,
        AttachmentMountMode mountMode,
        string? anchorId) =>
        ReplaceActive(ActiveScene.SetAttachmentMountMode(sourceId, mountMode, anchorId));

    public SceneWorkspace SetActiveAttachmentModelAnchor(
        Guid sourceId,
        AttachmentModelAnchor? anchor) =>
        ReplaceActive(ActiveScene.SetAttachmentModelAnchor(sourceId, anchor));

    public SceneWorkspace SetActiveAttachmentDisplayName(Guid sourceId, string displayName) =>
        ReplaceActive(ActiveScene.SetAttachmentDisplayName(sourceId, displayName));

    public SceneWorkspace MoveAttachment(Guid sourceId, int destinationIndex) =>
        ReplaceActive(ActiveScene.MoveAttachment(sourceId, destinationIndex));

    public SceneWorkspace MoveActiveAttachmentTo(
        Guid sourceId,
        AttachmentPlacement placement,
        int destinationIndex) =>
        ReplaceActive(ActiveScene.MoveAttachmentTo(sourceId, placement, destinationIndex));

    public SceneWorkspace MoveActiveMainModelTo(int frontAttachmentCount) =>
        ReplaceActive(ActiveScene.MoveMainModelTo(frontAttachmentCount));

    public SceneWorkspace SetActiveBackgroundOverride(
        Motara.Persistence.BackgroundDefinition? backgroundOverride)
    {
        SceneDocument updated = ActiveScene.SetBackgroundOverride(backgroundOverride);
        return ReferenceEquals(updated, ActiveScene) ? this : ReplaceActive(updated);
    }

    private void EnsureSceneExists(SceneId sceneId)
    {
        if (!Scenes.Any(scene => scene.Id == sceneId))
        {
            throw new KeyNotFoundException("The requested scene does not exist.");
        }
    }
}
