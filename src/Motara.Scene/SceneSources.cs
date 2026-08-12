using System.Text.Json.Serialization;
using Motara.Persistence;
using Motara.Media;

namespace Motara.Scene;

public enum MainModelTrackingMode
{
    SharedTracking = 0,
    IdleAnimation = 1,
    Manual = 2,
}

public sealed record TrackingChannelBindings(bool Face, bool Hand, bool Body)
{
    public static TrackingChannelBindings Default { get; } = new(
        Face: true,
        Hand: false,
        Body: false);

    public static TrackingChannelBindings None { get; } = new(
        Face: false,
        Hand: false,
        Body: false);

    public bool HasAny => Face || Hand || Body;
}

public enum AttachmentMountMode
{
    Canvas = 0,
    MainModelAnchor = 1,
}

public enum AttachmentPlacement
{
    BeforeMainModel = 0,
    AfterMainModel = 1,
}

public enum AttachmentModelAnchorKind
{
    ModelPlane = 0,
    ArtMesh = 1,
}

public sealed record AttachmentModelAnchor
{
    [JsonConstructor]
    public AttachmentModelAnchor(
        AttachmentModelAnchorKind kind,
        double planeX,
        double planeY,
        string? artMeshId,
        int triangleIndex,
        double barycentricU,
        double barycentricV)
    {
        if (!Enum.IsDefined(kind)
            || !double.IsFinite(planeX)
            || !double.IsFinite(planeY)
            || !double.IsFinite(barycentricU)
            || !double.IsFinite(barycentricV))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (kind == AttachmentModelAnchorKind.ModelPlane)
        {
            if (artMeshId is not null
                || triangleIndex != -1
                || barycentricU != 0
                || barycentricV != 0)
            {
                throw new ArgumentException("Model-plane anchors cannot carry ArtMesh geometry.");
            }
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(artMeshId);
            if (planeX != 0
                || planeY != 0
                || triangleIndex < -1
                || barycentricU < 0
                || barycentricV < 0
                || barycentricU + barycentricV > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(barycentricU));
            }
        }

        Kind = kind;
        PlaneX = planeX;
        PlaneY = planeY;
        ArtMeshId = artMeshId?.Trim();
        TriangleIndex = triangleIndex;
        BarycentricU = barycentricU;
        BarycentricV = barycentricV;
    }

    public AttachmentModelAnchorKind Kind { get; }

    public double PlaneX { get; }

    public double PlaneY { get; }

    public string? ArtMeshId { get; }

    public int TriangleIndex { get; }

    public double BarycentricU { get; }

    public double BarycentricV { get; }

    public static AttachmentModelAnchor ForModelPlane(double x, double y) =>
        new(AttachmentModelAnchorKind.ModelPlane, x, y, null, -1, 0, 0);

    public static AttachmentModelAnchor ForArtMesh(
        string artMeshId,
        int triangleIndex,
        double barycentricU,
        double barycentricV) =>
        new(
            AttachmentModelAnchorKind.ArtMesh,
            0,
            0,
            artMeshId,
            triangleIndex,
            barycentricU,
            barycentricV);
}

public sealed record AttachmentInstance
{
    public AttachmentInstance(
        Guid sourceId,
        string sourceTypeId,
        string resourceReference,
        AttachmentMountMode mountMode,
        string? anchorId,
        AttachmentPlacement placement,
        bool isVisible,
        bool isLocked,
        SceneTransform transform,
        BackgroundVideoOptions? videoOptions = null,
        string? displayName = null,
        AttachmentModelAnchor? modelAnchor = null)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("Source ID cannot be empty.", nameof(sourceId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTypeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceReference);
        if (!Enum.IsDefined(mountMode))
        {
            throw new ArgumentOutOfRangeException(nameof(mountMode));
        }

        if (!Enum.IsDefined(placement))
        {
            throw new ArgumentOutOfRangeException(nameof(placement));
        }

        if (mountMode == AttachmentMountMode.MainModelAnchor)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(anchorId);
        }
        else if (anchorId is not null)
        {
            throw new ArgumentException("Canvas attachments cannot carry a model anchor.", nameof(anchorId));
        }

        if (mountMode != AttachmentMountMode.MainModelAnchor && modelAnchor is not null)
        {
            throw new ArgumentException(
                "Canvas attachments cannot carry a model anchor.",
                nameof(modelAnchor));
        }

