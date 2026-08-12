using Motara.Tracking.Abstractions;

namespace Motara.App.Tracking;

internal sealed record TrackingChannelSelections
{
    internal const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? FaceSourceId { get; init; }

    public string? HandSourceId { get; init; }

    public string? BodySourceId { get; init; }

    internal static TrackingChannelSelections Default { get; } = new();

    internal int ConfiguredChannelCount =>
        (FaceSourceId is not null ? 1 : 0)
        + (HandSourceId is not null ? 1 : 0)
        + (BodySourceId is not null ? 1 : 0);

    internal string? GetSourceId(TrackingChannel channel) => channel switch
    {
        TrackingChannel.Face => FaceSourceId,
        TrackingChannel.Hand => HandSourceId,
        TrackingChannel.Body => BodySourceId,
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };

    internal TrackingChannelSelections WithSource(TrackingChannel channel, string? sourceId)
    {
        if (sourceId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        }

        return channel switch
        {
            TrackingChannel.Face => this with { FaceSourceId = sourceId },
            TrackingChannel.Hand => this with { HandSourceId = sourceId },
            TrackingChannel.Body => this with { BodySourceId = sourceId },
            _ => throw new ArgumentOutOfRangeException(nameof(channel)),
        };
    }

    internal TrackingChannelSelections Normalize(
        TrackingSourceRegistry registry,
        bool includeDeveloper)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            FaceSourceId = NormalizeSource(registry, TrackingChannel.Face, FaceSourceId, includeDeveloper),
            HandSourceId = NormalizeSource(registry, TrackingChannel.Hand, HandSourceId, includeDeveloper),
            BodySourceId = NormalizeSource(registry, TrackingChannel.Body, BodySourceId, includeDeveloper),
        };
    }

    internal static void Validate(TrackingChannelSelections selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentOutOfRangeException.ThrowIfNotEqual(
            selections.SchemaVersion,
            CurrentSchemaVersion);
        if (selections.FaceSourceId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(selections.FaceSourceId);
        }

        if (selections.HandSourceId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(selections.HandSourceId);
        }

        if (selections.BodySourceId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(selections.BodySourceId);
        }
    }

    private static string? NormalizeSource(
        TrackingSourceRegistry registry,
        TrackingChannel channel,
        string? sourceId,
        bool includeDeveloper)
    {
        if (sourceId is null
            || !registry.TryGetFactory(sourceId, out ITrackingSourceFactory? factory)
            || factory is null
            || !factory.Descriptor.Channels.Contains(channel)
            || (!includeDeveloper && factory.Descriptor.IsDeveloperOnly))
        {
            return null;
        }

        return sourceId;
    }
}
