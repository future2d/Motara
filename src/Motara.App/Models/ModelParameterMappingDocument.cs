using System.Collections.Immutable;
using Motara.App.ViewModels;
using Motara.ModelRuntime.Abstractions;

namespace Motara.App.Models;

internal sealed record ModelParameterMappingDocument(
    ModelCatalogViewModel.ModelCatalogEntryViewModel Model,
    ModelCapabilities Capabilities,
    ImmutableArray<ModelParameterSettingConfiguration> ParameterSettings,
    ImmutableArray<ModelParameterMappingIssue> BindingIssues,
    bool WasGenerated)
{
    internal ModelParameterMappingDocument(
        ModelCatalogViewModel.ModelCatalogEntryViewModel model,
        ModelCapabilities capabilities,
        IEnumerable<ModelParameterSettingConfiguration> parameterSettings,
        IEnumerable<ModelParameterMappingIssue> bindingIssues,
        bool wasGenerated)
        : this(
            model,
            capabilities,
            parameterSettings.ToImmutableArray(),
            bindingIssues.ToImmutableArray(),
            wasGenerated)
    {
    }
}
