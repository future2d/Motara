using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Motara.Core.Formulas;
using Motara.Core.Parameters;
using Motara.Tracking.Abstractions;

namespace Motara.App.Tracking;

internal sealed class OpenSeeFaceLocalTrackingSourceFactory : ITrackingSourceFactory
{
    internal const string SourceId = "openseeface.local-camera";
    internal static string DefaultExecutablePath =>
        Path.Combine(AppContext.BaseDirectory, "tracking", "OpenSeeFace", "facetracker.exe");
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private string executablePath;
    private OpenSeeFaceConfiguration captureConfiguration;
    private CompiledSourceFormulaProgram formulaProgram = SourceFormulaCompiler.Compile(
        OpenSeeFaceMappingDefaults.CreateProfile().ToFormulaProfile());

    internal OpenSeeFaceLocalTrackingSourceFactory(
        TimeProvider timeProvider,
        ILogger? logger = null,
        string? executablePath = null,
        OpenSeeFaceConfiguration? captureConfiguration = null)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        this.executablePath = NormalizeExecutablePath(
            executablePath
                ?? Environment.GetEnvironmentVariable("MOTARA_OPENSEEFACE_EXE")
                ?? DefaultExecutablePath);
        this.captureConfiguration = captureConfiguration ?? OpenSeeFaceConfiguration.Create();
    }

    internal string ExecutablePath => executablePath;

    internal OpenSeeFaceConfiguration CaptureConfiguration => captureConfiguration;

    internal void ConfigureExecutablePath(string path)
    {
        executablePath = NormalizeExecutablePath(path);
        OpenSeeFaceTrackingLog.PathConfigured(logger, executablePath);
    }

    internal void ConfigureCapture(OpenSeeFaceConfiguration configuration)
    {
        OpenSeeFaceConfiguration.Validate(configuration);
        captureConfiguration = configuration;
        OpenSeeFaceTrackingLog.CaptureConfigured(
            logger,
            configuration.CameraIndex,
            configuration.Width,
            configuration.Height,
            configuration.Fps);
    }

    internal void ConfigureMapping(SourceMappingProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!StringComparer.Ordinal.Equals(document.AdapterId, "openseeface")
            || !document.InputIds.SequenceEqual(OpenSeeFaceInputSchema.InputIds))
        {
            throw new ArgumentException("The mapping does not match the OpenSeeFace input schema.", nameof(document));
        }

        foreach (SourceMappingOutputDocument output in document.Outputs)
        {
            if (!StandardParameterCatalog.Registry.TryGetSlot(output.ParameterId, out int slot))
            {
                continue;
            }

            ParameterDefinition standard = StandardParameterCatalog.Definitions[slot];
            if (standard.NeutralValue != output.NeutralValue
                || standard.SuggestedMinimum != output.SuggestedMinimum
                || standard.SuggestedMaximum != output.SuggestedMaximum)
            {
                throw new ArgumentException(
                    $"Mapping metadata conflicts with the built-in parameter: {output.ParameterId}",
                    nameof(document));
            }
        }

        CompiledSourceFormulaProgram compiled = SourceFormulaCompiler.Compile(document.ToFormulaProfile());
        Volatile.Write(ref formulaProgram, compiled);
        OpenSeeFaceTrackingLog.FormulaConfigured(logger, compiled.InputIds.Length, compiled.OutputDefinitions.Length);
    }

    internal async Task<IReadOnlyList<OpenSeeFaceCamera>> ListCamerasAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(executablePath))
        {
            return [];
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-l");
        process.StartInfo.ArgumentList.Add("1");
        if (!process.Start())
        {
            return [];
        }

        string output = await process.StandardOutput
            .ReadToEndAsync(cancellationToken)
            .ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var cameras = new List<OpenSeeFaceCamera>();
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf(':');
            if (separator <= 0
                || !int.TryParse(
                    line[..separator].Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int index))
            {
                continue;
            }

            string name = line[(separator + 1)..].Trim();
            if (name.Length > 0)
            {
                cameras.Add(new OpenSeeFaceCamera(index, name));
            }
        }

        OpenSeeFaceTrackingLog.CamerasListed(logger, cameras.Count);
        return cameras;
    }

    public TrackingSourceDescriptor Descriptor { get; } = new(
        SourceId,
        "openseeface",
        "Menu.Tracking.Source.OpenSeeFace", "Icon.Lucide.ScanFace", [TrackingChannel.Face]);

    public ValueTask<TrackingSourceAvailability> CheckAvailabilityAsync(TrackingChannel channel, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (channel != TrackingChannel.Face) return ValueTask.FromResult(TrackingSourceAvailability.Unavailable("tracking.channel.unsupported"));
        return ValueTask.FromResult(File.Exists(executablePath)
            ? TrackingSourceAvailability.Available
            : MissingRuntime());

        TrackingSourceAvailability MissingRuntime()
        {
            OpenSeeFaceTrackingLog.RuntimeMissing(logger, executablePath);
            return TrackingSourceAvailability.Unavailable("tracking.openseeface.runtime_missing");
        }
    }

    public ValueTask<ITrackingSource> CreateAsync(TrackingChannel channel, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (channel != TrackingChannel.Face) throw new InvalidOperationException("OpenSeeFace supports only the face channel.");
        if (!File.Exists(executablePath)) throw new InvalidOperationException("OpenSeeFace runtime is unavailable.");
        return ValueTask.FromResult<ITrackingSource>(new OpenSeeFaceLocalTrackingSource(
            executablePath,
            timeProvider,
            logger,
            captureConfiguration,
            Volatile.Read(ref formulaProgram)));
    }

    private static string NormalizeExecutablePath(string path) =>
        Path.GetFullPath(
            string.IsNullOrWhiteSpace(path)
                ? throw new ArgumentException("An OpenSeeFace executable path is required.", nameof(path))
                : path.Trim());
}

