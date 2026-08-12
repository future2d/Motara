using Microsoft.Extensions.Logging;

namespace Motara.Collaboration.Drive;

internal static partial class ModelDriveEvents
{
    [LoggerMessage(8130, LogLevel.Information,
        "Model drive activated; generation={Generation}; samplingRateHz={SamplingRateHz}")]
    internal static partial void Activated(ILogger logger, ulong generation, int samplingRateHz);

    [LoggerMessage(8131, LogLevel.Information,
        "Model drive released")]
    internal static partial void Released(ILogger logger);

    [LoggerMessage(8132, LogLevel.Information,
        "Model drive sampling rate downgraded; samplingRateHz={SamplingRateHz}")]
    internal static partial void SamplingRateDowngraded(ILogger logger, int samplingRateHz);
}
