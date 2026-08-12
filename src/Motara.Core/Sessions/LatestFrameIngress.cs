using System.Threading.Channels;
using Motara.Tracking.Abstractions;

namespace Motara.Core.Sessions;

/// <summary>Owns a capacity-one input channel with latest-frame-wins behavior.</summary>
public sealed class LatestFrameIngress
{
    private readonly Channel<RawTrackingFrame> channel = Channel.CreateBounded<RawTrackingFrame>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
        });
    private readonly object writerGate = new();
    private long droppedFrameCount;

    /// <summary>Gets the exact number of unprocessed frames displaced by newer input.</summary>
    public long DroppedFrameCount => Interlocked.Read(ref droppedFrameCount);

    /// <summary>
    /// Publishes a frame from the session's single active tracking publisher, replacing an
    /// older unprocessed frame when necessary.
    /// </summary>
    public void Publish(RawTrackingFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (writerGate)
        {
            if (channel.Writer.TryWrite(frame))
            {
                return;
            }

            if (channel.Reader.TryRead(out _))
            {
                Interlocked.Increment(ref droppedFrameCount);
            }

            if (!channel.Writer.TryWrite(frame))
            {
                throw new InvalidOperationException("Capacity-one ingress could not accept a frame after replacement.");
            }
        }
    }

    /// <summary>Attempts to consume the current latest frame.</summary>
    public bool TryRead(out RawTrackingFrame? frame) => channel.Reader.TryRead(out frame);

    /// <summary>Discards the pending frame before its source layout changes.</summary>
    public int Clear()
    {
        lock (writerGate)
        {
            return channel.Reader.TryRead(out _) ? 1 : 0;
        }
    }
}
