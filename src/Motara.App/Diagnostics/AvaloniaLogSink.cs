using System.Globalization;
using Avalonia.Logging;
using Microsoft.Extensions.Logging;

namespace Motara.App.Diagnostics;

internal sealed class AvaloniaLogSink(ILogger<AvaloniaLogSink> logger) : ILogSink
{
    private static readonly EventId FrameworkEvent = new(1010, "AvaloniaFrameworkEvent");

    public bool IsEnabled(LogEventLevel level, string area) =>
        !IsHighFrequencyLayoutDiagnostic(level, area)
        && logger.IsEnabled(MapLevel(level));

    public void Log(
        LogEventLevel level,
        string area,
        object? source,
        string messageTemplate) => Log(level, area, source, messageTemplate, []);

    public void Log(
        LogEventLevel level,
        string area,
        object? source,
        string messageTemplate,
        params object?[] propertyValues)
    {
        LogLevel mappedLevel = MapLevel(level);
        if (!IsEnabled(level, area))
        {
            return;
        }

        string safeArea = LogSanitizer.Sanitize(area);
        string safeTemplate = LogSanitizer.Sanitize(messageTemplate);
        string safeValues = string.Join(
            ", ",
            propertyValues.Select(static value => LogSanitizer.Sanitize(
                Convert.ToString(value, CultureInfo.InvariantCulture))));
        string message = propertyValues.Length == 0
            ? $"{safeArea}: {safeTemplate}"
            : $"{safeArea}: {safeTemplate} [{safeValues}]";
        logger.Log(
            mappedLevel,
            FrameworkEvent,
            message,
            exception: null,
            static (state, _) => state);
    }

    private static LogLevel MapLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => LogLevel.Trace,
        LogEventLevel.Debug => LogLevel.Debug,
        LogEventLevel.Information => LogLevel.Information,
        LogEventLevel.Warning => LogLevel.Warning,
        LogEventLevel.Error => LogLevel.Error,
        LogEventLevel.Fatal => LogLevel.Critical,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    private static bool IsHighFrequencyLayoutDiagnostic(LogEventLevel level, string area) =>
        level <= LogEventLevel.Information
        && string.Equals(area, "Layout", StringComparison.Ordinal);
}
