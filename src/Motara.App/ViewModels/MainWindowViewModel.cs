using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Diagnostics;
using Motara.App.Backgrounds;
using Motara.App.Collaboration;
using Motara.Collaboration.Identity;
using Motara.Collaboration.Migration;
using Motara.Collaboration.Invites;
using Motara.App.Input;
using Motara.App.Localization;
using Motara.App.Models;
using Motara.App.Parameters;
using Motara.App.Screenshots;
using Motara.App.Rendering;
using Motara.App.Shell;
using Motara.App.Shortcuts;
using Motara.App.Tracking;
using Motara.Core.Formulas;
using Motara.Core.Sessions;
using Motara.ModelLibrary;
using Motara.ModelRuntime.Abstractions;
using Motara.Media;
using Motara.Media.Ndi;
using Motara.Media.Spout2;
using Motara.Output.CubismEditor;
using Motara.Persistence;
using Motara.Scene;
using Motara.Tracking.Abstractions;

namespace Motara.App.ViewModels;

public interface IAsyncCommand : ICommand
{
    bool IsExecuting { get; }

    Task ExecuteAsync(object? parameter, CancellationToken cancellationToken);
}

/// <summary>Coordinates localized shell interaction, immutable session projection, and UI settings persistence.</summary>
public sealed partial class MainWindowViewModel : INotifyPropertyChanged, IDisposable, IAsyncDisposable
{
    private static readonly JsonSerializerOptions MappingJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ISessionController sessionController;
    private readonly IUiSettingsStore settingsStore;
    private readonly ILogOperations logOperations;
    private SessionSnapshot currentSessionSnapshot;
    private ImmutableArray<DestinationViewModel> allDestinations;
    private readonly InputActionRegistry inputActionRegistry;
    private readonly IShortcutStore shortcutStore;
    private readonly SemaphoreSlim settingsMutationGate = new(1, 1);
    private readonly ScreenshotPathProvider screenshotPathProvider = new();
    private readonly PlatformScreenshotFolderLauncher screenshotFolderLauncher = new();
    private readonly CancellationTokenSource snapshotPumpCancellation = new();
    private CancellationTokenSource shortcutReloadCancellation = new();
    private long shortcutReloadGeneration;
    private readonly Task snapshotPump;
    private readonly object disposalGate = new();
    private Task? disposalTask;
    private TaskCompletionSource? settingsMutationsDrained;
    private UiSettings settings;
    private LocalizationManager localization;
    private SceneWorkspace currentSceneWorkspace = SceneWorkspace.CreateDefault();
    private SceneId? presentedSceneId;
    private Guid? selectedSceneSourceId;
    private ModelId? currentMainModelId;
    private double? currentMainModelFrameRate;
    private double? currentWindowPresentationFrameRate;
    private readonly Dictionary<ModelId, int> modelMappingBindingCounts = [];
    private FaceTrackingSessionController? faceTrackingController;
    private FaceTrackingSessionController? handTrackingController;
    private ITrackingChannelSelectionStore? trackingChannelSelectionStore;
    private TrackingSourceRegistry? trackingSourceRegistry;
    private TrackingChannelSelections trackingChannelSelections = TrackingChannelSelections.Default;
    private ILogger trackingSelectionLogger = NullLogger.Instance;
    private Func<IFacialMocapConfigurationViewModel>? createIFacialMocapConfiguration;
    private IIFacialMocapConfigurationStore? iFacialMocapConfigurationStore;
    private Action<Motara.Tracking.iFacialMocap.IFacialMocapOptions>? configureIFacialMocapSource;
    private Func<string, CancellationToken, Task<bool>>? selectIFacialMocapSourceAsync;
    private ILogger iFacialMocapSelectionLogger = NullLogger.Instance;
    private Func<OpenSeeFaceConfigurationViewModel>? createOpenSeeFaceConfiguration;
    private ImmutableDictionary<string, SourceMappingAdapterContext> sourceMappingContexts =
        ImmutableDictionary<string, SourceMappingAdapterContext>.Empty.WithComparers(StringComparer.Ordinal);
    private SourceMappingProfileDocument? sourceMappingAppliedBaseline;
    private SourceMappingProfileDocument? resolvedSourceMapping;
    private string? sourceMappingsRoot;
    private string? modelsRoot;
    private string? scenesRoot;
    private Func<ModelId, CancellationToken, Task<ModelCapabilities?>>? modelCapabilitiesProvider;
    private readonly ModelParameterMappingService modelParameterMappingService = new();
    private Func<ModelParameterMappingDocument, CancellationToken, Task>? modelParameterMappingSave;
    private ModelParameterObservationSource? modelParameterObservationSource;
    private Func<ModelCatalogViewModel.ModelCatalogEntryViewModel, CancellationToken, Task<ModelPhysicsConfiguration>>?
        modelPhysicsLoad;
    private Func<ModelCatalogViewModel.ModelCatalogEntryViewModel, ModelPhysicsConfiguration, CancellationToken, Task>?
        modelPhysicsSave;
    private ILogger? modelPhysicsSettingsLogger;
    private Func<ModelCatalogViewModel.ModelCatalogEntryViewModel, CancellationToken, Task<ModelBasicSettingsDocument>>?
        modelBasicSettingsLoad;
    private Func<ModelCatalogViewModel.ModelCatalogEntryViewModel, ModelBasicSettingsUpdate, CancellationToken, Task>?
        modelBasicSettingsSave;
    private ILogger? modelBasicSettingsLogger;
    private Func<CancellationToken, Task<ParameterPriorityProfile>>? parameterPriorityLoad;
    private Func<ParameterPriorityProfile, CancellationToken, Task>? parameterPrioritySave;
    private Action<ParameterPriorityProfile>? parameterPriorityPublish;
    private ILogger? parameterPriorityLogger;
    private TrackingSourceStatus faceTrackingSourceStatus = TrackingSourceStatus.Empty;
    private TrackingSourceStatus handTrackingSourceStatus = TrackingSourceStatus.Empty;
    private int activeSettingsMutations;
    private int disposed;
    private CubismEditorOutputTarget? cubismEditorOutput;
    private InputBindingWorkspaceViewModel? shortcutMenuWorkspace;
    private ImmutableDictionary<string, BackgroundDefinition?> shortcutBackgroundTargets =
        ImmutableDictionary<string, BackgroundDefinition?>.Empty
            .WithComparers(StringComparer.Ordinal)
            .Add(ShortcutTargetCatalog.SharedBackgroundId, null);

    internal event Action<InputBindingProfile>? ShortcutProfileChanged;
    internal event Action? ShortcutMenuInvalidated;
    private Func<CancellationToken, Task<CubismEditorMappingDocument>>? cubismEditorMappingLoad;
    private Func<CubismEditorMappingDocument, CancellationToken, Task>? cubismEditorMappingSave;
    private ILogger? cubismEditorMappingLogger;
    private ILogger backgroundSettingsLogger = NullLogger.Instance;
    private IBackgroundAssetStore backgroundAssetStore;
    private IBackgroundRecentAssetStore backgroundRecentAssetStore;
    private ILogger<BackgroundEditorViewModel> backgroundEditorLogger =
        NullLogger<BackgroundEditorViewModel>.Instance;
    private ILogger<BackgroundPresenter> backgroundPresenterLogger =
        NullLogger<BackgroundPresenter>.Instance;
    private readonly VideoSignalRegistry videoSignalRegistry;
    private CompositionVideoOutputController? compositionVideoOutput;

