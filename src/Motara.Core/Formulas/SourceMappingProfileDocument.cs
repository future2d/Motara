using System.Collections.Immutable;
using Motara.Core.Parameters;

namespace Motara.Core.Formulas;

public sealed record SourceMappingOutputDocument(
    string ParameterId,
    string? Subtitle,
    string Formula,
    double NeutralValue,
    double SuggestedMinimum,
    double SuggestedMaximum,
    double Smoothing);

public sealed record SourceMappingProfileDocument(
    int SchemaVersion,
    string ProfileId,
    string VendorId,
    string TechnologyId,
    string AdapterId,
    string Channel,
    ImmutableArray<string> InputIds,
    ImmutableArray<SourceMappingOutputDocument> Outputs)
{
    public const int CurrentSchemaVersion = 1;

    public static SourceMappingProfileDocument Create(
        string profileId,
        string vendorId,
        string technologyId,
        string adapterId,
        string channel,
        IEnumerable<string> inputIds,
        IEnumerable<SourceMappingOutputDocument> outputs)
    {
        var document = new SourceMappingProfileDocument(
            CurrentSchemaVersion,
            profileId,
            vendorId,
            technologyId,
            adapterId,
            channel,
            ImmutableArray.CreateRange(inputIds),
            ImmutableArray.CreateRange(outputs));
        document.Validate();
        return document;
    }

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentException("Unsupported source mapping schema version.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(VendorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(TechnologyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(AdapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Channel);
        if (InputIds.IsDefault || Outputs.IsDefaultOrEmpty)
        {
            throw new ArgumentException("Source mapping inputs and outputs are required.");
        }

        if (InputIds.Any(string.IsNullOrWhiteSpace)
            || InputIds.Distinct(StringComparer.Ordinal).Count() != InputIds.Length)
        {
            throw new ArgumentException("Source mapping input identifiers must be unique.");
        }

        foreach (SourceMappingOutputDocument output in Outputs)
        {
            ArgumentNullException.ThrowIfNull(output);
            if (!GlobalParameterId.IsValid(output.ParameterId))
            {
                throw new ArgumentException($"Invalid global parameter identifier: {output.ParameterId}");
            }
            ArgumentNullException.ThrowIfNull(output.Formula);
            if (!double.IsFinite(output.NeutralValue)
                || !double.IsFinite(output.SuggestedMinimum)
                || !double.IsFinite(output.SuggestedMaximum)
                || output.SuggestedMinimum > output.NeutralValue
                || output.NeutralValue > output.SuggestedMaximum
                || output.Smoothing is < 0 or > 1)
            {
                throw new ArgumentException($"Invalid output metadata: {output.ParameterId}");
            }
        }

        if (Outputs.Select(output => output.ParameterId).Distinct(StringComparer.Ordinal).Count()
            != Outputs.Length)
        {
            throw new ArgumentException("Source mapping output identifiers must be unique.");
        }
    }

    public SourceFormulaProfile ToFormulaProfile()
    {
        Validate();
        return SourceFormulaProfile.Create(
            AdapterId,
            InputIds,
            Outputs
                .Where(static output => !string.IsNullOrWhiteSpace(output.Formula))
                .Select(output => new SourceFormulaDefinition(
                output.ParameterId,
                output.Formula,
                output.NeutralValue,
                output.SuggestedMinimum,
                output.SuggestedMaximum,
                output.Smoothing)));
    }
}
