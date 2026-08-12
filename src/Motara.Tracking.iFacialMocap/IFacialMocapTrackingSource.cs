using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Motara.Core.Formulas;
using Motara.Tracking.Abstractions;

namespace Motara.Tracking.iFacialMocap;

/// <summary>Owns one bounded iFacialMocap UDP v2 receive loop.</summary>
public sealed class IFacialMocapTrackingSource :
    ITrackingSource,
    ITrackingSourceOutputLayout,
    ITrackingSourceCalibration
{
    /// <summary>Stable source identifier for iFacialMocap UDP v2 face input.</summary>
    public const string SourceId = "ifacialmocap.face.udp.v2";

    /// <summary>Official request string for ampersand-delimited v2 blendshape values.</summary>
    public const string VersionTwoHandshake =
        "iFacialMocap_sahuasouryya9218sauhuiayeta91555dy3719|sendDataVersion=v2";

    public const string LookForwardCommand = "iFacialMocap_lookForward";

    private const int MaximumPacketsPerSecond = 120;
    private static readonly byte[] HandshakeBytes = Encoding.UTF8.GetBytes(VersionTwoHandshake);
    private static readonly byte[] LookForwardBytes = Encoding.UTF8.GetBytes(LookForwardCommand);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(2);

    private readonly IFacialMocapOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<IFacialMocapTrackingSource> logger;
    private readonly CompiledSourceFormulaProgram formulaProgram;
    private readonly TrackingOutputDefinition[] outputDefinitions;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly TaskCompletionSource readCompleted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private Socket? socket;
    private long acceptedPackets;
    private long droppedPackets;
    private long handshakeRetries;
    private int readStarted;
    private int disposed;

    internal IFacialMocapTrackingSource(
        IFacialMocapOptions options,
        TimeProvider timeProvider,
        ILogger<IFacialMocapTrackingSource> logger,
        CompiledSourceFormulaProgram? formulaProgram = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.formulaProgram = formulaProgram ?? IFacialMocapFormulaProfile.Program;
        outputDefinitions = this.formulaProgram.OutputDefinitions
            .Select(definition => new TrackingOutputDefinition(
                definition.OutputId,
                definition.NeutralValue,
                definition.SuggestedMinimum,
                definition.SuggestedMaximum,
                definition.Smoothing))
            .ToArray();
    }

    string ITrackingSource.SourceId => SourceId;

    IReadOnlyList<TrackingOutputDefinition> ITrackingSourceOutputLayout.OutputDefinitions => outputDefinitions;

    public async Task<TrackingCalibrationResult> CalibrateAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        try
        {
            using var calibrationSocket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp);
            await calibrationSocket.SendToAsync(
                LookForwardBytes,
                SocketFlags.None,
                new IPEndPoint(options.DeviceAddress, options.DevicePort),
                cancellationToken).ConfigureAwait(false);
            IFacialMocapSourceLog.CalibrationSent(logger, options.DevicePort);
            return TrackingCalibrationResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            string errorCode = exception.GetType().Name;
            IFacialMocapSourceLog.CalibrationFailed(logger, errorCode);
            return TrackingCalibrationResult.Failure(errorCode);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RawTrackingFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref readStarted, 1) != 0)
        {
            throw new InvalidOperationException("An iFacialMocap source can be read only once.");
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeCancellation.Token);
        CancellationToken linkedToken = linkedCancellation.Token;
        long startTimestamp = timeProvider.GetTimestamp();
        long rateWindowTimestamp = startTimestamp;
        int packetsInWindow = 0;
        long sequence = 0;

        try
        {
            socket = CreateAndBindSocket();
            await SendHandshakeAsync(socket, linkedToken).ConfigureAwait(false);
            IFacialMocapSourceLog.Started(logger, options.LocalPort, options.DevicePort);
            IFacialMocapSourceLog.FormulaReady(
                logger,
                IFacialMocapFormulaProfile.Program.InputIds.Length,
                IFacialMocapFormulaProfile.Program.OutputDefinitions.Length);
            var buffer = new byte[IFacialMocapPacketParser.MaximumPacketBytes];

            while (true)
            {
                linkedToken.ThrowIfCancellationRequested();
                SocketReceiveMessageFromResult? received = await ReceiveWithRetryAsync(
                    socket,
                    buffer,
                    linkedToken).ConfigureAwait(false);
                if (received is null)
                {
                    continue;
                }

                long nowTimestamp = timeProvider.GetTimestamp();
                if (timeProvider.GetElapsedTime(rateWindowTimestamp, nowTimestamp) >= TimeSpan.FromSeconds(1))
                {
                    rateWindowTimestamp = nowTimestamp;
                    packetsInWindow = 0;
                }

                if (++packetsInWindow > MaximumPacketsPerSecond
                    || received.Value.RemoteEndPoint is not IPEndPoint sender
                    || !sender.Address.Equals(options.DeviceAddress)
                    || (received.Value.SocketFlags & SocketFlags.Truncated) != 0
                    || !IFacialMocapPacketParser.TryParse(
                        buffer.AsSpan(0, received.Value.ReceivedBytes),
                        out IFacialMocapPacket? packet)
                    || packet is null
                    || !TryCreateCanonicalFrame(
                        packet,
                        sequence,
                        timeProvider.GetElapsedTime(startTimestamp, nowTimestamp),
                        out RawTrackingFrame? frame)
                    || frame is null)
                {
                    Interlocked.Increment(ref droppedPackets);
                    continue;
                }

                sequence++;
                Interlocked.Increment(ref acceptedPackets);
                yield return frame;
            }
        }
        finally
        {
            socket?.Dispose();
            socket = null;
            readCompleted.TrySetResult();
            IFacialMocapSourceLog.Stopped(
                logger,
                Interlocked.Read(ref acceptedPackets),
                Interlocked.Read(ref droppedPackets),
                Interlocked.Read(ref handshakeRetries));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetimeCancellation.Cancel();
        socket?.Dispose();
        if (Volatile.Read(ref readStarted) != 0)
        {
            try
            {
                await readCompleted.Task.WaitAsync(DisposeTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                IFacialMocapSourceLog.DisposeTimedOut(logger);
            }
        }

        lifetimeCancellation.Dispose();
    }

    private Socket CreateAndBindSocket()
    {
        var created = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            created.Bind(new IPEndPoint(options.LocalAddress, options.LocalPort));
            return created;
        }
        catch (Exception exception)
        {
            created.Dispose();
            IFacialMocapSourceLog.Faulted(logger, exception.GetType().Name);
            throw;
        }
    }

    private async ValueTask<SocketReceiveMessageFromResult?> ReceiveWithRetryAsync(
        Socket activeSocket,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        receiveCancellation.CancelAfter(options.HandshakeRetryInterval);
        try
        {
            return await activeSocket.ReceiveMessageFromAsync(
                buffer,
                SocketFlags.None,
                new IPEndPoint(IPAddress.Any, 0),
                receiveCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            long retry = Interlocked.Increment(ref handshakeRetries);
            if (retry == 1 || retry % 30 == 0)
            {
                IFacialMocapSourceLog.HandshakeRetried(logger, retry);
            }

            await SendHandshakeAsync(activeSocket, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception)
        {
            IFacialMocapSourceLog.Faulted(logger, exception.GetType().Name);
            throw;
        }
    }

    private async ValueTask SendHandshakeAsync(Socket activeSocket, CancellationToken cancellationToken)
    {
        await activeSocket.SendToAsync(
            HandshakeBytes,
            SocketFlags.None,
            new IPEndPoint(options.DeviceAddress, options.DevicePort),
            cancellationToken).ConfigureAwait(false);
    }

    private bool TryCreateCanonicalFrame(
        IFacialMocapPacket packet,
        long sequence,
        TimeSpan monotonicTimestamp,
        out RawTrackingFrame? frame)
    {
        var rawValues = new double[IFacialMocapInputSchema.InputIds.Length];
        var rawValidity = new ParameterValidity[IFacialMocapInputSchema.InputIds.Length];
        Array.Fill(rawValidity, ParameterValidity.Missing);

        if (packet.Head is { } head)
        {
            Set("Head.EulerYDegrees", head.EulerY);
            Set("Head.EulerXDegrees", head.EulerX);
            Set("Head.EulerZDegrees", head.EulerZ);
            Set("Head.PositionX", head.PositionX);
            Set("Head.PositionY", head.PositionY);
            Set("Head.PositionZ", head.PositionZ);
        }

        SetEye("Left", packet.LeftEye);
        SetEye("Right", packet.RightEye);
        foreach ((string name, double value) in packet.BlendShapes)
        {
            if (IFacialMocapInputSchema.TryGetBlendShapeSlot(name, out int slot))
            {
                rawValues[slot] = value;
                rawValidity[slot] = ParameterValidity.Valid;
            }
        }

        SourceFormulaEvaluation evaluation = formulaProgram.Evaluate(
            rawValues,
            rawValidity);
        if (!evaluation.Validity.Contains(ParameterValidity.Valid))
        {
            frame = null;
            return false;
        }

        frame = new RawTrackingFrame(
            SourceId,
            sequence,
            monotonicTimestamp,
            timeProvider.GetUtcNow(),
            evaluation.Values.AsSpan(),
            evaluation.Validity.AsSpan(),
            TrackingPresence.Tracked);
        return true;

        void Set(string id, double value)
        {
            int slot = IFacialMocapInputSchema.GetRequiredSlot(id);
            rawValues[slot] = value;
            rawValidity[slot] = ParameterValidity.Valid;
        }

        void SetEye(string side, IFacialMocapEulerAngles? eye)
        {
            if (eye is null) return;
            Set($"Eye.{side}.EulerXDegrees", eye.X);
            Set($"Eye.{side}.EulerYDegrees", eye.Y);
            Set($"Eye.{side}.EulerZDegrees", eye.Z);
        }
    }
}

internal static partial class IFacialMocapSourceLog
{
    [LoggerMessage(6400, LogLevel.Information, "iFacialMocap UDP source started on port {LocalPort} for device port {DevicePort}")]
    internal static partial void Started(ILogger logger, int localPort, int devicePort);

    [LoggerMessage(6401, LogLevel.Debug, "iFacialMocap UDP handshake retry count reached {RetryCount}")]
    internal static partial void HandshakeRetried(ILogger logger, long retryCount);

    [LoggerMessage(6402, LogLevel.Information, "iFacialMocap UDP source stopped; accepted {AcceptedPackets}, dropped {DroppedPackets}, handshake retries {HandshakeRetries}")]
    internal static partial void Stopped(
        ILogger logger,
        long acceptedPackets,
        long droppedPackets,
        long handshakeRetries);

    [LoggerMessage(6403, LogLevel.Warning, "iFacialMocap UDP source faulted with {ErrorCode}")]
    internal static partial void Faulted(ILogger logger, string errorCode);

    [LoggerMessage(6404, LogLevel.Warning, "iFacialMocap UDP source disposal exceeded its bounded wait")]
    internal static partial void DisposeTimedOut(ILogger logger);

    [LoggerMessage(6405, LogLevel.Debug, "iFacialMocap formula program ready with {InputCount} inputs and {OutputCount} outputs")]
    internal static partial void FormulaReady(ILogger logger, int inputCount, int outputCount);

    [LoggerMessage(6406, LogLevel.Information, "iFacialMocap look-forward calibration sent to device port {DevicePort}")]
    internal static partial void CalibrationSent(ILogger logger, int devicePort);

    [LoggerMessage(6407, LogLevel.Warning, "iFacialMocap look-forward calibration failed with {ErrorCode}")]
    internal static partial void CalibrationFailed(ILogger logger, string errorCode);
}
