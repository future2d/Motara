namespace Motara.ModelRuntime.Abstractions;

public enum ModelRuntimeState
{
    Empty = 0,
    Loading = 1,
    Loaded = 2,
    Degraded = 3,
    Faulted = 4,
    Disposed = 5,
}
