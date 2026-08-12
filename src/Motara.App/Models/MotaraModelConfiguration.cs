using System.Collections.Immutable;

namespace Motara.App.Models;

internal sealed record ModelParameterBindingConfiguration(
    string SourceParameterId,
    string ModelParameterId);

internal sealed record ModelParameterSettingConfiguration(
    string ModelParameterId,
    string? GlobalParameterId,
    double InputMinimum,
    double InputMaximum,
    double OutputMinimum,
    double OutputMaximum,
    bool ClampInput,
    bool ClampOutput,
    bool EnableAutoBlink,
    bool EnableAutoBreath);

internal sealed record ModelSourceMappingSelection(
    string VendorId,
    string TechnologyId,
    string AdapterId,
    string ProfileId,
    string FileName,
    bool IsEnabled,
    string Channel = "face");

internal sealed record ModelFileLayoutConfiguration(string? Preview)
{
    internal void Validate()
    {
        if (Preview is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Preview)
            || Preview.Contains('\\', StringComparison.Ordinal)
            || !StringComparer.Ordinal.Equals(
                Path.GetDirectoryName(Preview)?.Replace('\\', '/'),
                "motara/assets")
            || !StringComparer.Ordinal.Equals(Path.GetFileName(Preview), "preview.png"))
        {
            throw new ArgumentException("Preview must use the canonical Motara model asset path.", nameof(Preview));
        }
    }
}

internal enum ModelIdleMotionMode
{
    Automatic,
    None,
    Asset,
}

internal sealed record ModelIdleMotionSelection(ModelIdleMotionMode Mode, string? AssetId)
{
    internal static ModelIdleMotionSelection Automatic { get; } = new(ModelIdleMotionMode.Automatic, null);

    internal static ModelIdleMotionSelection None { get; } = new(ModelIdleMotionMode.None, null);

    internal static ModelIdleMotionSelection Asset(string assetId) =>
        new(ModelIdleMotionMode.Asset, assetId);

    internal void Validate() => ValidateAssetSelection(Mode == ModelIdleMotionMode.Asset, AssetId);

    private static void ValidateAssetSelection(bool requiresAsset, string? assetId)
    {
        if (requiresAsset != (assetId is not null))
        {
            throw new ArgumentException("The idle motion selection has an invalid asset ID.");
        }

        if (assetId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        }
    }
}

internal enum ModelLostTrackingIdleMotionMode
{
    UseRegularIdle,
    None,
    Asset,
}

internal sealed record ModelLostTrackingIdleMotionSelection(
    ModelLostTrackingIdleMotionMode Mode,
    string? AssetId)
{
    internal static ModelLostTrackingIdleMotionSelection UseRegularIdle { get; } =
        new(ModelLostTrackingIdleMotionMode.UseRegularIdle, null);

    internal static ModelLostTrackingIdleMotionSelection None { get; } =
        new(ModelLostTrackingIdleMotionMode.None, null);

    internal static ModelLostTrackingIdleMotionSelection Asset(string assetId) =>
        new(ModelLostTrackingIdleMotionMode.Asset, assetId);

    internal void Validate()
    {
        bool requiresAsset = Mode == ModelLostTrackingIdleMotionMode.Asset;
        if (requiresAsset != (AssetId is not null))
        {
            throw new ArgumentException("The lost-tracking idle motion selection has an invalid asset ID.");
        }

        if (AssetId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(AssetId);
        }
    }
}