internal sealed class OpenSeeFaceLocalTrackingSource : ITrackingSource, ITrackingSourceOutputLayout
{
    internal const int AutomaticUdpPort = 0;
    private readonly string executablePath;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly CancellationTokenSource lifetime = new();
    private Process? process;
    private WindowsChildProcessJob? processJob;

    private readonly OpenSeeFaceConfiguration captureConfiguration;
    private readonly CompiledSourceFormulaProgram formulaProgram;
    private readonly TrackingOutputDefinition[] outputDefinitions;
    private readonly int configuredUdpPort;
    private int listeningPort;

    internal OpenSeeFaceLocalTrackingSource(
        string executablePath,
        TimeProvider timeProvider,
        ILogger logger,
        OpenSeeFaceConfiguration captureConfiguration,
        CompiledSourceFormulaProgram? formulaProgram = null,
        int udpPort = AutomaticUdpPort)
    {
        this.executablePath = executablePath;
        this.timeProvider = timeProvider;
        this.logger = logger;
        OpenSeeFaceConfiguration.Validate(captureConfiguration);
        this.captureConfiguration = captureConfiguration;
        this.formulaProgram = formulaProgram ?? SourceFormulaCompiler.Compile(
            OpenSeeFaceMappingDefaults.CreateProfile().ToFormulaProfile());
        if (udpPort is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(udpPort));
        }

