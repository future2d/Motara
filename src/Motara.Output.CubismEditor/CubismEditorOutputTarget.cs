using System.Text.Json;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Output.Abstractions;

namespace Motara.Output.CubismEditor;

/// <summary>Owns the Cubism Editor external-API connection and parameter output lifecycle.</summary>
public sealed class CubismEditorOutputTarget : IOutputParameterPublisher, IAsyncDisposable
{
    private static readonly TimeSpan EditModePollingInterval = TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private CubismEditorConnectionOptions options;
    private readonly Func<ICubismEditorTransport> transportFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<CubismEditorOutputTarget> logger;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object frameGate = new();
    private readonly object modelParametersGate = new();
    private CancellationTokenSource? workerCancellation;
    private Task? worker;
    private OutputParameterFrame? latestFrame;
    private CubismEditorOutputStatus status;
    private ImmutableArray<CubismEditorModelParameter> currentModelParameters = [];
    private string? currentEditMode;
    private string? pluginToken;
    private long requestSequence;
    private int disposed;

    public CubismEditorOutputTarget(CubismEditorConnectionOptions options)
        : this(options, static () => new ClientWebSocketCubismEditorTransport())
    {
    }

    public CubismEditorOutputTarget(
        CubismEditorConnectionOptions options,
        ILogger<CubismEditorOutputTarget> logger)
        : this(options, static () => new ClientWebSocketCubismEditorTransport(), logger: logger)
    {
    }

    public CubismEditorOutputTarget(
        CubismEditorConnectionOptions options,
        Func<ICubismEditorTransport> transportFactory,
        TimeProvider? timeProvider = null,
        ILogger<CubismEditorOutputTarget>? logger = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<CubismEditorOutputTarget>.Instance;
        status = CubismEditorOutputStatus.Stopped(options);
    }

    public event EventHandler<CubismEditorOutputStatus>? StatusChanged;

    public event EventHandler? ActivityChanged;

    public CubismEditorOutputStatus Status => Volatile.Read(ref status);

    /// <summary>Parameters reported by the currently resolved model in Cubism Editor.</summary>
    public ImmutableArray<CubismEditorModelParameter> CurrentModelParameters
    {
        get
        {
            lock (modelParametersGate)
            {
                return currentModelParameters;
            }
        }
    }

    public bool IsActive => Status.State != CubismEditorOutputState.Stopped;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (worker is not null) return;
            workerCancellation = new CancellationTokenSource();
            PublishStatus(CubismEditorOutputState.Connecting, null, null);
            worker = Task.Run(() => RunAsync(workerCancellation.Token), CancellationToken.None);
            CubismEditorOutputLog.Started(logger, options.Endpoint.Host, options.Endpoint.Port);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? activeWorker;
        CancellationTokenSource? cancellation;
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            activeWorker = worker;
            cancellation = workerCancellation;
            worker = null;
            workerCancellation = null;
            cancellation?.Cancel();
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (activeWorker is not null)
        {
            await activeWorker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellation?.Dispose();
        PublishStatus(CubismEditorOutputState.Stopped, null, null);
        lock (modelParametersGate) currentModelParameters = [];
        Volatile.Write(ref currentEditMode, null);
        CubismEditorOutputLog.Stopped(logger);
    }