internal sealed record MotaraModelConfiguration(
    int SchemaVersion,
    string ModelId,
    Guid CollaborationModelInstanceId,
    ModelPhysicsConfiguration Physics,
    ImmutableArray<ModelParameterSettingConfiguration> ParameterSettings,
    ImmutableArray<ModelSourceMappingSelection> SourceMappingSelections,
    ModelFileLayoutConfiguration? FileLayout = null,
    string? Nickname = null,
    ModelIdleMotionSelection IdleMotion = null!,
    ModelLostTrackingIdleMotionSelection LostTrackingIdleMotion = null!)
{
    internal const int CurrentSchemaVersion = 1;

    internal static MotaraModelConfiguration Create(
        string modelId,
        IEnumerable<ModelParameterSettingConfiguration>? parameterSettings = null,
        IEnumerable<ModelSourceMappingSelection>? sourceMappingSelections = null,
        Guid? collaborationModelInstanceId = null,
        ModelPhysicsConfiguration? physics = null,
        ModelFileLayoutConfiguration? fileLayout = null,
        string? nickname = null,
        ModelIdleMotionSelection? idleMotion = null,
        ModelLostTrackingIdleMotionSelection? lostTrackingIdleMotion = null)
    {
        var configuration = new MotaraModelConfiguration(
            CurrentSchemaVersion,
            modelId,
            collaborationModelInstanceId ?? Guid.NewGuid(),
            physics ?? ModelPhysicsConfiguration.Default,
            parameterSettings?.ToImmutableArray() ?? [],
            sourceMappingSelections?.ToImmutableArray() ?? [],
            fileLayout,
            nickname,
            idleMotion ?? ModelIdleMotionSelection.Automatic,
            lostTrackingIdleMotion ?? ModelLostTrackingIdleMotionSelection.UseRegularIdle);
        configuration.Validate();
        return configuration;
    }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(SchemaVersion, CurrentSchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(ModelId);
        if (CollaborationModelInstanceId == Guid.Empty)
        {
            throw new ArgumentException("A collaboration model instance ID is required.");
        }
        ArgumentNullException.ThrowIfNull(Physics);
        Physics.Validate();
        FileLayout?.Validate();
        if (Nickname is not null
            && (Nickname.Length > 128
                || Nickname.Length == 0
                || !StringComparer.Ordinal.Equals(Nickname, Nickname.Trim())))
        {
            throw new ArgumentException("Model nickname must be trimmed and contain at most 128 characters.");
        }
        ArgumentNullException.ThrowIfNull(IdleMotion);
        ArgumentNullException.ThrowIfNull(LostTrackingIdleMotion);
        IdleMotion.Validate();
        LostTrackingIdleMotion.Validate();
        if (ParameterSettings.IsDefault || SourceMappingSelections.IsDefault)
        {
            throw new ArgumentException("Model configuration collections must be initialized.");
        }

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (ModelParameterSettingConfiguration setting in ParameterSettings)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(setting.ModelParameterId);
            if (!targets.Add(setting.ModelParameterId))
            {
                throw new ArgumentException($"Duplicate model parameter setting: {setting.ModelParameterId}");
            }

            if (setting.GlobalParameterId is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(setting.GlobalParameterId);
            }

            ValidateRange(setting.InputMinimum, setting.InputMaximum, "input");
            ValidateRange(setting.OutputMinimum, setting.OutputMaximum, "output");
        }

        foreach (ModelSourceMappingSelection selection in SourceMappingSelections)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(selection.VendorId);
            ArgumentException.ThrowIfNullOrWhiteSpace(selection.TechnologyId);
            ArgumentException.ThrowIfNullOrWhiteSpace(selection.AdapterId);
            ArgumentException.ThrowIfNullOrWhiteSpace(selection.ProfileId);
            ArgumentException.ThrowIfNullOrWhiteSpace(selection.FileName);
            ArgumentException.ThrowIfNullOrWhiteSpace(selection.Channel);
            if (!StringComparer.Ordinal.Equals(selection.FileName, Path.GetFileName(selection.FileName)))
            {
                throw new ArgumentException("Mapping selection must use a file name only.");
            }
        }
    }

    private static void ValidateRange(double minimum, double maximum, string name)
    {
        if (!double.IsFinite(minimum)
            || !double.IsFinite(maximum)
            || minimum > 0
            || maximum < 0
            || (minimum == 0 && maximum == 0))
        {
            throw new ArgumentException($"Model parameter {name} range must be finite, contain zero, and have a non-zero side.");
        }
    }
}
