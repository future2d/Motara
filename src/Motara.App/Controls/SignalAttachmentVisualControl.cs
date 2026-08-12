using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using System.Runtime.InteropServices;
using Motara.App.Backgrounds;
using Motara.Media;
using Motara.Scene;

namespace Motara.App.Controls;

internal sealed record SignalAttachmentVisual(
    Guid SourceId,
    string DisplayName,
    string SourceTypeId,
    string ResourceReference,
    BackgroundVideoOptions VideoOptions,
    IBackgroundVideoPlayback Playback,
    SceneTransform Transform,
    SceneTransform LocalTransform,
    AttachmentMountMode MountMode,
    string? AnchorId,
    AttachmentModelAnchor? ModelAnchor,
    double ReferenceHeight,
    AttachmentPlacement Placement,
    bool IsLocked);

internal sealed class SignalAttachmentVisualControl : Control
{
    private const byte HitTestAlphaThreshold = 8;
    private IReadOnlyList<SignalAttachmentVisual> visuals = [];

    internal IReadOnlyList<SignalAttachmentVisual> Visuals
    {
        get => visuals;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(visuals, value))
            {
                return;
            }

            foreach (SignalAttachmentVisual visual in visuals)
            {
                visual.Playback.FrameChanged -= OnFrameChanged;
            }

            visuals = value;
            foreach (SignalAttachmentVisual visual in visuals)
            {
                visual.Playback.FrameChanged += OnFrameChanged;
            }

            InvalidateVisual();
        }
    }

    internal bool UpdateTransform(Guid sourceId, SceneTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        for (int index = 0; index < visuals.Count; index++)
        {
            SignalAttachmentVisual current = visuals[index];
            if (current.SourceId != sourceId || current.Transform == transform)
            {
                continue;
            }

            SignalAttachmentVisual[] updated = visuals.ToArray();
            updated[index] = current with { Transform = transform };
            visuals = updated;
            InvalidateVisual();
            return true;
        }

        return false;
    }

    internal bool TryGetTopmostVisual(Point point, out SignalAttachmentVisual? visual)
    {
        for (int index = visuals.Count - 1; index >= 0; index--)
        {
            SignalAttachmentVisual candidate = visuals[index];
            if (ContainsVisual(candidate, point, Bounds.Size))
            {
                visual = candidate;
                return true;
            }
        }

        visual = null;
        return false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        foreach (SignalAttachmentVisual visual in visuals)
        {
            visual.Playback.FrameChanged += OnFrameChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        foreach (SignalAttachmentVisual visual in visuals)
        {
            visual.Playback.FrameChanged -= OnFrameChanged;
        }

        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        Point center = new(Bounds.Width / 2, Bounds.Height / 2);
        foreach (SignalAttachmentVisual visual in visuals)
        {
            Bitmap bitmap;
            try
            {
                bitmap = visual.Playback.Bitmap;
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0)
            {
                continue;
            }

            double referenceHeight = visual.ReferenceHeight > 0 ? visual.ReferenceHeight : 1080;
            double offsetX = visual.Transform.X / referenceHeight * Bounds.Height;
            double offsetY = visual.Transform.Y / referenceHeight * Bounds.Height;
            double scale = Math.Max(0.001, visual.Transform.Scale);
            Matrix transform =
                Matrix.CreateTranslation(-center.X, -center.Y)
                * Matrix.CreateScale(scale, scale)
                * Matrix.CreateRotation(visual.Transform.RotationDegrees * Math.PI / 180d)
                * Matrix.CreateTranslation(center.X + offsetX, center.Y + offsetY);
            using (context.PushTransform(transform))
            {
                context.DrawImage(bitmap, new Rect(bitmap.Size), new Rect(Bounds.Size));
            }
        }
    }

    private void OnFrameChanged(object? sender, EventArgs args) => InvalidateVisual();

    internal static bool ContainsVisual(SignalAttachmentVisual visual, Point point, Size bounds)
    {
        if (!TryGetLocalPoint(visual, point, bounds, out Point local))
        {
            return false;
        }

        if (local.X < 0 || local.X > bounds.Width || local.Y < 0 || local.Y > bounds.Height)
        {
            return false;
        }

        return IsOpaqueAt(visual, local, bounds);
    }

    private static bool TryGetLocalPoint(
        SignalAttachmentVisual visual,
        Point point,
        Size bounds,
        out Point local)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            local = default;
            return false;
        }

        Point center = new(bounds.Width / 2, bounds.Height / 2);
        double referenceHeight = visual.ReferenceHeight > 0 ? visual.ReferenceHeight : 1080;
        double offsetX = visual.Transform.X / referenceHeight * bounds.Height;
        double offsetY = visual.Transform.Y / referenceHeight * bounds.Height;
        double scale = Math.Max(0.001, visual.Transform.Scale);
        double radians = -visual.Transform.RotationDegrees * Math.PI / 180d;
        double relativeX = point.X - center.X - offsetX;
        double relativeY = point.Y - center.Y - offsetY;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        local = new Point(
            center.X + ((cos * relativeX) - (sin * relativeY)) / scale,
            center.Y + ((sin * relativeX) + (cos * relativeY)) / scale);
        return true;
    }

    private static bool IsOpaqueAt(SignalAttachmentVisual visual, Point local, Size bounds)
    {
        Bitmap bitmap;
        try
        {
            bitmap = visual.Playback.Bitmap;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0)
        {
            return false;
        }

        int bytesPerPixel = Math.Max(1, (bitmap.Format?.BitsPerPixel ?? 32) / 8);
        if (bytesPerPixel < 4)
        {
            return true;
        }

        int pixelX = Math.Clamp(
            (int)Math.Floor(local.X / bounds.Width * bitmap.PixelSize.Width),
            0,
            bitmap.PixelSize.Width - 1);
        int pixelY = Math.Clamp(
            (int)Math.Floor(local.Y / bounds.Height * bitmap.PixelSize.Height),
            0,
            bitmap.PixelSize.Height - 1);
        nint buffer = Marshal.AllocHGlobal(bytesPerPixel);
        try
        {
            bitmap.CopyPixels(
                new PixelRect(pixelX, pixelY, 1, 1),
                buffer,
                bytesPerPixel,
                bytesPerPixel);
            return Marshal.ReadByte(buffer, 3) >= HitTestAlphaThreshold;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            BackgroundVideoFrameSnapshot? snapshot = visual.Playback.CaptureCurrentFrame();
            return snapshot is null
                || IsSnapshotPixelOpaque(snapshot, local, bounds);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsSnapshotPixelOpaque(
        BackgroundVideoFrameSnapshot snapshot,
        Point local,
        Size bounds)
    {
        if (snapshot.Width <= 0
            || snapshot.Height <= 0
            || snapshot.BgraPixels.Length < snapshot.Width * snapshot.Height * 4)
        {
            return true;
        }

        int pixelX = Math.Clamp(
            (int)Math.Floor(local.X / bounds.Width * snapshot.Width),
            0,
            snapshot.Width - 1);
        int pixelY = Math.Clamp(
            (int)Math.Floor(local.Y / bounds.Height * snapshot.Height),
            0,
            snapshot.Height - 1);
        int alphaOffset = ((pixelY * snapshot.Width) + pixelX) * 4 + 3;
        return snapshot.BgraPixels[alphaOffset] >= HitTestAlphaThreshold;
    }
}
