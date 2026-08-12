namespace Motara.App.Rendering;

// Compatibility facade for the existing Composition presenter. Ownership is
// maintained by the same latest-wins ring used by the GPU-primary pipeline.
internal sealed class GpuCompositionOutputBufferCoordinator
{
    private readonly GpuOutputRing ring;

    internal GpuCompositionOutputBufferCoordinator(int bufferCount) =>
        ring = new GpuOutputRing(bufferCount);

    internal int TryAcquireRenderBuffer() => ring.AcquireForRender();

    internal void MarkReady(int index, long generation, long presentationEpoch = 0) =>
        ring.MarkReady(index, generation, presentationEpoch);

    internal void ReleaseRenderBuffer(int index) => ring.ReleaseRenderBuffer(index);

    internal bool TryTakeLatestReady(long currentPresentationEpoch, out int index) =>
        ring.TryTakeLatestReady(currentPresentationEpoch, out index);

    internal void MarkPresented(int index) => ring.MarkPresented(index);

    internal void MarkPresentationFailed(int index) => ring.MarkPresentationFailed(index);

    internal void DropInvalidatedReadyBuffers(long currentPresentationEpoch) =>
        ring.DropInvalidatedReadyBuffers(currentPresentationEpoch);

    internal void DropReadyBuffers() => ring.DropReadyBuffers();
}
