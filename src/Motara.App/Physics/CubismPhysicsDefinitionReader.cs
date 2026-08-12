using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;

namespace Motara.App.Physics;

internal static class CubismPhysicsDefinitionReader
{
    private const int MaximumSettings = 256;
    private const int MaximumInputsPerSetting = 128;
    private const int MaximumOutputsPerSetting = 128;
    private const int MaximumVerticesPerSetting = 64;

    internal static async Task<CubismPhysicsDefinition> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                },
                cancellationToken).ConfigureAwait(false);
            return Parse(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Cubism physics document is invalid.", exception);
        }
    }

    private static CubismPhysicsDefinition Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid();
        }

        JsonElement meta = RequiredObject(root, "Meta");
        JsonElement settingsElement = RequiredArray(root, "PhysicsSettings");
        int declaredSettings = RequiredNonNegativeInt(meta, "PhysicsSettingCount");
        int declaredInputs = RequiredNonNegativeInt(meta, "TotalInputCount");
        int declaredOutputs = RequiredNonNegativeInt(meta, "TotalOutputCount");
        int declaredVertices = RequiredNonNegativeInt(meta, "VertexCount");
        if (declaredSettings > MaximumSettings || settingsElement.GetArrayLength() > MaximumSettings)
        {
            throw Invalid();
        }

        JsonElement forces = RequiredObject(meta, "EffectiveForces");
        Vector2 gravity = ReadVector(RequiredObject(forces, "Gravity"));
        Vector2 wind = ReadVector(RequiredObject(forces, "Wind"));
        var settings = ImmutableArray.CreateBuilder<CubismPhysicsSettingDefinition>(settingsElement.GetArrayLength());
        int inputCount = 0;
        int outputCount = 0;
        int vertexCount = 0;
        foreach (JsonElement setting in settingsElement.EnumerateArray())
        {
            CubismPhysicsSettingDefinition parsed = ParseSetting(setting);
            checked
            {
                inputCount += parsed.Inputs.Length;
                outputCount += parsed.Outputs.Length;
                vertexCount += parsed.Vertices.Length;
            }
            settings.Add(parsed);
        }

        if (settings.Count != declaredSettings
            || inputCount != declaredInputs
            || outputCount != declaredOutputs
            || vertexCount != declaredVertices)
        {
            throw Invalid();
        }

        return new CubismPhysicsDefinition(settings.MoveToImmutable(), gravity, wind);
    }

    private static CubismPhysicsSettingDefinition ParseSetting(JsonElement setting)
    {
        JsonElement normalization = RequiredObject(setting, "Normalization");
        JsonElement inputsElement = RequiredArray(setting, "Input");
        JsonElement outputsElement = RequiredArray(setting, "Output");
        JsonElement verticesElement = RequiredArray(setting, "Vertices");
        if (inputsElement.GetArrayLength() > MaximumInputsPerSetting
            || outputsElement.GetArrayLength() > MaximumOutputsPerSetting
            || verticesElement.GetArrayLength() is < 2 or > MaximumVerticesPerSetting)
        {
            throw Invalid();
        }

        var inputs = ImmutableArray.CreateBuilder<CubismPhysicsInputDefinition>(inputsElement.GetArrayLength());
        foreach (JsonElement input in inputsElement.EnumerateArray())
        {
            inputs.Add(new CubismPhysicsInputDefinition(
                RequiredIdentifier(RequiredObject(input, "Source"), "Id"),
                RequiredFiniteNumber(input, "Weight"),
                RequiredType(input),
                RequiredBoolean(input, "Reflect")));
        }

        var outputs = ImmutableArray.CreateBuilder<CubismPhysicsOutputDefinition>(outputsElement.GetArrayLength());
        foreach (JsonElement output in outputsElement.EnumerateArray())
        {
            int vertexIndex = RequiredNonNegativeInt(output, "VertexIndex");
            if (vertexIndex >= verticesElement.GetArrayLength())
            {
                throw Invalid();
            }

            outputs.Add(new CubismPhysicsOutputDefinition(
                RequiredIdentifier(RequiredObject(output, "Destination"), "Id"),
                vertexIndex,
                RequiredFiniteNumber(output, "Scale"),
                RequiredFiniteNumber(output, "Weight"),
                RequiredType(output),
                RequiredBoolean(output, "Reflect")));
        }

        var vertices = ImmutableArray.CreateBuilder<CubismPhysicsParticleDefinition>(verticesElement.GetArrayLength());
        foreach (JsonElement vertex in verticesElement.EnumerateArray())
        {
            var particle = new CubismPhysicsParticleDefinition(
                ReadVector(RequiredObject(vertex, "Position")),
                RequiredFiniteNumber(vertex, "Mobility"),
                RequiredFiniteNumber(vertex, "Delay"),
                RequiredFiniteNumber(vertex, "Acceleration"),
                RequiredFiniteNumber(vertex, "Radius"));
            if (particle.Mobility < 0 || particle.Delay < 0 || particle.Acceleration < 0 || particle.Radius < 0)
            {
                throw Invalid();
            }

            vertices.Add(particle);
        }

        return new CubismPhysicsSettingDefinition(
            ReadNormalization(RequiredObject(normalization, "Position")),
            ReadNormalization(RequiredObject(normalization, "Angle")),
            inputs.MoveToImmutable(),
            outputs.MoveToImmutable(),
            vertices.MoveToImmutable());
    }

    private static CubismPhysicsNormalization ReadNormalization(JsonElement element)
    {
        double minimum = RequiredFiniteNumber(element, "Minimum");
        double @default = RequiredFiniteNumber(element, "Default");
        double maximum = RequiredFiniteNumber(element, "Maximum");
        if (minimum > @default || @default > maximum)
        {
            throw Invalid();
        }

        return new CubismPhysicsNormalization(minimum, @default, maximum);
    }

    private static CubismPhysicsValueType RequiredType(JsonElement element)
    {
        string type = RequiredString(element, "Type");
        return type switch
        {
            "X" => CubismPhysicsValueType.X,
            "Y" => CubismPhysicsValueType.Y,
            "Angle" => CubismPhysicsValueType.Angle,
            _ => throw Invalid(),
        };
    }

    private static Vector2 ReadVector(JsonElement element)
    {
        double x = RequiredFiniteNumber(element, "X");
        double y = RequiredFiniteNumber(element, "Y");
        if (x is < float.MinValue or > float.MaxValue || y is < float.MinValue or > float.MaxValue)
        {
            throw Invalid();
        }

        return new Vector2((float)x, (float)y);
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

    private static int RequiredNonNegativeInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out JsonElement value)
            || !value.TryGetInt32(out int result)
            || result < 0)
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

    private static string RequiredIdentifier(JsonElement element, string propertyName)
    {
        string value = RequiredString(element, propertyName);
        if (value.Length is 0 or > 256)
        {
            throw Invalid();
        }

        return value;
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not string result)
        {
            throw Invalid();
        }

        return result;
    }

    private static InvalidDataException Invalid() => new("The Cubism physics document is invalid.");
}
