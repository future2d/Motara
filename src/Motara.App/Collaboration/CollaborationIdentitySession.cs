using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Collaboration.Identity;
using Motara.Collaboration.Invites;

namespace Motara.App.Collaboration;

internal sealed class CollaborationIdentitySession : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<DeviceIdentityHandle>> loadIdentityAsync;
    private readonly FriendInviteTokenService tokenService;
    private readonly ILogger<CollaborationIdentitySession> logger;
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private DeviceIdentityHandle? identityHandle;
    private bool disposed;

    internal CollaborationIdentitySession(
        Func<CancellationToken, Task<DeviceIdentityHandle>> loadIdentityAsync,
        FriendInviteTokenService tokenService,
        ILogger<CollaborationIdentitySession>? logger = null)
    {
        this.loadIdentityAsync = loadIdentityAsync
            ?? throw new ArgumentNullException(nameof(loadIdentityAsync));
        this.tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        this.logger = logger ?? NullLogger<CollaborationIdentitySession>.Instance;
    }

    internal DeviceIdentity? Identity => identityHandle?.Identity;

    internal DeviceIdentityHandle Handle => identityHandle
        ?? throw new InvalidOperationException("The collaboration identity is not initialized.");

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (identityHandle is not null)
            {
                return;
            }

            try
            {
                identityHandle = await loadIdentityAsync(cancellationToken).ConfigureAwait(false);
                CollaborationIdentitySessionEvents.Initialized(logger);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                CollaborationIdentitySessionEvents.Failed(logger, exception.GetType().Name);
                throw;
            }
        }
        finally
        {
            initializationGate.Release();
        }
    }

    internal string CreateFriendInvite(string inviterDisplayName, TimeSpan lifetime)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        DeviceIdentityHandle handle = identityHandle
            ?? throw new InvalidOperationException("The collaboration identity is not initialized.");
        return tokenService.Create(handle, inviterDisplayName, lifetime);
    }

    public async ValueTask DisposeAsync()
    {
        await initializationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (identityHandle is not null)
            {
                await identityHandle.DisposeAsync().ConfigureAwait(false);
                identityHandle = null;
            }

            CollaborationIdentitySessionEvents.Disposed(logger);
        }
        finally
        {
            initializationGate.Release();
        }
    }
}

internal static partial class CollaborationIdentitySessionEvents
{
    [LoggerMessage(8040, LogLevel.Information, "Collaboration identity session initialized")]
    internal static partial void Initialized(ILogger logger);

    [LoggerMessage(8041, LogLevel.Warning,
        "Collaboration identity session initialization failed with {ErrorType}")]
    internal static partial void Failed(ILogger logger, string errorType);

    [LoggerMessage(8042, LogLevel.Debug, "Collaboration identity session disposed")]
    internal static partial void Disposed(ILogger logger);
}