    private MainWindowViewModel(
        ISessionController sessionController,
        IUiSettingsStore settingsStore,
        UiSettings settings,
        LocalizationManager localization,
        ModelCatalogViewModel modelCatalog,
        string modelsRoot,
        ILogOperations? logOperations,
        InputActionRegistry? inputActionRegistry = null,
        IShortcutStore? shortcutStore = null)
    {
        this.sessionController = sessionController;
        this.settingsStore = settingsStore;
        this.settings = settings;
        this.logOperations = logOperations ?? new UnavailableLogOperations
        {
            MinimumLevel = settings.DiagnosticLogLevel,
        };
        this.localization = localization;
        Navigation = new NavigationState(
            settings.IsDeveloperModeEnabled,
            settings.IsNavigationRailVisible);
        TopLevelWorkspace = new TopLevelWorkspaceState();
        currentSessionSnapshot = sessionController.Current;
        ModelCatalog = modelCatalog;
        ModelCatalog.PropertyChanged += OnModelCatalogPropertyChanged;
        var paths = new AppDataPaths();
        backgroundAssetStore = new BackgroundAssetStore(
            paths,
            NullLogger<BackgroundAssetStore>.Instance);
        backgroundRecentAssetStore = new BackgroundRecentAssetStore(
            paths,
            NullLogger<BackgroundRecentAssetStore>.Instance);
        videoSignalRegistry = new VideoSignalRegistry(
        [
            new Spout2ProtocolAdapter(),
            new NdiProtocolAdapter(),
        ]);
        this.inputActionRegistry = inputActionRegistry ?? BuiltInInputActions.CreateRegistry();
        this.shortcutStore = shortcutStore ?? new ShortcutStore(paths.InputBindingsPath);
        allDestinations = CreateDestinations(localization);

        SelectDestinationCommand = new DelegateCommand(
            parameter => SelectDestination((NavigationDestination)parameter!));
        SelectMenuNodeCommand = new DelegateCommand(
            parameter =>
            {
                var selection = (MenuSelection)parameter!;
                SelectMenuNode(selection.Level, selection.NodeId);
            });
        CloseNavigationCommand = new AsyncDelegateCommand(CloseNavigationAsync, ReportCommandFailure);
        RestoreNavigationCommand = new AsyncDelegateCommand(RestoreNavigationAsync, ReportCommandFailure);
        SetDeveloperModeCommand = new AsyncDelegateCommand(
            (parameter, cancellationToken) => SetDeveloperModeAsync((bool)parameter!, cancellationToken),
            ReportCommandFailure);
        SetApplicationLanguageCommand = new AsyncDelegateCommand(
            (parameter, cancellationToken) => SetApplicationLanguageAsync(
                (ApplicationLanguage)parameter!,
                cancellationToken),
            ReportCommandFailure);
        StartSessionCommand = new AsyncDelegateCommand(StartSessionAsync, ReportCommandFailure);
        StopSessionCommand = new AsyncDelegateCommand(StopSessionAsync, ReportCommandFailure);
        SetDiagnosticLogLevelCommand = new AsyncDelegateCommand(
            (parameter, cancellationToken) => SetDiagnosticLogLevelAsync(
                (DiagnosticLogLevel)parameter!,
                cancellationToken),
            ReportCommandFailure);
        OpenLogsFolderCommand = new AsyncDelegateCommand(
            logOperations: this.logOperations,
            operation: static (operations, cancellationToken) =>
                operations.OpenLogsFolderAsync(cancellationToken),
            reportFailure: ReportCommandFailure);
        ExportDiagnosticLogsCommand = new AsyncDelegateCommand(
            logOperations: this.logOperations,
            operation: static (operations, cancellationToken) =>
                operations.ExportDiagnosticLogsAsync(cancellationToken),
            reportFailure: ReportCommandFailure);
        var sceneSelectionCommand = new LatestWinsAsyncDelegateCommand(
            async (parameter, cancellationToken) =>
            {
                if (parameter is SceneId sceneId)
                {
                    if (SceneActivationRequested is null)
                    {
                        throw new InvalidOperationException("Scene activation is unavailable.");
                    }

                    _ = await SceneActivationRequested(sceneId, cancellationToken);
                    return;
                }

                if (SceneDeactivationRequested is null)
                {
                    throw new InvalidOperationException("Scene deactivation is unavailable.");
                }

                await SceneDeactivationRequested(cancellationToken);
            },
            ReportCommandFailure);
        ActivateSceneCommand = sceneSelectionCommand;
        SelectNoSceneCommand = sceneSelectionCommand;
        CreateSceneCommand = new DelegateCommand(_ => OpenCreateScenePrompt());
        RenameActiveSceneCommand = new DelegateCommand(_ => OpenRenameScenePrompt());
        DeleteActiveSceneCommand = new DelegateCommand(_ => OpenDeleteSceneConfirmation());
        OrganizeActiveSceneCommand = new AsyncDelegateCommand(
            OrganizeActiveSceneAsync,
            ReportCommandFailure);
        SelectMainModelSourceCommand = new DelegateCommand(SelectSceneSource);
        SelectSceneSourceCommand = SelectMainModelSourceCommand;
        SetMainModelTrackingCommand = new AsyncDelegateCommand(
            (parameter, cancellationToken) => SetMainModelTrackingAsync(
                (MainModelTrackingMode)parameter!,
                cancellationToken),
            ReportCommandFailure);
        SelectFaceTrackingSourceCommand = new AsyncDelegateCommand(
            (parameter, cancellationToken) => SelectFaceTrackingSourceAsync(
                parameter as string,
                cancellationToken),
            ReportCommandFailure);
        SelectTrackingSourceCommand = new AsyncDelegateCommand(
            (parameter, cancellationToken) =>
            {
                var selection = (TrackingSourceSelection)parameter!;
                return SelectTrackingSourceAsync(
                    selection.Channel,
                    selection.SourceId,
                    cancellationToken);
            },
            ReportCommandFailure);
        CalibrateFaceTrackingCommand = new AsyncDelegateCommand(
            CalibrateFaceTrackingAsync,
            ReportCommandFailure);
        OpenIFacialMocapConfigurationCommand = new AsyncDelegateCommand(
            OpenIFacialMocapConfigurationAsync,
            ReportCommandFailure);
        OpenOpenSeeFaceConfigurationCommand = new AsyncDelegateCommand(
            OpenOpenSeeFaceConfigurationAsync,
            ReportCommandFailure);
        OpenSourceMappingEditorCommand = new AsyncDelegateCommand(
            OpenSourceMappingEditorAsync,
            ReportCommandFailure);
        OpenSceneEffectEditorCommand = new DelegateCommand(_ => OpenSceneEffectEditor());
        OpenScreenshotWorkspaceCommand = new DelegateCommand(_ => OpenScreenshotWorkspace());
        OpenCubismEditorOutputSettingsCommand = new DelegateCommand(_ => OpenCubismEditorOutputSettings());
        OpenCubismEditorMappingCommand = new AsyncDelegateCommand(
            OpenCubismEditorMappingAsync,
            ReportCommandFailure);
        OpenWindowPresentationSettingsCommand = new DelegateCommand(_ => OpenWindowPresentationSettings());
        OpenSpout2VideoOutputSettingsCommand = new DelegateCommand(_ => OpenVideoOutputSettings(VideoSignalProtocol.Spout2));
        OpenNdiVideoOutputSettingsCommand = new DelegateCommand(_ => OpenVideoOutputSettings(VideoSignalProtocol.Ndi));
        OpenSceneAttachmentWorkspaceCommand = new DelegateCommand(_ => OpenSceneAttachmentWorkspace());
        OpenGlobalBackgroundEditorCommand = new DelegateCommand(_ => OpenGlobalBackgroundEditor());
        OpenSceneBackgroundEditorCommand = new DelegateCommand(_ => OpenSceneBackgroundEditor());
        SetSceneBackgroundModeCommand = new AsyncDelegateCommand(
            (parameter, cancellationToken) => SetSceneBackgroundModeAsync(
                (bool)parameter!,
                cancellationToken),
            ReportCommandFailure);
        OpenIdentityMigrationWorkspaceCommand = new DelegateCommand(
            parameter => OpenIdentityMigrationWorkspace((IdentityMigrationMode)parameter!));
        OpenModelParameterMappingCommand = new AsyncDelegateCommand(
            OpenModelParameterMappingAsync,
            ReportCommandFailure);
        OpenModelPhysicsSettingsCommand = new AsyncDelegateCommand(
            OpenModelPhysicsSettingsAsync,
            ReportCommandFailure);
        OpenModelBasicSettingsCommand = new AsyncDelegateCommand(
            OpenModelBasicSettingsAsync,
            ReportCommandFailure);
        OpenModelAdvancedSettingsCommand = new DelegateCommand(OpenModelAdvancedSettings);
        OpenParameterPriorityWorkspaceCommand = new AsyncDelegateCommand(
            OpenParameterPriorityWorkspaceAsync,
            ReportCommandFailure);
        SetModelCatalogLayoutModeCommand = new AsyncDelegateCommand(
            (parameter, cancellationToken) => SetModelCatalogLayoutModeAsync(
                (ModelCatalogLayoutMode)parameter!,
                cancellationToken),
            ReportCommandFailure);
        AssignViewedModelCommand = new DelegateCommand(_ => AssignViewedModel());
        snapshotPump = PumpSnapshotsAsync(snapshotPumpCancellation.Token);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal event Action<ModelId>? ModelAssignmentRequested;

    internal event Action<ModelRenderingBackendPreference>? RenderingBackendPreferenceChanged;

    internal Func<SceneId, CancellationToken, Task<bool>>? SceneActivationRequested { get; set; }

    internal Func<CancellationToken, Task>? SceneDeactivationRequested { get; set; }

    internal Func<string, CancellationToken, Task<SceneId>>? SceneCreationRequested { get; set; }

    internal Func<SceneId, string, CancellationToken, Task>? SceneRenameRequested { get; set; }

    internal Func<SceneId, CancellationToken, Task>? SceneDeletionRequested { get; set; }

    internal Func<bool, CancellationToken, Task>? MainModelVisibilityRequested { get; set; }

    internal Func<bool, CancellationToken, Task>? MainModelLockRequested { get; set; }

    internal Func<Guid, bool, CancellationToken, Task<bool>>? SceneAttachmentVisibilityRequested { get; set; }

    internal Func<Guid, bool, CancellationToken, Task<bool>>? SceneAttachmentLockRequested { get; set; }

    internal Func<Guid, SceneTransform, CancellationToken, Task<bool>>? SceneAttachmentTransformRequested { get; set; }

    internal Func<Guid, AttachmentMountMode, CancellationToken, Task<bool>>? SceneAttachmentMountModeRequested { get; set; }

    internal Func<Guid, AttachmentModelAnchor, SceneTransform, CancellationToken, Task<bool>>?
        SceneAttachmentModelBindingRequested { get; set; }

    internal Func<Guid, AttachmentPlacement, int, CancellationToken, Task>? SceneAttachmentMoveRequested { get; set; }

    internal Func<int, CancellationToken, Task>? MainModelMoveRequested { get; set; }

    internal Func<Guid, string, CancellationToken, Task>? SceneAttachmentDisplayNameRequested { get; set; }

    internal Func<Guid, CancellationToken, Task>? SceneAttachmentRemovalRequested { get; set; }

    internal Func<MainModelTrackingMode, TrackingChannelBindings, string?, CancellationToken, Task>?
        MainModelTrackingRequested { get; set; }

    internal Func<SceneEffectInstance?, CancellationToken, Task>? SceneBlurEffectRequested { get; set; }

    internal Func<BackgroundDefinition?, CancellationToken, Task>? SceneBackgroundOverrideRequested { get; set; }

    internal Func<VideoSignalProtocol, VideoSignalSourceDescriptor, CancellationToken, Task>? SceneSignalAttachmentRequested { get; set; }

    internal Func<string, string, string, BackgroundVideoOptions, AttachmentPlacement, CancellationToken, Task>? SceneAttachmentRequested { get; set; }

    internal event Action<ScreenshotCaptureRequest>? ScreenshotRequested;

    public NavigationState Navigation { get; }

    public TopLevelWorkspaceState TopLevelWorkspace { get; }

    public SessionSnapshot CurrentSessionSnapshot => currentSessionSnapshot;

    public ModelCatalogViewModel ModelCatalog { get; }

    internal CollaborationWorkspaceViewModel? CollaborationWorkspace { get; private set; }

    private CollaborationIdentityArchiveService? collaborationIdentityArchiveService;

    public LocalizationManager Localization => localization;

    public ApplicationLanguage ApplicationLanguage => settings.ApplicationLanguage;

    public ImmutableArray<DestinationViewModel> Destinations => Navigation.VisibleDestinations
        .Select(id => allDestinations.Single(destination => destination.Id == id))
        .ToImmutableArray();

    public bool IsDeveloperModeEnabled => settings.IsDeveloperModeEnabled;

    public DiagnosticLogLevel DiagnosticLogLevel => settings.DiagnosticLogLevel;

    public ModelRenderingBackendPreference ModelRenderingBackendPreference =>
        settings.ModelRenderingBackendPreference;

    public bool RestoreActiveSceneOnStartup => settings.RestoreActiveSceneOnStartup;

    public ModelCatalogLayoutMode ModelCatalogLayoutMode => settings.ModelCatalogLayoutMode;

    public int WindowWidthPixels => settings.WindowWidthPixels;

    public int WindowHeightPixels => settings.WindowHeightPixels;

    public double ContentScale => settings.ContentScale;

    public ContentScaleMode ContentScaleMode => settings.ContentScaleMode;

    public FrameRateMode FrameRateMode => settings.FrameRateMode;

    public ScreenshotSettings ScreenshotSettings => settings.Screenshot;

    internal BackgroundDefinition GlobalBackground => settings.GlobalBackground;

    internal BackgroundDefinition? CurrentSceneBackgroundOverride => PresentedSceneId is SceneId sceneId
        ? currentSceneWorkspace.Scenes.Single(scene => scene.Id == sceneId).BackgroundOverride
        : null;

    internal ResolvedBackground EffectiveBackground => BackgroundResolver.Resolve(
        GlobalBackground,
        CurrentSceneBackgroundOverride,
        PresentedSceneId is not null);

    public bool IsWindowSizeLocked => settings.IsWindowSizeLocked;

    internal ModelId? CurrentMainModelId => currentMainModelId;

    internal double? CurrentMainModelFrameRate => currentMainModelFrameRate;

    internal double? CurrentWindowPresentationFrameRate => currentWindowPresentationFrameRate;

    internal int CurrentModelMappingBindingCount => CurrentMainModelId is ModelId modelId
        && modelMappingBindingCounts.TryGetValue(modelId, out int count)
            ? count
            : 0;

    internal SceneWorkspace CurrentSceneWorkspace => currentSceneWorkspace;

    internal SceneId? PresentedSceneId => presentedSceneId;

    internal Guid? SelectedSceneSourceId => selectedSceneSourceId;

    internal bool IsMainModelSourceSelected =>
        PresentedSceneId is SceneId sceneId
            && currentSceneWorkspace.Scenes.Single(scene => scene.Id == sceneId).MainModel is
                { SourceId: Guid sourceId }
            && sourceId == selectedSceneSourceId;

    internal TrackingSourceStatus FaceTrackingSourceStatus => faceTrackingSourceStatus;

    internal TrackingSourceStatus HandTrackingSourceStatus => handTrackingSourceStatus;

    internal TrackingChannelSelections TrackingChannelSelections => trackingChannelSelections;

    internal bool RememberFaceTrackingOnStartup => settings.RememberFaceTrackingOnStartup;

    internal FaceTrackingSessionController? FaceTrackingController => faceTrackingController;

    internal FaceTrackingSessionController? HandTrackingController => handTrackingController;

    internal TrackingSourceRegistry? TrackingSourceRegistry => trackingSourceRegistry;

    public Exception? LastCommandException { get; private set; }

    public string? CommandErrorMessage { get; private set; }

    internal ModelSourceMappingReviewViewModel? ModelMappingReview { get; private set; }

    internal ModelConfigurationReviewViewModel? ModelConfigurationReview { get; private set; }

    public ICommand SelectDestinationCommand { get; }

    public ICommand SelectMenuNodeCommand { get; }

    public IAsyncCommand CloseNavigationCommand { get; }

    public IAsyncCommand RestoreNavigationCommand { get; }

    public IAsyncCommand SetDeveloperModeCommand { get; }

    public IAsyncCommand SetApplicationLanguageCommand { get; }

    public IAsyncCommand StartSessionCommand { get; }

    public IAsyncCommand StopSessionCommand { get; }

    public IAsyncCommand SetDiagnosticLogLevelCommand { get; }

    public IAsyncCommand OpenLogsFolderCommand { get; }

    public IAsyncCommand ExportDiagnosticLogsCommand { get; }

    public IAsyncCommand ActivateSceneCommand { get; }

    public IAsyncCommand SelectNoSceneCommand { get; }

    public ICommand CreateSceneCommand { get; }

    public ICommand RenameActiveSceneCommand { get; }

    public ICommand DeleteActiveSceneCommand { get; }

    public IAsyncCommand OrganizeActiveSceneCommand { get; }

    internal string? SceneOrganizationStatusText { get; private set; }

    public ICommand SelectMainModelSourceCommand { get; }

    public ICommand SelectSceneSourceCommand { get; }

    internal IAsyncCommand SetMainModelTrackingCommand { get; }

    public IAsyncCommand SelectFaceTrackingSourceCommand { get; }

    public IAsyncCommand SelectTrackingSourceCommand { get; }

    internal IAsyncCommand CalibrateFaceTrackingCommand { get; }

    internal IAsyncCommand OpenIFacialMocapConfigurationCommand { get; }

    internal IAsyncCommand OpenOpenSeeFaceConfigurationCommand { get; }

    internal ICommand OpenSourceMappingEditorCommand { get; }

    internal InputBindingWorkspaceViewModel? ShortcutMenuWorkspace => shortcutMenuWorkspace;

    internal ICommand OpenSceneEffectEditorCommand { get; }

    internal ICommand OpenScreenshotWorkspaceCommand { get; }

    internal ICommand OpenCubismEditorOutputSettingsCommand { get; }

    internal IAsyncCommand OpenCubismEditorMappingCommand { get; }

    internal ICommand OpenWindowPresentationSettingsCommand { get; }

    internal ICommand OpenSpout2VideoOutputSettingsCommand { get; }

    internal ICommand OpenNdiVideoOutputSettingsCommand { get; }

    internal ICommand OpenSceneAttachmentWorkspaceCommand { get; }

    internal ICommand OpenGlobalBackgroundEditorCommand { get; }

    internal bool IsSpout2VideoOutputEnabled =>
        compositionVideoOutput?.IsEnabled(VideoSignalProtocol.Spout2) == true;

    internal bool IsNdiVideoOutputEnabled =>
        compositionVideoOutput?.IsEnabled(VideoSignalProtocol.Ndi) == true;

    internal ICommand OpenSceneBackgroundEditorCommand { get; }

    internal IAsyncCommand SetSceneBackgroundModeCommand { get; }

    internal ICommand OpenIdentityMigrationWorkspaceCommand { get; }

    internal bool IsCubismEditorOutputEnabled => cubismEditorOutput?.IsActive == true;

    internal bool IsCubismEditorAlwaysOutput => settings.CubismEditor.AlwaysOutput;

    internal string CubismEditorOutputStatusText => cubismEditorOutput?.Status.State switch
    {
        CubismEditorOutputState.Connecting => Localization.GetString("Menu.Output.Cubism.Status.Connecting"),
        CubismEditorOutputState.EditorUnavailable => Localization.GetString("Menu.Output.Cubism.Status.Unavailable"),
        CubismEditorOutputState.WaitingForApproval => Localization.GetString("Menu.Output.Cubism.Status.WaitingApproval"),
        CubismEditorOutputState.ModelUnavailable => Localization.GetString("Menu.Output.Cubism.Status.ModelUnavailable"),
        CubismEditorOutputState.Connected => Localization.GetString("Menu.Output.Cubism.Status.Connected"),
        CubismEditorOutputState.Reconnecting => Localization.GetString("Menu.Output.Cubism.Status.Reconnecting"),
        CubismEditorOutputState.ProtocolError => Localization.GetString("Menu.Output.Cubism.Status.ProtocolError"),
        _ => Localization.GetString("Menu.Output.NotEnabled"),
    };

    internal string CubismEditorOutputEndpointText => cubismEditorOutput?.Status.Endpoint.AbsoluteUri
        ?? Localization.GetString("Menu.Common.NoData");

    internal string CubismEditorOutputModelUidText => cubismEditorOutput?.Status.ModelUid
        ?? Localization.GetString("Menu.Common.NoData");

    internal MenuInformationState CubismEditorOutputInformationState => cubismEditorOutput?.Status.State switch
    {
        CubismEditorOutputState.Connected => MenuInformationState.Positive,
        CubismEditorOutputState.ProtocolError => MenuInformationState.Error,
        _ => MenuInformationState.Neutral,
    };

    internal IAsyncCommand OpenModelParameterMappingCommand { get; }

    internal IAsyncCommand OpenModelPhysicsSettingsCommand { get; }

    internal IAsyncCommand OpenModelBasicSettingsCommand { get; }

    internal ICommand OpenModelAdvancedSettingsCommand { get; }

    internal IAsyncCommand OpenParameterPriorityWorkspaceCommand { get; }

    internal bool CanEditCurrentModelMapping =>
        CurrentMainModelId is not null && modelCapabilitiesProvider is not null;

    internal IAsyncCommand SetModelCatalogLayoutModeCommand { get; }

    internal ICommand AssignViewedModelCommand { get; }

    internal InputActionRegistry InputActionRegistry => inputActionRegistry;

    internal void AttachCubismEditorOutput(CubismEditorOutputTarget output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (cubismEditorOutput is not null)
        {
            cubismEditorOutput.StatusChanged -= OnCubismEditorOutputStatusChanged;
        }

        cubismEditorOutput = output;
        output.StatusChanged += OnCubismEditorOutputStatusChanged;
        OnPropertyChanged(nameof(IsCubismEditorOutputEnabled));
        OnPropertyChanged(nameof(CubismEditorOutputStatusText));
        OnPropertyChanged(nameof(CubismEditorOutputEndpointText));
        OnPropertyChanged(nameof(CubismEditorOutputModelUidText));
        OnPropertyChanged(nameof(CubismEditorOutputInformationState));
    }

    internal void AttachCompositionVideoOutput(CompositionVideoOutputController output)
    {
        compositionVideoOutput = output ?? throw new ArgumentNullException(nameof(output));
        OnPropertyChanged(nameof(IsSpout2VideoOutputEnabled));
        OnPropertyChanged(nameof(IsNdiVideoOutputEnabled));
    }

    internal Task<bool> TrySetVideoSignalOutputEnabledAsync(
        VideoSignalProtocol protocol,
        bool enabled,
        CancellationToken cancellationToken) =>
        TryExecuteAsync(async () =>
        {
            CompositionVideoOutputController output = compositionVideoOutput
                ?? throw new InvalidOperationException("Video output is unavailable.");
            await output.SetEnabledAsync(protocol, enabled, cancellationToken).ConfigureAwait(false);
            OnPropertyChanged(protocol == VideoSignalProtocol.Spout2
                ? nameof(IsSpout2VideoOutputEnabled)
                : nameof(IsNdiVideoOutputEnabled));
        }, cancellationToken);

    private void OpenVideoOutputSettings(VideoSignalProtocol protocol)
    {
        CompositionVideoOutputController output = compositionVideoOutput
            ?? throw new InvalidOperationException("Video output is unavailable.");
        var workspace = new CompositionVideoOutputSettingsViewModel(
            protocol,
            output.GetSettings(protocol),
            (settings, cancellationToken) => output.ApplySettingsAsync(protocol, settings, cancellationToken),
            TopLevelWorkspace.Close);
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent($"video-output.{protocol}", workspace),
            protocol == VideoSignalProtocol.Spout2
                ? "menu.output.video.spout2"
                : "menu.output.video.ndi");
    }

