using System.Diagnostics;

namespace Motara.App.Models;

internal interface IModelsFolderLauncher
{
    void Open(string path);
}

internal sealed class PlatformModelsFolderLauncher : IModelsFolderLauncher
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
