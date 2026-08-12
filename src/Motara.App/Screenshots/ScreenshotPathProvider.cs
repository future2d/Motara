namespace Motara.App.Screenshots;

internal interface IScreenshotPathProvider
{
    string ResolveDirectory(string? overrideDirectory);
}

internal sealed class ScreenshotPathProvider : IScreenshotPathProvider
{
    public string ResolveDirectory(string? overrideDirectory)
    {
        if (overrideDirectory is not null)
        {
            if (string.IsNullOrWhiteSpace(overrideDirectory))
            {
                throw new ArgumentException("Screenshot directory cannot be blank.", nameof(overrideDirectory));
            }

            return Path.GetFullPath(overrideDirectory);
        }

        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(pictures))
        {
            pictures = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Pictures");
        }

        return Path.Combine(pictures, "Motara");
    }
}