        configuredUdpPort = udpPort;
        outputDefinitions = this.formulaProgram.OutputDefinitions
            .Select(definition => new TrackingOutputDefinition(
                definition.OutputId,
                definition.NeutralValue,
                definition.SuggestedMinimum,
                definition.SuggestedMaximum,
                definition.Smoothing))
            .ToArray();
    }

    public string SourceId => OpenSeeFaceLocalTrackingSourceFactory.SourceId;
    public IReadOnlyList<TrackingOutputDefinition> OutputDefinitions => outputDefinitions;

    internal int ListeningPort => Volatile.Read(ref listeningPort);

    public async IAsyncEnumerable<RawTrackingFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        long started = timeProvider.GetTimestamp();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, configuredUdpPort));
        }
        catch (SocketException exception)
        {
            OpenSeeFaceTrackingLog.SocketBindFailed(logger, configuredUdpPort, exception.SocketErrorCode);
            throw;
        }

        int activePort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        Volatile.Write(ref listeningPort, activePort);
        try
        {
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(captureConfiguration.CameraIndex.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-W");
            startInfo.ArgumentList.Add(captureConfiguration.Width.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-H");
            startInfo.ArgumentList.Add(captureConfiguration.Height.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-F");
            startInfo.ArgumentList.Add(captureConfiguration.Fps.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add("127.0.0.1");
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(activePort.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add("3");
            process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("Process.Start returned null.");
            }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    processJob = WindowsChildProcessJob.CreateFor(process);
                    OpenSeeFaceTrackingLog.ProcessJobAttached(logger, process.Id);
                }
                catch (Exception exception)
                {
                    OpenSeeFaceTrackingLog.ProcessJobAttachFailed(
                        logger,
                        process.Id,
                        exception.GetType().Name);
                    StopProcess(process);
                    process = null;
                    throw;
                }
            }

            OpenSeeFaceTrackingLog.ProcessStarted(
                logger,
                captureConfiguration.CameraIndex,
                $"127.0.0.1:{activePort}");
        }
        catch (Exception exception)
        {
            OpenSeeFaceTrackingLog.ProcessStartFailed(logger, exception.GetType().Name);
            throw;
        }
        long sequence = 0;
        byte[] buffer = new byte[65535];
        double[] rawValues = new double[OpenSeeFaceInputSchema.InputCount];
        ParameterValidity[] rawValidity = new ParameterValidity[rawValues.Length];
        try
        {
            while (true)
            {
                SocketReceiveFromResult received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, 0), linked.Token).ConfigureAwait(false);
                if (!OpenSeeFacePacketParser.TryParse(
                    buffer.AsSpan(0, received.ReceivedBytes),
                    rawValues,
                    rawValidity))
                {
                    OpenSeeFaceTrackingLog.PacketDropped(logger, received.ReceivedBytes);
                    continue;
                }

                SourceFormulaEvaluation evaluation = formulaProgram.Evaluate(rawValues, rawValidity);
                if (!evaluation.Validity.Contains(ParameterValidity.Valid))
                {
                    OpenSeeFaceTrackingLog.FrameDropped(logger);
                    continue;
                }

                yield return new RawTrackingFrame(
                    SourceId,
                    sequence++,
                    timeProvider.GetElapsedTime(started),
                    DateTimeOffset.UtcNow,
                    evaluation.Values.AsSpan(),
                    evaluation.Validity.AsSpan(),
                    GetPresence(rawValues));
            }
        }
        finally
        {
            await StopTrackedProcessAsync().ConfigureAwait(false);
            OpenSeeFaceTrackingLog.ProcessStopped(logger);
        }
    }

    internal static TrackingPresence GetPresence(ReadOnlySpan<double> rawValues) =>
        rawValues[OpenSeeFaceInputSchema.GetRequiredSlot("Tracking.Success")] > 0
            ? TrackingPresence.Tracked
            : TrackingPresence.Lost;

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        await StopTrackedProcessAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }

    internal static bool StopProcess(Process? active)
    {
        if (active is null)
        {
            return true;
        }

        try
        {
            if (!active.HasExited)
            {
                active.Kill(entireProcessTree: true);
            }

            return active.HasExited || active.WaitForExit(2000);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and Kill/WaitForExit.
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            active.Dispose();
        }
    }

    private async Task StopTrackedProcessAsync()
    {
        Process? active = Interlocked.Exchange(ref process, null);
        WindowsChildProcessJob? job = Interlocked.Exchange(ref processJob, null);
        bool stopped = await StopProcessAsync(active).ConfigureAwait(false);
        job?.Dispose();
        if (!stopped)
        {
            OpenSeeFaceTrackingLog.ProcessStopFailed(logger);
        }
    }

    private static async Task<bool> StopProcessAsync(Process? active)
    {
        if (active is null)
        {
            return true;
        }

        try
        {
            if (!active.HasExited)
            {
                active.Kill(entireProcessTree: true);
            }

            await active.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            active.Dispose();
        }
    }
}

internal readonly record struct OpenSeeFaceCamera(int Index, string Name)
{
    public override string ToString() => $"{Index}: {Name}";
}

internal static class OpenSeeFacePacketParser
{
    internal const int PacketBytes = 1785;
    internal const int RightEyeOpenOffset = 20;
    internal const int LeftEyeOpenOffset = 24;
    internal const int SuccessOffset = 28;
    internal const int PnpErrorOffset = 29;
    internal const int QuaternionOffset = 33;
    internal const int EulerOffset = 49;
    internal const int TranslationOffset = 61;
    internal const int LandmarkConfidenceOffset = 73;
    internal const int LandmarkPositionOffset = 345;
    internal const int Point3DOffset = 889;
    internal const int FeaturesOffset = 1729;

    internal static bool TryParse(
        ReadOnlySpan<byte> buffer,
        Span<double> values,
        Span<ParameterValidity> validity)
    {
        if (buffer.Length != PacketBytes
            || values.Length != OpenSeeFaceInputSchema.InputCount
            || validity.Length != values.Length
            || buffer[SuccessOffset] > 1)
        {
            return false;
        }

        int slot = 0;
        if (!TryReadFinite(buffer, RightEyeOpenOffset, out values[slot++])
            || !TryReadFinite(buffer, LeftEyeOpenOffset, out values[slot++]))
        {
            return false;
        }

        values[slot++] = buffer[SuccessOffset];
        if (!TryReadFinite(buffer, PnpErrorOffset, out values[slot++])) return false;
        for (int index = 0; index < 4; index++)
        {
            if (!TryReadFinite(buffer, QuaternionOffset + index * sizeof(float), out values[slot++])) return false;
        }

        if (!TryReadFinite(buffer, EulerOffset, out double rawEulerXDegrees)
            || !TryReadFinite(buffer, EulerOffset + sizeof(float), out double rawEulerYDegrees)
            || !TryReadFinite(buffer, EulerOffset + 2 * sizeof(float), out double rawEulerZDegrees))
        {
            return false;
        }

        // OpenSeeFace reports OpenCV Euler angles. Its frontal reference is
        // approximately X=180 and Z=90 degrees; normalize them into signed
        // pose angles so a neutral face evaluates to zero instead of hitting
        // the canonical parameter limits.
        values[slot++] = NormalizeSignedDegrees(-(rawEulerXDegrees + 180d));
        values[slot++] = NormalizeSignedDegrees(-rawEulerYDegrees);
        values[slot++] = NormalizeSignedDegrees(rawEulerZDegrees - 90d);

        for (int index = 0; index < 3; index++)
        {
            if (!TryReadFinite(buffer, TranslationOffset + index * sizeof(float), out values[slot++])) return false;
        }

        for (int index = 0; index < OpenSeeFaceInputSchema.FeatureCount; index++)
        {
            if (!TryReadFinite(buffer, FeaturesOffset + index * sizeof(float), out values[slot++])) return false;
        }

        validity.Clear();
        return slot == values.Length;
    }