        ArgumentNullException.ThrowIfNull(transform);
        SourceId = sourceId;
        SourceTypeId = sourceTypeId;
        ResourceReference = resourceReference;
        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? Path.GetFileName(resourceReference)
            : displayName.Trim();
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            DisplayName = resourceReference;
        }
        MountMode = mountMode;
        AnchorId = anchorId;
        ModelAnchor = modelAnchor;
        Placement = placement;
        IsVisible = isVisible;
        IsLocked = isLocked;
        Transform = transform;
        VideoOptions = videoOptions ?? BackgroundVideoOptions.Default;
    }

    public Guid SourceId { get; }

    public string SourceTypeId { get; }

    public string ResourceReference { get; }

    public string DisplayName { get; init; }

    public AttachmentMountMode MountMode { get; }

    public string? AnchorId { get; }

    public AttachmentModelAnchor? ModelAnchor { get; }

    public AttachmentPlacement Placement { get; init; }

    public bool IsVisible { get; init; }

    public bool IsLocked { get; init; }

    public SceneTransform Transform { get; }

    public BackgroundVideoOptions VideoOptions { get; }

    public AttachmentInstance SetPlacement(AttachmentPlacement placement)
    {
        if (!Enum.IsDefined(placement))
        {
            throw new ArgumentOutOfRangeException(nameof(placement));
        }

        return Placement == placement ? this : this with { Placement = placement };
    }

    public AttachmentInstance SetVisibility(bool isVisible) =>
        IsVisible == isVisible ? this : this with { IsVisible = isVisible };

    public AttachmentInstance SetLock(bool isLocked) =>
        IsLocked == isLocked ? this : this with { IsLocked = isLocked };

    public AttachmentInstance SetTransform(SceneTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        return Transform == transform
            ? this
            : new AttachmentInstance(
                SourceId,
                SourceTypeId,
                ResourceReference,
                MountMode,
                AnchorId,
                Placement,
                IsVisible,
                IsLocked,
                transform,
                VideoOptions,
                DisplayName,
                ModelAnchor);
    }

    public AttachmentInstance SetMountMode(AttachmentMountMode mountMode, string? anchorId)
    {
        if (!Enum.IsDefined(mountMode))
        {
            throw new ArgumentOutOfRangeException(nameof(mountMode));
        }

        return MountMode == mountMode && StringComparer.Ordinal.Equals(AnchorId, anchorId)
            ? this
            : new AttachmentInstance(
                SourceId,
                SourceTypeId,
                ResourceReference,
                mountMode,
                anchorId,
                Placement,
                IsVisible,
                IsLocked,
                Transform,
                VideoOptions,
                DisplayName,
                mountMode == AttachmentMountMode.MainModelAnchor ? ModelAnchor : null);
    }

    public AttachmentInstance SetModelAnchor(AttachmentModelAnchor? anchor)
    {
        if (MountMode != AttachmentMountMode.MainModelAnchor)
        {
            throw new InvalidOperationException(
                "A model anchor requires MainModelAnchor mode.");
        }

        return EqualityComparer<AttachmentModelAnchor?>.Default.Equals(ModelAnchor, anchor)
            ? this
            : new AttachmentInstance(
                SourceId,
                SourceTypeId,
                ResourceReference,
                MountMode,
                AnchorId,
                Placement,
                IsVisible,
                IsLocked,
                Transform,
                VideoOptions,
                DisplayName,
                anchor);
    }

    public AttachmentInstance SetDisplayName(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        string normalized = displayName.Trim();
        if (normalized.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(displayName));
        }

        return StringComparer.Ordinal.Equals(DisplayName, normalized)
            ? this
            : this with { DisplayName = normalized };
    }

    public static AttachmentInstance Create(
        string sourceTypeId,
        string resourceReference,
        AttachmentPlacement placement = AttachmentPlacement.AfterMainModel,
        BackgroundVideoOptions? videoOptions = null,
        string? displayName = null) =>
        new(
            Guid.NewGuid(),
            sourceTypeId,
            resourceReference,
            AttachmentMountMode.Canvas,
            anchorId: null,
            placement,
            isVisible: true,
            isLocked: false,
            SceneTransform.Default,
            videoOptions,
            displayName);
}
