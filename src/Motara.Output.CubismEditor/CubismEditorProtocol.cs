using System.Text.Json;

namespace Motara.Output.CubismEditor;

/// <summary>Parses the JSON response envelope defined by the Cubism Editor external API.</summary>
public static class CubismEditorProtocol
{
    public static CubismEditorResponse ParseResponse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new CubismEditorProtocolException("The Cubism Editor response must be a JSON object.");
            }

            string type = ReadRequiredString(root, "Type");
            string method = ReadRequiredString(root, "Method");
            string? requestId = ReadOptionalString(root, "RequestId");
            if (!root.TryGetProperty("Data", out JsonElement data))
            {
                throw new CubismEditorProtocolException("The Cubism Editor response is missing Data.");
            }

            string? errorType = type == "Error" ? ReadOptionalString(data, "ErrorType") : null;
            return new CubismEditorResponse(type, method, requestId, data.Clone(), errorType);
        }
        catch (JsonException exception)
        {
            throw new CubismEditorProtocolException("The Cubism Editor response is not valid JSON.", exception);
        }
    }

    private static string ReadRequiredString(JsonElement parent, string propertyName) =>
        ReadOptionalString(parent, propertyName)
        ?? throw new CubismEditorProtocolException($"The Cubism Editor response is missing {propertyName}.");

    private static string? ReadOptionalString(JsonElement parent, string propertyName) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(propertyName, out JsonElement property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

/// <summary>Represents a Cubism Editor response or protocol error without interpreting application state.</summary>
public sealed record CubismEditorResponse(
    string Type,
    string Method,
    string? RequestId,
    JsonElement Data,
    string? ErrorType);

/// <summary>Indicates that a peer message cannot be interpreted as a Cubism Editor response.</summary>
public sealed class CubismEditorProtocolException : Exception
{
    public CubismEditorProtocolException(string message)
        : base(message)
    {
    }

    public CubismEditorProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
