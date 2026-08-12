using System.Collections.Immutable;
using Motara.Core.Parameters;

namespace Motara.App.Models;

internal static class StandardModelParameterMappings
{
    private static readonly ImmutableDictionary<string, string> ExplicitModelParameterIds =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BrowInnerUp"] = "BrowInnerUp",
            ["EyeLSquint"] = "EyeLSquint",
            ["EyeRSquint"] = "EyeRSquint",
            ["MouthShrug"] = "MouthShrug",
            ["MouthFunnel"] = "MouthFunnel",
            ["CheekPuff"] = "CheekPuff",
            ["JawOpen"] = "JawOpen",
            ["MouthX"] = "MouthX",
            ["MouthPressLipOpen"] = "MouthPressLipOpen",
        }.ToImmutableDictionary(StringComparer.Ordinal);

    internal static ImmutableArray<ModelParameterMapping> All { get; } =
        StandardParameterCatalog.Definitions
            .Select(definition => new ModelParameterMapping(
                definition.Id,
                ExplicitModelParameterIds.GetValueOrDefault(definition.Id, $"Param{definition.Id}")))
            .ToImmutableArray();
}
