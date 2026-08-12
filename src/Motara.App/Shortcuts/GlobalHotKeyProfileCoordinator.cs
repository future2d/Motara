using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Persistence;

namespace Motara.App.Shortcuts;

internal interface IGlobalHotKeyProfileRegistrar
{
    void Replace(IReadOnlyList<InputBinding> bindings, CancellationToken cancellationToken);
}

internal sealed class GlobalHotKeyProfileCoordinator : IDisposable
{
    private readonly object sync = new();
    private readonly IGlobalHotKeyProfileRegistrar registrar;
    private readonly Action<Action> scheduleOnOwnerThread;
    private readonly ILogger logger;
    private InputBindingProfile? pendingProfile;
    private bool isDrainScheduled;
    private bool isDisposed;

    internal GlobalHotKeyProfileCoordinator(
        IGlobalHotKeyProfileRegistrar registrar,
        Action<Action> scheduleOnOwnerThread,
        ILogger? logger = null)
    {
        this.registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        this.scheduleOnOwnerThread = scheduleOnOwnerThread
            ?? throw new ArgumentNullException(nameof(scheduleOnOwnerThread));
        this.logger = logger ?? NullLogger<GlobalHotKeyProfileCoordinator>.Instance;
    }

    internal void RequestApply(InputBindingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (sync)
        {
            if (isDisposed) return;
            pendingProfile = profile;
            if (isDrainScheduled) return;
            isDrainScheduled = true;
        }
        scheduleOnOwnerThread(DrainOnOwnerThread);
    }

    public void Dispose()
    {
        lock (sync)
        {
            isDisposed = true;
            pendingProfile = null;
        }
    }

    private void DrainOnOwnerThread()
    {
        while (true)
        {
            InputBindingProfile? profile;
            lock (sync)
            {
                if (isDisposed)
                {
                    isDrainScheduled = false;
                    return;
                }
                profile = pendingProfile;
                pendingProfile = null;
                if (profile is null)
                {
                    isDrainScheduled = false;
                    return;
                }
            }

            InputBinding[] globals = profile.Bindings
                .Where(binding => binding.Scope == InputBindingScope.Global && binding.IsGlobalEnabled)
                .ToArray();
            try
            {
                registrar.Replace(globals, CancellationToken.None);
            }
            catch (Exception exception)
            {
                GlobalHotKeyProfileCoordinatorLog.ApplyFailed(logger, exception, globals.Length);
            }
        }
    }
}

internal static partial class GlobalHotKeyProfileCoordinatorLog
{
    [LoggerMessage(6853, LogLevel.Error, "Global hotkey profile replacement failed for {BindingCount} bindings")]
    internal static partial void ApplyFailed(ILogger logger, Exception exception, int bindingCount);
}
