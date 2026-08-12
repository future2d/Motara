using Microsoft.Extensions.Logging;
using Motara.Persistence;

namespace Motara.App.Backgrounds;

internal static partial class BackgroundStateLog
{
    [LoggerMessage(6770, LogLevel.Information, "Global background applied with kind {Kind}")]
    internal static partial void GlobalApplied(ILogger logger, BackgroundKind kind);

    [LoggerMessage(6771, LogLevel.Information, "Scene background mode applied; custom={IsCustom}")]
    internal static partial void SceneModeApplied(ILogger logger, bool isCustom);
}
