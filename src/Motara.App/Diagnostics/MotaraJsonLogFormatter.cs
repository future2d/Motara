using System.Collections;
using System.Globalization;
using System.Text.Json;
using Serilog.Events;
using Serilog.Formatting;

namespace Motara.App.Diagnostics;

internal sealed class MotaraJsonLogFormatter : ITextFormatter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        ReadEventId(logEvent, out int eventId, out string? eventName);
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["TimestampUtc"] = logEvent.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["Level"] = logEvent.Level.ToString(),
            ["EventId"] = eventId,
            ["EventName"] = eventName,
            ["Category"] = ReadScalarString(logEvent, "SourceContext"),
            ["ProcessInstanceId"] = ReadScalarString(logEvent, "ProcessInstanceId"),
            ["SessionId"] = ReadScalarString(logEvent, "SessionId"),
            ["Message"] = LogSanitizer.Sanitize(logEvent.RenderMessage(CultureInfo.InvariantCulture)),
        };

        foreach ((string name, LogEventPropertyValue value) in logEvent.Properties)
        {
            if (name is "EventId" or "SourceContext" or "ProcessInstanceId" or "SessionId")
            {
                continue;
            }

            payload[name] = ConvertValue(value);
        }

        if (logEvent.Exception is Exception exception)
        {
            payload["Exception"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Type"] = exception.GetType().FullName,
                ["HResult"] = exception.HResult,
                ["Message"] = LogSanitizer.Sanitize(exception.Message),
                ["StackTrace"] = LogSanitizer.Sanitize(exception.StackTrace),
            };
        }

        output.Write(JsonSerializer.Serialize(payload, SerializerOptions));
        output.WriteLine();
    }

    private static void ReadEventId(LogEvent logEvent, out int eventId, out string? eventName)
    {
        eventId = 0;
        eventName = null;
        if (!logEvent.Properties.TryGetValue("EventId", out LogEventPropertyValue? value)
            || value is not StructureValue structure)
        {
            return;
        }

        foreach (LogEventProperty property in structure.Properties)
        {
            if (property.Name == "Id" && property.Value is ScalarValue { Value: int id })
            {
                eventId = id;
            }
            else if (property.Name == "Name" && property.Value is ScalarValue { Value: string name })
            {
                eventName = name;
            }
        }
    }

    private static string? ReadScalarString(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out LogEventPropertyValue? value)
        && value is ScalarValue { Value: not null } scalar
            ? LogSanitizer.Sanitize(Convert.ToString(scalar.Value, CultureInfo.InvariantCulture))
            : null;

    private static object? ConvertValue(LogEventPropertyValue value) => value switch
    {
        ScalarValue { Value: string text } => LogSanitizer.Sanitize(text),
        ScalarValue { Value: null } => null,
        ScalarValue scalar when scalar.Value is bool
            or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal => scalar.Value,
        ScalarValue scalar => LogSanitizer.Sanitize(
            Convert.ToString(scalar.Value, CultureInfo.InvariantCulture)),
        SequenceValue sequence => sequence.Elements.Select(ConvertValue).ToArray(),
        StructureValue structure => structure.Properties.ToDictionary(
            static property => property.Name,
            static property => ConvertValue(property.Value),
            StringComparer.Ordinal),
        DictionaryValue dictionary => dictionary.Elements.ToDictionary(
            static pair => LogSanitizer.Sanitize(Convert.ToString(
                ((ScalarValue)pair.Key).Value,
                CultureInfo.InvariantCulture)),
            static pair => ConvertValue(pair.Value),
            StringComparer.Ordinal),
        _ => LogSanitizer.Sanitize(value.ToString()),
    };
}
