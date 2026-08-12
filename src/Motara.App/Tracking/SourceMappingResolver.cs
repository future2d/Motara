using Motara.Core.Formulas;

namespace Motara.App.Tracking;

internal static class SourceMappingResolver
{
    internal static SourceMappingProfileDocument Resolve(
        SourceMappingProfileDocument? sceneOverride,
        SourceMappingProfileDocument? modelSelection,
        SourceMappingProfileDocument? globalProfile,
        SourceMappingProfileDocument defaultProfile) =>
        sceneOverride
        ?? modelSelection
        ?? globalProfile
        ?? defaultProfile
        ?? throw new ArgumentNullException(nameof(defaultProfile));
}
