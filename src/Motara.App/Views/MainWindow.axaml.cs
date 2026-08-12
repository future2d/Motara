using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Motara.App.Controls;
using Motara.App.Backgrounds;
using Motara.App.Collaboration;
using Motara.App.Diagnostics;
using Motara.App.Input;
using Motara.App.Localization;
using Motara.App.Models;
using Motara.App.Parameters;
using Motara.App.Scenes;
using Motara.App.Screenshots;
using Motara.App.Rendering;
using Motara.App.Sessions;
using Motara.App.Shell;
using Motara.App.Shortcuts;
using Motara.App.Themes;
using Motara.App.Tracking;
using Motara.App.ViewModels;
using Motara.Persistence;
using Motara.Media;
using Motara.ModelLibrary;
using Motara.Tracking.iFacialMocap;
using Motara.ModelRuntime.Abstractions;
using Motara.ModelRuntime.PurismCore;
using Motara.Output.CubismEditor;
using Motara.Scene;
using Motara.Tracking.Abstractions;
using Motara.Core.Formulas;
using Motara.Collaboration.Friends;
using Motara.Collaboration.Handshake;
using Motara.Collaboration.Identity;
using Motara.Collaboration.Invites;
using Motara.Collaboration.Migration;
using Motara.Collaboration.Profile;
using Motara.Collaboration.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Motara.App.Views;

