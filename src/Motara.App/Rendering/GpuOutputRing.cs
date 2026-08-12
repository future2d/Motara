namespace Motara.App.Rendering;

internal enum GpuOutputSlotState
{
    Free,
    Rendering,
    PendingFence,
    Ready,
    Presenting,
}

internal sealed class GpuOutputRing
{
    private sealed class Slot
    {
        internal GpuOutputSlotState State;
        internal long Generation;
        internal long PresentationEpoch;
        internal long ReadySequence;
    }

    private readonly object gate = new();
    private readonly Slot[] slots;
    private int lastPresentedIndex = -1;
    private long nextReadySequence;

    internal GpuOutputRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        slots = Enumerable.Range(0, capacity).Select(static _ => new Slot()).ToArray();
    }

    internal int AcquireForRender()
    {
        lock (gate)
        {
            int index = -1;
            for (int candidate = 0; candidate < slots.Length; candidate++)
            {
                if (slots[candidate].State == GpuOutputSlotState.Free
                    && candidate != lastPresentedIndex)
                {
                    index = candidate;
                    break;
                }
            }
            if (index < 0 && lastPresentedIndex >= 0 && slots[lastPresentedIndex].State == GpuOutputSlotState.Free)
            {
                index = lastPresentedIndex;
            }
            if (index < 0)
            {
                index = FindOldestReady();
            }

            if (index < 0)
            {
                return -1;
            }

            slots[index].State = GpuOutputSlotState.Rendering;
            return index;
        }
    }

    internal void MarkPendingFence(int index, long generation) =>
        Transition(index, GpuOutputSlotState.Rendering, GpuOutputSlotState.PendingFence, generation);

    internal void MarkReady(int index, long generation, long presentationEpoch = 0)
    {
        lock (gate)
        {
            ValidateIndex(index);
            if (slots[index].State is not (GpuOutputSlotState.Rendering or GpuOutputSlotState.PendingFence))
            {
                throw new InvalidOperationException("Only rendered output can become ready.");
            }

            slots[index].Generation = generation;
            slots[index].PresentationEpoch = presentationEpoch;
            slots[index].ReadySequence = ++nextReadySequence;
            slots[index].State = GpuOutputSlotState.Ready;
        }
    }

    internal bool TryTakeLatestReady(out int index) => TryTakeLatestReady(0, out index);

    internal bool TryTakeLatestReady(long currentPresentationEpoch, out int index)
    {
        lock (gate)
        {
            index = -1;
            long newestGeneration = long.MinValue;
            long newestSequence = long.MinValue;
            for (int candidate = 0; candidate < slots.Length; candidate++)
            {
                if (slots[candidate].State != GpuOutputSlotState.Ready)
                {
                    continue;
                }

                if (currentPresentationEpoch != 0
                    && slots[candidate].PresentationEpoch != currentPresentationEpoch)
                {
                    slots[candidate].State = GpuOutputSlotState.Free;
                    continue;
                }

                if (slots[candidate].Generation > newestGeneration
                    || (slots[candidate].Generation == newestGeneration
                        && slots[candidate].ReadySequence >= newestSequence))
                {
                    newestGeneration = slots[candidate].Generation;
                    newestSequence = slots[candidate].ReadySequence;
                    index = candidate;
                }
            }

            if (index < 0)
            {
                return false;
            }

            for (int candidate = 0; candidate < slots.Length; candidate++)
            {
                if (candidate != index && slots[candidate].State == GpuOutputSlotState.Ready)
                {
                    slots[candidate].State = GpuOutputSlotState.Free;
                }
            }

            slots[index].State = GpuOutputSlotState.Presenting;
            return true;
        }
    }

    internal void MarkPresented(int index) =>
        MarkPresentationComplete(index);

    internal void MarkPresentationFailed(int index) => MarkPresentationComplete(index);

    internal void ReleaseRenderBuffer(int index) =>
        Transition(index, GpuOutputSlotState.Rendering, GpuOutputSlotState.Free, 0);

    internal void DropInvalidatedReadyBuffers(long currentPresentationEpoch)
    {
        lock (gate)
        {
            foreach (Slot slot in slots)
            {
                if (slot.State == GpuOutputSlotState.Ready
                    && slot.PresentationEpoch != currentPresentationEpoch)
                {
                    slot.State = GpuOutputSlotState.Free;
                }
            }
        }
    }

    internal void DropReadyBuffers()
    {
        lock (gate)
        {
            foreach (Slot slot in slots)
            {
                if (slot.State == GpuOutputSlotState.Ready)
                {
                    slot.State = GpuOutputSlotState.Free;
                }
            }
        }
    }

    private int FindOldestReady()
    {
        int index = -1;
        long oldest = long.MaxValue;
        for (int candidate = 0; candidate < slots.Length; candidate++)
        {
            if (slots[candidate].State == GpuOutputSlotState.Ready
                && slots[candidate].Generation < oldest)
            {
                oldest = slots[candidate].Generation;
                index = candidate;
            }
        }

        return index;
    }

    private void Transition(
        int index,
        GpuOutputSlotState expected,
        GpuOutputSlotState next,
        long generation)
    {
        lock (gate)
        {
            ValidateIndex(index);
            if (slots[index].State != expected)
            {
                throw new InvalidOperationException($"Output slot {index} is {slots[index].State}, not {expected}.");
            }

            slots[index].Generation = generation;
            slots[index].State = next;
        }
    }

    private void MarkPresentationComplete(int index)
    {
        lock (gate)
        {
            ValidateIndex(index);
            if (slots[index].State != GpuOutputSlotState.Presenting)
            {
                throw new InvalidOperationException("Only a presenting output can complete.");
            }

            slots[index].State = GpuOutputSlotState.Free;
            lastPresentedIndex = index;
        }
    }

    private void ValidateIndex(int index) => ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, slots.Length);
}
