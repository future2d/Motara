using Motara.Tracking.Abstractions;

namespace Motara.App.ViewModels;

public sealed record SourceMappingInputItem(
    TrackingInputDefinition Definition,
    string Subtitle)
{
    public string Id => Definition.Id;

    public string Category => Definition.Category;
}
