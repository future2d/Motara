using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Models;
using Motara.App.Shell;

namespace Motara.App.ViewModels;

internal enum ModelPhysicsSettingsApplyResult
{
    Success,
    ValidationFailed,
    StorageFailure,
}

internal sealed class ModelPhysicsSettingsViewModel : INotifyPropertyChanged, IWorkspaceCloseGuard
{
    private readonly Func<ModelPhysicsConfiguration, CancellationToken, Task> saveAsync;
    private readonly ILogger logger;
    private ModelPhysicsConfiguration baseline;
    private bool isEnabled;
    private double strength;
    private double windSimulation;
    private double dragPhysics;
    private PhysicsCalculationFrameRate calculationFrameRate;
    private bool motionExpansionEnabled;
    private double motionExpansionX;
    private double motionExpansionY;
    private double motionExpansionZ;
    private bool isCloseConfirmationVisible;

    internal ModelPhysicsSettingsViewModel(
        ModelPhysicsConfiguration configuration,
        Func<ModelPhysicsConfiguration, CancellationToken, Task> saveAsync,
        ILogger? logger = null)
    {
        baseline = configuration ?? throw new ArgumentNullException(nameof(configuration));
        baseline.Validate();
        this.saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
        this.logger = logger ?? NullLogger.Instance;
        isEnabled = configuration.Enabled;
        strength = configuration.Strength;
        windSimulation = configuration.WindSimulation;
        dragPhysics = configuration.DragPhysics;
        calculationFrameRate = configuration.CalculationFrameRate;
        motionExpansionEnabled = configuration.MotionExpansionEnabled;
        motionExpansionX = configuration.MotionExpansionX;
        motionExpansionY = configuration.MotionExpansionY;
        motionExpansionZ = configuration.MotionExpansionZ;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool IsEnabled
    {
        get => isEnabled;
        set => Set(ref isEnabled, value);
    }

    internal double Strength
    {
        get => strength;
        set => SetInteger(ref strength, value);
    }

    internal double WindSimulation
    {
        get => windSimulation;
        set => SetInteger(ref windSimulation, value);
    }

    internal double DragPhysics
    {
        get => dragPhysics;
        set => SetInteger(ref dragPhysics, value);
    }

    internal PhysicsCalculationFrameRate CalculationFrameRate
    {
        get => calculationFrameRate;
        set => Set(ref calculationFrameRate, value);
    }

    internal bool MotionExpansionEnabled
    {
        get => motionExpansionEnabled;
        set => Set(ref motionExpansionEnabled, value);
    }

    internal double MotionExpansionX
    {
        get => motionExpansionX;
        set => SetInteger(ref motionExpansionX, value);
    }

    internal double MotionExpansionY
    {
        get => motionExpansionY;
        set => SetInteger(ref motionExpansionY, value);
    }

    internal double MotionExpansionZ
    {
        get => motionExpansionZ;
        set => SetInteger(ref motionExpansionZ, value);
    }

    internal bool IsDirty => !CreateConfiguration().Equals(baseline);

    internal bool IsCloseConfirmationVisible
    {
        get => isCloseConfirmationVisible;
        private set => Set(ref isCloseConfirmationVisible, value);
    }

    internal async Task<ModelPhysicsSettingsApplyResult> ApplyAsync(CancellationToken cancellationToken)
    {
        ModelPhysicsConfiguration configuration;
        try
        {
            configuration = CreateConfiguration();
            configuration.Validate();
        }
        catch (ArgumentException)
        {
            return ModelPhysicsSettingsApplyResult.ValidationFailed;
        }

        try
        {
            await saveAsync(configuration, cancellationToken).ConfigureAwait(false);
            baseline = configuration;
            Raise(nameof(IsDirty));
            ModelPhysicsSettingsLog.Applied(
                logger,
                configuration.Enabled,
                configuration.Strength,
                configuration.WindSimulation,
                configuration.DragPhysics,
                configuration.CalculationFrameRate,
                configuration.MotionExpansionEnabled);
            return ModelPhysicsSettingsApplyResult.Success;
        }
        catch (IOException exception)
        {
            ModelPhysicsSettingsLog.ApplyFailed(logger, exception);
            return ModelPhysicsSettingsApplyResult.StorageFailure;
        }
        catch (UnauthorizedAccessException exception)
        {
            ModelPhysicsSettingsLog.ApplyFailed(logger, exception);
            return ModelPhysicsSettingsApplyResult.StorageFailure;
        }
    }

    public Task<bool> RequestCloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsDirty)
        {
            return Task.FromResult(true);
        }

        IsCloseConfirmationVisible = true;
        return Task.FromResult(false);
    }

    internal void CancelClose() => IsCloseConfirmationVisible = false;

    internal void DiscardAndClose()
    {
        isEnabled = baseline.Enabled;
        strength = baseline.Strength;
        windSimulation = baseline.WindSimulation;
        dragPhysics = baseline.DragPhysics;
        calculationFrameRate = baseline.CalculationFrameRate;
        motionExpansionEnabled = baseline.MotionExpansionEnabled;
        motionExpansionX = baseline.MotionExpansionX;
        motionExpansionY = baseline.MotionExpansionY;
        motionExpansionZ = baseline.MotionExpansionZ;
        IsCloseConfirmationVisible = false;
        Raise(nameof(IsEnabled));
        Raise(nameof(Strength));
        Raise(nameof(WindSimulation));
        Raise(nameof(DragPhysics));
        Raise(nameof(CalculationFrameRate));
        Raise(nameof(MotionExpansionEnabled));
        Raise(nameof(MotionExpansionX));
        Raise(nameof(MotionExpansionY));
        Raise(nameof(MotionExpansionZ));
        Raise(nameof(IsDirty));
    }

    private ModelPhysicsConfiguration CreateConfiguration() => new(
        isEnabled,
        strength,
        windSimulation,
        dragPhysics,
        calculationFrameRate,
        motionExpansionEnabled,
        motionExpansionX,
        motionExpansionY,
        motionExpansionZ);

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        Raise(nameof(IsDirty));
        return true;
    }

    private void SetInteger(
        ref double field,
        double value,
        [CallerMemberName] string? name = null)
    {
        double truncated = double.IsFinite(value) ? Math.Truncate(value) : value;
        if (!Set(ref field, truncated, name) && !value.Equals(truncated))
        {
            Raise(name);
        }
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal static partial class ModelPhysicsSettingsLog
{
    [LoggerMessage(6540, LogLevel.Information, "Model physics settings applied; enabled={Enabled}, strength={Strength}, windSimulation={WindSimulation}, dragPhysics={DragPhysics}, calculationFrameRate={CalculationFrameRate}, motionExpansionEnabled={MotionExpansionEnabled}")]
    internal static partial void Applied(
        ILogger logger,
        bool enabled,
        double strength,
        double windSimulation,
        double dragPhysics,
        PhysicsCalculationFrameRate calculationFrameRate,
        bool motionExpansionEnabled);

    [LoggerMessage(6541, LogLevel.Warning, "Model physics settings save failed")]
    internal static partial void ApplyFailed(ILogger logger, Exception exception);
}
