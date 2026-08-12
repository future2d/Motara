namespace Motara.App.Diagnostics;

internal enum LogStoragePlatform
{
    Windows,
    Linux,
    MacOs,
}

internal sealed class PlatformLogStoragePathProvider : ILogStoragePathProvider
{
    public string GetLogsRoot()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        LogStoragePlatform platform = OperatingSystem.IsWindows()
            ? LogStoragePlatform.Windows
            : OperatingSystem.IsLinux()
                ? LogStoragePlatform.Linux
                : OperatingSystem.IsMacOS()
                    ? LogStoragePlatform.MacOs
                    : throw new PlatformNotSupportedException("The log storage platform is unsupported.");
        return Path.GetFullPath(ResolvePath(
            platform,
            localApplicationData,
            home,
            Environment.GetEnvironmentVariable("XDG_STATE_HOME")));
    }

    internal static string ResolvePath(
        LogStoragePlatform platform,
        string localApplicationData,
        string home,
        string? xdgStateHome) => platform switch
    {
        LogStoragePlatform.Windows => Path.Combine(
            RequireRoot(localApplicationData, nameof(localApplicationData)),
            "Motara",
            "Logs"),
        LogStoragePlatform.Linux => Path.Combine(
            string.IsNullOrWhiteSpace(xdgStateHome)
                ? Path.Combine(RequireRoot(home, nameof(home)), ".local", "state")
                : xdgStateHome,
            "Motara",
            "Logs"),
        LogStoragePlatform.MacOs => Path.Combine(
            RequireRoot(home, nameof(home)),
            "Library",
            "Logs",
            "Motara"),
        _ => throw new ArgumentOutOfRangeException(nameof(platform)),
    };

    private static string RequireRoot(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
}
