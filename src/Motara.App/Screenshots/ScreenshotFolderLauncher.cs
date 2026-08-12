using System.Diagnostics;

namespace Motara.App.Screenshots;

internal interface IScreenshotFolderLauncher
{
    void Open(string path);
}

internal sealed class PlatformScreenshotFolderLauncher : IScreenshotFolderLauncher
{
    public void Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalizedPath = Path.GetFullPath(path);
        Directory.CreateDirectory(normalizedPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = normalizedPath,
            UseShellExecute = true,
        });
    }
}
