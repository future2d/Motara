using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Core.Formulas;
using Motara.Core.Parameters;
using Motara.Tracking.Abstractions;

namespace Motara.Tracking.iFacialMocap;

/// <summary>Describes and creates configured iFacialMocap UDP tracking sources.</summary>
public sealed class IFacialMocapTrackingSourceFactory : ITrackingSourceFactory
{
    private IFacialMocapOptions? options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<IFacialMocapTrackingSource> logger;
    private CompiledSourceFormulaProgram formulaProgram = SourceFormulaCompiler.Compile(
        IFacialMocapMappingDefaults.CreateProfile().ToFormulaProfile());

    /// <summary>Creates a factory that is unavailable until options are supplied.</summary>
    public IFacialMocapTrackingSourceFactory(
        IFacialMocapOptions? options,
        TimeProvider timeProvider,
        ILogger<IFacialMocapTrackingSource>? logger = null)
    {
        this.options = options;
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? NullLogger<IFacialMocapTrackingSource>.Instance;
    }

    /// <inheritdoc />
    public TrackingSourceDescriptor Descriptor { get; } = new(
        IFacialMocapTrackingSource.SourceId,
        new TrackingTechnologyDescriptor(
            "apple-arkit",
            "Menu.Tracking.Technology.AppleArkit",
            "Icon.Lucide.ScanFace"),
        "Menu.Tracking.Source.IFacialMocap",
        "Icon.Lucide.Radio",
        [TrackingChannel.Face],
        rawParameterSchemaVersion: 1,
        resourceSharing: TrackingResourceSharing.None);

    /// <summary>Atomically replaces the options used for newly created sources.</summary>
    public void Configure(IFacialMocapOptions configuredOptions)
    {
        ArgumentNullException.ThrowIfNull(configuredOptions);
        Volatile.Write(ref options, configuredOptions);
    }

    /// <summary>Validates and atomically replaces the mapping used by newly created sources.</summary>
    public void ConfigureMapping(SourceMappingProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!StringComparer.Ordinal.Equals(document.AdapterId, "ifacialmocap")
            || !document.InputIds.SequenceEqual(IFacialMocapInputSchema.InputIds))
        {
            throw new ArgumentException("The mapping does not match the iFacialMocap input schema.", nameof(document));
        }

        foreach (SourceMappingOutputDocument output in document.Outputs)
        {
            if (!StandardParameterCatalog.Registry.TryGetSlot(output.ParameterId, out int slot))
            {
                continue;
            }

            ParameterDefinition standard = StandardParameterCatalog.Definitions[slot];
            if (standard.NeutralValue != output.NeutralValue
                || standard.SuggestedMinimum != output.SuggestedMinimum
                || standard.SuggestedMaximum != output.SuggestedMaximum)
            {
                throw new ArgumentException(
                    $"Mapping metadata conflicts with the built-in parameter: {output.ParameterId}",
                    nameof(document));
            }
        }

        CompiledSourceFormulaProgram compiled = SourceFormulaCompiler.Compile(document.ToFormulaProfile());
        Volatile.Write(ref formulaProgram, compiled);
    }

    /// <inheritdoc />
    public ValueTask<TrackingSourceAvailability> CheckAvailabilityAsync(
        TrackingChannel channel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (channel != TrackingChannel.Face)
        {
            return ValueTask.FromResult(
                TrackingSourceAvailability.Unavailable("tracking.channel.unsupported"));
        }

        return ValueTask.FromResult(Volatile.Read(ref options) is null
            ? TrackingSourceAvailability.Unavailable("tracking.ifacialmocap.not_configured")
            : TrackingSourceAvailability.Available);
    }

    /// <inheritdoc />
    public ValueTask<ITrackingSource> CreateAsync(
        TrackingChannel channel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (channel != TrackingChannel.Face)
        {
            throw new InvalidOperationException("iFacialMocap supports only the face channel.");
        }

        IFacialMocapOptions? configuredOptions = Volatile.Read(ref options);
        if (configuredOptions is null)
        {
            throw new InvalidOperationException("iFacialMocap is not configured.");
        }

        return ValueTask.FromResult<ITrackingSource>(
            new IFacialMocapTrackingSource(
                configuredOptions,
                timeProvider,
                logger,
                Volatile.Read(ref formulaProgram)));
    }
}