    /// <summary>Applies a new local endpoint and safely reconnects when output is active.</summary>
    public async Task ConfigureAsync(
        CubismEditorConnectionOptions connectionOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectionOptions);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        bool restart = IsActive;
        if (restart)
        {
            await StopAsync(cancellationToken).ConfigureAwait(false);
        }
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            options = connectionOptions;
            pluginToken = null;
        }
        finally
        {
            lifecycleGate.Release();
        }

        CubismEditorOutputLog.ConfigurationChanged(logger, options.AlwaysOutput);
        if (restart)
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            PublishStatus(CubismEditorOutputState.Stopped, null, null);
        }
    }

    public void PublishFrame(OutputParameterFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        lock (frameGate) latestFrame = frame;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        lifecycleGate.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        bool hasConnected = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                PublishStatus(hasConnected ? CubismEditorOutputState.Reconnecting : CubismEditorOutputState.Connecting, null, null);
                await using ICubismEditorTransport transport = transportFactory();
                await transport.ConnectAsync(options.Endpoint, cancellationToken).ConfigureAwait(false);
                CubismEditorOutputLog.TransportConnected(logger, options.Endpoint.Host, options.Endpoint.Port);

                CubismEditorResponse registration = await SendRequestAsync(
                    transport, "RegisterPlugin", new RegisterPluginData(options.ApplicationName, pluginToken, Environment.ProcessPath), cancellationToken).ConfigureAwait(false);
                EnsureSuccess(registration);
                pluginToken = ReadRequiredDataString(registration, "Token");
                CubismEditorOutputLog.PluginRegistered(logger);

                while (!cancellationToken.IsCancellationRequested)
                {
                    CubismEditorResponse approval = await SendRequestAsync(
                        transport, "GetIsApproval", EmptyData.Instance, cancellationToken).ConfigureAwait(false);
                    EnsureSuccess(approval);
                    if (ReadRequiredDataBoolean(approval, "Result")) break;
                    PublishStatus(CubismEditorOutputState.WaitingForApproval, null, null);
                    await Task.Delay(options.RetryInterval, cancellationToken).ConfigureAwait(false);
                }

                if (cancellationToken.IsCancellationRequested) break;
                string? modelUid = await TryResolveCurrentModelAsync(transport, cancellationToken).ConfigureAwait(false);
                while (modelUid is null && !cancellationToken.IsCancellationRequested)
                {
                    PublishStatus(CubismEditorOutputState.ModelUnavailable, null, null);
                    await Task.Delay(options.RetryInterval, cancellationToken).ConfigureAwait(false);
                    modelUid = await TryResolveCurrentModelAsync(transport, cancellationToken).ConfigureAwait(false);
                }

                if (modelUid is null) break;
                hasConnected = true;
                PublishStatus(CubismEditorOutputState.Connected, modelUid, null);
                CubismEditorOutputLog.ModelResolved(logger);
                ImmutableArray<CubismEditorModelParameter> parameters =
                    await LoadModelParametersAsync(transport, modelUid, cancellationToken).ConfigureAwait(false);
                lock (modelParametersGate) currentModelParameters = parameters;
                CubismEditorOutputLog.ModelParametersLoaded(logger, parameters.Length);
                await RefreshParametersAsync(transport, modelUid, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (CubismEditorProtocolException exception)
            {
                PublishStatus(CubismEditorOutputState.ProtocolError, null, exception.Message);
                CubismEditorOutputLog.ProtocolFault(logger, exception);
            }
            catch (Exception exception) when (exception is System.Net.WebSockets.WebSocketException or HttpRequestException or IOException or System.Net.Sockets.SocketException)
            {
                PublishStatus(CubismEditorOutputState.EditorUnavailable, null, exception.GetType().Name);
                CubismEditorOutputLog.EditorUnavailable(logger, exception);
            }
            catch (Exception exception)
            {
                PublishStatus(CubismEditorOutputState.ProtocolError, null, exception.GetType().Name);
                CubismEditorOutputLog.UnexpectedFault(logger, exception);
            }

            try { await Task.Delay(options.RetryInterval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private async Task<string?> TryResolveCurrentModelAsync(ICubismEditorTransport transport, CancellationToken cancellationToken)
    {
        CubismEditorResponse response = await SendRequestAsync(transport, "GetCurrentModelUID", EmptyData.Instance, cancellationToken).ConfigureAwait(false);
        if (response.Type == "Error" && string.Equals(response.ErrorType, "InvalidModel", StringComparison.Ordinal)) return null;
        EnsureSuccess(response);
        return ReadRequiredDataString(response, "ModelUID");
    }

    private async Task RefreshParametersAsync(ICubismEditorTransport transport, string modelUid, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.RefreshInterval, timeProvider);
        DateTimeOffset nextEditModePollingUtc = DateTimeOffset.MinValue;
        bool isPhysicsEditMode = false;
        bool hasPublishedParameters = false;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                CubismEditorConnectionOptions activeOptions = Volatile.Read(ref options);
                if (!activeOptions.AlwaysOutput
                    && timeProvider.GetUtcNow() >= nextEditModePollingUtc)
                {
                    bool nextIsPhysicsEditMode = await IsPhysicsEditModeAsync(transport, cancellationToken)
                        .ConfigureAwait(false);
                    if (hasPublishedParameters && !nextIsPhysicsEditMode)
                    {
                        await ClearParameterValuesAsync(transport, modelUid, cancellationToken)
                            .ConfigureAwait(false);
                        hasPublishedParameters = false;
                    }

                    isPhysicsEditMode = nextIsPhysicsEditMode;
                    nextEditModePollingUtc = timeProvider.GetUtcNow() + EditModePollingInterval;
                }

                if (!activeOptions.AlwaysOutput && !isPhysicsEditMode) continue;
                OutputParameterFrame? frame;
                lock (frameGate) frame = latestFrame;
                if (frame is null) continue;
                CubismEditorResponse response = await SendRequestAsync(
                    transport,
                    "SetParameterValues",
                    new SetParameterValuesData(modelUid, frame.Values.Select(static value => new CubismParameterValue(value.Id, value.Value)).ToArray()),
                    cancellationToken).ConfigureAwait(false);
                EnsureSuccess(response);
                hasPublishedParameters = true;
            }
        }
        finally
        {
            if (hasPublishedParameters)
            {
                try
                {
                    await ClearParameterValuesAsync(transport, modelUid, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is CubismEditorProtocolException or IOException or System.Net.WebSockets.WebSocketException)
                {
                    CubismEditorOutputLog.ParametersClearFailed(logger, exception);
                }
            }
        }
    }

    private async Task ClearParameterValuesAsync(
        ICubismEditorTransport transport,
        string modelUid,
        CancellationToken cancellationToken)
    {
        CubismEditorResponse response = await SendRequestAsync(
            transport,
            "ClearParameterValues",
            new ClearParameterValuesData(modelUid),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        CubismEditorOutputLog.ParametersCleared(logger, modelUid);
    }

    private async Task<bool> IsPhysicsEditModeAsync(
        ICubismEditorTransport transport,
        CancellationToken cancellationToken)
    {
        CubismEditorResponse response = await SendRequestAsync(
            transport,
            "GetCurrentEditMode",
            EmptyData.Instance,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        string mode = ReadRequiredDataString(response, "EditMode");
        string? previous = Interlocked.Exchange(ref currentEditMode, mode);
        if (!StringComparer.Ordinal.Equals(previous, mode))
        {
            CubismEditorOutputLog.EditModeChanged(logger, mode);
        }

        return StringComparer.Ordinal.Equals(mode, "Physics");
    }

    private async Task<ImmutableArray<CubismEditorModelParameter>> LoadModelParametersAsync(
        ICubismEditorTransport transport,
        string modelUid,
        CancellationToken cancellationToken)
    {
        CubismEditorResponse response = await SendRequestAsync(
            transport,
            "GetParameters",
            new GetParametersData(modelUid),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        if (response.Data.ValueKind != JsonValueKind.Object
            || !response.Data.TryGetProperty("Parameters", out JsonElement parameters)
            || parameters.ValueKind != JsonValueKind.Array)
        {
            throw new CubismEditorProtocolException("Cubism Editor parameter list is missing Parameters.");
        }

        return parameters.EnumerateArray()
            .Select(ReadModelParameter)
            .ToImmutableArray();
    }

    private static CubismEditorModelParameter ReadModelParameter(JsonElement element)
    {
        string id = ReadRequiredProperty(element, "Id");
        double minimum = ReadRequiredNumber(element, "Min", "Minimum");
        double defaultValue = ReadRequiredNumber(element, "Default");
        double maximum = ReadRequiredNumber(element, "Max", "Maximum");
        string? name = element.TryGetProperty("Name", out JsonElement nameElement)
            && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;
        return new CubismEditorModelParameter(id, minimum, defaultValue, maximum, name);
    }

    private static string ReadRequiredProperty(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? throw new CubismEditorProtocolException($"Cubism Editor parameter is missing {propertyName}.")
            : throw new CubismEditorProtocolException($"Cubism Editor parameter is missing {propertyName}.");

    private static double ReadRequiredNumber(JsonElement element, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(propertyName, out JsonElement value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out double number))
            {
                return number;
            }
        }

        throw new CubismEditorProtocolException(
            $"Cubism Editor parameter is missing {string.Join(" or ", propertyNames)}.");
    }

    private async Task<CubismEditorResponse> SendRequestAsync(ICubismEditorTransport transport, string method, object data, CancellationToken cancellationToken)
    {
        string requestId = Interlocked.Increment(ref requestSequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string request = CreateRequestJson(method, data, requestId, timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        await transport.SendTextAsync(request, timeout.Token).ConfigureAwait(false);
        CubismEditorResponse response = CubismEditorProtocol.ParseResponse(await transport.ReceiveTextAsync(timeout.Token).ConfigureAwait(false));
        if (!string.Equals(response.Method, method, StringComparison.Ordinal)
            || (response.RequestId is not null && !string.Equals(response.RequestId, requestId, StringComparison.Ordinal)))
        {
            throw new CubismEditorProtocolException("The Cubism Editor response does not match the pending request.");
        }
        return response;
    }

    private static string CreateRequestJson(string method, object data, string requestId, long timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentOutOfRangeException.ThrowIfNegative(timestamp);
        return JsonSerializer.Serialize(new CubismRequestEnvelope(timestamp, requestId, method, data), JsonOptions);
    }

    private static void EnsureSuccess(CubismEditorResponse response)
    {
        if (response.Type == "Response") return;
        if (response.Type == "Error") throw new CubismEditorProtocolException($"Cubism Editor rejected {response.Method}: {response.ErrorType ?? "UnknownError"}.");
        throw new CubismEditorProtocolException($"Unexpected Cubism Editor response type: {response.Type}.");
    }

    private static string ReadRequiredDataString(CubismEditorResponse response, string propertyName) =>
        response.Data.ValueKind == JsonValueKind.Object && response.Data.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new CubismEditorProtocolException($"Cubism Editor response {response.Method} is missing {propertyName}.");

    private static bool ReadRequiredDataBoolean(CubismEditorResponse response, string propertyName) =>
        response.Data.ValueKind == JsonValueKind.Object && response.Data.TryGetProperty(propertyName, out JsonElement value)
        && (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? value.GetBoolean()
            : throw new CubismEditorProtocolException($"Cubism Editor response {response.Method} is missing {propertyName}.");

    private void PublishStatus(CubismEditorOutputState state, string? modelUid, string? detail)
    {
        var next = new CubismEditorOutputStatus(state, options.Endpoint, modelUid, detail);
        CubismEditorOutputStatus previous = Interlocked.Exchange(ref status, next);
        if (previous != next)
        {
            StatusChanged?.Invoke(this, next);
            if ((previous.State != CubismEditorOutputState.Stopped)
                != (next.State != CubismEditorOutputState.Stopped))
            {
                ActivityChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private sealed record CubismRequestEnvelope(long Timestamp, string RequestId, string Method, object Data)
    {
        public string Type { get; } = "Request";
    }

    private sealed record RegisterPluginData(string Name, string? Token, string? Path);
    private sealed record GetParametersData(string ModelUID);
    private sealed record SetParameterValuesData(string ModelUID, IReadOnlyList<CubismParameterValue> Parameters);
    private sealed record ClearParameterValuesData(string ModelUID);
    private sealed record CubismParameterValue(string Id, double Value);
    private sealed class EmptyData { public static EmptyData Instance { get; } = new(); }
}

/// <summary>Describes a parameter returned by Cubism Editor's current model.</summary>
public sealed record CubismEditorModelParameter(
    string Id,
    double Minimum,
    double Default,
    double Maximum,
    string? Name = null);

internal static partial class CubismEditorOutputLog
{
    [LoggerMessage(6700, LogLevel.Information, "Cubism Editor output started for {Host}:{Port}")]
    internal static partial void Started(ILogger logger, string host, int port);
    [LoggerMessage(6701, LogLevel.Information, "Cubism Editor output stopped")]
    internal static partial void Stopped(ILogger logger);
    [LoggerMessage(6702, LogLevel.Information, "Cubism Editor WebSocket connected to {Host}:{Port}")]
    internal static partial void TransportConnected(ILogger logger, string host, int port);
    [LoggerMessage(6703, LogLevel.Information, "Cubism Editor plugin registration completed")]
    internal static partial void PluginRegistered(ILogger logger);
    [LoggerMessage(6704, LogLevel.Information, "Cubism Editor current model resolved")]
    internal static partial void ModelResolved(ILogger logger);
    [LoggerMessage(6705, LogLevel.Warning, "Cubism Editor protocol failure; output will retry")]
    internal static partial void ProtocolFault(ILogger logger, Exception exception);
    [LoggerMessage(6706, LogLevel.Debug, "Cubism Editor is unavailable; output will retry")]
    internal static partial void EditorUnavailable(ILogger logger, Exception exception);
    [LoggerMessage(6707, LogLevel.Warning, "Cubism Editor output unexpected failure; output will retry")]
    internal static partial void UnexpectedFault(ILogger logger, Exception exception);
    [LoggerMessage(6708, LogLevel.Information, "Cubism Editor output connection configuration updated; always output {AlwaysOutput}")]
    internal static partial void ConfigurationChanged(ILogger logger, bool alwaysOutput);
    [LoggerMessage(6709, LogLevel.Information, "Cubism Editor model parameter list loaded with {ParameterCount} parameters")]
    internal static partial void ModelParametersLoaded(ILogger logger, int parameterCount);
    [LoggerMessage(6710, LogLevel.Information, "Cubism Editor edit mode changed to {EditMode}")]
    internal static partial void EditModeChanged(ILogger logger, string editMode);
    [LoggerMessage(6711, LogLevel.Information, "Cubism Editor output cleared externally supplied parameters for model {ModelUid}")]
    internal static partial void ParametersCleared(ILogger logger, string modelUid);
    [LoggerMessage(6712, LogLevel.Warning, "Cubism Editor output could not clear externally supplied parameters")]
    internal static partial void ParametersClearFailed(ILogger logger, Exception exception);
}