    private static double NormalizeSignedDegrees(double degrees)
    {
        double normalized = degrees % 360d;
        if (normalized > 180d)
        {
            normalized -= 360d;
        }
        else if (normalized < -180d)
        {
            normalized += 360d;
        }

        return Math.Abs(normalized) < 1e-4d ? 0d : normalized;
    }

    private static bool TryReadFinite(ReadOnlySpan<byte> buffer, int offset, out double value)
    {
        float parsed = BitConverter.ToSingle(buffer.Slice(offset, sizeof(float)));
        value = parsed;
        return float.IsFinite(parsed);
    }
}

internal static partial class OpenSeeFaceTrackingLog
{
    [LoggerMessage(6700, LogLevel.Information, "OpenSeeFace process started on camera {CameraIndex}; endpoint={Endpoint}")]
    internal static partial void ProcessStarted(ILogger logger, int cameraIndex, string endpoint);

    [LoggerMessage(6701, LogLevel.Information, "OpenSeeFace process stopped")]
    internal static partial void ProcessStopped(ILogger logger);

    [LoggerMessage(6702, LogLevel.Warning, "OpenSeeFace runtime missing at {ExecutablePath}")]
    internal static partial void RuntimeMissing(ILogger logger, string executablePath);

    [LoggerMessage(6703, LogLevel.Error, "OpenSeeFace process failed to start: {ErrorType}")]
    internal static partial void ProcessStartFailed(ILogger logger, string errorType);

    [LoggerMessage(6704, LogLevel.Debug, "OpenSeeFace packet dropped; bytes={ByteCount}")]
    internal static partial void PacketDropped(ILogger logger, int byteCount);

    [LoggerMessage(6705, LogLevel.Information, "OpenSeeFace executable path configured: {ExecutablePath}")]
    internal static partial void PathConfigured(ILogger logger, string executablePath);

    [LoggerMessage(6706, LogLevel.Information, "OpenSeeFace capture configured: camera={CameraIndex}, size={Width}x{Height}, fps={Fps}")]
    internal static partial void CaptureConfigured(ILogger logger, int cameraIndex, int width, int height, int fps);

    [LoggerMessage(6707, LogLevel.Debug, "OpenSeeFace camera discovery completed: count={Count}")]
    internal static partial void CamerasListed(ILogger logger, int count);

    [LoggerMessage(6708, LogLevel.Warning, "OpenSeeFace process did not exit within the termination timeout")]
    internal static partial void ProcessStopFailed(ILogger logger);

    [LoggerMessage(6709, LogLevel.Information, "OpenSeeFace mapping configured with {InputCount} inputs and {OutputCount} outputs")]
    internal static partial void FormulaConfigured(ILogger logger, int inputCount, int outputCount);

    [LoggerMessage(6710, LogLevel.Debug, "OpenSeeFace frame dropped because no mapped output was valid")]
    internal static partial void FrameDropped(ILogger logger);

    [LoggerMessage(6711, LogLevel.Error, "OpenSeeFace UDP receiver failed to bind local port {Port}; socketError={SocketError}")]
    internal static partial void SocketBindFailed(ILogger logger, int port, SocketError socketError);

    [LoggerMessage(6714, LogLevel.Information, "OpenSeeFace process assigned to a kill-on-close job; pid={ProcessId}")]
    internal static partial void ProcessJobAttached(ILogger logger, int processId);

    [LoggerMessage(6715, LogLevel.Error, "OpenSeeFace process could not be assigned to a kill-on-close job; pid={ProcessId}; error={ErrorType}")]
    internal static partial void ProcessJobAttachFailed(ILogger logger, int processId, string errorType);
}
