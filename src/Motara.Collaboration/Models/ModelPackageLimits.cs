namespace Motara.Collaboration.Models;

public sealed record ModelPackageLimits
{
    public static ModelPackageLimits Default { get; } = new(
        maxFileCount: 4096,
        maxFileBytes: 512L * 1024 * 1024,
        maxPackageBytes: 2L * 1024 * 1024 * 1024);

    public ModelPackageLimits(int maxFileCount, long maxFileBytes, long maxPackageBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPackageBytes);

        MaxFileCount = maxFileCount;
        MaxFileBytes = maxFileBytes;
        MaxPackageBytes = maxPackageBytes;
    }

    public int MaxFileCount { get; }

    public long MaxFileBytes { get; }

    public long MaxPackageBytes { get; }
}
