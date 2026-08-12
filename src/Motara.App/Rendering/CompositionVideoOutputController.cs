using Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Media;

namespace Motara.App.Rendering;

internal sealed class CompositionVideoOutputController : IAsyncDisposable
{
    internal sealed record Settings(string Name, int Width, int Height, double FramesPerSecond)
    {
        internal static Settings Default { get; } = new("Motara", 0, 0, 60);
    }
    private readonly VideoSignalRegistry registry;
    private readonly CompositionFramePublisher publisher;
    private readonly Func<PixelSize> sizeProvider;
    private readonly ILogger logger;
    private readonly object gate = new();
    private readonly HashSet<VideoSignalProtocol> enabled = [];
    private readonly Dictionary<VideoSignalProtocol, Settings> settings = [];
    private int disposed;

    internal CompositionVideoOutputController(
        VideoSignalRegistry registry,
        CompositionFramePublisher publisher,
        Func<PixelSize> sizeProvider,
        ILogger<CompositionVideoOutputController>? logger = null)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.sizeProvider = sizeProvider ?? throw new ArgumentNullException(nameof(sizeProvider));
        this.logger = logger ?? NullLogger<CompositionVideoOutputController>.Instance;
    }

    internal bool IsEnabled(VideoSignalProtocol protocol)
    {
        lock (gate)
        {
            return enabled.Contains(protocol);
        }
    }

    internal Settings GetSettings(VideoSignalProtocol protocol)
    {
        lock (gate)
        {
            return settings.TryGetValue(protocol, out Settings? value) ? value : Settings.Default;
        }
    }

    internal async Task ApplySettingsAsync(
        VideoSignalProtocol protocol,
        Settings value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Width < 0 || value.Height < 0 || value.FramesPerSecond is < 1 or > 240
            || string.IsNullOrWhiteSpace(value.Name))
        {
            throw new ArgumentException("Video output settings are invalid.", nameof(value));
        }

        bool restart = IsEnabled(protocol);
        if (restart)
        {
            await SetEnabledAsync(protocol, false, cancellationToken).ConfigureAwait(false);
        }

        lock (gate)
        {
            settings[protocol] = value with { Name = value.Name.Trim() };
        }

        if (restart)
        {
            await SetEnabledAsync(protocol, true, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task SetEnabledAsync(
        VideoSignalProtocol protocol,
        bool value,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        string targetId = $"composition.{protocol.ToString().ToLowerInvariant()}";
        if (!value)
        {
            await publisher.RemoveTargetAsync(targetId, cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                enabled.Remove(protocol);
            }

            CompositionVideoOutputLog.Stopped(logger, protocol);
            return;
        }

        lock (gate)
        {
            if (enabled.Contains(protocol))
            {
                return;
            }
        }

        Settings outputSettings = GetSettings(protocol);
        PixelSize size = sizeProvider();
        int width = outputSettings.Width > 0 ? outputSettings.Width : size.Width;
        int height = outputSettings.Height > 0 ? outputSettings.Height : size.Height;
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new InvalidOperationException("The composition canvas has no usable size.");
        }

        IVideoSignalProtocolAdapter adapter = registry.GetRequiredAdapter(protocol);
        IVideoSignalSender sender = adapter.CreateSender();
        try
        {
            await publisher.AddTargetAsync(
                targetId,
                sender,
                new VideoSignalOutputOptions(protocol, outputSettings.Name, width, height, outputSettings.FramesPerSecond),
                cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                enabled.Add(protocol);
            }

            CompositionVideoOutputLog.Started(logger, protocol, width, height);
        }
        catch
        {
            await sender.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        VideoSignalProtocol[] protocols;
        lock (gate)
        {
            protocols = [.. enabled];
            enabled.Clear();
        }

        foreach (VideoSignalProtocol protocol in protocols)
        {
            await publisher.RemoveTargetAsync(
                $"composition.{protocol.ToString().ToLowerInvariant()}",
                CancellationToken.None).ConfigureAwait(false);
        }
    }
}

internal static partial class CompositionVideoOutputLog
{
    [LoggerMessage(6860, LogLevel.Information, "Composition video output started for {Protocol} at {Width}x{Height}")]
    internal static partial void Started(ILogger logger, VideoSignalProtocol protocol, int width, int height);

    [LoggerMessage(6861, LogLevel.Information, "Composition video output stopped for {Protocol}")]
    internal static partial void Stopped(ILogger logger, VideoSignalProtocol protocol);
}
