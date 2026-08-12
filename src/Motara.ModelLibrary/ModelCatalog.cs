using System.Collections.Immutable;
using Motara.ModelRuntime.Abstractions;

namespace Motara.ModelLibrary;

public enum ModelCatalogStatus
{
    Empty = 0,
    Ready = 1,
    Faulted = 2,
}

public sealed record ModelCatalogEntry(
    ModelId Id,
    string DisplayName,
    string DescriptorPath,
    ModelDescriptor? Descriptor,
    ModelError? Error)
{
    public bool IsSelectable => Descriptor is not null && Error is null;
}

public sealed record ModelCatalogSnapshot
{
    public static ModelCatalogSnapshot Empty { get; } = new(
        0,
        ModelCatalogStatus.Empty,
        [],
        null);

    public ModelCatalogSnapshot(
        long revision,
        ModelCatalogStatus status,
        ImmutableArray<ModelCatalogEntry> entries,
        ModelError? error)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (entries.IsDefault)
        {
            throw new ArgumentException("Catalog entries must be initialized.", nameof(entries));
        }

        Revision = revision;
        Status = status;
        Entries = entries;
        Error = error;
    }

    public long Revision { get; }

    public ModelCatalogStatus Status { get; }

    public ImmutableArray<ModelCatalogEntry> Entries { get; }

    public ModelError? Error { get; }
}

public interface IModelCatalog
{
    ModelCatalogSnapshot Current { get; }

    Task<ModelCatalogSnapshot> RefreshAsync(CancellationToken cancellationToken);
}

public sealed record ModelCatalogLimits
{
    public static ModelCatalogLimits Default { get; } = new(
        maxDepth: 32,
        maxFiles: 20_000,
        maxDescriptorBytes: 16 * 1024 * 1024);

    public ModelCatalogLimits(int maxDepth, int maxFiles, long maxDescriptorBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFiles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDescriptorBytes);

        MaxDepth = maxDepth;
        MaxFiles = maxFiles;
        MaxDescriptorBytes = maxDescriptorBytes;
    }

    public int MaxDepth { get; }

    public int MaxFiles { get; }

    public long MaxDescriptorBytes { get; }
}
