using Motara.Persistence;

namespace Motara.App.Diagnostics;

internal interface ILogOperations
{
    DiagnosticLogLevel MinimumLevel { get; set; }

    Task OpenLogsFolderAsync(CancellationToken cancellationToken);

    Task ExportDiagnosticLogsAsync(CancellationToken cancellationToken);
}

internal sealed class UnavailableLogOperations : ILogOperations
{
    public DiagnosticLogLevel MinimumLevel { get; set; } = DiagnosticLogLevel.Information;

    public Task OpenLogsFolderAsync(CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException("Log folder operations are unavailable."));

    public Task ExportDiagnosticLogsAsync(CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException("Log export is unavailable."));
}
