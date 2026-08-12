using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Motara.App.Collaboration;
using Motara.App.Models;
using Motara.ModelRuntime.Abstractions;

namespace Motara.App.Controls;

/// <summary>Draws runtime-only remote member sources above the local main model.</summary>
public sealed class RemoteModelCanvas : Control
{
    private readonly DispatcherTimer frameTimer;
    private RemoteMemberModelSourceRegistry? registry;
    private int preparing;

    public RemoteModelCanvas()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        frameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1d / 60d) };
        frameTimer.Tick += (_, _) => PrepareAndInvalidate();
    }

    internal void Attach(RemoteMemberModelSourceRegistry value)
    {
        registry = value ?? throw new ArgumentNullException(nameof(value));
        if (VisualRoot is not null)
        {
            frameTimer.Start();
        }

        InvalidateVisual();
    }

    public override void Render(Avalonia.Media.DrawingContext context)
    {
        base.Render(context);
        RemoteMemberModelSourceRegistry? currentRegistry = registry;
        if (currentRegistry is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        PixelSize pixelSize = PixelSize.FromSize(Bounds.Size, scaling);
        foreach (RemoteMemberModelSource source in currentRegistry.Sources)
        {
            if (!source.IsVisible
                || source.RenderableRuntime is not { } runtime
                || runtime.ModelRuntime.CurrentFrame is not { } frame)
            {
                continue;
            }

            try
            {
                context.Custom(runtime.Renderer.CreateDrawOperation(frame, pixelSize, scaling));
            }
            catch (ObjectDisposedException)
            {
                // A withdrawn member may be released between the snapshot and render pass.
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (registry is not null)
        {
            frameTimer.Start();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        frameTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void PrepareAndInvalidate()
    {
        RemoteMemberModelSourceRegistry? currentRegistry = registry;
        if (currentRegistry is null)
        {
            return;
        }

        bool requiresPreparation = currentRegistry.Sources.Any(static source =>
            source.RenderableRuntime?.Renderer is IModelFramePreparationTarget
            {
                RequiresFramePreparation: true,
            });
        if (!requiresPreparation || Interlocked.Exchange(ref preparing, 1) != 0)
        {
            InvalidateVisual();
            return;
        }

        _ = PrepareAsync(currentRegistry);
    }

    private async Task PrepareAsync(RemoteMemberModelSourceRegistry currentRegistry)
    {
        try
        {
            PixelSize pixelSize = PixelSize.FromSize(
                Bounds.Size,
                TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
            foreach (RemoteMemberModelSource source in currentRegistry.Sources)
            {
                if (source.RenderableRuntime is not { } runtime
                    || runtime.ModelRuntime.CurrentFrame is not { } frame
                    || runtime.Renderer is not IModelFramePreparationTarget
                    {
                        RequiresFramePreparation: true,
                    })
                {
                    continue;
                }

                await runtime.Renderer.PrepareFrameAsync(
                    frame,
                    pixelSize,
                    CancellationToken.None).ConfigureAwait(false);
            }

            await Dispatcher.UIThread.InvokeAsync(InvalidateVisual);
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            Interlocked.Exchange(ref preparing, 0);
        }
    }

}
