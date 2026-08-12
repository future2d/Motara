using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Tracking;

namespace Motara.App.ViewModels;

internal sealed class OpenSeeFaceConfigurationViewModel : INotifyPropertyChanged
{
    private readonly IOpenSeeFaceConfigurationStore store;
    private readonly Action<OpenSeeFaceConfiguration> configureSource;
    private readonly Func<CancellationToken, Task<IReadOnlyList<OpenSeeFaceCamera>>> listCamerasAsync;
    private readonly Func<string, CancellationToken, Task<bool>> selectSourceAsync;
    private readonly ILogger<OpenSeeFaceConfigurationViewModel> logger;
    private IReadOnlyList<OpenSeeFaceCamera> cameras = Array.Empty<OpenSeeFaceCamera>();
    private int selectedCameraIndex;
    private string widthText = "640";
    private string heightText = "360";
    private string fpsText = "24";
    private string? errorResourceKey;
    private bool isLoading;
    private bool isRefreshing;
    private bool isSubmitting;

    internal OpenSeeFaceConfigurationViewModel(
        IOpenSeeFaceConfigurationStore store,
        OpenSeeFaceConfiguration defaultConfiguration,
        Action<OpenSeeFaceConfiguration> configureSource,
        Func<CancellationToken, Task<IReadOnlyList<OpenSeeFaceCamera>>> listCamerasAsync,
        Func<string, CancellationToken, Task<bool>> selectSourceAsync,
        ILogger<OpenSeeFaceConfigurationViewModel>? logger = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        OpenSeeFaceConfiguration.Validate(defaultConfiguration);
        selectedCameraIndex = defaultConfiguration.CameraIndex;
        widthText = defaultConfiguration.Width.ToString(CultureInfo.InvariantCulture);
        heightText = defaultConfiguration.Height.ToString(CultureInfo.InvariantCulture);
        fpsText = defaultConfiguration.Fps.ToString(CultureInfo.InvariantCulture);
        this.configureSource = configureSource ?? throw new ArgumentNullException(nameof(configureSource));
        this.listCamerasAsync = listCamerasAsync ?? throw new ArgumentNullException(nameof(listCamerasAsync));
        this.selectSourceAsync = selectSourceAsync ?? throw new ArgumentNullException(nameof(selectSourceAsync));
        this.logger = logger ?? NullLogger<OpenSeeFaceConfigurationViewModel>.Instance;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal IReadOnlyList<OpenSeeFaceCamera> Cameras
    {
        get => cameras;
        private set => SetField(ref cameras, value);
    }

    internal int SelectedCameraIndex
    {
        get => selectedCameraIndex;
        set => SetField(ref selectedCameraIndex, value);
    }

    internal string WidthText
    {
        get => widthText;
        set => SetField(ref widthText, value ?? string.Empty);
    }

    internal string HeightText
    {
        get => heightText;
        set => SetField(ref heightText, value ?? string.Empty);
    }

    internal string FpsText
    {
        get => fpsText;
        set => SetField(ref fpsText, value ?? string.Empty);
    }

    internal string? ErrorResourceKey
    {
        get => errorResourceKey;
        private set => SetField(ref errorResourceKey, value);
    }

    internal bool IsLoading
    {
        get => isLoading;
        private set => SetField(ref isLoading, value);
    }

    internal bool IsRefreshing
    {
        get => isRefreshing;
        private set => SetField(ref isRefreshing, value);
    }

    internal bool IsSubmitting
    {
        get => isSubmitting;
        private set => SetField(ref isSubmitting, value);
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        ErrorResourceKey = null;
        try
        {
            OpenSeeFaceConfiguration? configuration = await store
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (configuration is not null)
            {
                ApplyConfiguration(configuration);
            }

            await RefreshCamerasAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ErrorResourceKey = "Workspace.Tracking.OpenSeeFace.Error.Load";
            OpenSeeFaceConfigurationWorkspaceLog.Failed(
                logger,
                "Load",
                exception.GetType().Name);
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal async Task RefreshCamerasAsync(CancellationToken cancellationToken)
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            IReadOnlyList<OpenSeeFaceCamera> discovered = await listCamerasAsync(cancellationToken)
                .ConfigureAwait(false);
            Cameras = discovered;
            if (discovered.Count == 0)
            {
                ErrorResourceKey = "Workspace.Tracking.OpenSeeFace.Error.NoCamera";
            }
            else if (!discovered.Any(camera => camera.Index == SelectedCameraIndex))
            {
                SelectedCameraIndex = discovered[0].Index;
                ErrorResourceKey = null;
            }
            else
            {
                ErrorResourceKey = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ErrorResourceKey = "Workspace.Tracking.OpenSeeFace.Error.CameraList";
            OpenSeeFaceConfigurationWorkspaceLog.Failed(
                logger,
                "ListCameras",
                exception.GetType().Name);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    internal async Task<bool> SaveAndStartAsync(CancellationToken cancellationToken)
    {
        if (IsSubmitting)
        {
            return false;
        }

        if (!TryCreateConfiguration(out OpenSeeFaceConfiguration configuration))
        {
            return false;
        }

        IsSubmitting = true;
        ErrorResourceKey = null;
        try
        {
            await store.SaveAsync(configuration, cancellationToken).ConfigureAwait(false);
            configureSource(configuration);
            bool started = await selectSourceAsync(
                OpenSeeFaceLocalTrackingSourceFactory.SourceId,
                cancellationToken).ConfigureAwait(false);
            if (!started)
            {
                ErrorResourceKey = "Workspace.Tracking.OpenSeeFace.Error.Start";
                OpenSeeFaceConfigurationWorkspaceLog.Failed(logger, "Start", "Unavailable");
                return false;
            }

            OpenSeeFaceConfigurationWorkspaceLog.Started(logger);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ErrorResourceKey = "Workspace.Tracking.OpenSeeFace.Error.SaveOrStart";
            OpenSeeFaceConfigurationWorkspaceLog.Failed(
                logger,
                "SaveOrStart",
                exception.GetType().Name);
            return false;
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private void ApplyConfiguration(OpenSeeFaceConfiguration configuration)
    {
        SelectedCameraIndex = configuration.CameraIndex;
        WidthText = configuration.Width.ToString(CultureInfo.InvariantCulture);
        HeightText = configuration.Height.ToString(CultureInfo.InvariantCulture);
        FpsText = configuration.Fps.ToString(CultureInfo.InvariantCulture);
    }

    private bool TryCreateConfiguration(out OpenSeeFaceConfiguration configuration)
    {
        configuration = null!;
        if (!int.TryParse(WidthText, NumberStyles.None, CultureInfo.InvariantCulture, out int width)
            || !int.TryParse(HeightText, NumberStyles.None, CultureInfo.InvariantCulture, out int height)
            || !int.TryParse(FpsText, NumberStyles.None, CultureInfo.InvariantCulture, out int fps))
        {
            ErrorResourceKey = "Workspace.Tracking.OpenSeeFace.Error.Parameters";
            return false;
        }

        try
        {
            configuration = OpenSeeFaceConfiguration.Create(
                SelectedCameraIndex,
                width,
                height,
                fps);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            ErrorResourceKey = "Workspace.Tracking.OpenSeeFace.Error.Parameters";
            return false;
        }
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal static partial class OpenSeeFaceConfigurationWorkspaceLog
{
    [LoggerMessage(6713, LogLevel.Information, "OpenSeeFace configuration saved and source started")]
    internal static partial void Started(ILogger logger);

    [LoggerMessage(6714, LogLevel.Warning, "OpenSeeFace configuration operation {Operation} failed with {ErrorType}")]
    internal static partial void Failed(ILogger logger, string operation, string errorType);
}
