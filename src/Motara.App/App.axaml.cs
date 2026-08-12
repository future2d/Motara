using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Logging;
using Motara.App.Diagnostics;
using Motara.App.Themes;
using Motara.App.Collaboration;

namespace Motara.App;

public sealed partial class App : Application, IDisposable
{
    private readonly MotaraLogHost logHost;
    private readonly bool ownsLogHost;
    private readonly CancellationTokenSource startupCancellation = new();
    private ApplicationExceptionHooks? exceptionHooks;
    private Task startupTask = Task.CompletedTask;

    public App()
        : this(
            MotaraLogHost.Create(
                new PlatformLogStoragePathProvider(),
                Motara.Persistence.DiagnosticLogLevel.Information),
            ownsLogHost: true)
    {
    }

    internal App(MotaraLogHost logHost)
        : this(logHost, ownsLogHost: false)
    {
    }

    private App(MotaraLogHost logHost, bool ownsLogHost)
    {
        ArgumentNullException.ThrowIfNull(logHost);
        this.logHost = logHost;
        this.ownsLogHost = ownsLogHost;
    }

    public IThemeManager ThemeManager { get; } = new ThemeManager(ThemePalette.WarmNeutralLight);

    internal Task StartupTask => Volatile.Read(ref startupTask);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ThemeManager.Apply(Resources);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            exceptionHooks = new ApplicationExceptionHooks(
                logHost.LoggerFactory.CreateLogger<ApplicationExceptionHooks>());
            desktop.Exit += (_, _) => Dispose();
            StartupInvitationResult invitation = StartupInvitationDispatcher.Parse(
                desktop.Args ?? []);
            ShutdownMode originalShutdownMode = desktop.ShutdownMode;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnFrameworkInitializationCompleted();
            Volatile.Write(
                ref startupTask,
                InitializeDesktopAsync(desktop, invitation, originalShutdownMode));
            return;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeDesktopAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        StartupInvitationResult invitation,
        ShutdownMode originalShutdownMode)
    {
        ILogger logger = logHost.LoggerFactory.CreateLogger("Motara.App.Startup");
        long startedAt = Environment.TickCount64;
        AppStartupLog.Started(logger);
        try
        {
            Views.MainWindow mainWindow = await Views.MainWindow.CreateDefaultAsync(
                logHost,
                startupCancellation.Token);
            desktop.MainWindow = mainWindow;
            mainWindow.DispatchStartupInvitation(
                invitation,
                logHost.LoggerFactory.CreateLogger("Motara.App.StartupInvitation"));
            mainWindow.Show();
            desktop.ShutdownMode = originalShutdownMode;
            AppStartupLog.Completed(logger, Environment.TickCount64 - startedAt);
        }
        catch (OperationCanceledException) when (startupCancellation.IsCancellationRequested)
        {
            AppStartupLog.Cancelled(logger, Environment.TickCount64 - startedAt);
        }
        catch (Exception exception)
        {
            AppStartupLog.Failed(
                logger,
                exception,
                exception.GetType().Name,
                Environment.TickCount64 - startedAt);
            desktop.Shutdown(-1);
        }
    }

    public void Dispose()
    {
        startupCancellation.Cancel();
        exceptionHooks?.Dispose();
        exceptionHooks = null;
        if (ownsLogHost)
        {
            logHost.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}

internal static partial class AppStartupLog
{
    [LoggerMessage(6000, LogLevel.Information, "Application startup composition started")]
    internal static partial void Started(ILogger logger);

    [LoggerMessage(6001, LogLevel.Information,
        "Application startup composition completed in {DurationMs} ms")]
    internal static partial void Completed(ILogger logger, long durationMs);

    [LoggerMessage(6002, LogLevel.Debug,
        "Application startup composition was cancelled after {DurationMs} ms")]
    internal static partial void Cancelled(ILogger logger, long durationMs);

    [LoggerMessage(6003, LogLevel.Critical,
        "Application startup composition failed with {ExceptionType} after {DurationMs} ms")]
    internal static partial void Failed(
        ILogger logger,
        Exception exception,
        string exceptionType,
        long durationMs);
}
