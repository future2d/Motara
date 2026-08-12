using Motara.Core.Formulas;
using Motara.Tracking.Abstractions;

namespace Motara.App.Tracking;

internal sealed record SourceMappingAdapterContext(
    string AdapterId,
    string SourceId,
    string DisplayNameResourceKey,
    Func<SourceMappingProfileDocument> CreateBuiltInProfile,
    IReadOnlyList<TrackingInputDefinition> Inputs,
    SourceMappingProfileStore Store,
    Action<SourceMappingProfileDocument> ConfigureMapping)
{
    internal SourceMappingProfileDocument CreateBuiltIn() => CreateBuiltInProfile();
}
