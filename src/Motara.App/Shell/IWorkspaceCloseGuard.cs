namespace Motara.App.Shell;

public interface IWorkspaceCloseGuard
{
    Task<bool> RequestCloseAsync(CancellationToken cancellationToken);
}
