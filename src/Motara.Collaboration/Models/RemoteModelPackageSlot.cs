namespace Motara.Collaboration.Models;

public sealed class RemoteModelPackageSlot : IAsyncDisposable
{
    private RemoteModelPackage? current;

    public RemoteModelPackage? Current => Volatile.Read(ref current);

    public async ValueTask ReplaceAsync(RemoteModelPackage next)
    {
        ArgumentNullException.ThrowIfNull(next);
        RemoteModelPackage? previous = Interlocked.Exchange(ref current, next);
        if (previous is not null)
            await previous.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask ReleaseAsync()
    {
        RemoteModelPackage? previous = Interlocked.Exchange(ref current, null);
        if (previous is not null)
            await previous.DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => ReleaseAsync();
}
