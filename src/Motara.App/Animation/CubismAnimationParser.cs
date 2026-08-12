using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.ModelRuntime.Abstractions;

namespace Motara.App.Animation;

internal static class CubismAnimationParser
{
    private const int MaximumAssetBytes = 1024 * 1024;
    private const int MaximumCurvesPerMotion = 1024;
    private const int MaximumSegmentsPerCurve = 4096;
    private const int MaximumExpressionParameters = 1024;
    private const int MaximumPoseGroups = 256;
    private const int MaximumPosePartsPerGroup = 256;
    private const int MaximumLinksPerPart = 256;
    private const int MaximumIdentifierLength = 256;

    internal static async Task<CubismAnimationSet> LoadAsync(
        IModelAssetSource assets,
        ImmutableArray<ModelAuxiliaryAsset> definitions,
        ModelCapabilities capabilities,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (definitions.IsDefault || definitions.Any(static definition => definition is null))
        {
            throw new ArgumentException("Animation definitions must be initialized.", nameof(definitions));
        }

        ILogger activeLogger = logger ?? NullLogger.Instance;
        var parameterIndexes = capabilities.Parameters
            .Select(static (parameter, index) => (parameter.Id, Index: index))
            .ToDictionary(static pair => pair.Id, static pair => pair.Index, StringComparer.Ordinal);
        var clips = ImmutableArray.CreateBuilder<CubismMotionClip>();
        var expressions = ImmutableArray.CreateBuilder<CubismExpression>();
        var poseGroups = ImmutableArray.CreateBuilder<CubismPoseGroup>();
        var diagnostics = ImmutableArray.CreateBuilder<CubismAnimationDiagnostic>();

        foreach (ModelAuxiliaryAsset definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using JsonDocument document = await ReadDocumentAsync(
                    assets,
                    definition.AssetId,
                    cancellationToken).ConfigureAwait(false);
                switch (definition.Kind)
                {
                    case ModelAuxiliaryAssetKind.Motion:
                        clips.Add(ParseMotion(
                            document.RootElement,
                            definition,
                            parameterIndexes,
                            diagnostics,
                            activeLogger));
                        break;

                    case ModelAuxiliaryAssetKind.Expression:
                        expressions.Add(ParseExpression(
                            document.RootElement,
                            definition,
                            parameterIndexes,
                            diagnostics,
                            activeLogger));
                        break;

                    case ModelAuxiliaryAssetKind.Pose:
                        poseGroups.AddRange(ParsePose(document.RootElement));
                        break;

                    default:
                        throw new InvalidDataException("The Cubism auxiliary asset kind is unsupported.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException
                or ArgumentException
                or NotSupportedException)
            {
                AddDiagnostic(diagnostics, activeLogger, definition, exception.GetType().Name);
            }
        }

        return new CubismAnimationSet(
            clips.ToImmutable(),
            expressions.ToImmutable(),
            poseGroups.ToImmutable(),
            diagnostics.ToImmutable());
    }

    private static async Task<JsonDocument> ReadDocumentAsync(
        IModelAssetSource assets,
        string assetId,
        CancellationToken cancellationToken)
    {
        long length = await assets.GetLengthAsync(assetId, cancellationToken).ConfigureAwait(false);
        if (length is < 0 or > MaximumAssetBytes)
        {
            throw Invalid();
        }

        await using Stream stream = await assets.OpenReadAsync(assetId, cancellationToken)
            .ConfigureAwait(false);
        byte[] buffer = new byte[81920];
        using var bytes = new MemoryStream((int)length);
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (bytes.Length > MaximumAssetBytes - read)
            {
                throw Invalid();
            }

            bytes.Write(buffer, 0, read);
        }

        try
        {
            return JsonDocument.Parse(
                bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Cubism animation document is invalid.", exception);
        }
    }

    private static CubismMotionClip ParseMotion(
        JsonElement root,
        ModelAuxiliaryAsset asset,
        Dictionary<string, int> parameterIndexes,
        ImmutableArray<CubismAnimationDiagnostic>.Builder diagnostics,
        ILogger logger)
    {
        JsonElement meta = RequiredObject(root, "Meta");
        double duration = RequiredPositiveFiniteNumber(meta, "Duration");
        bool loop = RequiredBoolean(meta, "Loop");
        double fadeInTime = OptionalNonNegativeFiniteNumber(meta, "FadeInTime", 0);
        double fadeOutTime = OptionalNonNegativeFiniteNumber(meta, "FadeOutTime", 0);
        JsonElement curvesElement = RequiredArray(root, "Curves");
        if (curvesElement.GetArrayLength() > MaximumCurvesPerMotion)
        {
            throw Invalid();
        }

        var curves = ImmutableArray.CreateBuilder<CubismAnimationCurve>();
        foreach (JsonElement curveElement in curvesElement.EnumerateArray())
        {
            string target = RequiredIdentifier(curveElement, "Target");
            string targetId = RequiredIdentifier(curveElement, "Id");
            if (!TryGetTarget(target, out CubismAnimationCurveTarget targetKind))
            {
                AddDiagnostic(diagnostics, logger, asset, "UnsupportedCurveTarget");
                continue;
            }

            int parameterIndex = -1;
            if (targetKind is CubismAnimationCurveTarget.Parameter
                && !parameterIndexes.TryGetValue(targetId, out parameterIndex))
            {
                AddDiagnostic(diagnostics, logger, asset, "UnknownParameterTarget");
                continue;
            }

            ImmutableArray<CubismAnimationSegment> segments = ParseSegments(
                RequiredArray(curveElement, "Segments"),
                duration);
            curves.Add(new CubismAnimationCurve(
                targetKind,
                targetId,
                parameterIndex,
                OptionalCurveFadeTime(curveElement, "FadeInTime"),
                OptionalCurveFadeTime(curveElement, "FadeOutTime"),
                segments));
        }

        if (curves.Count == 0)
        {
            throw Invalid();
        }

        return new CubismMotionClip(asset, duration, loop, fadeInTime, fadeOutTime, curves.ToImmutable());
    }

    private static CubismExpression ParseExpression(
        JsonElement root,
        ModelAuxiliaryAsset asset,
        Dictionary<string, int> parameterIndexes,
        ImmutableArray<CubismAnimationDiagnostic>.Builder diagnostics,
        ILogger logger)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid();
        }

