using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace Motara.Media;

public readonly record struct VideoStreamInfo(int Width, int Height, double FramesPerSecond, bool HasAlpha);

public sealed class VideoFrame : IDisposable
{
    private byte[]? buffer;
    private readonly ArrayPool<byte>? pool;

    internal VideoFrame(
        byte[] buffer,
        int length,
        long index,
        TimeSpan timestamp,
        ArrayPool<byte>? pool = null)
    {
        this.buffer = buffer;
        this.pool = pool;
        Length = length;
        Index = index;
        Timestamp = timestamp;
    }

    public ReadOnlyMemory<byte> Pixels => buffer is { } value ? value.AsMemory(0, Length) : ReadOnlyMemory<byte>.Empty;
    public int Length { get; }
    public long Index { get; }
    public TimeSpan Timestamp { get; }

    public static VideoFrame CopyFrom(
        ReadOnlySpan<byte> pixels,
        long index,
        TimeSpan timestamp)
    {
        byte[] copy = pixels.ToArray();
        return new VideoFrame(copy, copy.Length, index, timestamp);
    }
    public void Dispose()
    {
        byte[]? released = Interlocked.Exchange(ref buffer, null);
        if (released is not null && pool is not null)
        {
            pool.Return(released);
        }
    }
}

public interface IVideoDecoder
{
    Task<VideoStreamInfo> ProbeAsync(string path, CancellationToken cancellationToken);
    IAsyncEnumerable<VideoFrame> DecodeLoopAsync(string path, VideoStreamInfo stream, CancellationToken cancellationToken);

    IAsyncEnumerable<VideoFrame> DecodeLoopAsync(
        string path,
        VideoStreamInfo stream,
        BackgroundVideoOptions options,
        CancellationToken cancellationToken) =>
        DecodeLoopAsync(path, stream, cancellationToken);
}

public sealed class FfmpegVideoDecoder(string ffprobePath, string ffmpegPath) : IVideoDecoder
{
    internal static IReadOnlyList<string> BuildDecodeArguments(
        string path,
        VideoStreamInfo stream,
        BackgroundVideoOptions options)
    {
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (options.Loop)
        {
            arguments.AddRange(["-stream_loop", "-1"]);
        }

        arguments.AddRange(["-readrate", options.PlaybackSpeed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)]);
        arguments.AddRange(["-i", path]);
        IReadOnlyList<string> additional = FfmpegArgumentTokenizer.Tokenize(options.FfmpegArguments);
        var filters = new List<string>();
        for (int index = 0; index < additional.Count; index++)
        {
            if (additional[index] is "-vf" or "-filter:v")
            {
                if (++index >= additional.Count)
                {
                    throw new ArgumentException("FFmpeg video filter requires a value.", nameof(options));
                }

                filters.Add(additional[index]);
                continue;
            }

            arguments.Add(additional[index]);
        }

        filters.Add($"format={(options.EnableAlpha ? "bgra" : "bgr0")}");
        arguments.AddRange(["-an", "-vf", string.Join(',', filters), "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"]);
        return arguments;
    }

    public async Task<VideoStreamInfo> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        ProcessStartInfo start = CreateStartInfo(
            ffprobePath,
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=width,height,r_frame_rate,pix_fmt",
            "-of", "json",
            path);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start ffprobe.");
        string json = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidDataException("FFmpeg could not probe the video.");
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement stream = document.RootElement.GetProperty("streams")[0];
        int width = stream.GetProperty("width").GetInt32();
        int height = stream.GetProperty("height").GetInt32();
        double fps = ParseRate(stream.GetProperty("r_frame_rate").GetString());
        string? pixelFormat = stream.TryGetProperty("pix_fmt", out JsonElement format) ? format.GetString() : null;
        return new VideoStreamInfo(width, height, fps <= 0 ? 30 : fps, pixelFormat?.Contains('a', StringComparison.OrdinalIgnoreCase) == true);
    }

    public async IAsyncEnumerable<VideoFrame> DecodeLoopAsync(string path, VideoStreamInfo stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (VideoFrame frame in DecodeLoopAsync(path, stream, BackgroundVideoOptions.Default, cancellationToken))
        {
            yield return frame;
        }
    }

    public async IAsyncEnumerable<VideoFrame> DecodeLoopAsync(
        string path,
        VideoStreamInfo stream,
        BackgroundVideoOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int length = checked(stream.Width * stream.Height * 4);
        ProcessStartInfo start = CreateStartInfo(ffmpegPath, BuildDecodeArguments(path, stream, options).ToArray());
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start ffmpeg.");
        long index = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    int offset = 0;
                    while (offset < length)
                    {
                        int read = await process.StandardOutput.BaseStream.ReadAsync(
                            buffer.AsMemory(offset, length - offset),
                            cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            yield break;
                        }

                        offset += read;
                    }

                    yield return new VideoFrame(
                        buffer,
                        length,
                        index,
                        TimeSpan.FromSeconds(index / stream.FramesPerSecond),
                        ArrayPool<byte>.Shared);
                    buffer = null!;
                    index++;
                }
                finally
                {
                    if (buffer is not null)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal static IReadOnlyList<string> TokenizeArguments(string arguments) =>
        FfmpegArgumentTokenizer.Tokenize(arguments);

    private static ProcessStartInfo CreateStartInfo(string fileName, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    private static double ParseRate(string? rate)
    {
        if (string.IsNullOrWhiteSpace(rate)) return 30;
        string[] parts = rate.Split('/');
        return parts.Length == 2 && double.TryParse(parts[0], out double numerator) && double.TryParse(parts[1], out double denominator) && denominator != 0 ? numerator / denominator : 30;
    }
}

internal static class FfmpegArgumentTokenizer
{
    internal static IReadOnlyList<string> Tokenize(string text)
    {
        var result = new List<string>();
        var token = new System.Text.StringBuilder();
        bool quoted = false;
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char current = text[i];
            if (quoted)
            {
                if (current == quote) { quoted = false; continue; }
                token.Append(current);
                continue;
            }

            if (current is '\'' or '"') { quoted = true; quote = current; continue; }
            if (char.IsWhiteSpace(current))
            {
                if (token.Length > 0) { result.Add(token.ToString()); token.Clear(); }
                continue;
            }
            token.Append(current);
        }

        if (quoted)
        {
            throw new ArgumentException("FFmpeg arguments contain an unmatched quote.", nameof(text));
        }
        if (token.Length > 0) result.Add(token.ToString());
        return result;
    }
}

public sealed class LatestVideoFrameMailbox : IDisposable
{
    private readonly object gate = new();
    private readonly Channel<VideoFrame> frames = Channel.CreateBounded<VideoFrame>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private int completed;

    public ValueTask PublishAsync(VideoFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (completed != 0)
            {
                frame.Dispose();
                return ValueTask.CompletedTask;
            }

            if (frames.Reader.TryRead(out VideoFrame? replaced))
            {
                replaced.Dispose();
            }

            if (!frames.Writer.TryWrite(frame))
            {
                frame.Dispose();
                throw new InvalidOperationException("The video frame mailbox could not accept a frame.");
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<VideoFrame> ReadAsync(CancellationToken cancellationToken) =>
        frames.Reader.ReadAsync(cancellationToken);

    public void Complete()
    {
        lock (gate)
        {
            if (completed != 0)
            {
                return;
            }

            completed = 1;
            frames.Writer.TryComplete();
            while (frames.Reader.TryRead(out VideoFrame? remaining))
            {
                remaining.Dispose();
            }
        }
    }

    public void Dispose()
    {
        Complete();
    }
}
