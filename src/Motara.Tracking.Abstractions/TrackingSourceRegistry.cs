using System.Collections.Immutable;

namespace Motara.Tracking.Abstractions;

/// <summary>Identifies an independently selectable tracking data channel.</summary>
public enum TrackingChannel
{
    Face = 0,
    Hand = 1,
    Body = 2,
}

public sealed record TrackingTechnologyDescriptor
{
    public TrackingTechnologyDescriptor(
        string id,
        string displayNameResourceKey,
        string iconResourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayNameResourceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(iconResourceKey);
        Id = id;
        DisplayNameResourceKey = displayNameResourceKey;
        IconResourceKey = iconResourceKey;
    }

    public string Id { get; }

    public string DisplayNameResourceKey { get; }

    public string IconResourceKey { get; }
}

public enum TrackingResourceSharing
{
    None = 0,
    CompatibleSettings = 1,
}

/// <summary>Describes one transport that can provide one or more tracking channels.</summary>
public sealed class TrackingSourceDescriptor
{
    public TrackingSourceDescriptor(
        string id,
        string technologyId,
        string displayNameResourceKey,
        string iconResourceKey,
        IEnumerable<TrackingChannel> channels,
        bool isDeveloperOnly = false)
        : this(
            id,
            new TrackingTechnologyDescriptor(
                technologyId,
                $"Menu.Tracking.Technology.{technologyId}",
                iconResourceKey),
            displayNameResourceKey,
            iconResourceKey,
            channels,
            rawParameterSchemaVersion: 1,
            resourceSharing: TrackingResourceSharing.None,
            isDeveloperOnly)
    {
    }

    public TrackingSourceDescriptor(
        string id,
        TrackingTechnologyDescriptor technology,
        string displayNameResourceKey,
        string iconResourceKey,
        IEnumerable<TrackingChannel> channels,
        int rawParameterSchemaVersion,
        TrackingResourceSharing resourceSharing,
        bool isDeveloperOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(technology);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayNameResourceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(iconResourceKey);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rawParameterSchemaVersion);
        if (!Enum.IsDefined(resourceSharing))
        {
            throw new ArgumentOutOfRangeException(nameof(resourceSharing));
        }

        ImmutableArray<TrackingChannel> supportedChannels = channels.Distinct().ToImmutableArray();
        if (supportedChannels.IsEmpty)
        {
            throw new ArgumentException("A tracking source must support at least one channel.", nameof(channels));
        }

        if (supportedChannels.Any(channel => !Enum.IsDefined(channel)))
        {
            throw new ArgumentException("A tracking source contains an invalid channel.", nameof(channels));
        }

        Id = id;
        Technology = technology;
        DisplayNameResourceKey = displayNameResourceKey;
        IconResourceKey = iconResourceKey;
        Channels = supportedChannels;
        RawParameterSchemaVersion = rawParameterSchemaVersion;
        ResourceSharing = resourceSharing;
        IsDeveloperOnly = isDeveloperOnly;
    }

    public string Id { get; }

    public TrackingTechnologyDescriptor Technology { get; }

    public string TechnologyId => Technology.Id;

    public string DisplayNameResourceKey { get; }

    public string IconResourceKey { get; }

    public ImmutableArray<TrackingChannel> Channels { get; }

    public int RawParameterSchemaVersion { get; }

    public TrackingResourceSharing ResourceSharing { get; }

    public bool IsDeveloperOnly { get; }
}

/// <summary>Reports whether a tracking transport can start on the current system.</summary>
public sealed record TrackingSourceAvailability(bool IsAvailable, string? ReasonCode)
{
    public static TrackingSourceAvailability Available { get; } = new(true, null);

    public static TrackingSourceAvailability Unavailable(string reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        return new TrackingSourceAvailability(false, reasonCode);
    }
}

/// <summary>Checks and creates isolated source instances for one transport.</summary>
public interface ITrackingSourceFactory
{
    TrackingSourceDescriptor Descriptor { get; }

    ValueTask<TrackingSourceAvailability> CheckAvailabilityAsync(
        TrackingChannel channel,
        CancellationToken cancellationToken);

    ValueTask<ITrackingSource> CreateAsync(
        TrackingChannel channel,
        CancellationToken cancellationToken);
}

/// <summary>Owns the immutable set of discoverable tracking transports.</summary>
public sealed class TrackingSourceRegistry
{
    private readonly ImmutableArray<ITrackingSourceFactory> factories;
    private readonly ImmutableArray<TrackingTechnologyDescriptor> technologies;
    private readonly Dictionary<string, ITrackingSourceFactory> factoriesById;

