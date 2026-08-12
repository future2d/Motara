namespace Motara.Tracking.Abstractions;

/// <summary>Identifies one shareable provider-owned resource and its compatible settings.</summary>
public sealed record TrackingResourceRequest
{
    public TrackingResourceRequest(
        string providerId,
        string resourceKind,
        string compatibilityKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(compatibilityKey);
        ProviderId = providerId;
        ResourceKind = resourceKind;
        CompatibilityKey = compatibilityKey;
    }

    public string ProviderId { get; }

    public string ResourceKind { get; }

    public string CompatibilityKey { get; }
}

/// <summary>Indicates that a requested shared resource has incompatible active settings.</summary>
public sealed class TrackingResourceConflictException : InvalidOperationException
{
    public TrackingResourceConflictException()
        : base("The tracking resource is already active with incompatible settings.")
    {
    }
}

/// <summary>Owns one reference to a provider-owned shared resource.</summary>
public interface ITrackingResourceLease<out TResource> : IAsyncDisposable
{
    TResource Resource { get; }
}

/// <summary>Coordinates asynchronous shared resource leases across tracking channels.</summary>
public interface ITrackingResourceCoordinator : IAsyncDisposable
{
    ValueTask<ITrackingResourceLease<TResource>> AcquireAsync<TResource>(
        TrackingChannel channel,
        TrackingResourceRequest request,
        Func<CancellationToken, ValueTask<TResource>> createAsync,
        Func<TResource, ValueTask> disposeAsync,
        CancellationToken cancellationToken);
}
