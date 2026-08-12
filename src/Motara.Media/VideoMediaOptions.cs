using System.Text.Json.Serialization;

namespace Motara.Media;

public sealed record BackgroundVideoOptions
{
    public const double DefaultPlaybackSpeed = 1.0;

    [JsonConstructor]
    public BackgroundVideoOptions(
        bool EnableAlpha = true,
        bool Loop = true,
        double PlaybackSpeed = DefaultPlaybackSpeed,
        string FfmpegArguments = "")
    {
        if (!double.IsFinite(PlaybackSpeed) || PlaybackSpeed < 0.1 || PlaybackSpeed > 8)
        {
            throw new ArgumentException("Video playback speed must be between 0.1 and 8.", nameof(PlaybackSpeed));
        }

        ArgumentNullException.ThrowIfNull(FfmpegArguments);
        if (FfmpegArguments.Length > 4096)
        {
            throw new ArgumentException("FFmpeg arguments are too long.", nameof(FfmpegArguments));
        }

        this.EnableAlpha = EnableAlpha;
        this.Loop = Loop;
        this.PlaybackSpeed = PlaybackSpeed;
        this.FfmpegArguments = FfmpegArguments.Trim();
    }

    public bool EnableAlpha { get; }
    public bool Loop { get; }
    public double PlaybackSpeed { get; }
    public string FfmpegArguments { get; }
    public static BackgroundVideoOptions Default { get; } = new();
}
