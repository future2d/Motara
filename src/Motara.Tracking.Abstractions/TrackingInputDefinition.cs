namespace Motara.Tracking.Abstractions;

public enum TrackingInputUnit
{
    Unitless = 0,
    Percent = 1,
    Degrees = 2,
    Position = 3,
}

public sealed record TrackingInputDefinition(
    string Id,
    string Category,
    string DisplayNameResourceKey,
    TrackingInputUnit Unit,
    double SuggestedMinimum,
    double SuggestedMaximum);
