using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Motara.Core.Parameters;
using Motara.Core.Sessions;
using Motara.App.Parameters;
using Motara.ModelRuntime.Abstractions;
using Motara.Tracking.Abstractions;

namespace Motara.App.Models;

internal sealed class ModelParameterBinding
{
    private readonly ImmutableArray<Route> routes;
    private readonly ImmutableArray<double> defaultValues;
    private readonly ImmutableArray<ModelParameter> parameters;
    private readonly int sourceCount;

    private ModelParameterBinding(
        ImmutableArray<Route> routes,
        ImmutableArray<double> defaultValues,
        ImmutableArray<ModelParameter> parameters,
        ImmutableArray<ModelParameterMappingIssue> issues,
        int sourceCount)
    {
        this.routes = routes;
        this.defaultValues = defaultValues;
        this.parameters = parameters;
        Issues = issues;
        this.sourceCount = sourceCount;
    }

    internal ImmutableArray<ModelParameterMappingIssue> Issues { get; }

    internal int RouteCount => routes.Length;

    internal bool HasAutomaticProviders => routes.Any(static route =>
        route.EnableAutoBlink || route.EnableAutoBreath);

    internal static ModelParameterBinding Create(
        ModelCapabilities capabilities,
        ImmutableArray<ParameterSample> sourceLayout)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        HashSet<string> availableTargets = capabilities.Parameters
            .Select(static parameter => parameter.Id)
            .ToHashSet(StringComparer.Ordinal);
        return CreateCore(
            capabilities,
            sourceLayout,
            StandardModelParameterMappings.All
                .Where(mapping => availableTargets.Contains(mapping.ModelParameterId))
                .Select(mapping => CreateDefaultSetting(capabilities, mapping)));
    }

    internal static ModelParameterBinding Create(
        ModelCapabilities capabilities,
        ImmutableArray<ParameterSample> sourceLayout,
        IEnumerable<ModelParameterMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        return CreateCore(
            capabilities,
            sourceLayout,
            mappings.Select(mapping => CreateDefaultSetting(capabilities, mapping)));
    }

    internal static ModelParameterBinding Create(
        ModelCapabilities capabilities,
        ImmutableArray<ParameterSample> sourceLayout,
        IEnumerable<ModelParameterSettingConfiguration> settings) =>
        CreateCore(capabilities, sourceLayout, settings);

    internal ModelParameterUpdate Bind(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Parameters.Length != sourceCount)
        {
            throw new ArgumentException("Snapshot parameter layout changed.", nameof(snapshot));
        }

        var values = ImmutableArray.CreateBuilder<ModelParameterValue>(routes.Length);
        foreach (Route route in routes)
        {
            if (route.SourceSlot < 0)
            {
                continue;
            }

            ParameterSample source = snapshot.Parameters[route.SourceSlot];
            if (source.Validity != ParameterValidity.Valid || !double.IsFinite(source.Value))
            {
                continue;
            }

            values.Add(new ModelParameterValue(
                route.TargetSlot,
                route.Map(source.Value)));
        }

        ImmutableArray<ModelParameterValue> result = values.ToImmutable();
        return new ModelParameterUpdate(snapshot.Revision, result.AsSpan());
    }

    internal ModelParameterUpdate Bind(
        SessionSnapshot snapshot,
        TimeSpan elapsed,
        ParameterArbitrator arbitrator)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(arbitrator);
        if (snapshot.Parameters.Length != sourceCount)
        {
            throw new ArgumentException("Snapshot parameter layout changed.", nameof(snapshot));
        }

        var values = ImmutableArray.CreateBuilder<ModelParameterValue>(routes.Length);
        Span<ParameterContribution> contributions = stackalloc ParameterContribution[3];
        foreach (Route route in routes)
        {
            int count = 0;
            if (route.SourceSlot >= 0)
            {
                ParameterSample source = snapshot.Parameters[route.SourceSlot];
                if (source.Validity == ParameterValidity.Valid && double.IsFinite(source.Value))
                {
                    contributions[count++] = new(
                        route.TargetSlot,
                        route.Map(source.Value),
                        ParameterProviderKind.Tracking);
                }
            }

            if (route.EnableAutoBreath)
            {
                contributions[count++] = new(
                    route.TargetSlot,
                    AutomaticParameterProvider.GetBreathValue(route.Setting, elapsed),
                    ParameterProviderKind.AutoBreath);
            }

            if (route.EnableAutoBlink
                && AutomaticParameterProvider.TryGetBlinkValue(route.Setting, elapsed, out double blink))
            {
                contributions[count++] = new(
                    route.TargetSlot,
                    blink,
                    ParameterProviderKind.AutoBlink);
            }

            ResolvedParameterValue resolved = arbitrator.Resolve(
                route.TargetSlot,
                route.DefaultValue,
                contributions[..count]);
            values.Add(new ModelParameterValue(route.TargetSlot, route.ClampFinal(resolved.Value)));
        }

        ImmutableArray<ModelParameterValue> result = values.ToImmutable();
        return new ModelParameterUpdate(snapshot.Revision, result.AsSpan());
    }

    internal ImmutableArray<double> GetBaselineValues(
        SessionSnapshot snapshot,
        TimeSpan elapsed,
        ParameterArbitrator arbitrator)
    {
        double[] values = defaultValues.ToArray();
        foreach (ModelParameterValue value in Bind(snapshot, elapsed, arbitrator).Values)
        {
            values[value.ParameterIndex] = value.Value;
        }

        return values.ToImmutableArray();
    }

    internal ImmutableArray<double> GetBaselineValues(
        SessionSnapshot snapshot,
        TimeSpan elapsed,
        ParameterArbitrator arbitrator,
        ImmutableArray<ParameterContribution> animation)
    {
        ModelParameterUpdate update = Bind(
            snapshot,
            elapsed,
            arbitrator,
            animation,
            [],
            []);
        double[] values = defaultValues.ToArray();
        foreach (ModelParameterValue value in update.Values)
        {
            values[value.ParameterIndex] = value.Value;
        }

        return values.ToImmutableArray();
    }

    internal ModelParameterUpdate Bind(
        SessionSnapshot snapshot,
        TimeSpan elapsed,
        ParameterArbitrator arbitrator,
        ImmutableArray<ParameterContribution> physics)
    {
        return Bind(snapshot, elapsed, arbitrator, [], physics, []);
    }

    internal ModelParameterUpdate Bind(
        SessionSnapshot snapshot,
        TimeSpan elapsed,
        ParameterArbitrator arbitrator,
        ImmutableArray<ParameterContribution> animation,
        ImmutableArray<ParameterContribution> physics,
        ImmutableArray<ModelPartOpacity> partOpacities)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(arbitrator);
        if (snapshot.Parameters.Length != sourceCount)
        {
            throw new ArgumentException("Snapshot parameter layout changed.", nameof(snapshot));
        }

        var externalByTarget = new Dictionary<int, List<ParameterContribution>>();
        AddExternalContributions(externalByTarget, animation);
        AddExternalContributions(externalByTarget, physics);
        var handled = new bool[defaultValues.Length];
        var values = ImmutableArray.CreateBuilder<ModelParameterValue>(defaultValues.Length);
        Span<ParameterContribution> contributions = stackalloc ParameterContribution[6];
        foreach (Route route in routes)
        {
            int count = AddBaseContributions(snapshot, elapsed, route, contributions);
            if (externalByTarget.TryGetValue(route.TargetSlot, out List<ParameterContribution>? external))
            {
                foreach (ParameterContribution contribution in external)
                {
                    contributions[count++] = contribution;
                }
            }

            ResolvedParameterValue resolved = arbitrator.Resolve(
                route.TargetSlot,
                route.DefaultValue,
                contributions[..count]);
            values.Add(new ModelParameterValue(route.TargetSlot, route.ClampFinal(resolved.Value)));
            handled[route.TargetSlot] = true;
        }

        foreach ((int target, List<ParameterContribution> external) in externalByTarget)
        {
            if ((uint)target >= (uint)defaultValues.Length || handled[target])
            {
                continue;
            }

            double resolved = arbitrator.Resolve(target, defaultValues[target], CollectionsMarshal.AsSpan(external)).Value;
            ModelParameter parameter = parameters[target];
            values.Add(new ModelParameterValue(
                target,
                Math.Clamp(resolved, parameter.Minimum, parameter.Maximum)));
        }

        ImmutableArray<ModelParameterValue> result = values.ToImmutable();
        return new ModelParameterUpdate(snapshot.Revision, result.AsSpan(), partOpacities.AsSpan());
    }

    internal ImmutableArray<ModelParameterObservation> Observe(
        SessionSnapshot snapshot,
        ModelParameterUpdate update)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(update);
        Dictionary<int, double> outputs = update.Values.ToDictionary(
            static value => value.ParameterIndex,
            static value => value.Value);
        var observations = ImmutableArray.CreateBuilder<ModelParameterObservation>(routes.Length);
        foreach (Route route in routes)
        {
            double? input = null;
            if (route.SourceSlot >= 0)
            {
                ParameterSample sample = snapshot.Parameters[route.SourceSlot];
                if (sample.Validity == ParameterValidity.Valid && double.IsFinite(sample.Value))
                {
                    input = sample.Value;
                }
            }

            observations.Add(new ModelParameterObservation(
                route.Setting.ModelParameterId,
                route.Setting.GlobalParameterId,
                input,
                outputs.TryGetValue(route.TargetSlot, out double output) ? output : null));
        }

        return observations.ToImmutable();
    }

    private static ModelParameterBinding CreateCore(
        ModelCapabilities capabilities,
        ImmutableArray<ParameterSample> sourceLayout,
        IEnumerable<ModelParameterSettingConfiguration> settings)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (sourceLayout.IsDefault)
        {
            throw new ArgumentException("Source layout must be initialized.", nameof(sourceLayout));
        }

        ArgumentNullException.ThrowIfNull(settings);
        ImmutableArray<ModelParameterSettingConfiguration> configured = settings.ToImmutableArray();
        ValidateUniqueTargets(configured);
        Dictionary<string, int> sourceSlots = CreateSourceSlots(sourceLayout);
        Dictionary<string, (ModelParameter Parameter, int Index)> targetSlots = capabilities.Parameters
            .Select(static (parameter, index) => (parameter, Index: index))
            .ToDictionary(
                static pair => pair.parameter.Id,
                static pair => (pair.parameter, pair.Index),
                StringComparer.Ordinal);
        var routes = ImmutableArray.CreateBuilder<Route>();
        var issues = ImmutableArray.CreateBuilder<ModelParameterMappingIssue>();
        foreach (ModelParameterSettingConfiguration setting in configured)
        {
            if (!targetSlots.TryGetValue(setting.ModelParameterId, out var target))
            {
                issues.Add(new ModelParameterMappingIssue(
                    ModelParameterMappingIssueCode.MissingModelParameter,
                    setting.GlobalParameterId ?? string.Empty,
                    setting.ModelParameterId));
                continue;
            }

            int sourceSlot = -1;
            if (setting.GlobalParameterId is not null
                && !sourceSlots.TryGetValue(setting.GlobalParameterId, out sourceSlot))
            {
                issues.Add(new ModelParameterMappingIssue(
                    ModelParameterMappingIssueCode.MissingSoftwareParameter,
                    setting.GlobalParameterId,
                    setting.ModelParameterId));
            }

            routes.Add(new Route(
                sourceSlot,
                target.Index,
                target.Parameter.Default,
                setting,
                setting.InputMinimum,
                setting.InputMaximum,
                setting.OutputMinimum,
                setting.OutputMaximum,
                setting.ClampInput,
                setting.ClampOutput));
        }

        return new ModelParameterBinding(
            routes.ToImmutable(),
            capabilities.Parameters.Select(static parameter => parameter.Default).ToImmutableArray(),
            capabilities.Parameters,
            issues.ToImmutable(),
            sourceLayout.Length);
    }

    private static void AddExternalContributions(
        Dictionary<int, List<ParameterContribution>> byTarget,
        ImmutableArray<ParameterContribution> contributions)
    {
        foreach (ParameterContribution contribution in contributions)
        {
            if (!double.IsFinite(contribution.Value)
                || !Enum.IsDefined(contribution.Provider)
                || contribution.ParameterIndex < 0)
            {
                continue;
            }

            if (!byTarget.TryGetValue(contribution.ParameterIndex, out List<ParameterContribution>? target))
            {
                target = [];
                byTarget.Add(contribution.ParameterIndex, target);
            }

            int existing = target.FindIndex(candidate => candidate.Provider == contribution.Provider);
            if (existing >= 0)
            {
                target[existing] = contribution;
            }
            else
            {
                target.Add(contribution);
            }
        }
    }

    private static int AddBaseContributions(
        SessionSnapshot snapshot,
        TimeSpan elapsed,
        Route route,
        Span<ParameterContribution> contributions)
    {
        int count = 0;
        if (route.SourceSlot >= 0)
        {
            ParameterSample source = snapshot.Parameters[route.SourceSlot];
            if (source.Validity == ParameterValidity.Valid && double.IsFinite(source.Value))
            {
                contributions[count++] = new(route.TargetSlot, route.Map(source.Value), ParameterProviderKind.Tracking);
            }
        }

        if (route.EnableAutoBreath)
        {
            contributions[count++] = new(route.TargetSlot,
                AutomaticParameterProvider.GetBreathValue(route.Setting, elapsed), ParameterProviderKind.AutoBreath);
        }

        if (route.EnableAutoBlink
            && AutomaticParameterProvider.TryGetBlinkValue(route.Setting, elapsed, out double blink))
        {
            contributions[count++] = new(route.TargetSlot, blink, ParameterProviderKind.AutoBlink);
        }

        return count;
    }

    private static Dictionary<string, int> CreateSourceSlots(ImmutableArray<ParameterSample> sourceLayout)
    {
        var sourceSlots = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < sourceLayout.Length; index++)
        {
            ParameterSample parameter = sourceLayout[index]
                ?? throw new ArgumentException("Source layout cannot contain null values.", nameof(sourceLayout));
            if (!sourceSlots.TryAdd(parameter.Id, index))
            {
                throw new ArgumentException(
                    $"Duplicate software parameter in source layout: {parameter.Id}",
                    nameof(sourceLayout));
            }
        }

        return sourceSlots;
    }

    private static void ValidateUniqueTargets(
        ImmutableArray<ModelParameterSettingConfiguration> settings)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (ModelParameterSettingConfiguration setting in settings)
        {
            ArgumentNullException.ThrowIfNull(setting);
            if (!targets.Add(setting.ModelParameterId))
            {
                throw new ArgumentException(
                    $"Duplicate model parameter setting: {setting.ModelParameterId}",
                    nameof(settings));
            }
        }
    }

    private static ModelParameterSettingConfiguration CreateDefaultSetting(
        ModelCapabilities capabilities,
        ModelParameterMapping mapping)
    {
        ModelParameter? target = capabilities.Parameters.FirstOrDefault(parameter =>
            StringComparer.Ordinal.Equals(parameter.Id, mapping.ModelParameterId));
        ParameterDefinition? definition = StandardParameterCatalog.Definitions.FirstOrDefault(parameter =>
            StringComparer.Ordinal.Equals(parameter.Id, mapping.SourceParameterId));
        double inputMinimum = definition?.SuggestedMinimum ?? target?.Minimum ?? -1;
        double inputMaximum = definition?.SuggestedMaximum ?? target?.Maximum ?? 1;
        return new ModelParameterSettingConfiguration(
            mapping.ModelParameterId,
            mapping.SourceParameterId,
            inputMinimum,
            inputMaximum,
            target?.Minimum ?? inputMinimum,
            target?.Maximum ?? inputMaximum,
            ClampInput: false,
            ClampOutput: false,
            EnableAutoBlink: mapping.ModelParameterId is "ParamEyeLOpen" or "ParamEyeROpen",
            EnableAutoBreath: mapping.ModelParameterId == "ParamBreath");
    }

    private readonly record struct Route(
        int SourceSlot,
        int TargetSlot,
        double DefaultValue,
        ModelParameterSettingConfiguration Setting,
        double InputMinimum,
        double InputMaximum,
        double OutputMinimum,
        double OutputMaximum,
        bool ClampInput,
        bool ClampOutput)
    {
        internal bool EnableAutoBlink => Setting.EnableAutoBlink;

        internal bool EnableAutoBreath => Setting.EnableAutoBreath;

        internal double Map(double input)
        {
            double prepared = ClampInput
                ? Math.Clamp(input, InputMinimum, InputMaximum)
                : input;
            double slope = prepared switch
            {
                > 0 when InputMaximum > 0 => OutputMaximum / InputMaximum,
                > 0 => OutputMinimum / InputMinimum,
                < 0 when InputMinimum < 0 => OutputMinimum / InputMinimum,
                < 0 => OutputMaximum / InputMaximum,
                _ => 0,
            };
            double output = prepared * slope;
            return ClampOutput
                ? Math.Clamp(output, OutputMinimum, OutputMaximum)
                : output;
        }

        internal double ClampFinal(double value) => ClampOutput
            ? Math.Clamp(value, OutputMinimum, OutputMaximum)
            : value;
    }
}
