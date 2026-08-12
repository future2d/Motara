using Avalonia;
using Microsoft.Extensions.Logging;
using Motara.App.Diagnostics;
using Motara.Persistence;

namespace Motara.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using MotaraLogHost logHost = MotaraLogHost.Create(
            new PlatformLogStoragePathProvider(),
            DiagnosticLogLevel.Information);
        ILogger logger = logHost.LoggerFactory.CreateLogger("Motara.App.Lifetime");
        ApplicationLifecycleLog.Started(logger);
        try
        {
            Avalonia.Logging.Logger.Sink = new AvaloniaLogSink(
                logHost.LoggerFactory.CreateLogger<AvaloniaLogSink>());
            BuildAvaloniaApp(logHost).StartWithClassicDesktopLifetime(args);
            ApplicationLifecycleLog.Stopped(logger);
        }
        catch (Exception exception)
        {
            ApplicationLifecycleLog.Fatal(logger, exception.GetType().Name, exception);
            throw;
        }
    }

    internal static AppBuilder BuildAvaloniaApp(MotaraLogHost logHost)
    {
        ArgumentNullException.ThrowIfNull(logHost);
        AppBuilder builder = AppBuilder.Configure(() => new App(logHost))
            .UsePlatformDetect();
        if (OperatingSystem.IsWindows())
        {
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = GetWindowsRenderingModes(),
            });
        }

        return builder;
    }

    internal static Win32RenderingMode[] GetWindowsRenderingModes() =>
    [
        Win32RenderingMode.Wgl,
        Win32RenderingMode.Software,
    ];
}