    public TrackingSourceRegistry(IEnumerable<ITrackingSourceFactory> factories)
        : this(null, factories, hasExplicitTechnologies: false)
    {
    }

    public TrackingSourceRegistry(
        IEnumerable<TrackingTechnologyDescriptor> technologies,
        IEnumerable<ITrackingSourceFactory> factories)
        : this(
            technologies ?? throw new ArgumentNullException(nameof(technologies)),
            factories,
            hasExplicitTechnologies: true)
    {
    }

    private TrackingSourceRegistry(
        IEnumerable<TrackingTechnologyDescriptor>? technologies,
        IEnumerable<ITrackingSourceFactory> factories,
        bool hasExplicitTechnologies = false)
    {
        ArgumentNullException.ThrowIfNull(factories);
        this.factories = factories.ToImmutableArray();
        var byId = new Dictionary<string, ITrackingSourceFactory>(StringComparer.Ordinal);
        foreach (ITrackingSourceFactory factory in this.factories)
        {
            ArgumentNullException.ThrowIfNull(factory);
            if (!byId.TryAdd(factory.Descriptor.Id, factory))
            {
                throw new ArgumentException(
                    $"Duplicate tracking source id '{factory.Descriptor.Id}'.",
                    nameof(factories));
            }
        }

        factoriesById = byId;
        this.technologies = hasExplicitTechnologies
            ? ValidateExplicitTechnologies(technologies!, this.factories)
            : InferTechnologies(this.factories);
    }

    public ImmutableArray<TrackingTechnologyDescriptor> Technologies => technologies;

    public ImmutableArray<TrackingSourceDescriptor> GetDescriptors(
        TrackingChannel channel,
        bool includeDeveloper)
    {
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        return factories
            .Select(factory => factory.Descriptor)
            .Where(descriptor => descriptor.Channels.Contains(channel)
                && (includeDeveloper || !descriptor.IsDeveloperOnly))
            .ToImmutableArray();
    }

    public ImmutableArray<TrackingTechnologyDescriptor> GetTechnologies(
        TrackingChannel channel,
        bool includeDeveloper)
    {
        ImmutableHashSet<string> technologyIds = GetDescriptors(channel, includeDeveloper)
            .Select(static descriptor => descriptor.TechnologyId)
            .ToImmutableHashSet(StringComparer.Ordinal);
        return technologies
            .Where(technology => technologyIds.Contains(technology.Id))
            .ToImmutableArray();
    }

    public bool TryGetFactory(string id, out ITrackingSourceFactory? factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return factoriesById.TryGetValue(id, out factory);
    }

    private static ImmutableArray<TrackingTechnologyDescriptor> InferTechnologies(
        ImmutableArray<ITrackingSourceFactory> factories)
    {
        var byId = new Dictionary<string, TrackingTechnologyDescriptor>(StringComparer.Ordinal);
        foreach (TrackingTechnologyDescriptor technology in factories.Select(
            static factory => factory.Descriptor.Technology))
        {
            if (byId.TryGetValue(technology.Id, out TrackingTechnologyDescriptor? existing))
            {
                if (existing != technology)
                {
                    throw new ArgumentException(
                        $"Conflicting tracking technology metadata for '{technology.Id}'.",
                        nameof(factories));
                }

                continue;
            }

            byId.Add(technology.Id, technology);
        }

        return [.. byId.Values];
    }

    private static ImmutableArray<TrackingTechnologyDescriptor> ValidateExplicitTechnologies(
        IEnumerable<TrackingTechnologyDescriptor> technologyValues,
        ImmutableArray<ITrackingSourceFactory> factories)
    {
        ImmutableArray<TrackingTechnologyDescriptor> materialized = technologyValues.ToImmutableArray();
        var byId = new Dictionary<string, TrackingTechnologyDescriptor>(StringComparer.Ordinal);
        foreach (TrackingTechnologyDescriptor technology in materialized)
        {
            ArgumentNullException.ThrowIfNull(technology);
            if (!byId.TryAdd(technology.Id, technology))
            {
                throw new ArgumentException(
                    $"Duplicate tracking technology id '{technology.Id}'.",
                    nameof(technologyValues));
            }
        }

        foreach (ITrackingSourceFactory factory in factories)
        {
            if (!byId.TryGetValue(factory.Descriptor.TechnologyId, out TrackingTechnologyDescriptor? registered)
                || registered != factory.Descriptor.Technology)
            {
                throw new ArgumentException(
                    $"Tracking source '{factory.Descriptor.Id}' references an unregistered technology.",
                    nameof(factories));
            }
        }

        return materialized;
    }
}