public readonly record struct CanvasGeometrySnapshot(Rect Bounds, Point Center);

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly record struct ModelTransitionOperation(
        long ModelGeneration,
        long CanvasGeneration,
        string Reason);

    private readonly NavigationRail navigationRail;
    private readonly CascadingMenuWorkspace menuWorkspace;
    private readonly ModelLibraryMenu modelLibraryMenu;
    private readonly CollaborationMenu collaborationMenu;
    private readonly TopLevelWorkspaceHost topLevelWorkspaceHost;
    private readonly Canvas primaryCanvas;
    private readonly BackgroundVisualControl backgroundLayer;
    private readonly SignalAttachmentVisualControl signalAttachmentBeforeLayer;
    private readonly SignalAttachmentVisualControl signalAttachmentAfterLayer;
    private readonly ModelArtMeshHighlightControl modelArtMeshHighlightLayer;
    private readonly Canvas scaledUiCanvas;
    private readonly ScaleTransform contentScaleTransform;
    private readonly ModelCanvas modelCanvas;
    private MainModelCanvasInteraction? mainModelCanvasInteraction;
    private SceneAttachmentCanvasInteraction? sceneAttachmentCanvasInteraction;
    private readonly Border modelTransitionBlank;
    private readonly RemoteModelCanvas remoteModelCanvas;
    private readonly ScreenshotPreviewOverlay screenshotPreviewOverlay;
    private MainWindowViewModel? ownedViewModel;
    private TrackingSessionController? ownedSessionController;
    private ModelSelectionController? ownedModelController;
    private ActiveModelDriveController? ownedModelDriveController;
    private ActiveModelDragPhysicsSource? ownedDragPhysicsSource;
    private ActiveModelAnimationSource? ownedAnimationSource;
    private ShortcutDispatcher? shortcutDispatcher;
    private WindowsGlobalHotKeyHost? globalHotKeyHost;
    private GlobalHotKeyProfileCoordinator? globalHotKeyProfileCoordinator;
    private Win32Properties.CustomWndProcHookCallback? hotKeyWndProcHook;
    private CubismEditorOutputTarget? ownedCubismEditorOutput;
    private CubismEditorOutputController? ownedCubismEditorOutputController;
    private CompositionFramePublisher? ownedCompositionFramePublisher;
    private CompositionVideoOutputController? ownedCompositionVideoOutput;
    private ParameterPriorityStore? ownedParameterPriorityStore;
    private FriendStore? ownedFriendStore;
    private ConsumedInviteStore? ownedConsumedInviteStore;
    private DeviceIdentityStore? ownedIdentityStore;
    private LocalCollaborationProfileStore? ownedCollaborationProfileStore;
    private MainModelAssignmentCoordinator? ownedMainModelCoordinator;
    private ActiveSceneCollaborationBridge? ownedCollaborationBridge;
    private RemoteMemberModelSourceRegistry? ownedRemoteModelSources;
    private RemoteModelPublicationPresenter? ownedRemoteModelPresenter;
    private bool attachmentAnchorSelectorsVisible;
    private Guid? attachmentAnchorPreviewSourceId;
    private Point? attachmentAnchorPreviewPoint;
    private ISceneRepository? ownedSceneRepository;
    private readonly CancellationTokenSource modelSelectionCancellation = new();
    private readonly CancellationTokenSource sourceMappingCancellation = new();
    private readonly CancellationTokenSource screenshotCancellation = new();
    private readonly CancellationTokenSource backgroundCancellation = new();
    private ScreenshotCoordinator? screenshotCoordinator;
    private BackgroundPresenter? backgroundPresenter;
    private SignalAttachmentScenePresenter? signalAttachmentPresenter;
    private ModelRenderFrame? latestMainModelFrame;
    private PixelSize latestMainModelPixelSize;
    private ModelRasterTransform latestMainModelRasterTransform = ModelRasterTransform.Identity;
    private double latestMainModelReferenceHeight = 1080;
    private SceneDocument? lastPresentedAttachmentScene;
    private CancellationTokenSource? signalAttachmentCancellation = new();
    private Task signalAttachmentApplyTask = Task.CompletedTask;
    private Task backgroundApplyTask = Task.CompletedTask;
    private Task backgroundDisposalTask = Task.CompletedTask;
    private readonly object backgroundDisposalGate = new();
    private int backgroundDisposalStarted;
    private Task ownedResourcesDisposal = Task.CompletedTask;
    private Task renderingBackendFallbackTask = Task.CompletedTask;
    private bool ownedResourcesDisposalStarted;
    private bool ownedResourcesDisposed;
    private Action<ModelRenderingBackendPreference>? renderingBackendPreferenceChanged;
    private bool modelRenderingFallbackNoticeShown;
    private long modelSelectionGeneration;
    private long canvasTransitionGeneration;
    private long renderingBackendTransitionGeneration;
    private long renderingBackendFallbackGeneration;
    private long canvasTransitionStartedAt;
    private ILogger<MainWindow> transitionLogger = NullLogger<MainWindow>.Instance;
    private TrackingStatusStructureKey? trackingStatusStructure;
    private int lifetimeDisposed;

    internal void DispatchStartupInvitation(
        StartupInvitationResult result,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        StartupInvitationEvents.Classified(logger, result.Status);
        MainWindowViewModel viewModel = ViewModel
            ?? throw new InvalidOperationException("The main window is not initialized.");
        if (result.Status == StartupInvitationStatus.Invalid)
        {
            viewModel.OpenStartupInvitationErrorWorkspace();
            return;
        }

        if (result is { Status: StartupInvitationStatus.Valid, Candidate: { } candidate })
        {
            viewModel.OpenInvitationCandidateWorkspace(candidate);
            StartupInvitationEvents.Dispatched(logger, candidate.Kind);
        }
    }

    public MainWindow()
        : this(new ThemeManager(ThemePalette.WarmNeutralLight))
    {
    }

    private MainWindow(IThemeManager themeManager)
    {
        ArgumentNullException.ThrowIfNull(themeManager);
        AvaloniaXamlLoader.Load(this);
        themeManager.Apply(Resources);
        navigationRail = this.FindControl<NavigationRail>("NavigationRail")!;
        menuWorkspace = this.FindControl<CascadingMenuWorkspace>("MenuWorkspace")!;
        modelLibraryMenu = this.FindControl<ModelLibraryMenu>("ModelLibraryMenu")!;
        collaborationMenu = this.FindControl<CollaborationMenu>("CollaborationMenu")!;
        topLevelWorkspaceHost = this.FindControl<TopLevelWorkspaceHost>("TopLevelWorkspaceHost")!;
        primaryCanvas = this.FindControl<Canvas>("PrimaryCanvas")!;
        backgroundLayer = this.FindControl<BackgroundVisualControl>("BackgroundLayer")!;
        signalAttachmentBeforeLayer = this.FindControl<SignalAttachmentVisualControl>("SignalAttachmentBeforeLayer")!;
        signalAttachmentAfterLayer = this.FindControl<SignalAttachmentVisualControl>("SignalAttachmentAfterLayer")!;
        modelArtMeshHighlightLayer = this.FindControl<ModelArtMeshHighlightControl>(
            "ModelArtMeshHighlightLayer")!;
        scaledUiCanvas = this.FindControl<Canvas>("ScaledUiCanvas")!;
        contentScaleTransform = new ScaleTransform(1, 1);
        scaledUiCanvas.RenderTransform = contentScaleTransform;
        modelCanvas = this.FindControl<ModelCanvas>("ModelCanvas")!;
        modelTransitionBlank = this.FindControl<Border>("ModelTransitionBlank")!;
        remoteModelCanvas = this.FindControl<RemoteModelCanvas>("RemoteModelCanvas")!;
        screenshotPreviewOverlay = this.FindControl<ScreenshotPreviewOverlay>("ScreenshotPreviewOverlay")!;
        SizeChanged += (_, _) =>
        {
            ApplyConfiguredContentScale();
            UpdateScaledUiBounds();
            UpdateRailHeight();
            PositionMenuWorkspace();
            UpdateModelCanvasSize();
            PositionTopLevelWorkspace();
        };
        primaryCanvas.SizeChanged += (_, _) =>
        {
            ApplyConfiguredContentScale();
            UpdateScaledUiBounds();
            UpdateRailHeight();
            PositionMenuWorkspace();
            UpdateModelCanvasSize();
            PositionTopLevelWorkspace();
            UpdateAttachmentAnchorSelectors();
        };
        primaryCanvas.DoubleTapped += OnCanvasDoubleTapped;
        KeyDown += OnWindowKeyDown;
        KeyUp += OnWindowKeyUp;
        Deactivated += (_, _) => SetAttachmentAnchorSelectorsVisible(false);
        ScalingChanged += (_, _) => ApplyPersistedWindowSize();
        menuWorkspace.HorizontalOffsetChanged += (_, _) => PositionMenuWorkspace();
        navigationRail.LayoutChanged += (_, _) => PositionMenuWorkspace();
        Closed += (_, _) => Dispose();
    }

    public MainWindow(MainWindowViewModel viewModel)
        : this()
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        BackgroundImageDecoder imageDecoder = new(viewModel.BackgroundAssetStore);
        BackgroundVideoPlaybackFactory videoPlaybackFactory = new(
                viewModel.BackgroundAssetStore,
                CreateFfmpegVideoDecoder(),
                viewModel.BackgroundPresenterLogger,
                BackgroundPresentationDispatcher.UiThread);
        backgroundPresenter = new BackgroundPresenter(
            imageDecoder,
            videoPlaybackFactory,
            viewModel.BackgroundPresenterLogger,
            BackgroundPresentationDispatcher.UiThread,
            new BackgroundSignalPlaybackFactory(
                viewModel.VideoSignalRegistry,
                BackgroundPresentationDispatcher.UiThread));
        signalAttachmentPresenter = new SignalAttachmentScenePresenter(
            new BackgroundSignalPlaybackFactory(
                viewModel.VideoSignalRegistry,
                BackgroundPresentationDispatcher.UiThread),
            imageDecoder,
            videoPlaybackFactory);
        signalAttachmentPresenter.Changed += OnSignalAttachmentsChanged;
        sceneAttachmentCanvasInteraction = new SceneAttachmentCanvasInteraction(
            primaryCanvas,
            signalAttachmentPresenter,
            viewModel.InputActionRegistry);
        sceneAttachmentCanvasInteraction.CommitRequested += OnAttachmentTransformCommitRequested;
        sceneAttachmentCanvasInteraction.AnchorSelectionRequested += OnAttachmentAnchorSelectionRequested;
        sceneAttachmentCanvasInteraction.AnchorSelectorPreviewChanged += OnAttachmentAnchorSelectorPreviewChanged;
        backgroundPresenter.SnapshotChanged += OnBackgroundSnapshotChanged;
        ownedCompositionFramePublisher = new CompositionFramePublisher();
        ownedCompositionVideoOutput = new CompositionVideoOutputController(
            viewModel.VideoSignalRegistry,
            ownedCompositionFramePublisher,
            () => CaptureModelCanvasPixelSize()
                ?? new PixelSize(
                    Math.Max(1, (int)Math.Round(Bounds.Width)),
                    Math.Max(1, (int)Math.Round(Bounds.Height))));
        viewModel.AttachCompositionVideoOutput(ownedCompositionVideoOutput);
        modelCanvas.CompositionFrameReadbackRequested = () => ownedCompositionFramePublisher.HasTargets;
        modelCanvas.CompositionFrameReady += OnCompositionFrameReady;
        backgroundLayer.Snapshot = backgroundPresenter.Current;
        ApplySignalAttachments(viewModel.PresentedSceneId is SceneId initialSceneId
            ? viewModel.CurrentSceneWorkspace.Scenes.Single(scene => scene.Id == initialSceneId)
            : null);
        viewModel.PropertyChanged += OnViewModelBackgroundPropertyChanged;
        viewModel.ShortcutProfileChanged += OnShortcutProfileChanged;
        Opened += OnWindowOpenedForGlobalHotKeys;
        mainModelCanvasInteraction = new MainModelCanvasInteraction(
            primaryCanvas,
            modelCanvas,
            viewModel.InputActionRegistry);
        mainModelCanvasInteraction.CommitRequested += OnMainModelTransformCommitRequested;
        mainModelCanvasInteraction.PreviewChanged += OnMainModelTransformPreviewChanged;
        mainModelCanvasInteraction.DragPhysicsInputRequested += OnMainModelDragPhysicsInputRequested;
        ApplyPersistedWindowSize();
        CanResize = !viewModel.IsWindowSizeLocked;
        ApplyConfiguredContentScale();
        modelCanvas.SetFrameRateMode(viewModel.FrameRateMode);
        KeyBindings.Add(new KeyBinding
        {
            Gesture = KeyGesture.Parse("Ctrl+Shift+N"),
            Command = viewModel.RestoreNavigationCommand,
        });
        navigationRail.Attach(viewModel);
        menuWorkspace.SetInputActions(viewModel.InputActionRegistry);
        menuWorkspace.Attach(viewModel);
        modelLibraryMenu.Attach(viewModel);
        if (viewModel.CollaborationWorkspace is { } collaborationWorkspace)
        {
            collaborationMenu.Attach(viewModel, collaborationWorkspace);
            collaborationMenu.GenerateInviteRequested += async (_, _) =>
            {
                try
                {
                    await viewModel.OpenFriendInviteGenerationWorkspaceAsync(CancellationToken.None);
                }
                catch (Exception)
                {
                    viewModel.TopLevelWorkspace.Close();
                }
            };
            collaborationMenu.AcceptFriendInviteRequested += (_, _) =>
                viewModel.OpenFriendInviteAcceptanceWorkspace();
            collaborationMenu.GenerateSessionInviteRequested += (_, _) =>
                viewModel.OpenSessionInviteGenerationWorkspace();
            collaborationMenu.AcceptSessionInviteRequested += (_, _) =>
                viewModel.OpenSessionInviteEntryWorkspace();
            collaborationMenu.LeaveSessionRequested += (_, _) =>
                collaborationWorkspace.LeaveSession();
            collaborationMenu.LocalProfileRequested += (_, _) =>
                viewModel.OpenLocalProfileSettingsWorkspace();
            collaborationMenu.ContactRequested += async (_, deviceId) =>
            {
                try
                {
                    await viewModel.OpenFriendDetailsWorkspaceAsync(
                        deviceId,
                        CancellationToken.None);
                }
                catch (Exception)
                {
                    viewModel.TopLevelWorkspace.Close();
                }
            };
        }
        topLevelWorkspaceHost.Attach(viewModel);
        modelCanvas.MainModelFrameRateChanged += viewModel.UpdateCurrentMainModelFrameRate;
        modelCanvas.MainModelFrameStateChanged += OnMainModelFrameStateChanged;
        modelCanvas.WindowPresentationFrameRateChanged +=
            viewModel.UpdateWindowPresentationFrameRate;
        viewModel.Navigation.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(NavigationState.IsRailVisible))
            {
                navigationRail.IsVisible = viewModel.Navigation.IsRailVisible;
            }

            if (args.PropertyName is nameof(NavigationState.SelectedDestination)
                or nameof(NavigationState.SelectedMenuPath)
                or nameof(NavigationState.IsRailVisible))
            {
                navigationRail.Refresh();
                menuWorkspace.Refresh();
                UpdateMenuPresentation();
                PositionMenuWorkspace();
                if (viewModel.Navigation.SelectedDestination == NavigationDestination.Collaboration)
                {
                    _ = InitializeCollaborationMenuAsync();
                }
            }
        };
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainWindowViewModel.WindowWidthPixels)
                or nameof(MainWindowViewModel.WindowHeightPixels))
            {
                ApplyPersistedWindowSize();
            }

            if (args.PropertyName is nameof(MainWindowViewModel.ContentScale)
                or nameof(MainWindowViewModel.ContentScaleMode))
            {
                ApplyConfiguredContentScale();
            }

            if (args.PropertyName == nameof(MainWindowViewModel.FrameRateMode))
            {
                modelCanvas.SetFrameRateMode(viewModel.FrameRateMode);
            }

            if (args.PropertyName == nameof(MainWindowViewModel.IsWindowSizeLocked))
            {
                CanResize = !viewModel.IsWindowSizeLocked;
            }

            if (args.PropertyName == nameof(MainWindowViewModel.Destinations))
            {
                navigationRail.Refresh();
                menuWorkspace.Refresh();
                PositionMenuWorkspace();
            }

            if (args.PropertyName == nameof(MainWindowViewModel.DiagnosticLogLevel))
            {
                menuWorkspace.Refresh();
                PositionMenuWorkspace();
            }

            if ((args.PropertyName is nameof(MainWindowViewModel.CubismEditorOutputStatusText)
                    or nameof(MainWindowViewModel.CubismEditorOutputEndpointText)
                    or nameof(MainWindowViewModel.CubismEditorOutputModelUidText)
                    or nameof(MainWindowViewModel.CubismEditorOutputInformationState))
                && viewModel.Navigation.SelectedDestination == NavigationDestination.Output)
            {
                menuWorkspace.Refresh();
                PositionMenuWorkspace();
            }

            if (args.PropertyName == nameof(MainWindowViewModel.CurrentWindowPresentationFrameRate)
                && (viewModel.Navigation.SelectedDestination is NavigationDestination.Session
                    or NavigationDestination.Output))
            {
                menuWorkspace.RefreshStatusValues();
            }

            if ((args.PropertyName is nameof(MainWindowViewModel.FaceTrackingSourceStatus)
                    or nameof(MainWindowViewModel.TrackingChannelSelections))
                && (viewModel.Navigation.SelectedDestination is NavigationDestination.Tracking
                    or NavigationDestination.Developer))
            {
                TrackingStatusStructureKey next = TrackingStatusStructureKey.From(
                    viewModel.FaceTrackingSourceStatus);
                if (args.PropertyName == nameof(MainWindowViewModel.TrackingChannelSelections)
                    || trackingStatusStructure != next)
                {
                    trackingStatusStructure = next;
                    menuWorkspace.Refresh();
                    PositionMenuWorkspace();
                }
                else
                {
                    menuWorkspace.RefreshStatusValues();
                }
            }

            if ((args.PropertyName is nameof(MainWindowViewModel.FaceTrackingSourceStatus)
                    or nameof(MainWindowViewModel.HandTrackingSourceStatus))
                && viewModel.Navigation.SelectedDestination == NavigationDestination.Session)
            {
                menuWorkspace.RefreshStatusValues();
            }

            if ((args.PropertyName == nameof(MainWindowViewModel.CurrentMainModelId)
                || args.PropertyName == nameof(MainWindowViewModel.CurrentSceneWorkspace)
                || args.PropertyName == nameof(MainWindowViewModel.PresentedSceneId)
                || args.PropertyName == nameof(MainWindowViewModel.SelectedSceneSourceId))
                && viewModel.Navigation.SelectedDestination == NavigationDestination.Scene)
            {
                menuWorkspace.Refresh();
                PositionMenuWorkspace();
            }

            if ((args.PropertyName is nameof(MainWindowViewModel.CurrentMainModelId)
                    or nameof(MainWindowViewModel.CurrentMainModelFrameRate)
                    or nameof(MainWindowViewModel.CurrentModelMappingBindingCount)
                    or nameof(MainWindowViewModel.CurrentSceneWorkspace)
                    or nameof(MainWindowViewModel.PresentedSceneId))
                && viewModel.Navigation.SelectedDestination is NavigationDestination.Session
                    or NavigationDestination.Mapping)
            {
                menuWorkspace.RefreshStatusValues();
            }

            if (args.PropertyName is nameof(MainWindowViewModel.CurrentSceneWorkspace)
                or nameof(MainWindowViewModel.PresentedSceneId))
            {
                ApplyModelSourceVisibility();
                ApplySignalAttachments(viewModel.PresentedSceneId is SceneId sceneId
                    ? viewModel.CurrentSceneWorkspace.Scenes.Single(scene => scene.Id == sceneId)
                    : null);
            }

        };
        viewModel.ModelCatalog.PropertyChanged += (_, _) =>
        {
            if (viewModel.Navigation.SelectedDestination == NavigationDestination.Model)
            {
                modelLibraryMenu.Refresh();
                PositionMenuWorkspace();
            }
        };
        if (viewModel.FaceTrackingController is { } faceTrackingController)
        {
            faceTrackingController.SourceStatusChanged += OnFaceTrackingSourceStatusChanged;
        }
        if (viewModel.HandTrackingController is { } handTrackingController)
        {
            handTrackingController.SourceStatusChanged += OnFaceTrackingSourceStatusChanged;
        }
        UpdateMenuPresentation();
        navigationRail.IsVisible = viewModel.Navigation.IsRailVisible;
        ApplyModelSourceVisibility();
        Opened += (_, _) =>
        {
            UpdateModelCanvasSize();
            PositionTopLevelWorkspace();
        };
        StartBackgroundApply(viewModel.EffectiveBackground);
    }

    internal static Size ConvertPixelsToDips(
        int widthPixels,
        int heightPixels,
        double renderScaling)
    {
        if (renderScaling <= 0 || !double.IsFinite(renderScaling))
        {
            throw new ArgumentOutOfRangeException(nameof(renderScaling));
        }

        return new Size(widthPixels / renderScaling, heightPixels / renderScaling);
    }

    private void ApplyPersistedWindowSize()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        Size size = ConvertPixelsToDips(
            viewModel.WindowWidthPixels,
            viewModel.WindowHeightPixels,
            RenderScaling);
        Width = size.Width;
        Height = size.Height;
    }

    private void ApplyContentScale(double scale)
    {
        if (!ShouldUpdateContentScale(contentScaleTransform.ScaleX, scale))
        {
            return;
        }

        contentScaleTransform.ScaleX = scale;
        contentScaleTransform.ScaleY = scale;
        UpdateScaledUiBounds();
        UpdateRailHeight();
        PositionMenuWorkspace();
        PositionTopLevelWorkspace();
    }

    internal static bool ShouldUpdateContentScale(double current, double requested) =>
        Math.Abs(current - requested) >= 0.005 - 1e-12;

    internal static double CalculateAutomaticContentScale(double clientHeightDip)
    {
        if (!double.IsFinite(clientHeightDip) || clientHeightDip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clientHeightDip));
        }

        return Math.Clamp(clientHeightDip / 720d, 0.25, 4);
    }

    private void ApplyConfiguredContentScale()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        double scale = viewModel.ContentScaleMode == ContentScaleMode.Automatic
            ? CalculateAutomaticContentScale(
                primaryCanvas.Bounds.Height > 0 ? primaryCanvas.Bounds.Height : Height)
            : viewModel.ContentScale;
        ApplyContentScale(scale);
    }

    private void UpdateScaledUiBounds()
    {
        if (primaryCanvas.Bounds.Width <= 0 || primaryCanvas.Bounds.Height <= 0)
        {
            return;
        }

        double scale = Math.Max(0.01, contentScaleTransform.ScaleX);
        double width = primaryCanvas.Bounds.Width / scale;
        double height = primaryCanvas.Bounds.Height / scale;
        scaledUiCanvas.Width = width;
        scaledUiCanvas.Height = height;
    }

    private Size GetUiViewportSize()
    {
        double scale = Math.Max(0.01, contentScaleTransform.ScaleX);
        return new Size(
            primaryCanvas.Bounds.Width / scale,
            primaryCanvas.Bounds.Height / scale);
    }

    private MainWindow(MainWindowViewModel viewModel, ModelSelectionController modelController)
        : this(
            viewModel,
            modelController,
            new InMemorySceneRepository(SceneWorkspace.CreateDefault()),
            SceneWorkspace.CreateDefault())
    {
    }

    private MainWindow(
        MainWindowViewModel viewModel,
        ModelSelectionController modelController,
        ISceneRepository sceneRepository,
        SceneWorkspace scenes)
        : this(viewModel, modelController, sceneRepository, scenes, sceneCoordinatorLogger: null)
    {
    }

    private MainWindow(
        MainWindowViewModel viewModel,
        ModelSelectionController modelController,
        ISceneRepository sceneRepository,
        SceneWorkspace scenes,
        ILogger<MainModelAssignmentCoordinator>? sceneCoordinatorLogger)
        : this(viewModel)
    {
        ArgumentNullException.ThrowIfNull(sceneRepository);
        ArgumentNullException.ThrowIfNull(scenes);
        ownedModelController = modelController;
        ownedSceneRepository = sceneRepository;
        modelController.SetRenderingBackendPreference(viewModel.ModelRenderingBackendPreference);
        renderingBackendPreferenceChanged = preference =>
        {
            void ApplyPreference()
            {
                if (preference == ModelRenderingBackendPreference.Gpu)
                {
                    PrepareForGpuRenderingRetry();
                }

                ModelRenderingBackendStatus current = modelController.RenderingBackendStatus;
                bool needsTransition = modelController.Active is not null
                    && (preference == ModelRenderingBackendPreference.Gpu
                        ? current.State != ModelRenderingBackendState.Gpu
                        : current.State != ModelRenderingBackendState.Cpu);
                long transitionGeneration = needsTransition
                    ? BeginCanvasTransition("RenderingBackend")
                    : 0;
                if (transitionGeneration != 0)
                {
                    Volatile.Write(
                        ref renderingBackendTransitionGeneration,
                        transitionGeneration);
                }

                modelController.SetRenderingBackendPreference(preference);
                modelCanvas.RefreshRenderingBackend();
                ModelRenderingBackendStatus updated = modelController.RenderingBackendStatus;
                if (transitionGeneration != 0
                    && updated.State is ModelRenderingBackendState.Cpu
                        or ModelRenderingBackendState.Gpu)
                {
                    EndRenderingBackendTransition(transitionGeneration);
                }
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                ApplyPreference();
            }
            else
            {
                Dispatcher.UIThread.Post(ApplyPreference);
            }
        };
        viewModel.RenderingBackendPreferenceChanged += renderingBackendPreferenceChanged;
        modelController.RenderingBackendStatusChanged += OnRenderingBackendStatusChanged;
        ownedMainModelCoordinator = new MainModelAssignmentCoordinator(
            sceneRepository,
            new ControllerRuntimeAdapter(this, modelController),
            scenes,
            sceneCoordinatorLogger);
        ownedMainModelCoordinator.StateChanged += OnMainModelAssignmentStateChanged;
        viewModel.ModelAssignmentRequested += OnMainModelAssignmentRequested;
        viewModel.SceneActivationRequested = OnSceneActivationRequestedAsync;
        viewModel.SceneDeactivationRequested = OnSceneDeactivationRequestedAsync;
        viewModel.SceneCreationRequested = (displayName, cancellationToken) =>
            ownedMainModelCoordinator.CreateSceneAsync(displayName, cancellationToken);
        viewModel.SceneRenameRequested = (sceneId, displayName, cancellationToken) =>
            ownedMainModelCoordinator.RenameSceneAsync(sceneId, displayName, cancellationToken);
        viewModel.SceneDeletionRequested = (sceneId, cancellationToken) =>
            ownedMainModelCoordinator.DeleteSceneAsync(sceneId, cancellationToken);
        viewModel.MainModelVisibilityRequested = (isVisible, cancellationToken) =>
            ownedMainModelCoordinator.SetMainModelVisibilityAsync(isVisible, cancellationToken);
        viewModel.MainModelLockRequested = (isLocked, cancellationToken) =>
            ownedMainModelCoordinator.SetMainModelLockAsync(isLocked, cancellationToken);
        viewModel.SceneAttachmentVisibilityRequested = (sourceId, isVisible, cancellationToken) =>
            ownedMainModelCoordinator.SetAttachmentVisibilityAsync(sourceId, isVisible, cancellationToken);
        viewModel.SceneAttachmentLockRequested = (sourceId, isLocked, cancellationToken) =>
            ownedMainModelCoordinator.SetAttachmentLockAsync(sourceId, isLocked, cancellationToken);
        viewModel.SceneAttachmentTransformRequested = (sourceId, transform, cancellationToken) =>
            ownedMainModelCoordinator.SetAttachmentTransformAsync(sourceId, transform, cancellationToken);
        viewModel.SceneAttachmentMountModeRequested = (sourceId, mountMode, cancellationToken) =>
        {
            AttachmentModelAnchor? initialAnchor = null;
            SceneTransform? initialLocalTransform = null;
            SceneTransform? presentedWorldTransform =
                signalAttachmentPresenter?.TryGetVisual(
                    sourceId,
                    out SignalAttachmentVisual? visual) == true
                    ? visual?.Transform
                    : null;
            if (mountMode == AttachmentMountMode.MainModelAnchor
                && (signalAttachmentPresenter is not { } presenter
                    || !presenter.TryCreateModelBindingAtVisualCenter(
                        sourceId,
                        primaryCanvas.Bounds.Size,
                        latestMainModelReferenceHeight,
                        latestMainModelRasterTransform,
                        out initialAnchor,
                        out initialLocalTransform)))
            {
                return Task.FromResult(false);
            }

            return ownedMainModelCoordinator.SetAttachmentMountModeAsync(
                sourceId,
                mountMode,
                presentedWorldTransform,
                initialAnchor,
                initialLocalTransform,
                cancellationToken);
        };
        viewModel.SceneAttachmentModelBindingRequested = (
            sourceId,
            anchor,
            localTransform,
            cancellationToken) => ownedMainModelCoordinator.SetAttachmentModelBindingAsync(
                sourceId,
                anchor,
                localTransform,
                cancellationToken);
        viewModel.SceneAttachmentMoveRequested = (sourceId, placement, destinationIndex, cancellationToken) =>
            ownedMainModelCoordinator.MoveAttachmentToAsync(
                sourceId,
                placement,
                destinationIndex,
                cancellationToken);
        viewModel.MainModelMoveRequested = (frontAttachmentCount, cancellationToken) =>
            ownedMainModelCoordinator.MoveMainModelToAsync(
                frontAttachmentCount,
                cancellationToken);
        viewModel.SceneAttachmentDisplayNameRequested = (sourceId, displayName, cancellationToken) =>
            ownedMainModelCoordinator.SetAttachmentDisplayNameAsync(
                sourceId,
                displayName,
                cancellationToken);
        viewModel.SceneAttachmentRemovalRequested = (sourceId, cancellationToken) =>
            ownedMainModelCoordinator.RemoveAttachmentAsync(sourceId, cancellationToken);
        viewModel.MainModelTrackingRequested =
            (trackingMode, channels, idleAnimationId, cancellationToken) =>
                ownedMainModelCoordinator.SetMainModelTrackingAsync(
                    trackingMode,
                    channels,
                    idleAnimationId,
                    cancellationToken);
        viewModel.SceneBlurEffectRequested = (effect, cancellationToken) =>
            ownedMainModelCoordinator.SetActiveSceneBlurEffectAsync(effect, cancellationToken);
        viewModel.SceneBackgroundOverrideRequested = (background, cancellationToken) =>
            ownedMainModelCoordinator.SetActiveBackgroundOverrideAsync(background, cancellationToken);
        viewModel.SceneSignalAttachmentRequested = (protocol, source, cancellationToken) =>
            ownedMainModelCoordinator.AddSignalAttachmentAsync(protocol, source.Id, cancellationToken);
        viewModel.SceneAttachmentRequested = (sourceTypeId, resourceReference, displayName, videoOptions, placement, cancellationToken) =>
            ownedMainModelCoordinator.AddAttachmentAsync(
                sourceTypeId,
                resourceReference,
                displayName,
                videoOptions,
                placement,
                cancellationToken);
        ApplyMainModelAssignmentState(
            ownedMainModelCoordinator.CurrentWorkspace,
            ownedMainModelCoordinator.PresentedSceneId,
            ownedMainModelCoordinator.PendingModelId);
        modelCanvas.Attach(modelController);
        Opened += OnModelHostOpened;
    }

    public MainWindowViewModel? ViewModel { get; }

    internal Task BackgroundApplyTask => Volatile.Read(ref backgroundApplyTask);

    internal Task RenderingBackendFallbackTask =>
        Volatile.Read(ref renderingBackendFallbackTask);

    internal void PrepareForGpuRenderingRetry()
    {
        Interlocked.Increment(ref renderingBackendFallbackGeneration);
        modelRenderingFallbackNoticeShown = false;
        if (ViewModel?.TopLevelWorkspace.Content?.Payload is not ModelRenderingFallbackNotice)
        {
            return;
        }

        ViewModel.TopLevelWorkspace.Close();
        ModelRenderingFallbackNoticeLog.ClearedForRetry(transitionLogger);
    }

    public CanvasGeometrySnapshot CapturePrimaryCanvasGeometry()
    {
        Rect bounds = primaryCanvas.Bounds;
        return new CanvasGeometrySnapshot(
            bounds,
            new Point(bounds.Center.X, bounds.Center.Y));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref lifetimeDisposed, 1) != 0)
        {
            return;
        }

        modelSelectionCancellation.Cancel();
        modelSelectionCancellation.Dispose();
        sourceMappingCancellation.Cancel();
        sourceMappingCancellation.Dispose();
        screenshotCancellation.Cancel();
        screenshotCancellation.Dispose();
        Task backgroundDisposal = DisposeBackgroundPresentationAsync();
        _ = backgroundDisposal.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        Task signalAttachmentDisposal = DisposeSignalAttachmentPresentationAsync();
        _ = signalAttachmentDisposal.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        mainModelCanvasInteraction?.Dispose();
        mainModelCanvasInteraction = null;
        if (sceneAttachmentCanvasInteraction is not null)
        {
            sceneAttachmentCanvasInteraction.CommitRequested -= OnAttachmentTransformCommitRequested;
            sceneAttachmentCanvasInteraction.AnchorSelectionRequested -= OnAttachmentAnchorSelectionRequested;
            sceneAttachmentCanvasInteraction.AnchorSelectorPreviewChanged -= OnAttachmentAnchorSelectorPreviewChanged;
        }
        sceneAttachmentCanvasInteraction?.Dispose();
        sceneAttachmentCanvasInteraction = null;
        SetAttachmentAnchorSelectorsVisible(false);
        if (ViewModel is not null)
        {
            ViewModel.ScreenshotRequested -= OnScreenshotRequested;
            ViewModel.ShortcutProfileChanged -= OnShortcutProfileChanged;
            if (renderingBackendPreferenceChanged is not null)
            {
                ViewModel.RenderingBackendPreferenceChanged -= renderingBackendPreferenceChanged;
                renderingBackendPreferenceChanged = null;
            }
        }
        Opened -= OnWindowOpenedForGlobalHotKeys;
        if (hotKeyWndProcHook is not null)
        {
            Win32Properties.RemoveWndProcHookCallback(this, hotKeyWndProcHook);
            hotKeyWndProcHook = null;
        }
        if (globalHotKeyHost is not null)
        {
            globalHotKeyProfileCoordinator?.Dispose();
            globalHotKeyProfileCoordinator = null;
            Task hotKeyDisposal = globalHotKeyHost.DisposeAsync().AsTask();
            _ = hotKeyDisposal.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            globalHotKeyHost = null;
        }
        if (ownedModelController is not null)
        {
            ownedModelController.RenderingBackendStatusChanged -= OnRenderingBackendStatusChanged;
        }
        modelCanvas.MainModelFrameStateChanged -= OnMainModelFrameStateChanged;
        if (screenshotCoordinator is not null)
        {
            screenshotCoordinator.Dispose();
        }
        screenshotPreviewOverlay.Detach();
        GC.SuppressFinalize(this);
    }

    private void OnRenderingBackendStatusChanged(
        object? sender,
        ModelRenderingBackendStatus status)
    {
        if (status.State is ModelRenderingBackendState.Cpu or ModelRenderingBackendState.Gpu)
        {
            long transitionGeneration = Volatile.Read(
                ref renderingBackendTransitionGeneration);
            if (transitionGeneration != 0)
            {
                EndRenderingBackendTransition(transitionGeneration);
            }
        }

        if (status.State is not (ModelRenderingBackendState.Cpu
                or ModelRenderingBackendState.SwitchingToCpu)
            || status.LastFaultReason is not { } reason)
        {
            return;
        }

        void ScheduleFallbackNotice()
        {
            if (modelRenderingFallbackNoticeShown
                || !IsVisible
                || Volatile.Read(ref lifetimeDisposed) != 0
                || ViewModel is not { } viewModel)
            {
                return;
            }

            modelRenderingFallbackNoticeShown = true;
            long generation = Interlocked.Increment(
                ref renderingBackendFallbackGeneration);
            ModelRenderingFallbackNoticeLog.Started(
                transitionLogger,
                generation,
                reason);
            Task operation = HandleRenderingBackendFallbackAsync(
                viewModel,
                reason,
                generation);
            Volatile.Write(
                ref renderingBackendFallbackTask,
                ObserveRenderingBackendFallbackAsync(operation, generation));
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ScheduleFallbackNotice();
        }
        else
        {
            Dispatcher.UIThread.Post(ScheduleFallbackNotice);
        }
    }

    private async Task HandleRenderingBackendFallbackAsync(
        MainWindowViewModel viewModel,
        ModelRenderingBackendFaultReason reason,
        long generation)
    {
        if (!IsCurrentRenderingBackendFallback(viewModel, generation))
        {
            return;
        }

        bool rollbackSucceeded = true;
        if (viewModel.ModelRenderingBackendPreference != ModelRenderingBackendPreference.Cpu)
        {
            rollbackSucceeded = await viewModel.TrySetModelRenderingBackendPreferenceAsync(
                ModelRenderingBackendPreference.Cpu,
                modelSelectionCancellation.Token);
        }

        ModelRenderingFallbackNoticeLog.RollbackCompleted(
            transitionLogger,
            generation,
            rollbackSucceeded);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!IsCurrentRenderingBackendFallback(viewModel, generation))
            {
                return;
            }

            viewModel.TopLevelWorkspace.Open(
                new TopLevelWorkspaceContent(
                    "model.rendering-fallback",
                    new ModelRenderingFallbackNotice(reason)),
                "developer.model-rendering.gpu");
            ModelRenderingFallbackNoticeLog.Presented(transitionLogger, reason);
        });
    }

    private async Task ObserveRenderingBackendFallbackAsync(Task operation, long generation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (modelSelectionCancellation.IsCancellationRequested)
        {
            ModelRenderingFallbackNoticeLog.Cancelled(transitionLogger, generation);
        }
        catch (Exception exception)
        {
            ModelRenderingFallbackNoticeLog.Failed(
                transitionLogger,
                exception,
                generation,
                exception.GetType().Name);
        }
    }

    private bool IsCurrentRenderingBackendFallback(
        MainWindowViewModel viewModel,
        long generation) => generation == Volatile.Read(ref renderingBackendFallbackGeneration)
            && modelRenderingFallbackNoticeShown
            && Volatile.Read(ref lifetimeDisposed) == 0
            && IsVisible
            && ReferenceEquals(ViewModel, viewModel);

    public async Task TryRestoreNavigationAsync(Control eventSource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventSource);
        if (!ReferenceEquals(eventSource, primaryCanvas) || ViewModel is null)
        {
            return;
        }

        await ViewModel.TryRestoreNavigationAsync(cancellationToken);
    }

    internal static Task<MainWindow> CreateDefaultAsync(
        MotaraLogHost logHost,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logHost);
        string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Motara",
            "ui-settings.json");
        return CreateDefaultAsync(
            new UiSettingsStore(
                settingsPath,
                logHost.LoggerFactory.CreateLogger<UiSettingsStore>()),
            new LocalizationManager(),
            TimeProvider.System,
            logHost,
            cancellationToken);
    }

    internal static async Task<MainWindow> CreateDefaultAsync(
        IUiSettingsStore settingsStore,
        LocalizationManager localization,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(timeProvider);

        UiSettings settings = await settingsStore.LoadAsync(cancellationToken);
        return await ComposeDefaultAsync(
            settingsStore,
            settings,
            localization,
            timeProvider,
            logHost: null,
            cancellationToken);
    }

    internal static async Task<MainWindow> CreateDefaultAsync(
        IUiSettingsStore settingsStore,
        LocalizationManager localization,
        TimeProvider timeProvider,
        MotaraLogHost logHost,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logHost);
        UiSettings settings = await settingsStore.LoadAsync(cancellationToken);
        return await ComposeDefaultAsync(
            settingsStore,
            settings,
            localization,
            timeProvider,
            logHost,
            cancellationToken);
    }

    private static async Task<MainWindow> ComposeDefaultAsync(
        IUiSettingsStore settingsStore,
        UiSettings settings,
        LocalizationManager localization,
        TimeProvider timeProvider,
        MotaraLogHost? logHost,
        CancellationToken cancellationToken)
    {
        ILoggerFactory? loggerFactory = logHost?.LoggerFactory;
        if (logHost is not null)
        {
            logHost.MinimumLevel = settings.DiagnosticLogLevel;
        }

        var paths = new AppDataPaths();
        var openSeeFaceConfigurationStore = new OpenSeeFaceConfigurationStore(
            Path.Combine(paths.DataRoot, "Tracking", "openseeface.json"),
            loggerFactory?.CreateLogger<OpenSeeFaceConfigurationStore>());
        OpenSeeFaceConfiguration? openSeeFaceConfiguration =
            await openSeeFaceConfigurationStore.LoadAsync(cancellationToken);
        var controller = loggerFactory is null
            ? new TrackingSessionController(
                timeProvider,
                sessionLogger: null,
                openSeeFaceConfiguration: openSeeFaceConfiguration)
            : new TrackingSessionController(
                timeProvider,
                loggerFactory.CreateLogger<Motara.Core.Sessions.ProcessingSession>(),
                loggerFactory.CreateLogger<IFacialMocapTrackingSource>(),
                openSeeFaceConfiguration: openSeeFaceConfiguration);
        var sceneRepository = loggerFactory is null
            ? new SceneRepository(Path.Combine(paths.DataRoot, "Scenes"))
            : new SceneRepository(
                Path.Combine(paths.DataRoot, "Scenes"),
                loggerFactory.CreateLogger<SceneRepository>());
        SceneWorkspace scenes = await sceneRepository.LoadAsync(cancellationToken);
        var catalog = loggerFactory is null
            ? new ModelCatalogScanner(paths.ModelsRoot)
            : new ModelCatalogScanner(
                paths.ModelsRoot,
                loggerFactory.CreateLogger<ModelCatalogScanner>());
        MainWindow? composedWindow = null;
        ILogOperations? logOperations = logHost is null
            ? null
            : new PlatformLogOperations(
                logHost,
                cancellationToken => SelectLogExportDestinationAsync(
                    composedWindow,
                    localization,
                    cancellationToken));
        var shortcutStore = new ShortcutStore(
            paths.InputBindingsPath,
            loggerFactory?.CreateLogger<ShortcutStore>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ShortcutStore>.Instance);
        InputActionRegistry inputActionRegistry = BuiltInInputActions.CreateRegistry();
        MainWindowViewModel viewModel = MainWindowViewModel.Create(
            controller,
            settingsStore,
            settings,
            localization,
            catalog,
            new PlatformModelsFolderLauncher(),
            paths.ModelsRoot,
            logOperations,
            new ModelImporter(
                paths.ModelsRoot,
                paths.ModelImportStagingRoot,
                loggerFactory?.CreateLogger<ModelImporter>()
                    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelImporter>.Instance),
            new ModelImportSourcePicker(cancellationToken => SelectModelImportSourceAsync(
                composedWindow,
                localization,
                cancellationToken)),
            inputActionRegistry,
            shortcutStore);
        IDeviceSecretProtector identityProtector = OperatingSystem.IsWindows()
            ? new WindowsDpapiDeviceSecretProtector()
            : new UnsupportedDeviceSecretProtector();
        var identityStore = new DeviceIdentityStore(
            paths.CollaborationRoot,
            identityProtector,
            timeProvider,
            loggerFactory?.CreateLogger<DeviceIdentityStore>());
        var collaborationProfileStore = new LocalCollaborationProfileStore(
            paths.CollaborationRoot,
            loggerFactory?.CreateLogger<LocalCollaborationProfileStore>());
        var inviteTokenService = new FriendInviteTokenService(
            timeProvider,
            loggerFactory?.CreateLogger<FriendInviteTokenService>());
        var sessionInviteTokenService = new SessionInviteTokenService(
            timeProvider,
            loggerFactory?.CreateLogger<SessionInviteTokenService>());
        var friendStore = new FriendStore(
            paths.CollaborationRoot,
            loggerFactory?.CreateLogger<FriendStore>());
        var relationshipSecretStore = new RelationshipSecretStore(
            paths.CollaborationRoot,
            identityProtector,
            timeProvider,
            loggerFactory?.CreateLogger<RelationshipSecretStore>());
        var relationshipService = new FriendRelationshipService(
            friendStore,
            relationshipSecretStore,
            loggerFactory?.CreateLogger<FriendRelationshipService>());
        var handshakeService = new FriendshipHandshakeService(
            friendStore,
            relationshipSecretStore,
            timeProvider,
            loggerFactory?.CreateLogger<FriendshipHandshakeService>());
        var consumedInviteStore = new ConsumedInviteStore(
            paths.CollaborationRoot,
            timeProvider,
            loggerFactory?.CreateLogger<ConsumedInviteStore>());
        var identitySession = new CollaborationIdentitySession(
            identityStore.LoadOrCreateAsync,
            inviteTokenService,
            loggerFactory?.CreateLogger<CollaborationIdentitySession>());
        var acceptanceService = new FriendInvitationAcceptanceService(
            inviteTokenService,
            friendStore,
            consumedInviteStore,
            timeProvider,
            loggerFactory?.CreateLogger<FriendInvitationAcceptanceService>());
        var identityArchiveService = new CollaborationIdentityArchiveService(
            paths.CollaborationRoot,
            identityProtector,
            timeProvider,
            loggerFactory?.CreateLogger<CollaborationIdentityArchiveService>());
        viewModel.AttachCollaborationWorkspace(new CollaborationWorkspaceViewModel(
            identitySession,
            inviteTokenService,
            sessionInviteTokenService,
            acceptanceService,
            friendStore,
            relationshipService,
            handshakeService,
            timeProvider,
            loggerFactory?.CreateLogger<CollaborationWorkspaceViewModel>(),
            collaborationProfileStore),
            identityArchiveService);
        viewModel.TopLevelWorkspace.AttachLogger(
            loggerFactory?.CreateLogger<TopLevelWorkspaceState>());
        viewModel.AttachBackgroundSettingsLogger(
            loggerFactory?.CreateLogger<MainWindowViewModel>());
        viewModel.AttachBackgroundAssetStore(
            new BackgroundAssetStore(
                paths,
                loggerFactory is null
                    ? NullLogger<BackgroundAssetStore>.Instance
                    : loggerFactory.CreateLogger<BackgroundAssetStore>()),
            new BackgroundRecentAssetStore(
                paths,
                loggerFactory is null
                    ? NullLogger<BackgroundRecentAssetStore>.Instance
                    : loggerFactory.CreateLogger<BackgroundRecentAssetStore>()),
            loggerFactory?.CreateLogger<BackgroundEditorViewModel>(),
            loggerFactory?.CreateLogger<BackgroundPresenter>());
        viewModel.AttachTrackingControllers(
            controller.TrackingController,
            controller.HandTrackingController);
        string trackingChannelSelectionPath = Path.Combine(
            paths.DataRoot,
            "Tracking",
            "channels.json");
        viewModel.AttachTrackingChannelSelectionStore(new TrackingChannelSelectionStore(
            trackingChannelSelectionPath,
            loggerFactory?.CreateLogger<TrackingChannelSelectionStore>()),
            loggerFactory?.CreateLogger<MainWindowViewModel>());
        string iFacialMocapConfigurationPath = Path.Combine(
            paths.DataRoot,
            "Tracking",
            "ifacialmocap.json");
        viewModel.AttachIFacialMocapConfiguration(
            new IFacialMocapConfigurationStore(
                iFacialMocapConfigurationPath,
                loggerFactory?.CreateLogger<IFacialMocapConfigurationStore>()),
            new LocalIpv4AddressProvider(
                loggerFactory?.CreateLogger<LocalIpv4AddressProvider>()),
            controller.IFacialMocapFactory.Configure,
            controller.TrackingController.SelectSourceAsync,
            loggerFactory?.CreateLogger<IFacialMocapConfigurationViewModel>(),
            loggerFactory?.CreateLogger<MainWindowViewModel>());
        viewModel.AttachOpenSeeFaceConfiguration(
            openSeeFaceConfigurationStore,
            controller.OpenSeeFaceFactory.CaptureConfiguration,
            controller.OpenSeeFaceFactory.ConfigureCapture,
            controller.OpenSeeFaceFactory.ListCamerasAsync,
            controller.TrackingController.SelectSourceAsync,
            loggerFactory?.CreateLogger<OpenSeeFaceConfigurationViewModel>());
        SourceMappingPaths iFacialMappingPaths = SourceMappingPaths.ForAdapter(
            paths.SourceMappingsRoot,
            "ifacialmocap");
        var iFacialMappingStore = new SourceMappingProfileStore(
            iFacialMappingPaths,
            loggerFactory?.CreateLogger<SourceMappingProfileStore>());
        SourceMappingPaths openSeeFaceMappingPaths = SourceMappingPaths.ForAdapter(
            paths.SourceMappingsRoot,
            "openseeface");
        var openSeeFaceMappingStore = new SourceMappingProfileStore(
            openSeeFaceMappingPaths,
            loggerFactory?.CreateLogger<SourceMappingProfileStore>());
        SourceMappingAdapterContext[] sourceMappingContexts =
        [
            new SourceMappingAdapterContext(
                "ifacialmocap",
                Motara.Tracking.iFacialMocap.IFacialMocapTrackingSource.SourceId,
                "Menu.Tracking.Source.IFacialMocap",
                Motara.Tracking.iFacialMocap.IFacialMocapMappingDefaults.CreateProfile,
                Motara.Tracking.iFacialMocap.IFacialMocapMappingDefaults.Inputs,
                iFacialMappingStore,
                controller.IFacialMocapFactory.ConfigureMapping),
            new SourceMappingAdapterContext(
                "openseeface",
                OpenSeeFaceLocalTrackingSourceFactory.SourceId,
                "Menu.Tracking.Source.OpenSeeFace",
                OpenSeeFaceMappingDefaults.CreateProfile,
                OpenSeeFaceMappingDefaults.Inputs,
                openSeeFaceMappingStore,
                controller.OpenSeeFaceFactory.ConfigureMapping),
        ];
        viewModel.AttachSourceMappings(
            sourceMappingContexts,
            paths.SourceMappingsRoot,
            paths.ModelsRoot,
            Path.Combine(paths.DataRoot, "Scenes"));
        var modelController = new ModelSelectionController(
            loggerFactory is null
                ? new PurismModelRuntimeFactory()
                : new PurismModelRuntimeFactory(loggerFactory.CreateLogger<PurismModelRuntime>()),
            loggerFactory is null
                ? new SkiaModelFrameRendererFactory()
                : new SkiaModelFrameRendererFactory(loggerFactory.CreateLogger<SkiaModelRenderer>()),
            catalog,
            loggerFactory?.CreateLogger<ModelSelectionController>());
        var modelMappingService = new ModelParameterMappingService(
            loggerFactory?.CreateLogger<ModelParameterMappingService>());
        var parameterPriorityStore = new ParameterPriorityStore(
            paths.ParameterPriorityPath,
            loggerFactory?.CreateLogger<ParameterPriorityStore>());
        var parameterPrioritySource = new ParameterPriorityProfileSource();
        var modelParameterObservationSource = new ModelParameterObservationSource();
        var cubismEditorOutput = loggerFactory is null
            ? new CubismEditorOutputTarget(new CubismEditorConnectionOptions(
                new Uri(settings.CubismEditor.Endpoint),
                alwaysOutput: settings.CubismEditor.AlwaysOutput))
            : new CubismEditorOutputTarget(
                new CubismEditorConnectionOptions(
                    new Uri(settings.CubismEditor.Endpoint),
                    alwaysOutput: settings.CubismEditor.AlwaysOutput),
                logger: loggerFactory.CreateLogger<CubismEditorOutputTarget>());
        var cubismEditorMappingStore = new CubismEditorMappingStore(
            Path.Combine(paths.SourceMappingsRoot, "Outputs", CubismEditorMappingStore.FileName),
            loggerFactory?.CreateLogger<CubismEditorMappingStore>());
        viewModel.AttachCubismEditorOutput(cubismEditorOutput);
        viewModel.AttachParameterPriority(
            parameterPriorityStore.LoadAsync,
            parameterPriorityStore.SaveAsync,
            parameterPrioritySource.Apply,
            loggerFactory?.CreateLogger<ParameterPriorityWorkspaceViewModel>());
        var activeBindingSource = new ActiveModelParameterBindingSource(
            async (active, cancellationToken) =>
            {
                ModelCatalogViewModel.ModelCatalogEntryViewModel? entry = viewModel.ModelCatalog.Entries
                    .FirstOrDefault(candidate => candidate.Id == active.Id);
                if (entry is null || active.Runtime.Capabilities is not { } capabilities)
                {
                    return [];
                }

                ModelParameterMappingDocument document = await modelMappingService.LoadAsync(
                    entry,
                    capabilities,
                    cancellationToken).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                    viewModel.UpdateCurrentModelMappingStatus(
                        active.Id,
                        document.ParameterSettings.Length));
                return document.ParameterSettings;
            },
            loggerFactory?.CreateLogger<ActiveModelParameterBindingSource>());
        var activePhysicsSource = new ActiveModelPhysicsSource(
            loggerFactory?.CreateLogger<ActiveModelPhysicsSource>());
        var dragPhysicsSource = new ActiveModelDragPhysicsSource();
        var activeAnimationSource = new ActiveModelAnimationSource(
            loggerFactory?.CreateLogger<ActiveModelAnimationSource>());
        var motionExpansionSource = new ActiveModelMotionExpansionSource();
        var modelDriveController = new ActiveModelDriveController(
            controller,
            modelController,
            loggerFactory?.CreateLogger<ActiveModelDriveController>(),
            activeBindingSource,
            parameterPrioritySource,
            observationSource: modelParameterObservationSource,
            physicsSource: activePhysicsSource,
            applicationFrameRateModeProvider: () => viewModel.FrameRateMode,
            motionExpansionSource: motionExpansionSource,
            animationSource: activeAnimationSource,
            dragPhysicsSource: dragPhysicsSource);
        var cubismEditorOutputController = new CubismEditorOutputController(
            controller,
            cubismEditorOutput,
            logger: loggerFactory?.CreateLogger<CubismEditorOutputController>());
        viewModel.AttachCubismEditorMapping(
            cubismEditorMappingStore.LoadAsync,
            async (document, cancellationToken) =>
            {
                await cubismEditorMappingStore.SaveAsync(document, cancellationToken).ConfigureAwait(false);
                cubismEditorOutputController.UpdateMapping(document.ToMapping());
            },
            loggerFactory?.CreateLogger<ModelParameterMappingEditorViewModel>());
        viewModel.AttachModelParameterMapping(
            modelController.GetCapabilitiesAsync,
            async (document, cancellationToken) =>
            {
                await modelMappingService.SaveAsync(document, cancellationToken).ConfigureAwait(false);
                if (modelController.Active is { } active && active.Id == document.Model.Id)
                {
                    await activeBindingSource.ReloadAsync(active, cancellationToken).ConfigureAwait(false);
                }
            },
            modelParameterObservationSource);
        viewModel.AttachModelPhysicsSettings(
            async (entry, cancellationToken) =>
            {
                MotaraModelConfiguration configuration = await new MotaraModelConfigurationStore(
                    entry.RootPath,
                    entry.DisplayName).LoadAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Model physics settings require a configuration file.");
                return configuration.Physics;
            },
            async (entry, physics, cancellationToken) =>
            {
                var store = new MotaraModelConfigurationStore(entry.RootPath, entry.DisplayName);
                MotaraModelConfiguration configuration = await store.LoadAsync(cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Model physics settings require a configuration file.");
                await store.SaveAsync(configuration with { Physics = physics }, cancellationToken)
                    .ConfigureAwait(false);
                if (modelController.Active is { } active && active.Id == entry.Id)
                {
                    await activePhysicsSource.ReloadAsync(active, cancellationToken).ConfigureAwait(false);
                }
            },
            loggerFactory?.CreateLogger<ModelPhysicsSettingsViewModel>());
        viewModel.AttachModelBasicSettings(
            async (entry, cancellationToken) =>
            {
                string descriptorName = ModelIdentity.FromDescriptorFilename(
                    Path.GetFileName(entry.Descriptor!.DescriptorPath)).DisplayName;
                MotaraModelConfiguration configuration = await new MotaraModelConfigurationStore(
                    entry.RootPath,
                    descriptorName).LoadAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Model basic settings require a configuration file.");
                return new ModelBasicSettingsDocument(
                    configuration,
                    entry.ThumbnailPath,
                    entry.Descriptor.Motions);
            },
            async (entry, update, cancellationToken) =>
            {
                string descriptorName = ModelIdentity.FromDescriptorFilename(
                    Path.GetFileName(entry.Descriptor!.DescriptorPath)).DisplayName;
                MotaraModelConfiguration configuration = update.Configuration;
                if (update.PreviewSourcePath is { } sourcePath)
                {
                    string targetPath = Path.Combine(
                        entry.RootPath,
                        "motara",
                        "assets",
                        "preview.png");
                    await new ModelPreviewNormalizer().NormalizeAsync(
                        sourcePath,
                        targetPath,
                        cancellationToken).ConfigureAwait(false);
                    configuration = configuration with
                    {
                        FileLayout = new ModelFileLayoutConfiguration("motara/assets/preview.png"),
                    };
                }
                await new MotaraModelConfigurationStore(entry.RootPath, descriptorName)
                    .SaveAsync(configuration, cancellationToken).ConfigureAwait(false);
                if (modelController.Active is { } active && active.Id == entry.Id)
                {
                    await activeAnimationSource.ReloadAsync(active, cancellationToken).ConfigureAwait(false);
                }
            },
            loggerFactory?.CreateLogger<ModelBasicSettingsViewModel>());
        var window = new MainWindow(
            viewModel,
            modelController,
            sceneRepository,
            scenes,
            loggerFactory?.CreateLogger<MainModelAssignmentCoordinator>())
        {
            ownedViewModel = viewModel,
            ownedSessionController = controller,
            ownedModelController = modelController,
            ownedModelDriveController = modelDriveController,
            ownedDragPhysicsSource = dragPhysicsSource,
            ownedAnimationSource = activeAnimationSource,
            shortcutDispatcher = new ShortcutDispatcher(
                loggerFactory?.CreateLogger<ShortcutDispatcher>()),
            ownedCubismEditorOutput = cubismEditorOutput,
            ownedCubismEditorOutputController = cubismEditorOutputController,
            ownedParameterPriorityStore = parameterPriorityStore,
            ownedIdentityStore = identityStore,
            ownedCollaborationProfileStore = collaborationProfileStore,
            ownedFriendStore = friendStore,
            ownedConsumedInviteStore = consumedInviteStore,
            transitionLogger = loggerFactory?.CreateLogger<MainWindow>()
                ?? NullLogger<MainWindow>.Instance,
        };
        window.modelCanvas.SetLogger(loggerFactory?.CreateLogger<ModelCanvas>());
        window.menuWorkspace.SetLogger(loggerFactory?.CreateLogger<CascadingMenuWorkspace>());
        window.AttachScreenshotCoordinator(new ScreenshotCoordinator(
            new ScreenshotService(
                new SkiaScreenshotFrameSource(
                    modelController,
                    viewModel.BackgroundAssetStore.GetManagedPath,
                    loggerFactory?.CreateLogger<SkiaScreenshotFrameSource>(),
                    () => window.backgroundPresenter?.Current),
                new ScreenshotPathProvider(),
                timeProvider,
                loggerFactory?.CreateLogger<ScreenshotService>()
                    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ScreenshotService>.Instance),
            timeProvider));
        window.modelCanvas.AttachMotionExpansionSource(motionExpansionSource);
        _ = InitializeSourceMappingsAsync(
            sourceMappingContexts,
            Path.Combine(paths.DataRoot, "Transactions"),
            window.sourceMappingCancellation.Token);
        _ = InitializeParameterPriorityAsync(
            parameterPriorityStore,
            parameterPrioritySource,
            window.sourceMappingCancellation.Token);
        _ = InitializeCubismEditorMappingAsync(
            cubismEditorMappingStore,
            cubismEditorOutputController,
            loggerFactory?.CreateLogger<CubismEditorMappingStore>(),
            window.sourceMappingCancellation.Token);
        if (settings.CubismEditor.StartOnLaunch)
        {
            _ = cubismEditorOutput.StartAsync(window.sourceMappingCancellation.Token);
        }
        composedWindow = window;
        window.Closing += window.OnOwnedWindowClosing;
        return window;
    }

    private static async Task<string?> SelectLogExportDestinationAsync(
        MainWindow? window,
        LocalizationManager localization,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (window is null)
        {
            throw new InvalidOperationException("The main window is unavailable.");
        }

        IStorageFile? file = await window.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = localization.GetString("Dialog.ExportLogs.Title"),
                SuggestedFileName = $"Motara-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
                DefaultExtension = "zip",
                FileTypeChoices =
                [
                    new FilePickerFileType(localization.GetString("Dialog.ExportLogs.ZipType"))
                    {
                        Patterns = ["*.zip"],
                    },
                ],
            });
        cancellationToken.ThrowIfCancellationRequested();
        return file?.TryGetLocalPath();
    }

    private static async Task<string?> SelectModelImportSourceAsync(
        MainWindow? window,
        LocalizationManager localization,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (window is null)
        {
            throw new InvalidOperationException("The main window is unavailable.");
        }

        IReadOnlyList<IStorageFile> files = await window.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = localization.GetString("Dialog.ImportModel.Title"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(localization.GetString("Dialog.ImportModel.DescriptorType"))
                    {
                        Patterns = ["*.model3.json", "model3.json", "*.zip", "*.rar", "*.7z"],
                    },
                ],
            });
        cancellationToken.ThrowIfCancellationRequested();
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private void PositionMenuWorkspace()
    {
        Size viewport = GetUiViewportSize();
        if (viewport.Width <= 0)
        {
            return;
        }

        if (menuWorkspace.IsVisible)
        {
            Canvas.SetLeft(
                menuWorkspace,
                menuWorkspace.CalculateLeft(GetMenuAnchor(), viewport.Width));
            menuWorkspace.Height = Math.Max(0, viewport.Height - 44);
        }

        if (modelLibraryMenu.IsVisible)
        {
            double width = Math.Min(760, Math.Max(480, viewport.Width - 104));
            modelLibraryMenu.Width = width;
            modelLibraryMenu.Height = Math.Max(0, viewport.Height - 44);
            Canvas.SetLeft(
                modelLibraryMenu,
                Math.Max(16, Math.Min(GetMenuAnchor(), viewport.Width - width - 16)));
        }

        if (collaborationMenu.IsVisible)
        {
            double width = Math.Min(720, Math.Max(480, viewport.Width - 104));
            collaborationMenu.Width = width;
            collaborationMenu.Height = Math.Max(0, viewport.Height - 44);
            Canvas.SetLeft(
                collaborationMenu,
                Math.Max(16, Math.Min(GetMenuAnchor(), viewport.Width - width - 16)));
        }
    }

    private double GetMenuAnchor() =>
        Canvas.GetLeft(navigationRail) + navigationRail.Bounds.Width + 12;

    private void UpdateMenuPresentation()
    {
        bool showModelLibrary = ViewModel?.Navigation is
        {
            IsRailVisible: true,
            SelectedDestination: NavigationDestination.Model,
        };
        modelLibraryMenu.IsVisible = showModelLibrary;
        bool showCollaboration = ViewModel?.Navigation is
        {
            IsRailVisible: true,
            SelectedDestination: NavigationDestination.Collaboration,
        };
        collaborationMenu.IsVisible = showCollaboration;
        if (showModelLibrary || showCollaboration)
        {
            menuWorkspace.IsVisible = false;
        }
        else
        {
            menuWorkspace.IsVisible = ViewModel?.Navigation is
            {
                IsRailVisible: true,
                SelectedDestination: not null,
            };
        }
    }

    private async Task InitializeCollaborationMenuAsync()
    {
        try
        {
            await collaborationMenu.InitializeAsync(CancellationToken.None);
            AttachActiveSceneCollaborationBridge();
            PositionMenuWorkspace();
        }
        catch (Exception)
        {
            // The workspace has already recorded a sanitized failure.
        }
    }

    private void AttachActiveSceneCollaborationBridge()
    {
        if (ownedCollaborationBridge is not null
            || ownedMainModelCoordinator is null
            || ViewModel?.CollaborationWorkspace?.SessionCoordinator is not { } session)
        {
            return;
        }

        ownedCollaborationBridge = new ActiveSceneCollaborationBridge(
            ownedMainModelCoordinator,
            session,
            ResolveModelInstanceIdAsync);
    }

    internal async Task AttachRemoteModelReceiverAsync(ModelPublicationReceiver receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        if (ownedRemoteModelPresenter is not null)
        {
            await ownedRemoteModelPresenter.DisposeAsync();
        }

        if (ownedRemoteModelSources is not null)
        {
            await ownedRemoteModelSources.DisposeAsync();
        }
        RemoteMemberModelRuntimeFactory factory = new(
            new PurismModelRuntimeFactory(),
            new SkiaModelFrameRendererFactory());
        ownedRemoteModelSources = new RemoteMemberModelSourceRegistry(factory.CreateAsync);
        ownedRemoteModelPresenter = new RemoteModelPublicationPresenter(
            receiver,
            ownedRemoteModelSources);
        remoteModelCanvas.Attach(ownedRemoteModelSources);
    }

    private async Task<ModelInstanceId?> ResolveModelInstanceIdAsync(
        ModelId modelId,
        CancellationToken cancellationToken)
    {
        ModelCatalogViewModel.ModelCatalogEntryViewModel? entry = ViewModel?.ModelCatalog.Entries
            .FirstOrDefault(candidate => candidate.Id == modelId);
        if (entry is null)
        {
            return null;
        }

        MotaraModelConfiguration? configuration = await new MotaraModelConfigurationStore(
            entry.RootPath,
            entry.DisplayName).LoadAsync(cancellationToken).ConfigureAwait(false);
        return configuration is null
            ? null
            : new ModelInstanceId(configuration.CollaborationModelInstanceId);
    }

    private void UpdateRailHeight()
    {
        double height = GetUiViewportSize().Height
            - Canvas.GetTop(navigationRail)
            - Canvas.GetBottom(navigationRail);
        if (height >= 0)
        {
            navigationRail.Height = height;
            navigationRail.SetAvailableHeight(height);
        }
    }

    private void UpdateModelCanvasSize()
    {
        modelCanvas.Width = primaryCanvas.Bounds.Width;
        modelCanvas.Height = primaryCanvas.Bounds.Height;
        backgroundLayer.Width = primaryCanvas.Bounds.Width;
        backgroundLayer.Height = primaryCanvas.Bounds.Height;
        signalAttachmentBeforeLayer.Width = primaryCanvas.Bounds.Width;
        signalAttachmentBeforeLayer.Height = primaryCanvas.Bounds.Height;
        signalAttachmentAfterLayer.Width = primaryCanvas.Bounds.Width;
        signalAttachmentAfterLayer.Height = primaryCanvas.Bounds.Height;
        modelArtMeshHighlightLayer.Width = primaryCanvas.Bounds.Width;
        modelArtMeshHighlightLayer.Height = primaryCanvas.Bounds.Height;
        modelTransitionBlank.Width = primaryCanvas.Bounds.Width;
        modelTransitionBlank.Height = primaryCanvas.Bounds.Height;
        screenshotPreviewOverlay.Width = primaryCanvas.Bounds.Width;
        screenshotPreviewOverlay.Height = primaryCanvas.Bounds.Height;
        UpdateAttachmentAnchorSelectors();
    }

    private void OnViewModelBackgroundPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.EffectiveBackground)
            && ViewModel is { } viewModel)
        {
            StartBackgroundApply(viewModel.EffectiveBackground);
        }
    }

    private static FfmpegVideoDecoder CreateFfmpegVideoDecoder()
    {
        string ffmpegRoot = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        return new FfmpegVideoDecoder(
            Path.Combine(ffmpegRoot, "ffprobe.exe"),
            Path.Combine(ffmpegRoot, "ffmpeg.exe"));
    }

    private void OnBackgroundSnapshotChanged(
        object? sender,
        BackgroundVisualSnapshot snapshot) => backgroundLayer.Snapshot = snapshot;

    private void StartBackgroundApply(ResolvedBackground background)
    {
        if (backgroundPresenter is not { } presenter
            || Volatile.Read(ref lifetimeDisposed) != 0
            || Volatile.Read(ref backgroundDisposalStarted) != 0)
        {
            return;
        }

        Task apply = ApplyBackgroundAsync(presenter, background);
        Volatile.Write(ref backgroundApplyTask, apply);
    }

    private void ApplySignalAttachments(SceneDocument? scene)
    {
        SignalAttachmentScenePresenter? presenter = signalAttachmentPresenter;
        if (presenter is null || Volatile.Read(ref lifetimeDisposed) != 0)
        {
            return;
        }

        CancellationTokenSource cancellation = new();
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref signalAttachmentCancellation,
            cancellation);
        previous?.Cancel();
        previous?.Dispose();
        signalAttachmentApplyTask = ApplySignalAttachmentsAsync(presenter, scene, cancellation);
    }

    private async Task ApplySignalAttachmentsAsync(
        SignalAttachmentScenePresenter presenter,
        SceneDocument? scene,
        CancellationTokenSource cancellation)
    {
        try
        {
            await presenter.ApplyAsync(scene, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel?.ReportExternalCommandFailure(exception);
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref signalAttachmentCancellation, null, cancellation),
                    cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    private async Task DisposeSignalAttachmentPresentationAsync()
    {
        CancellationTokenSource? cancellation = Interlocked.Exchange(
            ref signalAttachmentCancellation,
            null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        SignalAttachmentScenePresenter? presenter = signalAttachmentPresenter;
        signalAttachmentPresenter = null;
        if (presenter is null)
        {
            return;
        }

        presenter.Changed -= OnSignalAttachmentsChanged;
        await presenter.DisposeAsync().ConfigureAwait(false);
    }

    private void OnSignalAttachmentsChanged(object? sender, EventArgs args)
    {
        SignalAttachmentScenePresenter? presenter = signalAttachmentPresenter;
        if (presenter is null)
        {
            return;
        }

        void Apply()
        {
            signalAttachmentBeforeLayer.Visuals = presenter.BeforeModel;
            signalAttachmentAfterLayer.Visuals = presenter.AfterModel;
            UpdateAttachmentAnchorSelectors();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply();
        }
        else
        {
            Dispatcher.UIThread.Post(Apply);
        }
    }

    private async Task ApplyBackgroundAsync(
        BackgroundPresenter presenter,
        ResolvedBackground background)
    {
        try
        {
            await presenter.ApplyAsync(background, backgroundCancellation.Token);
        }
        catch (OperationCanceledException) when (backgroundCancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref backgroundDisposalStarted) != 0)
        {
        }
        catch (Exception exception)
        {
            ViewModel?.ReportExternalCommandFailure(exception);
        }
    }

    private Task DisposeBackgroundPresentationAsync()
    {
        lock (backgroundDisposalGate)
        {
            if (backgroundDisposalStarted != 0)
            {
                return backgroundDisposalTask;
            }

            backgroundDisposalStarted = 1;
            if (ViewModel is not null)
            {
                ViewModel.PropertyChanged -= OnViewModelBackgroundPropertyChanged;
            }

            backgroundCancellation.Cancel();
            if (backgroundPresenter is not { } presenter)
            {
                backgroundCancellation.Dispose();
                return backgroundDisposalTask;
            }

            presenter.SnapshotChanged -= OnBackgroundSnapshotChanged;
            backgroundPresenter = null;
            backgroundDisposalTask = DisposeBackgroundPresenterCoreAsync(presenter);
            return backgroundDisposalTask;
        }
    }

    private async Task DisposeBackgroundPresenterCoreAsync(BackgroundPresenter presenter)
    {
        try
        {
            await presenter.DisposeAsync();
        }
        finally
        {
            backgroundCancellation.Dispose();
        }
    }

    private void AttachScreenshotCoordinator(ScreenshotCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        screenshotCoordinator = coordinator;
        screenshotPreviewOverlay.Attach(coordinator);
        ViewModel!.ScreenshotRequested += OnScreenshotRequested;
    }

    private void OnScreenshotRequested(ScreenshotCaptureRequest request) =>
        _ = CaptureScreenshotAsync(request);

    private void OnWindowKeyDown(object? sender, KeyEventArgs args)
    {
        if (IsControlKey(args.Key) || args.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SetAttachmentAnchorSelectorsVisible(true);
        }

        if (ViewModel is null)
        {
            return;
        }

        InputResolution? resolution = ViewModel.InputActionRegistry.Resolve(
            new InputContext([InputBindingScope.Application], args.Source is TextBox),
            InputGesture.KeyChord(args.Key.ToString(), ToInputModifiers(args.KeyModifiers)));
        if (resolution is null)
        {
            return;
        }

        if (resolution.Value.ActionId == BuiltInInputActions.CaptureScreenshot
            || resolution.Value.ActionId == "Software.Screenshot")
        {
            ViewModel.RequestScreenshot();
        }
        else if (shortcutDispatcher is not null)
        {
            _ = shortcutDispatcher.DispatchAsync(
                resolution.Value.ActionId,
                CreateShortcutRuntimeContext(),
                modelSelectionCancellation.Token);
        }
        args.Handled = resolution.Value.ShouldConsume;
    }

    private void OnWindowOpenedForGlobalHotKeys(object? sender, EventArgs args)
    {
        if (!OperatingSystem.IsWindows() || globalHotKeyHost is not null) return;
        globalHotKeyHost = new WindowsGlobalHotKeyHost(
            () => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero,
            logger: transitionLogger);
        globalHotKeyProfileCoordinator = new GlobalHotKeyProfileCoordinator(
            globalHotKeyHost,
            action => Dispatcher.UIThread.Post(action),
            transitionLogger);
        globalHotKeyHost.HotKeyPressed += (_, binding) => DispatchShortcutAction(binding.ActionId);
        hotKeyWndProcHook = (IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            const uint WmHotKey = 0x0312;
            if (message == WmHotKey && globalHotKeyHost.TryHandleHotKey(wParam.ToInt32()))
                handled = true;
            return IntPtr.Zero;
        };
        Win32Properties.AddWndProcHookCallback(this, hotKeyWndProcHook);
        if (ViewModel is not null) OnShortcutProfileChanged(ViewModel.InputActionRegistry.Profile);
    }

    private void OnShortcutProfileChanged(InputBindingProfile profile) =>
        globalHotKeyProfileCoordinator?.RequestApply(profile);

    private void DispatchShortcutAction(string actionId)
    {
        if (ViewModel is null) return;
        if (actionId == BuiltInInputActions.CaptureScreenshot || actionId == "Software.Screenshot")
        {
            ViewModel.RequestScreenshot();
            return;
        }
        if (shortcutDispatcher is not null)
            _ = shortcutDispatcher.DispatchAsync(actionId, CreateShortcutRuntimeContext(), modelSelectionCancellation.Token);
    }

    private ShortcutRuntimeContext CreateShortcutRuntimeContext()
    {
        var commands = new Dictionary<string, Func<CancellationToken, Task>>(StringComparer.Ordinal);
        if (ViewModel is { } viewModel)
        {
            commands["Software.Camera.Calibrate"] = cancellationToken =>
                viewModel.CalibrateFaceTrackingCommand.ExecuteAsync(null, cancellationToken);
            foreach (SceneDocument scene in viewModel.CurrentSceneWorkspace.Scenes)
            {
                SceneId sceneId = scene.Id;
                commands[$"Software.Scene.Change/{sceneId.Value:N}"] = async cancellationToken =>
                    _ = await OnSceneActivationRequestedAsync(sceneId, cancellationToken).ConfigureAwait(false);
            }
            commands["Software.Scene.Change/scene:none"] = OnSceneDeactivationRequestedAsync;
            foreach (string transformTarget in new[]
            {
                "move:left", "move:right", "move:up", "move:down",
                "rotate:left", "rotate:right", "scale:up", "scale:down",
            })
            {
                string target = transformTarget;
                commands[$"Software.Model.Transform/transform:{target}"] = cancellationToken =>
                    ApplyModelTransformShortcutAsync(target, cancellationToken);
            }
            foreach (ModelCatalogViewModel.ModelCatalogEntryViewModel entry in
                viewModel.ModelCatalog.Entries.Where(static entry => entry.IsSelectable))
            {
                ModelId modelId = entry.Id;
                commands[$"Scene.Model.Change/{modelId.Value}"] = cancellationToken =>
                    AssignMainModelAsync(modelId, cancellationToken);
            }
            commands[$"Software.TrackingSource.Switch/{ShortcutTargetCatalog.NoTrackingSourceId}"] =
                cancellationToken => viewModel.SelectFaceTrackingSourceFromShortcutAsync(
                    ShortcutTargetCatalog.NoTrackingSourceId,
                    cancellationToken);
            if (viewModel.TrackingSourceRegistry is { } trackingRegistry)
            {
                foreach (TrackingSourceDescriptor descriptor in trackingRegistry.GetDescriptors(
                    TrackingChannel.Face,
                    viewModel.IsDeveloperModeEnabled))
                {
                    string sourceId = descriptor.Id;
                    commands[$"Software.TrackingSource.Switch/{sourceId}"] = cancellationToken =>
                        viewModel.SelectFaceTrackingSourceFromShortcutAsync(sourceId, cancellationToken);
                }
            }
            foreach (string targetId in viewModel.ShortcutBackgroundTargetIds)
            {
                string capturedTargetId = targetId;
                commands[$"Scene.Background.Change/{capturedTargetId}"] = cancellationToken =>
                    viewModel.ApplyShortcutBackgroundTargetAsync(capturedTargetId, cancellationToken);
            }
            if (viewModel.PresentedSceneId is SceneId presentedId)
            {
                SceneDocument scene = viewModel.CurrentSceneWorkspace.Scenes.Single(candidate => candidate.Id == presentedId);
                if (scene.MainModel is { } mainModel)
                {
                    bool nextVisible = !mainModel.IsVisible;
                    commands[$"Scene.Source.Toggle/{mainModel.SourceId:N}"] = async cancellationToken =>
                        _ = await viewModel.TrySetMainModelVisibilityAsync(nextVisible, cancellationToken).ConfigureAwait(false);
                }
                foreach (AttachmentInstance attachment in scene.Attachments)
                {
                    Guid sourceId = attachment.SourceId;
                    bool nextVisible = !attachment.IsVisible;
                    commands[$"Scene.Source.Toggle/{sourceId:N}"] = async cancellationToken =>
                        _ = await viewModel.TrySetSceneAttachmentVisibilityAsync(sourceId, nextVisible, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        return new ShortcutRuntimeContext(ownedAnimationSource, commands);
    }

    private async Task ApplyModelTransformShortcutAsync(
        string target,
        CancellationToken cancellationToken)
    {
        if (ownedMainModelCoordinator is null)
        {
            return;
        }

        MainModelInstance? mainModel = ownedMainModelCoordinator.CurrentScene.MainModel;
        if (mainModel is null)
        {
            return;
        }

        const double moveStep = 0.05;
        const double rotationStep = 5;
        const double scaleFactor = 1.05;
        SceneTransform current = mainModel.Transform;
        SceneTransform next = target switch
        {
            "move:left" => new(current.X - moveStep, current.Y, current.Scale, current.RotationDegrees),
            "move:right" => new(current.X + moveStep, current.Y, current.Scale, current.RotationDegrees),
            "move:up" => new(current.X, current.Y - moveStep, current.Scale, current.RotationDegrees),
            "move:down" => new(current.X, current.Y + moveStep, current.Scale, current.RotationDegrees),
            "rotate:left" => new(current.X, current.Y, current.Scale, current.RotationDegrees - rotationStep),
            "rotate:right" => new(current.X, current.Y, current.Scale, current.RotationDegrees + rotationStep),
            "scale:up" => new(current.X, current.Y, current.Scale * scaleFactor, current.RotationDegrees),
            "scale:down" => new(current.X, current.Y, current.Scale / scaleFactor, current.RotationDegrees),
            _ => current,
        };

        await ownedMainModelCoordinator.SetMainModelTransformAsync(
            mainModel.SourceId,
            next,
            cancellationToken).ConfigureAwait(false);
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs args)
    {
        if (IsControlKey(args.Key))
        {
            SetAttachmentAnchorSelectorsVisible(false);
        }
    }

    private void SetAttachmentAnchorSelectorsVisible(bool visible)
    {
        attachmentAnchorSelectorsVisible = visible;
        if (!visible)
        {
            attachmentAnchorPreviewSourceId = null;
            attachmentAnchorPreviewPoint = null;
        }

        UpdateAttachmentAnchorSelectors();
    }

    private static bool IsControlKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl;

    private static InputModifiers ToInputModifiers(KeyModifiers modifiers)
    {
        InputModifiers result = InputModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control)) result |= InputModifiers.Control;
        if (modifiers.HasFlag(KeyModifiers.Alt)) result |= InputModifiers.Alt;
        if (modifiers.HasFlag(KeyModifiers.Shift)) result |= InputModifiers.Shift;
        if (modifiers.HasFlag(KeyModifiers.Meta)) result |= InputModifiers.Meta;
        return result;
    }

    private async Task CaptureScreenshotAsync(ScreenshotCaptureRequest request)
    {
        try
        {
            if (screenshotCoordinator is null
                || modelCanvas.Bounds.Width <= 0
                || modelCanvas.Bounds.Height <= 0)
            {
                throw new InvalidOperationException("Screenshot capture is unavailable.");
            }

            MainWindowViewModel viewModel = ViewModel
                ?? throw new InvalidOperationException("Screenshot capture is unavailable.");

            double scaling = TopLevel.GetTopLevel(modelCanvas)?.RenderScaling ?? 1;
            PixelSize currentPixels = PixelSize.FromSize(modelCanvas.Bounds.Size, scaling);
            PixelSize targetPixels = request.Settings.UseCustomResolution
                ? new PixelSize(request.Settings.WidthPixels, request.Settings.HeightPixels)
                : currentPixels;
            var renderRequest = new ScreenshotRenderRequest(
                modelCanvas.Bounds.Size,
                targetPixels,
                request.Settings.FramingMode,
                request.Settings.UseTransparentBackground,
                viewModel.EffectiveBackground);
            await screenshotCoordinator.CaptureAsync(
                request,
                renderRequest,
                screenshotCancellation.Token);
        }
        catch (OperationCanceledException) when (screenshotCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel?.ReportExternalCommandFailure(exception);
        }
    }

    private void PositionTopLevelWorkspace()
    {
        Size viewport = GetUiViewportSize();
        topLevelWorkspaceHost.Width = viewport.Width;
        topLevelWorkspaceHost.Height = viewport.Height;
    }

    private ModelTransitionOperation BeginModelTransition(string reason)
    {
        return new ModelTransitionOperation(
            Interlocked.Increment(ref modelSelectionGeneration),
            BeginCanvasTransition(reason),
            reason);
    }

    private void EndModelTransition(
        ModelTransitionOperation operation,
        bool replacementReady)
    {
        EndCanvasTransition(
            operation.CanvasGeneration,
            operation.Reason,
            replacementReady);
    }

    private long BeginCanvasTransition(string reason)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            throw new InvalidOperationException("Canvas transitions require the UI thread.");
        }

        long generation = Interlocked.Increment(ref canvasTransitionGeneration);
        Volatile.Write(ref canvasTransitionStartedAt, Stopwatch.GetTimestamp());
        modelTransitionBlank.IsVisible = true;
        CanvasTransitionLog.Started(transitionLogger, generation, reason);
        return generation;
    }

    private void EndCanvasTransition(
        long generation,
        string reason,
        bool replacementReady)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => EndCanvasTransition(
                generation,
                reason,
                replacementReady));
            return;
        }

        if (generation != Volatile.Read(ref canvasTransitionGeneration))
        {
            CanvasTransitionLog.Superseded(transitionLogger, generation, reason);
            return;
        }

        modelTransitionBlank.IsVisible = false;
        long startedAt = Volatile.Read(ref canvasTransitionStartedAt);
        CanvasTransitionLog.Completed(
            transitionLogger,
            generation,
            reason,
            replacementReady,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
    }

    private void EndRenderingBackendTransition(long generation)
    {
        if (Interlocked.CompareExchange(
                ref renderingBackendTransitionGeneration,
                0,
                generation) != generation)
        {
            return;
        }

        EndCanvasTransition(
            generation,
            "RenderingBackend",
            replacementReady: true);
    }

    private void ApplyModelSourceVisibility()
    {
        if (ViewModel is null)
        {
            return;
        }

        bool modelVisible = ViewModel.PresentedSceneId is SceneId sceneId
            ? ViewModel.CurrentSceneWorkspace.Scenes
                .Single(scene => scene.Id == sceneId).MainModel?.IsVisible is not false
            : true;
        modelCanvas.IsVisible = modelVisible;
        MainModelInstance? mainModel = ViewModel.PresentedSceneId is SceneId presentedSceneId
            ? ViewModel.CurrentSceneWorkspace.Scenes
                .Single(scene => scene.Id == presentedSceneId).MainModel
            : null;
        SceneDocument? presentedScene = ViewModel.PresentedSceneId is SceneId attachmentSceneId
            ? ViewModel.CurrentSceneWorkspace.Scenes.Single(scene => scene.Id == attachmentSceneId)
            : null;
        mainModelCanvasInteraction?.Configure(
            mainModel,
            presentedScene?.ReferenceHeight ?? 1080);
        sceneAttachmentCanvasInteraction?.Configure(presentedScene);
    }

    private void OnMainModelTransformPreviewChanged(
        object? sender,
        MainModelTransformPreview preview)
    {
        signalAttachmentPresenter?.UpdateMainModelTransformPreview(
            preview.SourceId,
            preview.Transform);
        sceneAttachmentCanvasInteraction?.UpdateMainModelTransformPreview(preview.Transform);
        UpdateAttachmentAnchorSelectors();
    }

    private void OnMainModelFrameStateChanged(
        ModelRenderFrame frame,
        PixelSize pixelSize,
        ModelRasterTransform rasterTransform,
        double referenceHeight)
    {
        if (ViewModel?.PresentedSceneId is not SceneId sceneId)
        {
            latestMainModelFrame = null;
            return;
        }

        latestMainModelFrame = frame;
        latestMainModelPixelSize = pixelSize;
        latestMainModelRasterTransform = rasterTransform;
        latestMainModelReferenceHeight = referenceHeight;

        MainModelInstance? mainModel = ViewModel.CurrentSceneWorkspace.Scenes
            .Single(scene => scene.Id == sceneId)
            .MainModel;
        if (mainModel is not null)
        {
            signalAttachmentPresenter?.UpdateMainModelFrameState(
                mainModel.SourceId,
                frame,
                pixelSize,
                referenceHeight,
                rasterTransform);
        }

        modelArtMeshHighlightLayer.SetFrameState(frame, pixelSize, rasterTransform);
        UpdateAttachmentAnchorSelectors();
    }

    private void OnAttachmentAnchorSelectorPreviewChanged(
        object? sender,
        AttachmentAnchorSelectorPreviewChanged preview)
    {
        attachmentAnchorPreviewSourceId = preview.SourceId;
        attachmentAnchorPreviewPoint = preview.Point;
        UpdateAttachmentAnchorSelectors();
    }

    private async void OnAttachmentAnchorSelectionRequested(
        object? sender,
        AttachmentAnchorSelectionRequested request)
    {
        if (latestMainModelFrame is null)
        {
            return;
        }

        SignalAttachmentScenePresenter? presenter = signalAttachmentPresenter;
        if (presenter is null
            || !presenter.TryCreateModelBinding(
                request.SourceId,
                request.Point,
                primaryCanvas.Bounds.Size,
                latestMainModelReferenceHeight,
                latestMainModelRasterTransform,
                out AttachmentModelAnchor? anchor,
                out SceneTransform? localTransform)
            || anchor is null
            || localTransform is null)
        {
            SceneAttachmentAnchorLog.SelectionMissed(
                transitionLogger,
                request.SourceId);
            attachmentAnchorPreviewSourceId = null;
            attachmentAnchorPreviewPoint = null;
            UpdateAttachmentAnchorSelectors();
            return;
        }

        try
        {
            presenter.UpdateAttachmentModelBindingPreview(
                request.SourceId,
                anchor,
                localTransform);
            if (ViewModel is null)
            {
                return;
            }

            await ViewModel.TrySetSceneAttachmentModelBindingAsync(
                request.SourceId,
                anchor,
                localTransform,
                modelSelectionCancellation.Token).ConfigureAwait(false);
            attachmentAnchorPreviewSourceId = null;
            attachmentAnchorPreviewPoint = null;
            Dispatcher.UIThread.Post(UpdateAttachmentAnchorSelectors);
        }
        catch (OperationCanceledException) when (modelSelectionCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SceneAttachmentAnchorLog.SelectionFailed(
                transitionLogger,
                request.SourceId,
                exception.GetType().Name);
            attachmentAnchorPreviewSourceId = null;
            attachmentAnchorPreviewPoint = null;
            Dispatcher.UIThread.Post(UpdateAttachmentAnchorSelectors);
        }
    }

    private void UpdateAttachmentAnchorSelectors()
    {
        modelArtMeshHighlightLayer.SetSelectedArtMesh(null);
        if (!attachmentAnchorSelectorsVisible
            || signalAttachmentPresenter is not { } presenter)
        {
            modelArtMeshHighlightLayer.SetAnchorSelectorPoints([]);
            return;
        }

        IReadOnlyList<AttachmentAnchorSelector> selectors = presenter
            .GetAttachmentAnchorSelectors(primaryCanvas.Bounds.Size);
        var labels = new AnchorSelectorVisual[selectors.Count];
        for (int index = 0; index < selectors.Count; index++)
        {
            AttachmentAnchorSelector selector = selectors[index];
            Point point = attachmentAnchorPreviewSourceId == selector.SourceId
                && attachmentAnchorPreviewPoint is { } preview ? preview : selector.Point;
            labels[index] = new AnchorSelectorVisual(point, selector.Label, selector.Kind);
        }

        modelArtMeshHighlightLayer.SetAnchorSelectors(labels);
    }

    private async void OnMainModelTransformCommitRequested(
        object? sender,
        MainModelTransformCommit commit)
    {
        if (ownedMainModelCoordinator is null)
        {
            return;
        }

        try
        {
            await ownedMainModelCoordinator.SetMainModelTransformAsync(
                commit.SourceId,
                commit.Transform,
                modelSelectionCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (modelSelectionCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel?.ReportExternalCommandFailure(exception);
        }
    }

    private async void OnAttachmentTransformCommitRequested(
        object? sender,
        AttachmentTransformCommit commit)
    {
        if (ownedMainModelCoordinator is null)
        {
            return;
        }

        try
        {
            await ownedMainModelCoordinator.SetAttachmentTransformAsync(
                commit.SourceId,
                commit.Transform,
                modelSelectionCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (modelSelectionCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel?.ReportExternalCommandFailure(exception);
        }
    }

    private void OnMainModelDragPhysicsInputRequested(
        object? sender,
        MainModelDragPhysicsInput input)
    {
        if (ownedDragPhysicsSource is null
            || ownedModelController?.Active is not { } active
            || ViewModel?.CurrentMainModelId != active.Id)
        {
            return;
        }

        ownedDragPhysicsSource.Publish(
            active.Id,
            new System.Numerics.Vector2(
                (float)input.NormalizedX,
                (float)input.NormalizedY));
    }

    private async void OnCanvasDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (args.Source is Control source)
        {
            await TryRestoreNavigationAsync(source, CancellationToken.None);
        }
    }

    private async void OnOwnedWindowClosing(object? sender, WindowClosingEventArgs args)
    {
        if (ownedResourcesDisposed)
        {
            return;
        }

        args.Cancel = true;
        if (!ownedResourcesDisposalStarted)
        {
            ownedResourcesDisposalStarted = true;
            modelSelectionCancellation.Cancel();
            ownedViewModel!.Dispose();
            ownedResourcesDisposal = DisposeOwnedResourcesAsync();
        }

        try
        {
            await ownedResourcesDisposal;
        }
        catch (Exception exception)
        {
            MainWindowShutdownLog.ResourceDisposalFailed(
                transitionLogger,
                exception,
                exception.GetType().Name);
        }
        finally
        {
            if (!ownedResourcesDisposed)
            {
                ownedResourcesDisposed = true;
                Close();
            }
        }
    }

    private async Task DisposeOwnedResourcesAsync()
    {
        if (ownedSessionController is not null)
        {
            await ownedSessionController.DisposeAsync();
            ownedSessionController = null;
            MainWindowShutdownLog.TrackingDisposed(transitionLogger);
        }

        await DisposeBackgroundPresentationAsync();

        if (ownedRemoteModelPresenter is not null)
        {
            await ownedRemoteModelPresenter.DisposeAsync();
        }

        if (ownedRemoteModelSources is not null)
        {
            await ownedRemoteModelSources.DisposeAsync();
        }

        if (ownedCollaborationBridge is not null)
        {
            await ownedCollaborationBridge.DisposeAsync();
        }

        if (ownedMainModelCoordinator is not null)
        {
            await ownedMainModelCoordinator.DisposeAsync();
        }

        if (ownedModelDriveController is not null)
        {
            await ownedModelDriveController.DisposeAsync();
        }

        if (ownedCubismEditorOutputController is not null)
        {
            await ownedCubismEditorOutputController.DisposeAsync();
        }

        if (ownedCompositionFramePublisher is not null)
        {
            if (ownedCompositionVideoOutput is not null)
            {
                await ownedCompositionVideoOutput.DisposeAsync();
                ownedCompositionVideoOutput = null;
            }

            await ownedCompositionFramePublisher.DisposeAsync();
            ownedCompositionFramePublisher = null;
        }

        if (ownedCubismEditorOutput is not null)
        {
            await ownedCubismEditorOutput.DisposeAsync();
        }

        if (ownedModelController is not null)
        {
            await ownedModelController.DisposeAsync();
        }

        await ownedViewModel!.DisposeAsync();
        if (ownedSceneRepository is IDisposable disposableRepository)
        {
            disposableRepository.Dispose();
        }

        ownedParameterPriorityStore?.Dispose();
        ownedConsumedInviteStore?.Dispose();
        ownedFriendStore?.Dispose();
        ownedIdentityStore?.Dispose();
        ownedCollaborationProfileStore?.Dispose();
    }

    private void OnCompositionFrameReady(SignalFrame frame)
    {
        ownedCompositionFramePublisher?.Publish(frame);
    }

    private sealed class UnsupportedDeviceSecretProtector : IDeviceSecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => throw new PlatformNotSupportedException();

        public byte[] Unprotect(ReadOnlySpan<byte> protectedValue) =>
            throw new PlatformNotSupportedException();
    }

    private async void OnMainModelAssignmentRequested(ModelId modelId) =>
        await AssignMainModelAsync(modelId, modelSelectionCancellation.Token);

    private async Task AssignMainModelAsync(ModelId modelId, CancellationToken cancellationToken)
    {
        if (ownedMainModelCoordinator is null || ViewModel is null)
        {
            return;
        }

        ModelTransitionOperation transition = BeginModelTransition("ModelSelection");
        bool replacementReady = false;
        IReadOnlyDictionary<Guid, SceneTransform> attachmentWorldTransforms =
            signalAttachmentPresenter?.CaptureFollowingWorldTransforms()
            ?? new Dictionary<Guid, SceneTransform>();
        ViewModel.SetCurrentModelSelection(modelId);
        try
        {
            bool selected = await ownedMainModelCoordinator.AssignAsync(
                modelId,
                attachmentWorldTransforms,
                cancellationToken);
            if (selected && ownedModelController?.Active?.Id == modelId)
            {
                ViewModel.SetCurrentModelSelection(modelId);
                await ViewModel.ApplyResolvedSourceMappingAsync(
                    modelId,
                    cancellationToken);
                await ownedModelController.Active!.Renderer.FirstFrameRendered.WaitAsync(
                    cancellationToken);
                replacementReady = true;
            }
        }
        catch (OperationCanceledException) when (
            transition.ModelGeneration != Volatile.Read(ref modelSelectionGeneration)
            || cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (
            transition.ModelGeneration != Volatile.Read(ref modelSelectionGeneration))
        {
        }
        catch (Exception exception)
        {
            ViewModel.ReportExternalCommandFailure(exception);
        }
        finally
        {
            EndModelTransition(transition, replacementReady);
        }
    }

    private void OnFaceTrackingSourceStatusChanged(object? sender, EventArgs args)
    {
        if (sender is not FaceTrackingSessionController controller || ViewModel is null)
        {
            return;
        }

        TrackingSourceStatus status = controller.SourceStatus;
        Action update = controller.Channel == TrackingChannel.Hand
            ? () => ViewModel.UpdateHandTrackingSourceStatus(status)
            : () => ViewModel.UpdateFaceTrackingSourceStatus(status);
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(update);
            return;
        }

        update();
    }

    private async Task<bool> OnSceneActivationRequestedAsync(
        SceneId sceneId,
        CancellationToken cancellationToken)
    {
        if (ownedMainModelCoordinator is null || ViewModel is null)
        {
            return false;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            modelSelectionCancellation.Token);
        ModelTransitionOperation transition = BeginModelTransition("SceneActivation");
        bool replacementReady = false;
        try
        {
            bool activated = await ownedMainModelCoordinator.ActivateSceneAsync(
                sceneId,
                linkedCancellation.Token);
            ModelId? activeModelId = ownedMainModelCoordinator.CurrentScene.MainModel is
                { ModelAssetId: string modelAssetId }
                ? ModelId.Create(modelAssetId)
                : null;
            ViewModel.SetCurrentModelSelection(activeModelId);
            if (activated
                && activeModelId is ModelId modelId
                && ownedModelController?.Active is { } active
                && active.Id == modelId)
            {
                await ViewModel.ApplyResolvedSourceMappingAsync(modelId, linkedCancellation.Token);
                await active.Renderer.FirstFrameRendered.WaitAsync(linkedCancellation.Token);
            }
            else if (activated)
            {
                await ViewModel.ApplyResolvedSourceMappingAsync(null, linkedCancellation.Token);
            }

            replacementReady = activated;
            return activated;
        }
        finally
        {
            EndModelTransition(transition, replacementReady);
        }
    }

    private async Task OnSceneDeactivationRequestedAsync(CancellationToken cancellationToken)
    {
        if (ownedMainModelCoordinator is null || ViewModel is null)
        {
            return;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            modelSelectionCancellation.Token);
        ModelTransitionOperation transition = BeginModelTransition("SceneDeactivation");
        bool replacementReady = false;
        try
        {
            await ownedMainModelCoordinator.DeactivateSceneAsync(linkedCancellation.Token);
            ViewModel.SetCurrentModelSelection(null);
            await ViewModel.ApplyResolvedSourceMappingAsync(null, linkedCancellation.Token);
            replacementReady = true;
        }
        finally
        {
            EndModelTransition(transition, replacementReady);
        }
    }

    private void OnMainModelAssignmentStateChanged(
        object? sender,
        MainModelAssignmentStateChangedEventArgs args)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyMainModelAssignmentState(
                args.Workspace,
                args.PresentedSceneId,
                args.PendingModelId));
            return;
        }

        ApplyMainModelAssignmentState(args.Workspace, args.PresentedSceneId, args.PendingModelId);
    }

    private void ApplyMainModelAssignmentState(
        SceneWorkspace workspace,
        SceneId? presentedSceneId,
        ModelId? pendingModelId)
    {
        if (ViewModel is null)
        {
            return;
        }

        SceneDocument? presentedScene = presentedSceneId is SceneId sceneId
            ? workspace.Scenes.Single(scene => scene.Id == sceneId)
            : null;
        ModelId? current = presentedScene?.MainModel is
            { ModelAssetId: string modelAssetId }
            ? ModelId.Create(modelAssetId)
            : null;
        ViewModel.UpdateSceneState(
            workspace,
            presentedSceneId,
            current,
            pendingModelId);
        if (!AreAttachmentsEqual(lastPresentedAttachmentScene, presentedScene))
        {
            lastPresentedAttachmentScene = presentedScene;
            ApplySignalAttachments(presentedScene);
        }
        modelCanvas.SetBlurRadius(
            presentedScene?.Effects
                .FirstOrDefault(effect => effect.EffectId == "builtin.blur" && effect.IsEnabled)
                ?.Blur?.Radius);
    }

    private static bool AreAttachmentsEqual(SceneDocument? left, SceneDocument? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Id != right.Id)
        {
            return false;
        }

        return left.ReferenceHeight.Equals(right.ReferenceHeight)
            && Equals(left.MainModel, right.MainModel)
            && left.Attachments.SequenceEqual(right.Attachments);
    }

    private async void OnModelHostOpened(object? sender, EventArgs args)
    {
        if (ownedMainModelCoordinator is null || ViewModel is null)
        {
            return;
        }

        try
        {
            await ViewModel.InitializeTrackingChannelSelectionsAsync(
                ViewModel.IsDeveloperModeEnabled,
                modelSelectionCancellation.Token);
            await ViewModel.ModelCatalog.RefreshAsync(modelSelectionCancellation.Token);
        }
        catch (OperationCanceledException) when (modelSelectionCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            ViewModel.ReportExternalCommandFailure(exception);
            return;
        }

        if (!ViewModel.RestoreActiveSceneOnStartup)
        {
            return;
        }

        ModelTransitionOperation transition = BeginModelTransition("StartupRestore");
        bool replacementReady = false;
        try
        {
            if (!await ownedMainModelCoordinator.RestoreActiveSceneAsync(
                    modelSelectionCancellation.Token))
            {
                return;
            }

            ModelId? modelId = ownedMainModelCoordinator.PresentedSceneId is SceneId sceneId
                && ownedMainModelCoordinator.CurrentWorkspace.Scenes
                    .Single(scene => scene.Id == sceneId).MainModel is
                    { ModelAssetId: string persistedId }
                ? ModelId.Create(persistedId)
                : null;
            ViewModel.SetCurrentModelSelection(modelId);
            if (modelId is ModelId restoredModelId
                && ownedModelController?.Active is { } active
                && active.Id == restoredModelId)
            {
                await active.Renderer.FirstFrameRendered.WaitAsync(
                    modelSelectionCancellation.Token);
            }

            replacementReady = true;
        }
        catch (OperationCanceledException) when (modelSelectionCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel.ReportExternalCommandFailure(exception);
        }
        finally
        {
            EndModelTransition(transition, replacementReady);
        }
    }

    private PixelSize? CaptureModelCanvasPixelSize()
    {
        if (modelCanvas.Bounds.Width <= 0 || modelCanvas.Bounds.Height <= 0)
        {
            return null;
        }

        double scaling = TopLevel.GetTopLevel(modelCanvas)?.RenderScaling ?? 1;
        return PixelSize.FromSize(modelCanvas.Bounds.Size, scaling);
    }

    private static async Task InitializeSourceMappingsAsync(
        IEnumerable<SourceMappingAdapterContext> contexts,
        string transactionsRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            await SourceMappingMutationTransaction.RecoverAsync(
                transactionsRoot,
                cancellationToken).ConfigureAwait(false);
            foreach (SourceMappingAdapterContext context in contexts)
            {
                SourceMappingProfileDocument builtIn = context.CreateBuiltIn();
                await context.Store.InitializeAsync(builtIn, cancellationToken).ConfigureAwait(false);
                SourceMappingProfileDocument saved = await context.Store.LoadSelectedAsync(
                    builtIn,
                    cancellationToken)
                    .ConfigureAwait(false);
                context.ConfigureMapping(saved);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SourceMappingProfileLog.Failed(
                contexts.FirstOrDefault()?.Store.Logger ?? NullLogger.Instance,
                "ConfigureOnStartup",
                exception.GetType().Name);
        }
    }

    private static async Task InitializeParameterPriorityAsync(
        ParameterPriorityStore store,
        ParameterPriorityProfileSource source,
        CancellationToken cancellationToken)
    {
        try
        {
            ParameterPriorityProfile profile = await store.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            source.Apply(profile);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task InitializeCubismEditorMappingAsync(
        CubismEditorMappingStore store,
        CubismEditorOutputController controller,
        ILogger<CubismEditorMappingStore>? logger,
        CancellationToken cancellationToken)
    {
        try
        {
            CubismEditorMappingDocument document = await store.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            controller.UpdateMapping(document.ToMapping());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            CubismEditorMappingStoreLog.InitializationFailed(
                logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CubismEditorMappingStore>.Instance,
                exception);
        }
    }

    private readonly record struct TrackingStatusStructureKey(
        TrackingSourceRunState State,
        string? IntendedSourceId,
        string? ActiveSourceId,
        string? ErrorCode)
    {
        internal static TrackingStatusStructureKey From(TrackingSourceStatus status) => new(
            status.State,
            status.IntendedSourceId,
            status.ActiveSourceId,
            status.ErrorCode);
    }

    private sealed class ControllerRuntimeAdapter(
        MainWindow owner,
        ModelSelectionController controller) : IMainModelRuntimeAdapter
    {
        public async Task ClearAsync(CancellationToken cancellationToken) =>
            await Task.Run(
                    () => controller.ClearAsync(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

        public async Task<MainModelRuntimeLoadResult> LoadAsync(
            ModelId modelId,
            CancellationToken cancellationToken)
        {
            PixelSize? initialFramePixelSize = await Dispatcher.UIThread.InvokeAsync(
                owner.CaptureModelCanvasPixelSize);
            bool selected = await Task.Run(
                () => controller.SelectAsync(
                    modelId,
                    initialFramePixelSize,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
            return new MainModelRuntimeLoadResult(
                selected,
                selected ? controller.Active?.Runtime.CurrentFrame : null,
                initialFramePixelSize ?? new PixelSize(1920, 1080),
                ModelRasterTransform.Identity);
        }
    }

    private sealed class InMemorySceneRepository(SceneWorkspace initial) : ISceneRepository
    {
        private SceneWorkspace current = initial;

        public bool HasPersistedState => true;

        public Task<SceneWorkspace> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(current);
        }

        public Task SaveAsync(SceneWorkspace workspace, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = workspace;
            return Task.CompletedTask;
        }
    }

}

internal static partial class CanvasTransitionLog
{
    [LoggerMessage(
        6280,
        LogLevel.Information,
        "Canvas transition generation {Generation} blanked the model presentation for {Reason}")]
    internal static partial void Started(
        ILogger logger,
        long generation,
        string reason);

    [LoggerMessage(
        6281,
        LogLevel.Information,
        "Canvas transition generation {Generation} completed for {Reason}; replacement ready={ReplacementReady}, duration={DurationMs} ms")]
    internal static partial void Completed(
        ILogger logger,
        long generation,
        string reason,
        bool replacementReady,
        double durationMs);

    [LoggerMessage(
        6282,
        LogLevel.Debug,
        "Canvas transition generation {Generation} for {Reason} was superseded")]
    internal static partial void Superseded(
        ILogger logger,
        long generation,
        string reason);
}

internal static partial class ModelRenderingFallbackNoticeLog
{
    [LoggerMessage(
        6286,
        LogLevel.Information,
        "GPU rendering fallback handling started for generation {Generation} and reason {Reason}")]
    internal static partial void Started(
        ILogger logger,
        long generation,
        ModelRenderingBackendFaultReason reason);

    [LoggerMessage(
        6287,
        LogLevel.Information,
        "GPU rendering fallback preference rollback completed for generation {Generation}; success: {Succeeded}")]
    internal static partial void RollbackCompleted(
        ILogger logger,
        long generation,
        bool succeeded);

    [LoggerMessage(
        6288,
        LogLevel.Debug,
        "GPU rendering fallback handling was cancelled for generation {Generation}")]
    internal static partial void Cancelled(ILogger logger, long generation);

    [LoggerMessage(
        6289,
        LogLevel.Error,
        "GPU rendering fallback handling failed for generation {Generation} with {ExceptionType}")]
    internal static partial void Failed(
        ILogger logger,
        Exception exception,
        long generation,
        string exceptionType);

    [LoggerMessage(
        6284,
        LogLevel.Warning,
        "GPU rendering fallback notice presented for {Reason}")]
    internal static partial void Presented(
        ILogger logger,
        ModelRenderingBackendFaultReason reason);

    [LoggerMessage(
        6285,
        LogLevel.Information,
        "GPU rendering fallback notice cleared before a GPU retry")]
    internal static partial void ClearedForRetry(ILogger logger);
}

internal static partial class MainWindowShutdownLog
{
    [LoggerMessage(6294, LogLevel.Information, "Face tracking sessions were disposed before other window-owned resources")]
    internal static partial void TrackingDisposed(ILogger logger);

    [LoggerMessage(6295, LogLevel.Error, "Window-owned resource disposal failed with {ExceptionType}; window shutdown will continue")]
    internal static partial void ResourceDisposalFailed(
        ILogger logger,
        Exception exception,
        string exceptionType);
}