    internal void AttachCubismEditorMapping(
        Func<CancellationToken, Task<CubismEditorMappingDocument>> loadAsync,
        Func<CubismEditorMappingDocument, CancellationToken, Task> saveAsync,
        ILogger? logger = null)
    {
        cubismEditorMappingLoad = loadAsync ?? throw new ArgumentNullException(nameof(loadAsync));
        cubismEditorMappingSave = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
        cubismEditorMappingLogger = logger;
    }

    internal Task<bool> TrySetCubismEditorOutputEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken) =>
        TryExecuteAsync(async () =>
        {
            CubismEditorOutputTarget output = cubismEditorOutput
                ?? throw new InvalidOperationException("Cubism Editor output is unavailable.");
            if (enabled)
            {
                await output.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await output.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken);

    internal Task<bool> TrySetCubismEditorAlwaysOutputAsync(
        bool alwaysOutput,
        CancellationToken cancellationToken) =>
        TryExecuteAsync(
            () => ApplyCubismEditorOutputSettingsAsync(
                settings.CubismEditor with { AlwaysOutput = alwaysOutput },
                cancellationToken),
            cancellationToken);

    internal void AttachModelParameterMapping(
        Func<ModelId, CancellationToken, Task<ModelCapabilities?>> provider,
        Func<ModelParameterMappingDocument, CancellationToken, Task>? saveAsync = null,
        ModelParameterObservationSource? observationSource = null)
    {
        modelCapabilitiesProvider = provider ?? throw new ArgumentNullException(nameof(provider));
        modelParameterMappingSave = saveAsync;
        modelParameterObservationSource = observationSource;
        if (ModelCatalog.ViewedEntry is not null)
        {
            PrepareViewedModelMappingReview();
        }
    }

    internal void AttachModelPhysicsSettings(
        Func<ModelCatalogViewModel.ModelCatalogEntryViewModel, CancellationToken, Task<ModelPhysicsConfiguration>> loadAsync,
        Func<ModelCatalogViewModel.ModelCatalogEntryViewModel, ModelPhysicsConfiguration, CancellationToken, Task> saveAsync,
        ILogger? logger = null)
    {
        modelPhysicsLoad = loadAsync ?? throw new ArgumentNullException(nameof(loadAsync));
        modelPhysicsSave = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
        modelPhysicsSettingsLogger = logger;
    }

    internal void AttachModelBasicSettings(
        Func<ModelCatalogViewModel.ModelCatalogEntryViewModel, CancellationToken, Task<ModelBasicSettingsDocument>> loadAsync,
        Func<ModelCatalogViewModel.ModelCatalogEntryViewModel, ModelBasicSettingsUpdate, CancellationToken, Task> saveAsync,
        ILogger? logger = null)
    {
        modelBasicSettingsLoad = loadAsync ?? throw new ArgumentNullException(nameof(loadAsync));
        modelBasicSettingsSave = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
        modelBasicSettingsLogger = logger;
    }

    internal void AttachParameterPriority(
        Func<CancellationToken, Task<ParameterPriorityProfile>> loadAsync,
        Func<ParameterPriorityProfile, CancellationToken, Task> saveAsync,
        Action<ParameterPriorityProfile> publish,
        ILogger? logger = null)
    {
        parameterPriorityLoad = loadAsync ?? throw new ArgumentNullException(nameof(loadAsync));
        parameterPrioritySave = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
        parameterPriorityPublish = publish ?? throw new ArgumentNullException(nameof(publish));
        parameterPriorityLogger = logger;
    }

    internal void AttachBackgroundSettingsLogger(ILogger? logger) =>
        backgroundSettingsLogger = logger ?? NullLogger.Instance;

    internal void AttachBackgroundAssetStore(
        IBackgroundAssetStore store,
        IBackgroundRecentAssetStore? recentStore = null,
        ILogger<BackgroundEditorViewModel>? logger = null,
        ILogger<BackgroundPresenter>? presenterLogger = null)
    {
        backgroundAssetStore = store ?? throw new ArgumentNullException(nameof(store));
        backgroundRecentAssetStore = recentStore ?? backgroundRecentAssetStore;
        backgroundEditorLogger = logger ?? NullLogger<BackgroundEditorViewModel>.Instance;
        backgroundPresenterLogger = presenterLogger ?? NullLogger<BackgroundPresenter>.Instance;
    }

    internal IBackgroundAssetStore BackgroundAssetStore => backgroundAssetStore;

    internal VideoSignalRegistry VideoSignalRegistry => videoSignalRegistry;

    internal ILogger<BackgroundPresenter> BackgroundPresenterLogger => backgroundPresenterLogger;

    internal Task SetGlobalBackgroundAsync(
        BackgroundDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return MutateSettingsAsync(
            current => current.GlobalBackground == definition
                ? current
                : current with { GlobalBackground = definition },
            () =>
            {
                OnPropertyChanged(nameof(GlobalBackground));
                OnPropertyChanged(nameof(EffectiveBackground));
                BackgroundStateLog.GlobalApplied(backgroundSettingsLogger, definition.Kind);
            },
            cancellationToken);
    }

    internal async Task SetSceneBackgroundModeAsync(
        bool custom,
        CancellationToken cancellationToken)
    {
        BackgroundDefinition? next = custom
            ? CurrentSceneBackgroundOverride ?? EffectiveBackground.Definition
            : null;
        await SetSceneBackgroundAsync(next, cancellationToken).ConfigureAwait(false);
        BackgroundStateLog.SceneModeApplied(backgroundSettingsLogger, custom);
    }

    internal async Task SetSceneBackgroundAsync(
        BackgroundDefinition? definition,
        CancellationToken cancellationToken)
    {
        if (PresentedSceneId is null)
        {
            throw new InvalidOperationException("A presented scene is required.");
        }

        if (SceneBackgroundOverrideRequested is null)
        {
            throw new InvalidOperationException("Scene background persistence is unavailable.");
        }

        await SceneBackgroundOverrideRequested(definition, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MainWindowViewModel> LoadAsync(
        ISessionController sessionController,
        IUiSettingsStore settingsStore,
        LocalizationManager localization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionController);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(localization);

        UiSettings settings = await settingsStore.LoadAsync(cancellationToken);
        return Create(sessionController, settingsStore, settings, localization);
    }

    internal static async Task<MainWindowViewModel> LoadAsync(
        ISessionController sessionController,
        IUiSettingsStore settingsStore,
        LocalizationManager localization,
        ILogOperations? logOperations,
        CancellationToken cancellationToken)
    {
        UiSettings settings = await settingsStore.LoadAsync(cancellationToken);
        return Create(sessionController, settingsStore, settings, localization, logOperations);
    }

    internal static async Task<MainWindowViewModel> LoadAsync(
        ISessionController sessionController,
        IUiSettingsStore settingsStore,
        LocalizationManager localization,
        IModelCatalog modelCatalog,
        IModelsFolderLauncher folderLauncher,
        string modelsRoot,
        ILogOperations? logOperations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionController);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(localization);
        UiSettings settings = await settingsStore.LoadAsync(cancellationToken);
        return Create(
            sessionController,
            settingsStore,
            settings,
            localization,
            modelCatalog,
            folderLauncher,
            modelsRoot,
            logOperations);
    }

    internal static MainWindowViewModel Create(
        ISessionController sessionController,
        IUiSettingsStore settingsStore,
        UiSettings settings,
        LocalizationManager localization,
        ILogOperations? logOperations = null)
    {
        ArgumentNullException.ThrowIfNull(sessionController);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(localization);
        var paths = new AppDataPaths();
        return Create(
            sessionController,
            settingsStore,
            settings,
            localization,
            new ModelCatalogScanner(paths.ModelsRoot),
            new PlatformModelsFolderLauncher(),
            paths.ModelsRoot,
            logOperations);
    }

    internal static MainWindowViewModel Create(
        ISessionController sessionController,
        IUiSettingsStore settingsStore,
        UiSettings settings,
        LocalizationManager localization,
        IModelCatalog modelCatalog,
        IModelsFolderLauncher folderLauncher,
        string modelsRoot,
        ILogOperations? logOperations = null,
        IModelImporter? modelImporter = null,
        IModelImportSourcePicker? modelImportSourcePicker = null,
        InputActionRegistry? inputActionRegistry = null,
        IShortcutStore? shortcutStore = null)
    {
        ArgumentNullException.ThrowIfNull(sessionController);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(localization);
        LocalizationManager effectiveLocalization = LocalizationManager.Create(
            settings.ApplicationLanguage,
            localization.Culture);
        var catalogViewModel = new ModelCatalogViewModel(
            modelCatalog,
            folderLauncher,
            modelsRoot,
            effectiveLocalization,
            modelImporter,
            modelImportSourcePicker);
        return new MainWindowViewModel(
            sessionController,
            settingsStore,
            settings,
            effectiveLocalization,
            catalogViewModel,
            modelsRoot,
            logOperations,
            inputActionRegistry,
            shortcutStore);
    }

    private async Task LoadShortcutMenuAsync(CancellationToken cancellationToken)
    {
        Guid? preservedSelectedEntryId = shortcutMenuWorkspace?.SelectedEntryId;
        (LayeredShortcutStore layeredStore, LayeredShortcutSnapshot snapshot, InputBindingProfile runtimeProfile,
            ImmutableArray<InputActionDescriptor> descriptors, ImmutableArray<ShortcutActionDefinition> actions,
            ShortcutTargetContext targetContext) =
            await CreateShortcutStateAsync(cancellationToken).ConfigureAwait(true);
        inputActionRegistry.ReplaceDescriptors(descriptors, runtimeProfile);
        var workspace = new InputBindingWorkspaceViewModel(
            snapshot,
            layeredStore,
            actions,
            action => ShortcutTargetCatalog.Build(action, targetContext, Localization.GetString),
            Localization.GetString);
        workspace.RestoreSelection(preservedSelectedEntryId);
        workspace.Applied += updated =>
        {
            InputBindingProfile updatedRuntime = BuildShortcutRuntimeProfile(updated, descriptors);
            inputActionRegistry.ReplaceDescriptors(descriptors, updatedRuntime);
            ShortcutProfileChanged?.Invoke(inputActionRegistry.Profile);
        };
        if (shortcutMenuWorkspace is not null)
        {
            shortcutMenuWorkspace.PropertyChanged -= OnShortcutMenuWorkspacePropertyChanged;
        }
        shortcutMenuWorkspace = workspace;
        shortcutMenuWorkspace.PropertyChanged += OnShortcutMenuWorkspacePropertyChanged;
        ShortcutProfileChanged?.Invoke(inputActionRegistry.Profile);
        ShortcutMenuInvalidated?.Invoke();
    }

    private async Task<ShortcutTargetContext> BuildShortcutTargetContextAsync(
        ModelDescriptor? activeModel,
        CancellationToken cancellationToken)
    {
        ImmutableArray<ShortcutTargetOption> modelTargets = ModelCatalog.Entries
            .Where(static entry => entry.IsSelectable)
            .Select(static entry => new ShortcutTargetOption(entry.Id.Value, entry.DisplayName))
            .ToImmutableArray();

        var trackingTargets = ImmutableArray.CreateBuilder<ShortcutTargetOption>();
        trackingTargets.Add(new ShortcutTargetOption(
            ShortcutTargetCatalog.NoTrackingSourceId,
            Localization.GetString("Menu.Tracking.None")));
        if (trackingSourceRegistry is not null)
        {
            trackingTargets.AddRange(trackingSourceRegistry
                .GetDescriptors(TrackingChannel.Face, IsDeveloperModeEnabled)
                .Select(descriptor => new ShortcutTargetOption(
                    descriptor.Id,
                    Localization.GetString(descriptor.DisplayNameResourceKey))));
        }

        BackgroundRecentAssets recent = await backgroundRecentAssetStore
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        var backgroundDefinitions = ImmutableDictionary.CreateBuilder<string, BackgroundDefinition?>(
            StringComparer.Ordinal);
        var backgroundOptions = ImmutableArray.CreateBuilder<ShortcutTargetOption>();
        backgroundDefinitions.Add(ShortcutTargetCatalog.SharedBackgroundId, null);
        backgroundOptions.Add(new ShortcutTargetOption(
            ShortcutTargetCatalog.SharedBackgroundId,
            Localization.GetString("Menu.Scene.Background.Shared")));
        foreach (BackgroundRecentAsset asset in recent.Images)
        {
            string id = ShortcutTargetCatalog.ImageBackgroundId(asset.AssetId);
            backgroundDefinitions[id] = BackgroundDefinition.Image(
                asset.AssetId,
                BackgroundLayoutMode.Fit);
            backgroundOptions.Add(new ShortcutTargetOption(id, asset.DisplayName));
        }
        foreach (BackgroundRecentAsset asset in recent.Videos)
        {
            string id = ShortcutTargetCatalog.VideoBackgroundId(asset.AssetId);
            backgroundDefinitions[id] = BackgroundDefinition.Video(
                asset.AssetId,
                BackgroundLayoutMode.Fit);
            backgroundOptions.Add(new ShortcutTargetOption(id, asset.DisplayName));
        }
        shortcutBackgroundTargets = backgroundDefinitions.ToImmutable();
        ShortcutRuntimeLog.TargetsBuilt(
            backgroundSettingsLogger,
            modelTargets.Length,
            trackingTargets.Count,
            backgroundOptions.Count);

        return new ShortcutTargetContext(
            activeModel,
            CurrentSceneWorkspace,
            modelTargets,
            trackingTargets.ToImmutable(),
            backgroundOptions.ToImmutable());
    }

    internal IEnumerable<string> ShortcutBackgroundTargetIds => shortcutBackgroundTargets.Keys;

    internal Task ApplyShortcutBackgroundTargetAsync(
        string targetId,
        CancellationToken cancellationToken)
    {
        if (!shortcutBackgroundTargets.TryGetValue(targetId, out BackgroundDefinition? definition))
        {
            return Task.FromException(new InvalidOperationException(
                $"Shortcut background target '{targetId}' is unavailable."));
        }

        return SetSceneBackgroundAsync(definition, cancellationToken);
    }

    internal Task SelectFaceTrackingSourceFromShortcutAsync(
        string targetId,
        CancellationToken cancellationToken) => SelectFaceTrackingSourceAsync(
            StringComparer.Ordinal.Equals(targetId, ShortcutTargetCatalog.NoTrackingSourceId)
                ? null
                : targetId,
            cancellationToken);

    private async Task LoadShortcutMenuSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LoadShortcutMenuAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportCommandFailure(exception);
            ShortcutMenuInvalidated?.Invoke();
        }
    }

    private void OnShortcutMenuWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs args) =>
        ShortcutMenuInvalidated?.Invoke();

    internal void CreateShortcutMenuEntry(ShortcutOwnerKind owner)
    {
        if (shortcutMenuWorkspace is null
            || owner == ShortcutOwnerKind.Model && CurrentMainModelId is null
            || owner == ShortcutOwnerKind.Scene && PresentedSceneId is null)
        {
            return;
        }
        shortcutMenuWorkspace.Create(
            owner,
            Localization.GetString("Workspace.InputBindings.NewDefaultName"));
        Navigation.SelectMenuNode(0, $"shortcuts.draft.{owner.ToString().ToLowerInvariant()}");
    }

    internal void CloseShortcutMenuEditor()
    {
        shortcutMenuWorkspace?.CancelEditor();
        if (!Navigation.SelectedMenuPath.IsEmpty)
        {
            Navigation.SelectMenuNode(0, Navigation.SelectedMenuPath[0]);
        }
    }

    private async Task<(LayeredShortcutStore Store, LayeredShortcutSnapshot Snapshot, InputBindingProfile RuntimeProfile,
        ImmutableArray<InputActionDescriptor> Descriptors, ImmutableArray<ShortcutActionDefinition> Actions,
        ShortcutTargetContext Targets)>
        CreateShortcutStateAsync(CancellationToken cancellationToken)
    {
        IShortcutStore? sceneStore = PresentedSceneId is SceneId sceneId && scenesRoot is not null
            ? new ShortcutStore(SceneStorageLayout.GetInputBindingsPath(scenesRoot, sceneId))
            : null;
        ModelCatalogViewModel.ModelCatalogEntryViewModel? modelEntry = CurrentMainModelId is ModelId modelId
            ? ModelCatalog.Entries.FirstOrDefault(entry => entry.Id == modelId)
            : null;
        IShortcutStore? modelStore = modelEntry is null
            ? null
            : new ShortcutStore(Path.Combine(modelEntry.RootPath, "motara", "input-bindings.motara.json"));
        var layeredStore = new LayeredShortcutStore(shortcutStore, sceneStore, modelStore);
        LayeredShortcutSnapshot snapshot = await layeredStore.LoadSnapshotAsync(cancellationToken).ConfigureAwait(true);
        InputActionRegistry staticRegistry = BuiltInInputActions.CreateRegistry();
        ShortcutTargetContext targets = await BuildShortcutTargetContextAsync(
            modelEntry?.Descriptor,
            cancellationToken).ConfigureAwait(true);
        ImmutableArray<InputActionDescriptor> descriptors = ShortcutActionCatalog.Build(
            staticRegistry,
            targets);
        InputBindingProfile runtimeProfile = BuildShortcutRuntimeProfile(snapshot.ActiveProfile, descriptors);
        ImmutableArray<ShortcutActionDefinition> actions = ShortcutActionCatalog.BuildDefinitions(staticRegistry);
        return (layeredStore, snapshot, runtimeProfile, descriptors, actions, targets);
    }

    private static InputBindingProfile BuildShortcutRuntimeProfile(
        ShortcutProfile profile,
        ImmutableArray<InputActionDescriptor> descriptors)
    {
        InputBindingProfile converted = profile.ToInputBindingProfile();
        IEnumerable<InputBinding> missingDefaults = descriptors
            .Where(descriptor => !converted.Bindings.Any(binding =>
                StringComparer.Ordinal.Equals(binding.ActionId, descriptor.Id)))
            .SelectMany(static descriptor => descriptor.DefaultBindings);
        return InputBindingProfile.Create(converted.Bindings.Concat(missingDefaults), converted.Unavailable);
    }

    private void QueueShortcutRuntimeReload()
    {
        CancellationTokenSource previous = shortcutReloadCancellation;
        shortcutReloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(snapshotPumpCancellation.Token);
        previous.Cancel();
        previous.Dispose();
        long generation = ++shortcutReloadGeneration;
        _ = ReloadShortcutRuntimeAsync(generation, shortcutReloadCancellation.Token);
    }

    private async Task ReloadShortcutRuntimeAsync(long generation, CancellationToken cancellationToken)
    {
        try
        {
            (_, _, InputBindingProfile runtimeProfile, ImmutableArray<InputActionDescriptor> descriptors, _, _) =
                await CreateShortcutStateAsync(cancellationToken).ConfigureAwait(true);
            if (generation != shortcutReloadGeneration || cancellationToken.IsCancellationRequested) return;
            inputActionRegistry.ReplaceDescriptors(descriptors, runtimeProfile);
            ShortcutProfileChanged?.Invoke(inputActionRegistry.Profile);
            if (Navigation.SelectedDestination == NavigationDestination.Shortcuts)
            {
                await LoadShortcutMenuSafelyAsync(cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportCommandFailure(exception);
        }
    }

    private void OpenGlobalBackgroundEditor()
    {
        ClearSelectedSceneSourceSelection();
        var editor = new BackgroundEditorViewModel(
            BackgroundEditorScope.Global,
            GlobalBackground,
            backgroundAssetStore,
            SetGlobalBackgroundAsync,
            TopLevelWorkspace.Close,
            backgroundEditorLogger,
            backgroundRecentAssetStore,
            videoSignalRegistry);
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent("background.global", editor),
            "menu.output.background");
    }

    private void OpenSceneBackgroundEditor()
    {
        ClearSelectedSceneSourceSelection();
        if (PresentedSceneId is not SceneId sceneId
            || CurrentSceneBackgroundOverride is not BackgroundDefinition current)
        {
            throw new InvalidOperationException("A custom scene background is required.");
        }

        var editor = new BackgroundEditorViewModel(
            BackgroundEditorScope.ForScene(sceneId),
            current,
            backgroundAssetStore,
            SetSceneBackgroundAsync,
            TopLevelWorkspace.Close,
            backgroundEditorLogger,
            backgroundRecentAssetStore,
            videoSignalRegistry);
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent("background.scene", editor),
            "menu.scene.background");
    }

    private void OpenSceneAttachmentWorkspace()
    {
        ClearSelectedSceneSourceSelection();
        if (PresentedSceneId is null || SceneAttachmentRequested is null)
        {
            throw new InvalidOperationException("A presented scene is required for attachments.");
        }

        var editor = new SceneAttachmentEditorViewModel(
            backgroundAssetStore,
            backgroundRecentAssetStore,
            videoSignalRegistry,
            SceneAttachmentRequested,
            TopLevelWorkspace.Close);
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent("scene.attachment-editor", editor),
            "menu.scene.add-attachment");
    }

    private void OpenSceneEffectEditor()
    {
        ClearSelectedSceneSourceSelection();
        if (SceneBlurEffectRequested is null)
        {
            throw new InvalidOperationException("Scene effects are unavailable.");
        }

        SceneEffectInstance? blur = CurrentSceneWorkspace.ActiveScene.Effects
            .FirstOrDefault(effect => effect.EffectId == "builtin.blur");
        var editor = new SceneEffectEditorViewModel(blur, SceneBlurEffectRequested);
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent("scene.effect.blur", editor),
            "menu-item-effects-scene-edit");
    }

    private void OpenWindowPresentationSettings()
    {
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent(
                "window-presentation-settings",
                new WindowPresentationSettingsViewModel(this)),
            "menu.output.window-presentation");
    }

    private void OpenScreenshotWorkspace()
    {
        var workspace = new ScreenshotWorkspaceViewModel(
            settings.Screenshot,
            ApplyScreenshotSettingsAsync,
            request => ScreenshotRequested?.Invoke(request),
            TopLevelWorkspace.Close,
            OpenScreenshotFolder);
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent("screenshot-settings", workspace),
            "menu.output.screenshot");
    }

    private void OpenCubismEditorOutputSettings()
    {
        var workspace = new CubismEditorOutputSettingsWorkspaceViewModel(
            settings.CubismEditor,
            ApplyCubismEditorOutputSettingsAsync,
            TopLevelWorkspace.Close);
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent("cubism-editor-output-settings", workspace),
            "menu.output.cubism-editor.connection");
    }

    private async Task OpenCubismEditorMappingAsync(CancellationToken cancellationToken)
    {
        if (cubismEditorMappingLoad is null || cubismEditorMappingSave is null)
        {
            throw new InvalidOperationException("Cubism Editor mapping persistence is unavailable.");
        }

        CubismEditorMappingDocument document = await cubismEditorMappingLoad(cancellationToken)
            .ConfigureAwait(true);
        CubismEditorOutputTarget output = cubismEditorOutput
            ?? throw new InvalidOperationException("Cubism Editor output is unavailable.");
        ImmutableArray<CubismEditorModelParameter> parameters = output.CurrentModelParameters;
        if (parameters.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException(
                "Cubism Editor has not reported a current model parameter list. Enable output and open a model in Cubism Editor first.");
        }

        ModelParameterMappingDocument editorDocument =
            CubismEditorMappingAdapter.CreateEditorDocument(document, parameters);
        var workspace = new ModelParameterMappingEditorViewModel(
            editorDocument,
            async (updated, token) =>
            {
                CubismEditorMappingDocument mapped = CubismEditorMappingAdapter.CreateOutputDocument(updated);
                await cubismEditorMappingSave(mapped, token).ConfigureAwait(false);
            },
            cubismEditorMappingLogger,
            sourceOutputs: Volatile.Read(ref resolvedSourceMapping)?.Outputs,
            parameterLocalizer: Localization.GetString,
            isExternalOutputMapping: true);
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent("cubism-editor-mapping", workspace),
            "menu.output.cubism-editor.mapping");
    }

    internal void RequestScreenshot() =>
        ScreenshotRequested?.Invoke(new ScreenshotCaptureRequest(settings.Screenshot));

    private void OpenScreenshotFolder()
    {
        string directory = screenshotPathProvider.ResolveDirectory(settings.Screenshot.SaveDirectory);
        screenshotFolderLauncher.Open(directory);
    }

    public void SelectDestination(NavigationDestination destination)
    {
        lock (disposalGate)
        {
            if (disposed == 0)
            {
                ClearSelectedSceneSourceSelection();
                Navigation.SelectDestination(destination);
                if (Navigation.SelectedDestination == NavigationDestination.Shortcuts)
                {
                    _ = LoadShortcutMenuSafelyAsync(snapshotPumpCancellation.Token);
                }
                if (Navigation.SelectedDestination == NavigationDestination.Model)
                {
                    ModelCatalog.RefreshCommand.Execute(null);
                }
            }
        }
    }

    internal void AttachCollaborationWorkspace(
        CollaborationWorkspaceViewModel workspace,
        CollaborationIdentityArchiveService identityArchiveService)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(identityArchiveService);
        CollaborationWorkspace = workspace;
        collaborationIdentityArchiveService = identityArchiveService;
        OnPropertyChanged(nameof(CollaborationWorkspace));
    }

    internal async Task OpenFriendInviteGenerationWorkspaceAsync(CancellationToken cancellationToken)
    {
        CollaborationWorkspaceViewModel collaboration = CollaborationWorkspace
            ?? throw new InvalidOperationException("Collaboration is unavailable.");
        var workspace = new FriendInviteGenerationViewModel(
            collaboration,
            TopLevelWorkspace.Close);
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent("collaboration.invite.generate", workspace),
            "collaboration.invite.generate");
        await workspace.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void OpenFriendInviteAcceptanceWorkspace(InvitationCandidate? candidate = null)
    {
        CollaborationWorkspaceViewModel collaboration = CollaborationWorkspace
            ?? throw new InvalidOperationException("Collaboration is unavailable.");
        var workspace = new FriendInviteAcceptanceViewModel(
            collaboration,
            TopLevelWorkspace.Close);
        if (candidate is { Kind: InvitationKind.Friend } friend)
        {
            workspace.InvitationText = $"https://www.motara.org/invite/friend/{friend.Token}";
        }

        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent(
                "collaboration.invite.accept",
                workspace),
            "collaboration.invite.accept");
    }

    internal void OpenLocalProfileSettingsWorkspace()
    {
        CollaborationWorkspaceViewModel collaboration = CollaborationWorkspace
            ?? throw new InvalidOperationException("Collaboration is unavailable.");
        if (collaboration.LocalIdentity is null)
        {
            return;
        }

        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent(
                "collaboration.profile.settings",
                new LocalProfileSettingsViewModel(
                    collaboration,
                    TopLevelWorkspace.Close)),
            "collaboration.profile.settings");
    }

    internal void OpenInvitationCandidateWorkspace(InvitationCandidate candidate)
    {
        if (candidate.Kind == InvitationKind.Session)
        {
            OpenSessionInviteAcceptanceWorkspace(candidate);
        }
        else
        {
            OpenFriendInviteAcceptanceWorkspace(candidate);
        }
    }

    internal void OpenStartupInvitationErrorWorkspace() => TopLevelWorkspace.Open(
        new TopLevelWorkspaceContent(
            "collaboration.startup.invalid",
            new StartupInvitationErrorViewModel(TopLevelWorkspace.Close)),
        "collaboration.startup.invalid");

    internal void OpenSessionInviteGenerationWorkspace()
    {
        CollaborationWorkspaceViewModel collaboration = CollaborationWorkspace
            ?? throw new InvalidOperationException("Collaboration is unavailable.");
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent(
                "collaboration.session.generate",
                new SessionInviteGenerationViewModel(collaboration, TopLevelWorkspace.Close)),
            "collaboration.session.generate");
    }

    internal void OpenSessionInviteEntryWorkspace()
    {
        CollaborationWorkspaceViewModel collaboration = CollaborationWorkspace
            ?? throw new InvalidOperationException("Collaboration is unavailable.");
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent(
                "collaboration.session.accept.entry",
                new SessionInviteEntryViewModel(
                    collaboration,
                    TopLevelWorkspace.Close,
                    OpenSessionInviteAcceptanceWorkspace)),
            "collaboration.session.accept.entry");
    }

    internal void OpenSessionInviteAcceptanceWorkspace(InvitationCandidate candidate)
    {
        CollaborationWorkspaceViewModel collaboration = CollaborationWorkspace
            ?? throw new InvalidOperationException("Collaboration is unavailable.");
        var workspace = new SessionInviteAcceptanceViewModel(
            collaboration,
            candidate,
            TopLevelWorkspace.Close);
        workspace.Initialize();
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent("collaboration.session.accept", workspace),
            "collaboration.session.accept");
    }

    internal async Task OpenFriendDetailsWorkspaceAsync(
        DeviceId deviceId,
        CancellationToken cancellationToken)
    {
        CollaborationWorkspaceViewModel collaboration = CollaborationWorkspace
            ?? throw new InvalidOperationException("Collaboration is unavailable.");
        if (!await TopLevelWorkspace.RequestCloseAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await collaboration.InitializeAsync(cancellationToken).ConfigureAwait(false);
        CollaborationContactItem contact = collaboration.GetRequiredContact(deviceId);
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent(
                "collaboration.friend.details",
                new FriendDetailsViewModel(collaboration, contact, TopLevelWorkspace.Close)),
            "collaboration.friend.details");
    }

    internal void OpenIdentityMigrationWorkspace(IdentityMigrationMode mode)
    {
        CollaborationWorkspaceViewModel collaboration = CollaborationWorkspace
            ?? throw new InvalidOperationException("Collaboration is unavailable.");
        CollaborationIdentityArchiveService archiveService = collaborationIdentityArchiveService
            ?? throw new InvalidOperationException("Collaboration identity migration is unavailable.");
        var workspace = new IdentityMigrationViewModel(
            mode,
            archiveService,
            collaboration.MarkIdentityImportCompleted,
            TopLevelWorkspace.Close);
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent(
                $"collaboration.identity.{mode.ToString().ToLowerInvariant()}",
                workspace),
            $"collaboration.identity.{mode.ToString().ToLowerInvariant()}");
    }

    public void SelectMenuNode(int level, string nodeId)
    {
        if (level == 0
            && selectedSceneSourceId is not null
            && !IsSceneSourceMenuNode(nodeId))
        {
            ClearSelectedSceneSourceSelection();
        }

        lock (disposalGate)
        {
            if (disposed == 0)
            {
                Navigation.SelectMenuNode(level, nodeId);
            }
        }
    }

    private static bool IsSceneSourceMenuNode(string nodeId) =>
        StringComparer.Ordinal.Equals(nodeId, "scene.main-model.current")
        || nodeId.StartsWith("scene.attachment.", StringComparison.Ordinal);

    internal void ClearSelectedSceneSourceSelection()
    {
        if (selectedSceneSourceId is null)
        {
            return;
        }

        selectedSceneSourceId = null;
        OnPropertyChanged(nameof(SelectedSceneSourceId));
        OnPropertyChanged(nameof(IsMainModelSourceSelected));
    }

    public Task SetModelCatalogLayoutModeAsync(
        ModelCatalogLayoutMode mode,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        return MutateSettingsAsync(
            current => current with { ModelCatalogLayoutMode = mode },
            () => OnPropertyChanged(nameof(ModelCatalogLayoutMode)),
            cancellationToken);
    }

    public Task ApplyWindowPresentationAsync(
        int widthPixels,
        int heightPixels,
        ContentScaleMode contentScaleMode,
        double contentScale,
        FrameRateMode frameRateMode,
        CancellationToken cancellationToken)
    {
        if (widthPixels <= 0 || heightPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(widthPixels));
        }

        if (!Enum.IsDefined(contentScaleMode))
        {
            throw new ArgumentOutOfRangeException(nameof(contentScaleMode));
        }

        if (contentScale is not (0.25 or 0.5 or 0.75 or 1 or 1.5 or 2 or 3 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(contentScale));
        }

        if (!Enum.IsDefined(frameRateMode))
        {
            throw new ArgumentOutOfRangeException(nameof(frameRateMode));
        }

        return MutateSettingsAsync(
            current => current with
            {
                WindowWidthPixels = widthPixels,
                WindowHeightPixels = heightPixels,
                ContentScaleMode = contentScaleMode,
                ContentScale = contentScale,
                FrameRateMode = frameRateMode,
            },
            () =>
            {
                OnPropertyChanged(nameof(WindowWidthPixels));
                OnPropertyChanged(nameof(WindowHeightPixels));
                OnPropertyChanged(nameof(ContentScaleMode));
                OnPropertyChanged(nameof(ContentScale));
                OnPropertyChanged(nameof(FrameRateMode));
            },
            cancellationToken);
    }

    internal Task ApplyScreenshotSettingsAsync(
        ScreenshotSettings screenshotSettings,
        CancellationToken cancellationToken)
    {
        ScreenshotSettings.Validate(screenshotSettings);
        return MutateSettingsAsync(
            current => current with { Screenshot = screenshotSettings },
            () => OnPropertyChanged(nameof(ScreenshotSettings)),
            cancellationToken);
    }

    internal async Task ApplyCubismEditorOutputSettingsAsync(
        CubismEditorOutputSettings cubismEditorSettings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cubismEditorSettings);
        CubismEditorOutputTarget output = cubismEditorOutput
            ?? throw new InvalidOperationException("Cubism Editor output is unavailable.");
        var options = new CubismEditorConnectionOptions(
            new Uri(cubismEditorSettings.Endpoint),
            alwaysOutput: cubismEditorSettings.AlwaysOutput);
        await output.ConfigureAsync(options, cancellationToken).ConfigureAwait(false);
        await MutateSettingsAsync(
            current => current with { CubismEditor = cubismEditorSettings },
            () => OnPropertyChanged(nameof(IsCubismEditorAlwaysOutput)),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> TrySetWindowSizeLockedAsync(
        bool isLocked,
        CancellationToken cancellationToken) =>
        TryExecuteAsync(
            () => MutateSettingsAsync(
                current => current with { IsWindowSizeLocked = isLocked },
                () => OnPropertyChanged(nameof(IsWindowSizeLocked)),
                cancellationToken),
            cancellationToken);

    private void AssignViewedModel()
    {
        if (ModelCatalog.ViewedEntry is { IsSelectable: true } entry
            && (ModelConfigurationReview is null
                || ModelConfigurationReview.State == ModelConfigurationReviewState.Ready)
            && (ModelMappingReview?.CanAssignModel ?? true))
        {
            ModelAssignmentRequested?.Invoke(entry.Id);
        }
    }

    public async Task CloseNavigationAsync(CancellationToken cancellationToken)
    {
        await MutateSettingsAsync(
            current => current with { IsNavigationRailVisible = false },
            () =>
            {
                ClearSelectedSceneSourceSelection();
                Navigation.CloseNavigation();
            },
            cancellationToken);
    }

    public async Task RestoreNavigationAsync(CancellationToken cancellationToken)
    {
        await MutateSettingsAsync(
            current => current with { IsNavigationRailVisible = true },
            Navigation.RestoreNavigation,
            cancellationToken);
    }

    public Task<bool> TryRestoreNavigationAsync(CancellationToken cancellationToken) =>
        TryExecuteAsync(() => RestoreNavigationAsync(cancellationToken), cancellationToken);

    public async Task SetDeveloperModeAsync(bool isEnabled, CancellationToken cancellationToken)
    {
        await MutateSettingsAsync(
            current => current with { IsDeveloperModeEnabled = isEnabled },
            () =>
            {
                Navigation.SetDeveloperMode(isEnabled);
                OnPropertyChanged(nameof(IsDeveloperModeEnabled));
                OnPropertyChanged(nameof(Destinations));
            },
            cancellationToken);
    }

    public Task<bool> TrySetDeveloperModeAsync(
        bool isEnabled,
        CancellationToken cancellationToken) =>
        TryExecuteAsync(
            () => SetDeveloperModeAsync(isEnabled, cancellationToken),
            cancellationToken);

    public Task SetApplicationLanguageAsync(
        ApplicationLanguage language,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(language))
        {
            throw new ArgumentOutOfRangeException(nameof(language));
        }

        return MutateSettingsAsync(
            current => current with { ApplicationLanguage = language },
            () =>
            {
                localization = LocalizationManager.Create(language, CultureInfo.CurrentUICulture);
                ModelCatalog.UpdateLocalization(localization);
                allDestinations = CreateDestinations(localization);
                OnPropertyChanged(nameof(Localization));
                OnPropertyChanged(nameof(ApplicationLanguage));
                OnPropertyChanged(nameof(Destinations));
            },
            cancellationToken);
    }

    public Task<bool> TrySetRestoreActiveSceneOnStartupAsync(
        bool isEnabled,
        CancellationToken cancellationToken) =>
        TryExecuteAsync(
            () => MutateSettingsAsync(
                current => current with { RestoreActiveSceneOnStartup = isEnabled },
                () => OnPropertyChanged(nameof(RestoreActiveSceneOnStartup)),
                cancellationToken),
            cancellationToken);

    internal Task<bool> TrySetRememberFaceTrackingOnStartupAsync(
        bool isEnabled,
        CancellationToken cancellationToken) =>
        TryExecuteAsync(
            () => MutateSettingsAsync(
                current => current with { RememberFaceTrackingOnStartup = isEnabled },
                () => OnPropertyChanged(nameof(RememberFaceTrackingOnStartup)),
                cancellationToken),
            cancellationToken);

    public Task SetDiagnosticLogLevelAsync(
        DiagnosticLogLevel level,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        return MutateSettingsAsync(
            current => current with { DiagnosticLogLevel = level },
            () =>
            {
                logOperations.MinimumLevel = level;
                OnPropertyChanged(nameof(DiagnosticLogLevel));
            },
            cancellationToken);
    }

    public Task SetModelRenderingBackendPreferenceAsync(
        ModelRenderingBackendPreference preference,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(preference))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }

        return MutateSettingsAsync(
            current => current with { ModelRenderingBackendPreference = preference },
            () =>
            {
                OnPropertyChanged(nameof(ModelRenderingBackendPreference));
                RenderingBackendPreferenceChanged?.Invoke(preference);
            },
            cancellationToken);
    }

    internal Task<bool> TrySetModelRenderingBackendPreferenceAsync(
        ModelRenderingBackendPreference preference,
        CancellationToken cancellationToken) =>
        TryExecuteAsync(
            () => SetModelRenderingBackendPreferenceAsync(preference, cancellationToken),
            cancellationToken);

    internal void SetCurrentModelSelection(ModelId? modelId) =>
        ModelCatalog.SetSelectedModel(modelId);

    private void OnModelCatalogPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ModelCatalogViewModel.ViewedModelId))
        {
            PrepareViewedModelMappingReview();
        }
    }

    private void PrepareViewedModelMappingReview()
    {
        if (ModelConfigurationReview is not null)
        {
            ModelConfigurationReview.PropertyChanged -= OnModelConfigurationReviewPropertyChanged;
        }

        ModelConfigurationReview = null;
        ModelMappingReview = null;
        OnPropertyChanged(nameof(ModelConfigurationReview));
        OnPropertyChanged(nameof(ModelMappingReview));
        if (ModelCatalog.ViewedEntry is not { } entry)
        {
            return;
        }

        if (modelCapabilitiesProvider is null)
        {
            PrepareModelSourceMappingReview();
            return;
        }

        ModelConfigurationReview = new ModelConfigurationReviewViewModel(
            entry,
            modelCapabilitiesProvider
                ?? (static (_, _) => Task.FromResult<ModelCapabilities?>(null)),
            ModelCatalog.ClearViewedModel,
            sourceMappingContexts.Values.FirstOrDefault()?.Store.Logger);
        ModelConfigurationReview.PropertyChanged += OnModelConfigurationReviewPropertyChanged;
        OnPropertyChanged(nameof(ModelConfigurationReview));
        _ = InitializeModelConfigurationReviewAsync(ModelConfigurationReview);
    }

    private async Task InitializeModelConfigurationReviewAsync(
        ModelConfigurationReviewViewModel review)
    {
        try
        {
            await review.InitializeAsync(snapshotPumpCancellation.Token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(ModelConfigurationReview, review)
                    && review.State == ModelConfigurationReviewState.Ready)
                {
                    PrepareModelSourceMappingReview();
                }
            });
        }
        catch (OperationCanceledException) when (snapshotPumpCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportCommandFailure(exception);
        }
    }

    private void OnModelConfigurationReviewPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnModelConfigurationReviewPropertyChanged(sender, args));
            return;
        }

        OnPropertyChanged(nameof(ModelConfigurationReview));
        if (args.PropertyName == nameof(ModelConfigurationReviewViewModel.State)
            && sender is ModelConfigurationReviewViewModel review
            && ReferenceEquals(review, ModelConfigurationReview)
            && review.State == ModelConfigurationReviewState.Ready)
        {
            PrepareModelSourceMappingReview();
        }
    }

    private void PrepareModelSourceMappingReview()
    {
        if (ModelMappingReview is not null
            || ModelCatalog.ViewedEntry is not { } entry)
        {
            return;
        }

        ModelMappingReview = new ModelSourceMappingReviewViewModel(
            entry.RootPath,
            entry.DisplayName,
            sourceMappingContexts.Keys,
            sourceMappingContexts.Values.FirstOrDefault()?.Store.Logger);
        OnPropertyChanged(nameof(ModelMappingReview));
        _ = InitializeModelSourceMappingReviewAsync(ModelMappingReview);
    }

    private async Task InitializeModelSourceMappingReviewAsync(
        ModelSourceMappingReviewViewModel review)
    {
        try
        {
            await review.InitializeAsync(snapshotPumpCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (snapshotPumpCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportCommandFailure(exception);
        }
    }

    private async Task OpenSourceMappingEditorAsync(CancellationToken cancellationToken)
    {
        if (sourceMappingContexts.IsEmpty)
        {
            throw new InvalidOperationException("Source mapping persistence is unavailable.");
        }

        SourceMappingAdapterContext initial = GetPreferredSourceMappingContext();
        SourceMappingEditorViewModel editor = await CreateSourceMappingEditorAsync(
            initial,
            cancellationToken).ConfigureAwait(true);
        var host = new SourceMappingEditorHostViewModel(
            editor,
            initial.AdapterId,
            sourceMappingContexts.Values.Select(context => new SourceMappingEditorAdapterItem(
                context.AdapterId,
                context.DisplayNameResourceKey)),
            sourceMappingContexts.Values.Select(context =>
                new KeyValuePair<string, Func<CancellationToken, Task<SourceMappingEditorViewModel>>>(
                    context.AdapterId,
                    token => CreateSourceMappingEditorAsync(context, token))));
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent("source-mapping-editor", host),
            "menu.mapping.source.edit");
    }

    private SourceMappingAdapterContext GetPreferredSourceMappingContext()
    {
        string? activeSourceId = faceTrackingController?.SourceStatus.IntendedSourceId;
        if (activeSourceId is not null
            && TryGetSourceMappingContext(activeSourceId, out SourceMappingAdapterContext active))
        {
            return active;
        }

        return sourceMappingContexts.TryGetValue("ifacialmocap", out SourceMappingAdapterContext? iFacial)
            ? iFacial!
            : sourceMappingContexts.Values.First();
    }

    private bool TryGetSourceMappingContext(
        string sourceId,
        out SourceMappingAdapterContext context)
    {
        SourceMappingAdapterContext? found = sourceMappingContexts.Values.FirstOrDefault(candidate =>
            StringComparer.Ordinal.Equals(candidate.SourceId, sourceId));
        context = found!;
        return found is not null;
    }

    private async Task<SourceMappingEditorViewModel> CreateSourceMappingEditorAsync(
        SourceMappingAdapterContext context,
        CancellationToken cancellationToken)
    {
        SourceMappingProfileDocument builtIn = context.CreateBuiltIn();
        SourceMappingProfileDocument document = await context.Store
            .LoadSelectedAsync(builtIn, cancellationToken).ConfigureAwait(true);
        sourceMappingAppliedBaseline = document;
        return new SourceMappingEditorViewModel(
            document,
            context.Inputs,
            ApplySourceMappingAsync,
            context.Store.Logger,
            (path, cancellation) => context.Store.ImportAsDraftAsync(path, cancellation),
            (profile, name, cancellation) => context.Store.SaveAsAsync(profile, name, cancellation),
            cancellation => context.Store.LoadDefaultAsync(builtIn, cancellation),
            cancellation => Task.Run(
                () => new PlatformModelsFolderLauncher().Open(context.Store.DirectoryPath),
                cancellation),
            SynchronizeSourceMappingReferencesAsync);
    }

    private async Task OpenModelParameterMappingAsync(
        object? parameter,
        CancellationToken cancellationToken)
    {
        ModelId modelId = parameter switch
        {
            ModelId id => id,
            _ when CurrentMainModelId is ModelId current => current,
            _ => throw new InvalidOperationException("Model parameter mapping requires a selected model."),
        };
        ModelCatalogViewModel.ModelCatalogEntryViewModel? entry = ModelCatalog.Entries
            .FirstOrDefault(candidate => candidate.Id == modelId);
        if (entry is null
            || modelCapabilitiesProvider is null
            || (ModelCatalog.ViewedModelId == modelId
                && ModelConfigurationReview?.State != ModelConfigurationReviewState.Ready))
        {
            throw new InvalidOperationException("Model parameter mapping is unavailable.");
        }

        ModelCapabilities? capabilities = await modelCapabilitiesProvider(modelId, cancellationToken)
            .ConfigureAwait(true);
        if (capabilities is null)
        {
            throw new InvalidOperationException("Model capabilities are unavailable.");
        }

        ModelParameterMappingDocument document = await modelParameterMappingService.LoadAsync(
            entry,
            capabilities,
            cancellationToken).ConfigureAwait(true);
        var editor = new ModelParameterMappingEditorViewModel(
            document,
            modelParameterMappingSave ?? modelParameterMappingService.SaveAsync,
            observationSource: modelParameterObservationSource,
            sourceOutputs: Volatile.Read(ref resolvedSourceMapping)?.Outputs,
            parameterLocalizer: Localization.GetString);
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent("model.parameter-mapping", editor),
            "menu-item-mapping-model-edit");
    }

    private async Task OpenModelPhysicsSettingsAsync(
        object? parameter,
        CancellationToken cancellationToken)
    {
        ModelId modelId = parameter switch
        {
            ModelId id => id,
            _ when CurrentMainModelId is ModelId current => current,
            _ => throw new InvalidOperationException("Model physics settings require a selected model."),
        };
        ModelCatalogViewModel.ModelCatalogEntryViewModel? entry = ModelCatalog.Entries
            .FirstOrDefault(candidate => candidate.Id == modelId);
        if (entry is null
            || modelPhysicsLoad is null
            || modelPhysicsSave is null
            || (ModelCatalog.ViewedModelId == modelId
                && ModelConfigurationReview?.State != ModelConfigurationReviewState.Ready))
        {
            throw new InvalidOperationException("Model physics settings are unavailable.");
        }

        ModelPhysicsConfiguration configuration = await modelPhysicsLoad(entry, cancellationToken)
            .ConfigureAwait(true);
        var workspace = new ModelPhysicsSettingsViewModel(
            configuration,
            (updated, token) => modelPhysicsSave(entry, updated, token),
            modelPhysicsSettingsLogger);
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent("model.physics-settings", workspace),
            "model-library.physics-settings");
    }

    private async Task OpenModelBasicSettingsAsync(
        object? parameter,
        CancellationToken cancellationToken)
    {
        ModelId modelId = parameter is ModelId id
            ? id
            : CurrentMainModelId ?? throw new InvalidOperationException(
                "Model basic settings require a selected model.");
        ModelCatalogViewModel.ModelCatalogEntryViewModel? entry = ModelCatalog.Entries
            .FirstOrDefault(candidate => candidate.Id == modelId);
        if (entry is null || modelBasicSettingsLoad is null || modelBasicSettingsSave is null
            || (ModelCatalog.ViewedModelId == modelId
                && ModelConfigurationReview?.State != ModelConfigurationReviewState.Ready))
        {
            throw new InvalidOperationException("Model basic settings are unavailable.");
        }

        ModelBasicSettingsDocument document = await modelBasicSettingsLoad(entry, cancellationToken)
            .ConfigureAwait(true);
        var workspace = new ModelBasicSettingsViewModel(
            document.Configuration,
            document.PreviewPath,
            document.Motions,
            async (update, token) =>
            {
                await modelBasicSettingsSave(entry, update, token).ConfigureAwait(true);
                await ModelCatalog.RefreshAsync(token).ConfigureAwait(true);
            },
            modelBasicSettingsLogger);
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent("model.basic-settings", workspace),
            "model-library.basic-settings");
    }

    private void OpenModelAdvancedSettings(object? parameter)
    {
        ModelId modelId = parameter is ModelId id
            ? id
            : CurrentMainModelId ?? throw new InvalidOperationException(
                "Model advanced settings require a selected model.");
        if (!ModelCatalog.Entries.Any(entry => entry.Id == modelId && entry.IsSelectable))
        {
            throw new InvalidOperationException("Model advanced settings are unavailable.");
        }
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent("model.advanced-settings", new ModelAdvancedSettingsViewModel()),
            "model-library.advanced-settings");
    }

    private async Task OpenParameterPriorityWorkspaceAsync(CancellationToken cancellationToken)
    {
        if (!IsDeveloperModeEnabled
            || parameterPriorityLoad is null
            || parameterPrioritySave is null
            || parameterPriorityPublish is null)
        {
            throw new InvalidOperationException("Parameter priority settings are unavailable.");
        }

        ParameterPriorityProfile profile = await parameterPriorityLoad(cancellationToken)
            .ConfigureAwait(true);
        var workspace = new ParameterPriorityWorkspaceViewModel(
            profile,
            parameterPrioritySave,
            parameterPriorityPublish,
            parameterPriorityLogger);
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent("parameter-priority", workspace),
            "menu.developer.parameter-priority");
    }

    private async Task ApplySourceMappingAsync(
        SourceMappingProfileDocument document,
        CancellationToken cancellationToken)
    {
        if (!sourceMappingContexts.TryGetValue(document.AdapterId, out SourceMappingAdapterContext? context))
        {
            throw new InvalidOperationException("Source mapping persistence is unavailable.");
        }

        SourceMappingAdapterContext mappingContext = context
            ?? throw new InvalidOperationException("Source mapping persistence is unavailable.");
        await mappingContext.Store.SaveSelectedAsync(document, cancellationToken).ConfigureAwait(false);

        await ApplyResolvedSourceMappingAsync(
            CurrentMainModelId,
            mappingContext.SourceId,
            cancellationToken).ConfigureAwait(false);
        sourceMappingAppliedBaseline = document;
        if (faceTrackingController?.SourceStatus.IntendedSourceId == mappingContext.SourceId)
        {
            _ = await faceTrackingController.SelectSourceAsync(
                mappingContext.SourceId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SynchronizeSourceMappingReferencesAsync(
        ImmutableArray<(string OldId, string NewId)> renames,
        CancellationToken cancellationToken)
    {
        if (renames.IsEmpty || sourceMappingsRoot is null || modelsRoot is null || scenesRoot is null) return;
        SourceMappingMutationTransaction transaction = await SourceMappingReferenceUpdater.PrepareUpdateAsync(
            renames, null, null, sourceMappingsRoot, modelsRoot, scenesRoot, cancellationToken,
            logger: sourceMappingContexts.Values.FirstOrDefault()?.Store.Logger).ConfigureAwait(false);
        await transaction.ApplyAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OpenCreateScenePrompt()
    {
        ClearSelectedSceneSourceSelection();
        var prompt = new SceneNamePromptViewModel(
            string.Empty,
            isRename: false,
            async (displayName, cancellationToken) =>
            {
                if (SceneCreationRequested is null)
                {
                    throw new InvalidOperationException("Scene creation is unavailable.");
                }

                try
                {
                    await SceneCreationRequested(displayName, cancellationToken);
                    TopLevelWorkspace.Close();
                    return true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ReportCommandFailure(exception);
                    return false;
                }
            },
            TopLevelWorkspace.Close);
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent("scene.name", prompt),
            "menu.scene.create");
    }

    private void OpenRenameScenePrompt()
    {
        ClearSelectedSceneSourceSelection();
        SceneDocument scene = CurrentSceneWorkspace.ActiveScene;
        var prompt = new SceneNamePromptViewModel(
            scene.DisplayName,
            isRename: true,
            async (displayName, cancellationToken) =>
            {
                if (SceneRenameRequested is null)
                {
                    throw new InvalidOperationException("Scene rename is unavailable.");
                }

                try
                {
                    await SceneRenameRequested(scene.Id, displayName, cancellationToken);
                    TopLevelWorkspace.Close();
                    return true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ReportCommandFailure(exception);
                    return false;
                }
            },
            TopLevelWorkspace.Close);
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent("scene.name", prompt),
            "menu.scene.rename");
    }

    private void OpenDeleteSceneConfirmation()
    {
        ClearSelectedSceneSourceSelection();
        SceneDocument scene = CurrentSceneWorkspace.ActiveScene;
        var confirmation = new SceneDeleteConfirmationViewModel(
            scene.DisplayName,
            async cancellationToken =>
            {
                if (SceneDeletionRequested is null)
                {
                    throw new InvalidOperationException("Scene deletion is unavailable.");
                }

                try
                {
                    await SceneDeletionRequested(scene.Id, cancellationToken);
                    TopLevelWorkspace.Close();
                    return true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ReportCommandFailure(exception);
                    return false;
                }
            },
            TopLevelWorkspace.Close);
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent("scene.delete", confirmation),
            "menu.scene.delete");
    }

    internal void UpdateModelAssignmentState(ModelId? currentModelId, ModelId? pendingModelId)
    {
        if (currentMainModelId != currentModelId)
        {
            currentMainModelId = currentModelId;
            OnPropertyChanged(nameof(CurrentMainModelId));
            OnPropertyChanged(nameof(CurrentModelMappingBindingCount));
            UpdateCurrentMainModelFrameRate(null);
            QueueShortcutRuntimeReload();
        }

    }

    internal void UpdateCurrentMainModelFrameRate(double? framesPerSecond)
    {
        if (framesPerSecond is double value && (!double.IsFinite(value) || value < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        if (currentMainModelFrameRate == framesPerSecond)
        {
            return;
        }

        currentMainModelFrameRate = framesPerSecond;
        OnPropertyChanged(nameof(CurrentMainModelFrameRate));
    }

    internal void UpdateWindowPresentationFrameRate(double? framesPerSecond)
    {
        if (framesPerSecond is double value && (!double.IsFinite(value) || value < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        if (currentWindowPresentationFrameRate == framesPerSecond)
        {
            return;
        }

        currentWindowPresentationFrameRate = framesPerSecond;
        OnPropertyChanged(nameof(CurrentWindowPresentationFrameRate));
    }

    internal void UpdateCurrentModelMappingStatus(ModelId modelId, int bindingCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bindingCount);
        modelMappingBindingCounts[modelId] = bindingCount;
        if (CurrentMainModelId == modelId)
        {
            OnPropertyChanged(nameof(CurrentModelMappingBindingCount));
        }
    }

    internal void UpdateSceneState(
        SceneWorkspace workspace,
        SceneId? nextPresentedSceneId,
        ModelId? currentModelId,
        ModelId? pendingModelId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!ReferenceEquals(currentSceneWorkspace, workspace))
        {
            currentSceneWorkspace = workspace;
            OnPropertyChanged(nameof(CurrentSceneWorkspace));
            OnPropertyChanged(nameof(CurrentSceneBackgroundOverride));
            OnPropertyChanged(nameof(EffectiveBackground));
            QueueShortcutRuntimeReload();
        }

        if (presentedSceneId != nextPresentedSceneId)
        {
            presentedSceneId = nextPresentedSceneId;
            OnPropertyChanged(nameof(PresentedSceneId));
            OnPropertyChanged(nameof(CurrentSceneBackgroundOverride));
            OnPropertyChanged(nameof(EffectiveBackground));
        }

        SceneDocument? nextPresentedScene = nextPresentedSceneId is SceneId sceneId
            ? workspace.Scenes.Single(scene => scene.Id == sceneId)
            : null;
        bool selectedSourceStillExists = selectedSceneSourceId is Guid selected
            && nextPresentedScene is not null
            && (nextPresentedScene.MainModel?.SourceId == selected
                || nextPresentedScene.Attachments.Any(attachment => attachment.SourceId == selected));
        if (selectedSceneSourceId is not null && !selectedSourceStillExists)
        {
            selectedSceneSourceId = null;
            OnPropertyChanged(nameof(SelectedSceneSourceId));
            OnPropertyChanged(nameof(IsMainModelSourceSelected));
        }

        UpdateModelAssignmentState(currentModelId, pendingModelId);
    }

    internal Task<bool> TrySetMainModelVisibilityAsync(
        bool isVisible,
        CancellationToken cancellationToken) =>
        TryExecuteAsync(
            () => MainModelVisibilityRequested is null
                ? Task.FromException(new InvalidOperationException("Main model visibility is unavailable."))
                : MainModelVisibilityRequested(isVisible, cancellationToken),
            cancellationToken);

    internal Task<bool> TrySetMainModelLockAsync(
        bool isLocked,
        CancellationToken cancellationToken) =>
        TryExecuteAsync(
            () => MainModelLockRequested is null
                ? Task.FromException(new InvalidOperationException("Main model lock is unavailable."))
                : MainModelLockRequested(isLocked, cancellationToken),
            cancellationToken);

    internal Task<bool> TrySetSceneAttachmentVisibilityAsync(
        Guid sourceId,
        bool isVisible,
        CancellationToken cancellationToken) =>
        TryExecuteAsync(
            async () =>
            {
                if (SceneAttachmentVisibilityRequested is null)
                {
                    throw new InvalidOperationException("Scene attachment visibility is unavailable.");
                }

                await SceneAttachmentVisibilityRequested(sourceId, isVisible, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);

    internal Task<bool> TrySetSceneAttachmentLockAsync(
        Guid sourceId,
        bool isLocked,
        CancellationToken cancellationToken) =>
        TryExecuteAsync(
            async () =>
            {
                if (SceneAttachmentLockRequested is null)
                {
                    throw new InvalidOperationException("Scene attachment lock is unavailable.");
                }

                await SceneAttachmentLockRequested(sourceId, isLocked, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);

    internal Task<bool> TrySetSceneAttachmentTransformAsync(
        Guid sourceId,
        SceneTransform transform,
        CancellationToken cancellationToken) =>
        TryExecuteAsync(
            async () =>
            {
                if (SceneAttachmentTransformRequested is null)
                {
                    throw new InvalidOperationException("Scene attachment transform is unavailable.");
                }

                await SceneAttachmentTransformRequested(sourceId, transform, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);

    internal Task<bool> TrySetSceneAttachmentMountModeAsync(
        Guid sourceId,
        AttachmentMountMode mountMode,
        CancellationToken cancellationToken) =>
        TryExecuteAsync(
            async () =>
            {
                if (SceneAttachmentMountModeRequested is null)
                {
                    throw new InvalidOperationException("Scene attachment mount mode is unavailable.");
                }

                await SceneAttachmentMountModeRequested(sourceId, mountMode, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);

    internal Task<bool> TrySetSceneAttachmentModelBindingAsync(
        Guid sourceId,
        AttachmentModelAnchor anchor,
        SceneTransform localTransform,
        CancellationToken cancellationToken) =>
        TryExecuteAsync(
            async () =>
            {
                if (SceneAttachmentModelBindingRequested is null)
                {
                    throw new InvalidOperationException("Scene attachment model binding is unavailable.");
                }

                _ = await SceneAttachmentModelBindingRequested(
                    sourceId,
                    anchor,
                    localTransform,
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    internal Task<bool> TryMoveSceneAttachmentAsync(
        Guid sourceId,
        AttachmentPlacement placement,
        int destinationIndex,
        CancellationToken cancellationToken) =>
        TryExecuteAsync(
            async () =>
            {
                if (SceneAttachmentMoveRequested is null)
                {
                    throw new InvalidOperationException("Scene attachment ordering is unavailable.");
                }

                await SceneAttachmentMoveRequested(sourceId, placement, destinationIndex, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);

    internal Task<bool> TryMoveMainModelAsync(
        int frontAttachmentCount,
        CancellationToken cancellationToken) =>
        TryExecuteAsync(
            () => MainModelMoveRequested is null
                ? Task.FromException(new InvalidOperationException("Main model ordering is unavailable."))
                : MainModelMoveRequested(frontAttachmentCount, cancellationToken),
            cancellationToken);

    internal Task<bool> TrySetSceneAttachmentDisplayNameAsync(
        Guid sourceId,
        string displayName,
        CancellationToken cancellationToken) =>
        TryExecuteAsync(
            () => SceneAttachmentDisplayNameRequested is null
                ? Task.FromException(new InvalidOperationException("Scene attachment naming is unavailable."))
                : SceneAttachmentDisplayNameRequested(sourceId, displayName, cancellationToken),
            cancellationToken);

    internal Task<bool> TryRemoveSceneAttachmentAsync(
        Guid sourceId,
        CancellationToken cancellationToken) =>
        TryExecuteAsync(
            async () =>
            {
                if (SceneAttachmentRemovalRequested is null)
                {
                    throw new InvalidOperationException("Scene attachment removal is unavailable.");
                }

                await SceneAttachmentRemovalRequested(sourceId, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);

    internal Task<bool> TrySetMainModelTrackingChannelAsync(
        TrackingChannel channel,
        bool isEnabled,
        CancellationToken cancellationToken) =>
        TryExecuteAsync(
            async () =>
            {
                MainModelInstance mainModel = GetPresentedMainModel();
                if (mainModel.TrackingMode != MainModelTrackingMode.SharedTracking)
                {
                    throw new InvalidOperationException(
                        "Tracking channels can be changed only in shared-tracking mode.");
                }

                TrackingChannelBindings current = mainModel.TrackingChannels;
                TrackingChannelBindings next = channel switch
                {
                    TrackingChannel.Face => current with { Face = isEnabled },
                    TrackingChannel.Hand => current with { Hand = isEnabled },
                    TrackingChannel.Body => current with { Body = isEnabled },
                    _ => throw new ArgumentOutOfRangeException(nameof(channel)),
                };
                await RequestMainModelTrackingAsync(
                    MainModelTrackingMode.SharedTracking,
                    next,
                    idleAnimationId: null,
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    private void SelectSceneSource(object? parameter)
    {
        Guid sourceId = (Guid)parameter!;
        SceneDocument? scene = PresentedSceneId is SceneId sceneId
            ? currentSceneWorkspace.Scenes.Single(candidate => candidate.Id == sceneId)
            : null;
        if (scene is null
            || (scene.MainModel?.SourceId != sourceId
                && !scene.Attachments.Any(attachment => attachment.SourceId == sourceId)))
        {
            return;
        }

        selectedSceneSourceId = selectedSceneSourceId == sourceId ? null : sourceId;
        OnPropertyChanged(nameof(SelectedSceneSourceId));
        OnPropertyChanged(nameof(IsMainModelSourceSelected));
    }

    private void ClearPresentedSceneSelection()
    {
        presentedSceneId = null;
        selectedSceneSourceId = null;
        OnPropertyChanged(nameof(PresentedSceneId));
        OnPropertyChanged(nameof(SelectedSceneSourceId));
        OnPropertyChanged(nameof(IsMainModelSourceSelected));
    }

    private async Task SetMainModelTrackingAsync(
        MainModelTrackingMode trackingMode,
        CancellationToken cancellationToken)
    {
        ClearSelectedSceneSourceSelection();
        MainModelInstance mainModel = GetPresentedMainModel();
        TrackingChannelBindings channels = trackingMode == MainModelTrackingMode.SharedTracking
            ? mainModel.TrackingChannels.HasAny
                ? mainModel.TrackingChannels
                : TrackingChannelBindings.Default
            : TrackingChannelBindings.None;
        string? idleAnimationId = trackingMode == MainModelTrackingMode.IdleAnimation
            ? mainModel.IdleAnimationId
            : null;
        await RequestMainModelTrackingAsync(
            trackingMode,
            channels,
            idleAnimationId,
            cancellationToken).ConfigureAwait(false);
    }

    private MainModelInstance GetPresentedMainModel()
    {
        if (PresentedSceneId is not SceneId sceneId
            || currentSceneWorkspace.Scenes.Single(scene => scene.Id == sceneId).MainModel is not
                { } mainModel)
        {
            throw new InvalidOperationException("The presented scene has no main model.");
        }

        return mainModel;
    }

    private Task RequestMainModelTrackingAsync(
        MainModelTrackingMode trackingMode,
        TrackingChannelBindings channels,
        string? idleAnimationId,
        CancellationToken cancellationToken) => MainModelTrackingRequested is null
        ? Task.FromException(new InvalidOperationException("Main model tracking is unavailable."))
        : MainModelTrackingRequested(trackingMode, channels, idleAnimationId, cancellationToken);

    internal void ReportExternalCommandFailure(Exception exception) =>
        ReportCommandFailure(exception);

    internal void AttachFaceTrackingController(FaceTrackingSessionController controller)
    {
        faceTrackingController = controller ?? throw new ArgumentNullException(nameof(controller));
        trackingSourceRegistry = controller.Registry;
        UpdateFaceTrackingSourceStatus(controller.SourceStatus);
    }

    internal void AttachTrackingControllers(
        FaceTrackingSessionController faceController,
        FaceTrackingSessionController handController)
    {
        AttachFaceTrackingController(faceController);
        this.handTrackingController = handController
            ?? throw new ArgumentNullException(nameof(handController));
        if (handController.Channel != TrackingChannel.Hand)
        {
            throw new ArgumentException(
                "The hand tracking controller must own the hand channel.",
                nameof(handController));
        }

        if (!ReferenceEquals(faceController.Registry, handController.Registry))
        {
            throw new ArgumentException(
                "Tracking channel controllers must share one source registry.",
                nameof(handController));
        }

        UpdateHandTrackingSourceStatus(handController.SourceStatus);
    }

    internal void AttachTrackingChannelSelectionStore(
        ITrackingChannelSelectionStore store,
        ILogger? logger = null)
    {
        trackingChannelSelectionStore = store ?? throw new ArgumentNullException(nameof(store));
        trackingSelectionLogger = logger ?? NullLogger.Instance;
    }

    internal void AttachSourceMappings(
        IEnumerable<SourceMappingAdapterContext> contexts,
        string? sourceMappingsRoot = null,
        string? modelsRoot = null,
        string? scenesRoot = null)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        SourceMappingAdapterContext[] materialized = contexts.ToArray();
        sourceMappingContexts = materialized.ToImmutableDictionary(
            static context => context.AdapterId,
            StringComparer.Ordinal);
        if (sourceMappingContexts.IsEmpty || sourceMappingContexts.Count != materialized.Length)
        {
            throw new ArgumentException("At least one uniquely identified mapping adapter is required.", nameof(contexts));
        }

        this.sourceMappingsRoot = sourceMappingsRoot is null ? null : Path.GetFullPath(sourceMappingsRoot);
        this.modelsRoot = modelsRoot is null ? null : Path.GetFullPath(modelsRoot);
        this.scenesRoot = scenesRoot is null ? null : Path.GetFullPath(scenesRoot);
        QueueShortcutRuntimeReload();
    }

    internal void AttachSourceMapping(
        SourceMappingProfileStore store,
        Action<SourceMappingProfileDocument> configureMapping,
        string? sourceMappingsRoot = null,
        string? modelsRoot = null,
        string? scenesRoot = null) => AttachSourceMappings(
        [
            new SourceMappingAdapterContext(
                "ifacialmocap",
                Motara.Tracking.iFacialMocap.IFacialMocapTrackingSource.SourceId,
                "Menu.Tracking.Source.IFacialMocap",
                Motara.Tracking.iFacialMocap.IFacialMocapMappingDefaults.CreateProfile,
                Motara.Tracking.iFacialMocap.IFacialMocapMappingDefaults.Inputs,
                store ?? throw new ArgumentNullException(nameof(store)),
                configureMapping ?? throw new ArgumentNullException(nameof(configureMapping))),
        ],
        sourceMappingsRoot,
        modelsRoot,
        scenesRoot);

    private async Task OrganizeActiveSceneAsync(
        object? parameter,
        CancellationToken cancellationToken)
    {
        if (scenesRoot is null || PresentedSceneId is not SceneId sceneId)
        {
            return;
        }

        var storage = new ScopedMotaraStorage(
            SceneStorageLayout.GetSceneDirectory(scenesRoot, sceneId),
            "scene.motara.json");
        ScopedMotaraOrganizationResult result = await storage
            .OrganizeAsync(cancellationToken)
            .ConfigureAwait(false);
        SceneOrganizationStatusText = Localization.GetString(result.Succeeded
            ? "Menu.Scene.Organized"
            : "Menu.Scene.OrganizeConflict");
        OnPropertyChanged(nameof(SceneOrganizationStatusText));
    }

    internal Task ApplyResolvedSourceMappingAsync(
        ModelId? modelId,
        CancellationToken cancellationToken) => ApplyResolvedSourceMappingAsync(
            modelId,
            faceTrackingController?.SourceStatus.IntendedSourceId,
            cancellationToken);

    internal async Task ApplyResolvedSourceMappingAsync(
        ModelId? modelId,
        string? sourceId,
        CancellationToken cancellationToken)
    {
        if (sourceId is null
            || !TryGetSourceMappingContext(sourceId, out SourceMappingAdapterContext context)
            || sourceMappingsRoot is null
            || modelsRoot is null
            || scenesRoot is null)
        {
            return;
        }

        SourceMappingProfileDocument builtIn = context.CreateBuiltIn();
        SourceMappingProfileDocument defaultProfile = await context.Store
            .LoadDefaultAsync(builtIn, cancellationToken).ConfigureAwait(false);
        SourceMappingProfileDocument globalProfile = await context.Store
            .LoadSelectedAsync(defaultProfile, cancellationToken).ConfigureAwait(false);
        SourceMappingProfileDocument? modelProfile = null;
        if (modelId is ModelId selectedModel
            && ModelCatalog.Entries.FirstOrDefault(entry => entry.Id == selectedModel) is { } entry)
        {
            string modelDirectory = entry.RootPath;
            var configurationStore = new MotaraModelConfigurationStore(
                modelDirectory,
                entry.DisplayName);
            MotaraModelConfiguration? configuration = await configurationStore
                .LoadAsync(cancellationToken).ConfigureAwait(false);
            ModelSourceMappingSelection? selection = configuration?.SourceMappingSelections
                .FirstOrDefault(item => item.IsEnabled
                    && StringComparer.Ordinal.Equals(item.AdapterId, context.AdapterId));
            if (selection is not null)
            {
                modelProfile = await LoadScopedMappingAsync(
                    modelDirectory,
                    "model.motara.json",
                    selection.AdapterId,
                    selection.ProfileId,
                    selection.FileName,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        SourceMappingProfileDocument? sceneProfile = null;
        SceneSourceMappingOverride? sceneOverride = currentSceneWorkspace.ActiveScene.SourceMappingOverride;
        if (sceneOverride is not null
            && StringComparer.Ordinal.Equals(sceneOverride.AdapterId, context.AdapterId))
        {
            sceneProfile = await LoadScopedMappingAsync(
                SceneStorageLayout.GetSceneDirectory(
                    scenesRoot,
                    currentSceneWorkspace.ActiveSceneId),
                "scene.motara.json",
                sceneOverride.AdapterId,
                sceneOverride.ProfileId,
                sceneOverride.FileName,
                cancellationToken).ConfigureAwait(false);
        }

        SourceMappingProfileDocument resolved = SourceMappingResolver.Resolve(
            sceneProfile,
            modelProfile,
            globalProfile,
            defaultProfile);
        await Task.Run(() => context.ConfigureMapping(resolved), cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref resolvedSourceMapping, resolved);
    }

    private static async Task<SourceMappingProfileDocument> LoadMappingAsync(
        string path,
        string expectedAdapterId,
        string expectedProfileId,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        SourceMappingProfileDocument document =
            await JsonSerializer.DeserializeAsync<SourceMappingProfileDocument>(
                stream,
                MappingJsonOptions,
                cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("Source mapping profile is empty.");
        document.Validate();
        if (!StringComparer.Ordinal.Equals(document.AdapterId, expectedAdapterId)
            || !StringComparer.Ordinal.Equals(document.ProfileId, expectedProfileId))
        {
            throw new JsonException("Source mapping identity does not match its selection.");
        }

        return document;
    }

    private static async Task<SourceMappingProfileDocument?> LoadScopedMappingAsync(
        string scopeRoot,
        string manifestFileName,
        string expectedAdapterId,
        string expectedProfileId,
        string preferredFileName,
        CancellationToken cancellationToken)
    {
        var storage = new ScopedMotaraStorage(scopeRoot, manifestFileName);
        string? path = await storage.ResolveMappingPathAsync(
            expectedAdapterId,
            expectedProfileId,
            preferredFileName,
            cancellationToken).ConfigureAwait(false);
        return path is null
            ? null
            : await LoadMappingAsync(
                path,
                expectedAdapterId,
                expectedProfileId,
                cancellationToken).ConfigureAwait(false);
    }

    internal async Task InitializeTrackingChannelSelectionsAsync(
        ITrackingChannelSelectionStore store,
        TrackingSourceRegistry registry,
        bool includeDeveloper,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);
        TrackingChannelSelections loaded = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        TrackingChannelSelections normalized = loaded.Normalize(registry, includeDeveloper);
        if (normalized != loaded)
        {
            await store.SaveAsync(normalized, cancellationToken).ConfigureAwait(false);
        }

        trackingChannelSelectionStore = store;
        trackingSourceRegistry = registry;
        TrackingChannelSelections activeSelections = settings.RememberFaceTrackingOnStartup
            ? normalized
            : normalized.WithSource(TrackingChannel.Face, null);
        await SetTrackingChannelSelectionsAsync(activeSelections).ConfigureAwait(false);

        string? faceSourceId = normalized.GetSourceId(TrackingChannel.Face);
        TrackingSelectionStartupLog.Initialized(
            trackingSelectionLogger,
            settings.RememberFaceTrackingOnStartup,
            faceSourceId is not null);
        if (settings.RememberFaceTrackingOnStartup
            && faceSourceId is not null
            && faceTrackingController is not null)
        {
            await SelectFaceTrackingSourceAsync(faceSourceId, cancellationToken).ConfigureAwait(false);
        }
    }

    internal Task InitializeTrackingChannelSelectionsAsync(
        bool includeDeveloper,
        CancellationToken cancellationToken)
    {
        if (trackingChannelSelectionStore is not { } store
            || trackingSourceRegistry is not { } registry)
        {
            return Task.CompletedTask;
        }

        return InitializeTrackingChannelSelectionsAsync(
            store,
            registry,
            includeDeveloper,
            cancellationToken);
    }

    internal void AttachIFacialMocapConfiguration(
        IIFacialMocapConfigurationStore store,
        ILocalIpv4AddressProvider addressProvider,
        Action<Motara.Tracking.iFacialMocap.IFacialMocapOptions> configureSource,
        Func<string, CancellationToken, Task<bool>> selectSourceAsync,
        ILogger<IFacialMocapConfigurationViewModel>? logger = null,
        ILogger? selectionLogger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(addressProvider);
        ArgumentNullException.ThrowIfNull(configureSource);
        ArgumentNullException.ThrowIfNull(selectSourceAsync);
        iFacialMocapConfigurationStore = store;
        configureIFacialMocapSource = configureSource;
        selectIFacialMocapSourceAsync = selectSourceAsync;
        iFacialMocapSelectionLogger = selectionLogger ?? NullLogger.Instance;
        createIFacialMocapConfiguration = () => new IFacialMocapConfigurationViewModel(
            store,
            addressProvider,
            configureSource,
            selectSourceAsync,
            logger);
    }

    internal void AttachOpenSeeFaceConfiguration(
        IOpenSeeFaceConfigurationStore store,
        OpenSeeFaceConfiguration defaultConfiguration,
        Action<OpenSeeFaceConfiguration> configureSource,
        Func<CancellationToken, Task<IReadOnlyList<OpenSeeFaceCamera>>> listCamerasAsync,
        Func<string, CancellationToken, Task<bool>> selectSourceAsync,
        ILogger<OpenSeeFaceConfigurationViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(defaultConfiguration);
        ArgumentNullException.ThrowIfNull(configureSource);
        ArgumentNullException.ThrowIfNull(listCamerasAsync);
        ArgumentNullException.ThrowIfNull(selectSourceAsync);
        createOpenSeeFaceConfiguration = () => new OpenSeeFaceConfigurationViewModel(
            store,
            defaultConfiguration,
            configureSource,
            listCamerasAsync,
            selectSourceAsync,
            logger);
    }

    internal void UpdateFaceTrackingSourceStatus(TrackingSourceStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (faceTrackingSourceStatus == status)
        {
            return;
        }

        faceTrackingSourceStatus = status;
        OnPropertyChanged(nameof(FaceTrackingSourceStatus));
    }

    internal void UpdateHandTrackingSourceStatus(TrackingSourceStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (handTrackingSourceStatus == status)
        {
            return;
        }

        handTrackingSourceStatus = status;
        OnPropertyChanged(nameof(HandTrackingSourceStatus));
    }

    private async Task SelectFaceTrackingSourceAsync(
        string? sourceId,
        CancellationToken cancellationToken)
    {
        if (sourceId is not null
            && TryGetSourceMappingContext(sourceId, out _))
        {
            await ApplyResolvedSourceMappingAsync(
                CurrentMainModelId,
                sourceId,
                cancellationToken).ConfigureAwait(false);
        }

        if (sourceId == Motara.Tracking.iFacialMocap.IFacialMocapTrackingSource.SourceId
            && iFacialMocapConfigurationStore is not null
            && configureIFacialMocapSource is not null
            && selectIFacialMocapSourceAsync is not null)
        {
            await SelectIFacialMocapSourceAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (faceTrackingController is null)
        {
            throw new InvalidOperationException("Face tracking source selection is unavailable.");
        }

        bool selected = await faceTrackingController.SelectSourceAsync(sourceId, cancellationToken);
        if (selected)
        {
            await PersistTrackingChannelSelectionAsync(
                TrackingChannel.Face,
                sourceId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SelectIFacialMocapSourceAsync(CancellationToken cancellationToken)
    {
        IFacialMocapConfiguration? configuration = await iFacialMocapConfigurationStore!
            .LoadAsync(cancellationToken).ConfigureAwait(false);
        if (configuration is null)
        {
            IFacialMocapSelectionLog.ConfigurationRequired(iFacialMocapSelectionLogger);
            await OpenIFacialMocapConfigurationAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        configureIFacialMocapSource!(configuration.ToOptions());
        bool selected = await selectIFacialMocapSourceAsync!(
            Motara.Tracking.iFacialMocap.IFacialMocapTrackingSource.SourceId,
            cancellationToken).ConfigureAwait(false);
        IFacialMocapSelectionLog.LocalConfigurationApplied(iFacialMocapSelectionLogger, selected);
        if (selected)
        {
            await PersistTrackingChannelSelectionAsync(
                TrackingChannel.Face,
                Motara.Tracking.iFacialMocap.IFacialMocapTrackingSource.SourceId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private Task SelectTrackingSourceAsync(
        TrackingChannel channel,
        string? sourceId,
        CancellationToken cancellationToken) => channel switch
    {
        TrackingChannel.Face => SelectFaceTrackingSourceAsync(sourceId, cancellationToken),
        TrackingChannel.Hand => SelectHandTrackingSourceAsync(sourceId, cancellationToken),
        TrackingChannel.Body when sourceId is null =>
            PersistTrackingChannelSelectionAsync(channel, null, cancellationToken),
        TrackingChannel.Body => Task.FromException(
            new InvalidOperationException("Body tracking source lifecycle is unavailable.")),
        _ => Task.FromException(new ArgumentOutOfRangeException(nameof(channel))),
    };

    private async Task CalibrateFaceTrackingAsync(CancellationToken cancellationToken)
    {
        if (faceTrackingController is null)
        {
            throw new InvalidOperationException("Face tracking calibration is unavailable.");
        }

        TrackingCalibrationResult result = await faceTrackingController
            .CalibrateAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                result.ReasonCode ?? "tracking.calibration.failed");
        }
    }

    private async Task SelectHandTrackingSourceAsync(
        string? sourceId,
        CancellationToken cancellationToken)
    {
        if (handTrackingController is null)
        {
            if (sourceId is null)
            {
                await PersistTrackingChannelSelectionAsync(
                    TrackingChannel.Hand,
                    null,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            throw new InvalidOperationException("Hand tracking source selection is unavailable.");
        }

        bool selected = await handTrackingController.SelectSourceAsync(
            sourceId,
            cancellationToken).ConfigureAwait(false);
        UpdateHandTrackingSourceStatus(handTrackingController.SourceStatus);
        if (selected)
        {
            await PersistTrackingChannelSelectionAsync(
                TrackingChannel.Hand,
                sourceId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PersistTrackingChannelSelectionAsync(
        TrackingChannel channel,
        string? sourceId,
        CancellationToken cancellationToken)
    {
        TrackingChannelSelections next = trackingChannelSelections.WithSource(channel, sourceId);
        if (trackingChannelSelectionStore is null)
        {
            await SetTrackingChannelSelectionsAsync(next).ConfigureAwait(false);
            return;
        }

        if (trackingSourceRegistry is not null)
        {
            next = next.Normalize(
                trackingSourceRegistry,
                IsDeveloperModeEnabled);
        }

        await trackingChannelSelectionStore.SaveAsync(next, cancellationToken)
            .ConfigureAwait(false);
        await SetTrackingChannelSelectionsAsync(next).ConfigureAwait(false);
    }

    private async Task SetTrackingChannelSelectionsAsync(TrackingChannelSelections selections)
    {
        void Apply()
        {
            trackingChannelSelections = selections;
            OnPropertyChanged(nameof(TrackingChannelSelections));
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(Apply);
    }

    private async Task OpenIFacialMocapConfigurationAsync(CancellationToken cancellationToken)
    {
        Func<IFacialMocapConfigurationViewModel>? create = createIFacialMocapConfiguration;
        if (create is null)
        {
            throw new InvalidOperationException("iFacialMocap configuration is unavailable.");
        }

        IFacialMocapConfigurationViewModel configuration = create();
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent(
                "tracking.ifacialmocap.configuration",
                configuration),
            "menu-item-tracking.source.apple-arkit.ifacialmocap");
        await configuration.InitializeAsync(cancellationToken);
    }

    private async Task OpenOpenSeeFaceConfigurationAsync(CancellationToken cancellationToken)
    {
        Func<OpenSeeFaceConfigurationViewModel>? create = createOpenSeeFaceConfiguration;
        if (create is null)
        {
            throw new InvalidOperationException("OpenSeeFace configuration is unavailable.");
        }

        OpenSeeFaceConfigurationViewModel configuration = create();
        TopLevelWorkspace.Open(
            new TopLevelWorkspaceContent(
                "tracking.openseeface.configuration",
                configuration),
            "menu-item-tracking.face.source.openseeface.local-camera");
        await configuration.InitializeAsync(cancellationToken);
    }

    public async Task StartSessionAsync(CancellationToken cancellationToken)
    {
        await sessionController.StartAsync(cancellationToken);
    }

    public async Task StopSessionAsync(CancellationToken cancellationToken)
    {
        await sessionController.StopAsync(cancellationToken);
    }

    public void ApplySnapshot(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (disposalGate)
        {
            if (disposed == 0)
            {
                currentSessionSnapshot = snapshot;
                OnPropertyChanged(nameof(CurrentSessionSnapshot));
            }
        }
    }

    public void Dispose()
    {
        _ = BeginDisposal();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await BeginDisposal();
        GC.SuppressFinalize(this);
    }

    private async Task PumpSnapshotsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (SessionSnapshot snapshot in sessionController
                .WatchSnapshotsAsync(cancellationToken))
            {
                lock (disposalGate)
                {
                    if (disposed == 0 && snapshot.Revision > currentSessionSnapshot.Revision)
                    {
                        currentSessionSnapshot = snapshot;
                        OnPropertyChanged(nameof(CurrentSessionSnapshot));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                ReportCommandFailure(exception);
            }
        }
    }

    private Task BeginDisposal()
    {
        lock (disposalGate)
        {
            if (disposalTask is not null)
            {
                return disposalTask;
            }

            Volatile.Write(ref disposed, 1);
            snapshotPumpCancellation.Cancel();
            shortcutReloadCancellation.Cancel();
            Task settingsDrained = activeSettingsMutations == 0
                ? Task.CompletedTask
                : (settingsMutationsDrained ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            disposalTask = FinishDisposalAsync(settingsDrained);
            return disposalTask;
        }
    }

    private async Task FinishDisposalAsync(Task settingsDrained)
    {
        await snapshotPump.ConfigureAwait(false);
        await settingsDrained.ConfigureAwait(false);
        if (CollaborationWorkspace is not null)
        {
            await CollaborationWorkspace.DisposeAsync().ConfigureAwait(false);
        }
        if (cubismEditorOutput is not null)
        {
            cubismEditorOutput.StatusChanged -= OnCubismEditorOutputStatusChanged;
        }
        videoSignalRegistry.Dispose();
        if (shortcutMenuWorkspace is not null)
        {
            shortcutMenuWorkspace.PropertyChanged -= OnShortcutMenuWorkspacePropertyChanged;
        }
        snapshotPumpCancellation.Dispose();
        shortcutReloadCancellation.Dispose();
        settingsMutationGate.Dispose();
    }

    private async Task MutateSettingsAsync(
        Func<UiSettings, UiSettings> createNext,
        Action applyState,
        CancellationToken cancellationToken)
    {
        lock (disposalGate)
        {
            if (disposed != 0)
            {
                return;
            }

            activeSettingsMutations++;
        }

        bool gateEntered = false;
        try
        {
            await settingsMutationGate.WaitAsync(cancellationToken);
            gateEntered = true;
            UiSettings current;
            lock (disposalGate)
            {
                if (disposed != 0)
                {
                    return;
                }

                current = settings;
            }

            UiSettings next = createNext(current);
            await settingsStore.SaveAsync(next, cancellationToken);
            void ApplySavedSettings()
            {
                lock (disposalGate)
                {
                    if (disposed == 0)
                    {
                        settings = next;
                        applyState();
                    }
                }
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                ApplySavedSettings();
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(ApplySavedSettings);
            }
        }
        finally
        {
            if (gateEntered)
            {
                settingsMutationGate.Release();
            }

            lock (disposalGate)
            {
                activeSettingsMutations--;
                if (disposed != 0 && activeSettingsMutations == 0)
                {
                    settingsMutationsDrained?.TrySetResult();
                }
            }
        }
    }

    private void ReportCommandFailure(Exception exception)
    {
        lock (disposalGate)
        {
            if (disposed != 0)
            {
                return;
            }

            LastCommandException = exception;
            CommandErrorMessage = Localization.GetString("Error.CommandFailed");
            OnPropertyChanged(nameof(LastCommandException));
            OnPropertyChanged(nameof(CommandErrorMessage));
        }
    }

    private async Task<bool> TryExecuteAsync(
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportCommandFailure(exception);
            return false;
        }
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void OnCubismEditorOutputStatusChanged(
        object? sender,
        CubismEditorOutputStatus status) => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(sender, cubismEditorOutput))
            {
                OnPropertyChanged(nameof(IsCubismEditorOutputEnabled));
                OnPropertyChanged(nameof(CubismEditorOutputStatusText));
                OnPropertyChanged(nameof(CubismEditorOutputEndpointText));
                OnPropertyChanged(nameof(CubismEditorOutputModelUidText));
                OnPropertyChanged(nameof(CubismEditorOutputInformationState));
            }
        });

    private static ImmutableArray<DestinationViewModel> CreateDestinations(LocalizationManager localization) =>
    [
        CreateDestination(NavigationDestination.Session, localization),
        CreateDestination(NavigationDestination.Collaboration, localization),
        CreateDestination(NavigationDestination.Model, localization),
        CreateDestination(NavigationDestination.Scene, localization),
        CreateDestination(NavigationDestination.Tracking, localization),
        CreateDestination(NavigationDestination.Mapping, localization),
        CreateDestination(NavigationDestination.Effects, localization),
        CreateDestination(NavigationDestination.Output, localization),
        CreateDestination(NavigationDestination.Shortcuts, localization),
        CreateDestination(NavigationDestination.Settings, localization),
        CreateDestination(NavigationDestination.Developer, localization),
    ];

    private static DestinationViewModel CreateDestination(
        NavigationDestination destination,
        LocalizationManager localization)
    {
        string stableName = destination.ToString();
        return new DestinationViewModel(
            destination,
            localization.GetString($"Navigation.{stableName}"),
            localization.GetString($"Accessibility.Navigation.{stableName}"));
    }

    public sealed record DestinationViewModel(
        NavigationDestination Id,
        string Label,
        string AccessibilityName);

    public sealed record MenuSelection(int Level, string NodeId);

    public sealed record TrackingSourceSelection(TrackingChannel Channel, string? SourceId);

    private static partial class IFacialMocapSelectionLog
    {
        [LoggerMessage(6610, LogLevel.Information, "iFacialMocap source selection requires local configuration")]
        internal static partial void ConfigurationRequired(ILogger logger);

        [LoggerMessage(6611, LogLevel.Information, "iFacialMocap local configuration applied before source selection; selected={Selected}")]
        internal static partial void LocalConfigurationApplied(ILogger logger, bool selected);
    }

    private static partial class TrackingSelectionStartupLog
    {
        [LoggerMessage(6613, LogLevel.Information, "Tracking selection initialized; remember face source {RememberFaceSource}, persisted face source present {HasPersistedFaceSource}")]
        internal static partial void Initialized(
            ILogger logger,
            bool rememberFaceSource,
            bool hasPersistedFaceSource);
    }

    private sealed class DelegateCommand(Action<object?> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute(parameter);
    }

    private sealed class AsyncDelegateCommand : IAsyncCommand
    {
        private readonly Func<object?, CancellationToken, Task> execute;
        private readonly Action<Exception> reportFailure;
        private int executionState;

        public AsyncDelegateCommand(
            Func<CancellationToken, Task> execute,
            Action<Exception> reportFailure)
            : this((_, cancellationToken) => execute(cancellationToken), reportFailure)
        {
        }

        public AsyncDelegateCommand(
            Func<object?, CancellationToken, Task> execute,
            Action<Exception> reportFailure)
        {
            this.execute = execute;
            this.reportFailure = reportFailure;
        }

        public AsyncDelegateCommand(
            ILogOperations logOperations,
            Func<ILogOperations, CancellationToken, Task> operation,
            Action<Exception> reportFailure)
            : this((_, cancellationToken) => operation(logOperations, cancellationToken), reportFailure)
        {
        }

        public event EventHandler? CanExecuteChanged;

        public bool IsExecuting => Volatile.Read(ref executionState) != 0;

        public bool CanExecute(object? parameter) => !IsExecuting;

        public void Execute(object? parameter) => _ = ExecuteAsync(parameter, CancellationToken.None);

        public async Task ExecuteAsync(object? parameter, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref executionState, 1, 0) != 0)
            {
                return;
            }

            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                await execute(parameter, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                reportFailure(exception);
            }
            finally
            {
                Volatile.Write(ref executionState, 0);
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private sealed class LatestWinsAsyncDelegateCommand : IAsyncCommand
    {
        private readonly object gate = new();
        private readonly Func<object?, CancellationToken, Task> execute;
        private readonly Action<Exception> reportFailure;
        private CancellationTokenSource? currentCancellation;

        public LatestWinsAsyncDelegateCommand(
            Func<object?, CancellationToken, Task> execute,
            Action<Exception> reportFailure)
        {
            this.execute = execute;
            this.reportFailure = reportFailure;
        }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool IsExecuting
        {
            get
            {
                lock (gate)
                {
                    return currentCancellation is not null;
                }
            }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _ = ExecuteAsync(parameter, CancellationToken.None);

        public async Task ExecuteAsync(object? parameter, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using CancellationTokenSource operationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationTokenSource? previousCancellation;
            lock (gate)
            {
                previousCancellation = currentCancellation;
                currentCancellation = operationCancellation;
            }

            TryCancel(previousCancellation);
            try
            {
                await execute(parameter, operationCancellation.Token);
            }
            catch (OperationCanceledException) when (
                operationCancellation.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                reportFailure(exception);
            }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(currentCancellation, operationCancellation))
                    {
                        currentCancellation = null;
                    }
                }
            }
        }

        private static void TryCancel(CancellationTokenSource? cancellation)
        {
            if (cancellation is null)
            {
                return;
            }

            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}

internal static partial class ShortcutRuntimeLog
{
    [LoggerMessage(
        2066,
        LogLevel.Debug,
        "Shortcut target context built with {ModelCount} models, {TrackingSourceCount} tracking sources, and {BackgroundCount} backgrounds")]
    internal static partial void TargetsBuilt(
        ILogger logger,
        int modelCount,
        int trackingSourceCount,
        int backgroundCount);
}
