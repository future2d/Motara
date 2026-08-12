using Microsoft.Extensions.Logging;
using Motara.App.Tracking;
using Motara.Core.Sessions;
using Motara.Tracking.Abstractions;
using Motara.Tracking.iFacialMocap;

namespace Motara.App.Sessions;

/// <summary>Coordinates the configured face and hand tracking transports.</summary>
public sealed class TrackingSessionController : ISessionController, IAsyncDisposable
{
    private readonly FaceTrackingSessionController controller;
    private readonly FaceTrackingSessionController handController;
    private readonly IFacialMocapTrackingSourceFactory iFacialMocapFactory;
    private readonly OpenSeeFaceLocalTrackingSourceFactory openSeeFaceFactory;
    private readonly MediaPipeLocalTrackingSourceFactory mediaPipeFactory;

    public TrackingSessionController(TimeProvider timeProvider)
        : this(timeProvider, sessionLogger: null, openSeeFaceExecutablePath: null)
    {
    }

    internal TrackingSessionController(
        TimeProvider timeProvider,
        ILogger<ProcessingSession>? sessionLogger,
        ILogger<IFacialMocapTrackingSource>? iFacialMocapLogger = null,
        string? openSeeFaceExecutablePath = null,
        OpenSeeFaceConfiguration? openSeeFaceConfiguration = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        iFacialMocapFactory = new IFacialMocapTrackingSourceFactory(
            options: null,
            timeProvider,
            iFacialMocapLogger);
        openSeeFaceFactory = new OpenSeeFaceLocalTrackingSourceFactory(
            timeProvider,
            sessionLogger,
            openSeeFaceExecutablePath,
            openSeeFaceConfiguration);
        string escapiPath = EscapiNativePaths.ResolveLibraryPath(
            AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable("MOTARA_ESCAPI_NATIVE"));
        var escapiFactory = new EscapiCameraFrameProviderFactory(
            escapiPath,
            timeProvider,
            sessionLogger);
        mediaPipeFactory = new MediaPipeLocalTrackingSourceFactory(
            timeProvider,
            logger: sessionLogger,
            frameProviderFactory: escapiFactory);
        var registry = new TrackingSourceRegistry(
        [
            iFacialMocapFactory,
            openSeeFaceFactory,
            mediaPipeFactory,
        ]);
        controller = new FaceTrackingSessionController(
            timeProvider,
            registry,
            sessionLogger: sessionLogger);
        handController = new FaceTrackingSessionController(
            timeProvider,
            registry,
            sessionLogger: sessionLogger,
            channel: TrackingChannel.Hand);
    }

    public SessionSnapshot Current => controller.Current;

    internal FaceTrackingSessionController TrackingController => controller;

    internal FaceTrackingSessionController HandTrackingController => handController;

    internal IFacialMocapTrackingSourceFactory IFacialMocapFactory => iFacialMocapFactory;

    internal OpenSeeFaceLocalTrackingSourceFactory OpenSeeFaceFactory => openSeeFaceFactory;

    public IAsyncEnumerable<SessionSnapshot> WatchSnapshotsAsync(CancellationToken cancellationToken) =>
        controller.WatchSnapshotsAsync(cancellationToken);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (controller.SourceStatus.IntendedSourceId is not null)
        {
            await controller.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        if (handController.SourceStatus.IntendedSourceId is not null)
        {
            await handController.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await controller.StopAsync(cancellationToken).ConfigureAwait(false);
        await handController.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await controller.DisposeAsync().ConfigureAwait(false);
        await handController.DisposeAsync().ConfigureAwait(false);
    }
}