        JsonElement parametersElement = RequiredArray(root, "Parameters");
        if (parametersElement.GetArrayLength() > MaximumExpressionParameters)
        {
            throw Invalid();
        }

        var parameters = ImmutableArray.CreateBuilder<CubismExpressionParameter>();
        foreach (JsonElement parameterElement in parametersElement.EnumerateArray())
        {
            string id = RequiredIdentifier(parameterElement, "Id");
            if (!parameterIndexes.TryGetValue(id, out int parameterIndex))
            {
                AddDiagnostic(diagnostics, logger, asset, "UnknownParameterTarget");
                continue;
            }

            parameters.Add(new CubismExpressionParameter(
                id,
                parameterIndex,
                RequiredFiniteNumber(parameterElement, "Value"),
                ParseExpressionBlend(OptionalString(parameterElement, "Blend") ?? "Add")));
        }

        if (parameters.Count == 0)
        {
            throw Invalid();
        }

        return new CubismExpression(
            asset,
            OptionalNonNegativeFiniteNumber(root, "FadeInTime", 1),
            parameters.ToImmutable());
    }

    private static ImmutableArray<CubismPoseGroup> ParsePose(JsonElement root)
    {
        JsonElement groupsElement = RequiredArray(root, "Groups");
        if (groupsElement.GetArrayLength() > MaximumPoseGroups)
        {
            throw Invalid();
        }

        var groups = ImmutableArray.CreateBuilder<CubismPoseGroup>();
        foreach (JsonElement groupElement in groupsElement.EnumerateArray())
        {
            if (groupElement.ValueKind != JsonValueKind.Array
                || groupElement.GetArrayLength() is 0 or > MaximumPosePartsPerGroup)
            {
                throw Invalid();
            }

            var partIds = new HashSet<string>(StringComparer.Ordinal);
            var parts = ImmutableArray.CreateBuilder<CubismPosePart>();
            foreach (JsonElement partElement in groupElement.EnumerateArray())
            {
                string partId = RequiredIdentifier(partElement, "Id");
                if (!partIds.Add(partId))
                {
                    throw Invalid();
                }

                ImmutableArray<string> links = OptionalIdentifiers(
                    partElement,
                    "Link",
                    MaximumLinksPerPart);
                parts.Add(new CubismPosePart(partId, links));
            }

            groups.Add(new CubismPoseGroup(parts.ToImmutable()));
        }

        return groups.ToImmutable();
    }

    private static ImmutableArray<CubismAnimationSegment> ParseSegments(
        JsonElement element,
        double duration)
    {
        if (element.GetArrayLength() < 2)
        {
            throw Invalid();
        }

        int index = 0;
        double startTime = RequiredFiniteNumber(element, ref index);
        double startValue = RequiredFiniteNumber(element, ref index);
        if (startTime < 0 || startTime > duration)
        {
            throw Invalid();
        }

        var segments = ImmutableArray.CreateBuilder<CubismAnimationSegment>();
        while (index < element.GetArrayLength())
        {
            if (segments.Count >= MaximumSegmentsPerCurve)
            {
                throw Invalid();
            }

            int segmentType = RequiredInt32(element, ref index);
            CubismAnimationSegment segment;
            switch (segmentType)
            {
                case 0:
                {
                    double endTime = RequiredFiniteNumber(element, ref index);
                    double endValue = RequiredFiniteNumber(element, ref index);
                    segment = new CubismAnimationSegment(
                        CubismAnimationSegmentKind.Linear,
                        startTime,
                        startValue,
                        0,
                        0,
                        0,
                        0,
                        endTime,
                        endValue);
                    break;
                }

                case 1:
                {
                    double controlPoint1Time = RequiredFiniteNumber(element, ref index);
                    double controlPoint1Value = RequiredFiniteNumber(element, ref index);
                    double controlPoint2Time = RequiredFiniteNumber(element, ref index);
                    double controlPoint2Value = RequiredFiniteNumber(element, ref index);
                    double endTime = RequiredFiniteNumber(element, ref index);
                    double endValue = RequiredFiniteNumber(element, ref index);
                    if (controlPoint1Time < startTime
                        || controlPoint1Time > controlPoint2Time
                        || controlPoint2Time > endTime)
                    {
                        throw Invalid();
                    }

                    segment = new CubismAnimationSegment(
                        CubismAnimationSegmentKind.Bezier,
                        startTime,
                        startValue,
                        controlPoint1Time,
                        controlPoint1Value,
                        controlPoint2Time,
                        controlPoint2Value,
                        endTime,
                        endValue);
                    break;
                }

                case 2:
                case 3:
                {
                    double endTime = RequiredFiniteNumber(element, ref index);
                    double endValue = RequiredFiniteNumber(element, ref index);
                    segment = new CubismAnimationSegment(
                        segmentType == 2
                            ? CubismAnimationSegmentKind.Stepped
                            : CubismAnimationSegmentKind.InverseStepped,
                        startTime,
                        startValue,
                        0,
                        0,
                        0,
                        0,
                        endTime,
                        endValue);
                    break;
                }

                default:
                    throw Invalid();
            }

            if (segment.EndTime <= startTime || segment.EndTime > duration)
            {
                throw Invalid();
            }

            segments.Add(segment);
            startTime = segment.EndTime;
            startValue = segment.EndValue;
        }

        return segments.ToImmutable();
    }

    private static bool TryGetTarget(string target, out CubismAnimationCurveTarget targetKind)
    {
        switch (target)
        {
            case "Parameter":
                targetKind = CubismAnimationCurveTarget.Parameter;
                return true;

            case "PartOpacity":
                targetKind = CubismAnimationCurveTarget.PartOpacity;
                return true;

            default:
                targetKind = default;
                return false;
        }
    }

    private static CubismExpressionBlendMode ParseExpressionBlend(string blend) => blend switch
    {
        "Add" => CubismExpressionBlendMode.Add,
        "Multiply" => CubismExpressionBlendMode.Multiply,
        "Overwrite" => CubismExpressionBlendMode.Overwrite,
        _ => throw Invalid(),
    };

    private static double? OptionalCurveFadeTime(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        if (!value.TryGetDouble(out double result) || !double.IsFinite(result) || result < -1)
        {
            throw Invalid();
        }

        return result < 0 ? null : result;
    }

    private static double OptionalNonNegativeFiniteNumber(
        JsonElement element,
        string propertyName,
        double fallback)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return fallback;
        }

        if (!value.TryGetDouble(out double result) || !double.IsFinite(result) || result < 0)
        {
            throw Invalid();
        }

        return result;
    }

    private static string? OptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString() is not string result)
        {
            throw Invalid();
        }

        return result;
    }

    private static ImmutableArray<string> OptionalIdentifiers(
        JsonElement element,
        string propertyName,
        int maximumCount)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement values))
        {
            return [];
        }

        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > maximumCount)
        {
            throw Invalid();
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        var identifiers = ImmutableArray.CreateBuilder<string>(values.GetArrayLength());
        foreach (JsonElement value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not string identifier
                || identifier.Length is 0 or > MaximumIdentifierLength
                || !unique.Add(identifier))
            {
                throw Invalid();
            }

            identifiers.Add(identifier);
        }

        return identifiers.MoveToImmutable();
    }

    private static JsonElement RequiredObject(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid();
        }

        return value;
    }

    private static JsonElement RequiredArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            throw Invalid();
        }

        return value;
    }

    private static string RequiredIdentifier(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not string result
            || result.Length is 0 or > MaximumIdentifierLength)
        {
            throw Invalid();
        }

        return result;
    }

    private static double RequiredFiniteNumber(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out JsonElement value)
            || !value.TryGetDouble(out double result)
            || !double.IsFinite(result))
        {
            throw Invalid();
        }

        return result;
    }

    private static double RequiredPositiveFiniteNumber(JsonElement element, string propertyName)
    {
        double value = RequiredFiniteNumber(element, propertyName);
        return value > 0 ? value : throw Invalid();
    }

    private static bool RequiredBoolean(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw Invalid();
        }

        return value.GetBoolean();
    }

    private static double RequiredFiniteNumber(JsonElement element, ref int index)
    {
        if (index >= element.GetArrayLength()
            || !element[index].TryGetDouble(out double result)
            || !double.IsFinite(result))
        {
            throw Invalid();
        }

        index++;
        return result;
    }

    private static int RequiredInt32(JsonElement element, ref int index)
    {
        if (index >= element.GetArrayLength() || !element[index].TryGetInt32(out int result))
        {
            throw Invalid();
        }

        index++;
        return result;
    }

    private static void AddDiagnostic(
        ImmutableArray<CubismAnimationDiagnostic>.Builder diagnostics,
        ILogger logger,
        ModelAuxiliaryAsset asset,
        string reason)
    {
        diagnostics.Add(new CubismAnimationDiagnostic(asset.AssetId, asset.Kind, reason));
        CubismAnimationLog.OptionalAssetSkipped(logger, asset.AssetId, asset.Kind, reason);
    }

    private static InvalidDataException Invalid() => new("The Cubism animation document is invalid.");
}

internal static partial class CubismAnimationLog
{
    [LoggerMessage(6700, LogLevel.Warning,
        "Cubism optional asset {AssetId} ({Kind}) was skipped: {Reason}")]
    internal static partial void OptionalAssetSkipped(
        ILogger logger,
        string assetId,
        ModelAuxiliaryAssetKind kind,
        string reason);
}
