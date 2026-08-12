using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Models;
using Motara.ModelLibrary;

namespace Motara.App.ViewModels;

internal sealed class ModelSourceMappingReviewViewModel : INotifyPropertyChanged
{
    private readonly string modelDirectory;
    private readonly string modelName;
    private readonly ImmutableHashSet<string> supportedAdapters;
    private readonly MotaraModelConfigurationStore store;
    private readonly ILogger logger;
    private ImmutableArray<ModelSourceMappingCandidate> pending = [];
    private MotaraModelConfiguration? configuration;
    private bool isLoading = true;
    private int isDeciding;

    internal ModelSourceMappingReviewViewModel(
        string modelDirectory,
        string modelName,
        IEnumerable<string> supportedAdapters,
        ILogger? logger = null)
    {
        this.modelDirectory = Path.GetFullPath(modelDirectory);
        this.modelName = modelName;
        this.supportedAdapters = supportedAdapters.ToImmutableHashSet(StringComparer.Ordinal);
        store = new MotaraModelConfigurationStore(this.modelDirectory, modelName);
        this.logger = logger ?? NullLogger.Instance;
        AcceptCommand = new AsyncCommand(AcceptAsync);
        DeclineCommand = new AsyncCommand(DeclineAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal ModelSourceMappingCandidate? PendingCandidate => pending.IsEmpty ? null : pending[0];

    internal bool IsLoading
    {
        get => isLoading;
        private set
        {
            if (isLoading != value)
            {
                isLoading = value;
                Raise();
                Raise(nameof(CanAssignModel));
            }
        }
    }

    internal bool IsReviewVisible => PendingCandidate is not null;

    internal bool CanAssignModel => !IsLoading && !IsReviewVisible;

    internal ICommand AcceptCommand { get; }

    internal ICommand DeclineCommand { get; }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            configuration = await store.LoadAsync(cancellationToken).ConfigureAwait(false)
                ?? CreateDefaultConfiguration();
            ImmutableArray<ModelSourceMappingCandidate> discovered =
                await ModelSourceMappingDiscovery.DiscoverAsync(modelDirectory, cancellationToken)
                    .ConfigureAwait(false);
            pending = discovered
                .Where(candidate => supportedAdapters.Contains(candidate.AdapterId))
                .Where(candidate => !configuration.SourceMappingSelections.Any(selection =>
                    SameIdentity(selection, candidate)))
                .ToImmutableArray();
            ModelSourceMappingReviewLog.Discovered(logger, discovered.Length, pending.Length);
        }
        finally
        {
            IsLoading = false;
            Raise(nameof(PendingCandidate));
            Raise(nameof(IsReviewVisible));
            Raise(nameof(CanAssignModel));
        }
    }

    internal Task AcceptAsync(CancellationToken cancellationToken) =>
        RecordDecisionAsync(isEnabled: true, cancellationToken);

    internal Task DeclineAsync(CancellationToken cancellationToken) =>
        RecordDecisionAsync(isEnabled: false, cancellationToken);

    private async Task RecordDecisionAsync(bool isEnabled, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref isDeciding, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (PendingCandidate is not { } candidate || configuration is null)
            {
                return;
            }

            var selection = new ModelSourceMappingSelection(
                candidate.VendorId,
                candidate.TechnologyId,
                candidate.AdapterId,
                candidate.ProfileId,
                candidate.FileName,
                isEnabled,
                candidate.Channel);
            configuration = configuration with
            {
                SourceMappingSelections = configuration.SourceMappingSelections.Add(selection),
            };
            await store.SaveAsync(configuration, cancellationToken).ConfigureAwait(false);
            pending = pending.RemoveAt(0);
            ModelSourceMappingReviewLog.Decided(logger, candidate.AdapterId, isEnabled);
            Raise(nameof(PendingCandidate));
            Raise(nameof(IsReviewVisible));
            Raise(nameof(CanAssignModel));
        }
        finally
        {
            Volatile.Write(ref isDeciding, 0);
        }
    }

    private MotaraModelConfiguration CreateDefaultConfiguration() =>
        MotaraModelConfiguration.Create(
            modelName,
            StandardModelParameterMappings.All.Select(mapping =>
            {
                Motara.Core.Parameters.ParameterDefinition definition =
                    Motara.Core.Parameters.StandardParameterCatalog.Definitions
                        .Single(candidate => candidate.Id == mapping.SourceParameterId);
                return new ModelParameterSettingConfiguration(
                    mapping.ModelParameterId,
                    mapping.SourceParameterId,
                    definition.SuggestedMinimum,
                    definition.SuggestedMaximum,
                    definition.SuggestedMinimum,
                    definition.SuggestedMaximum,
                    ClampInput: false,
                    ClampOutput: false,
                    EnableAutoBlink: mapping.ModelParameterId is "ParamEyeLOpen" or "ParamEyeROpen",
                    EnableAutoBreath: mapping.ModelParameterId == "ParamBreath");
            }));

    private static bool SameIdentity(
        ModelSourceMappingSelection selection,
        ModelSourceMappingCandidate candidate) =>
        StringComparer.Ordinal.Equals(selection.VendorId, candidate.VendorId)
        && StringComparer.Ordinal.Equals(selection.TechnologyId, candidate.TechnologyId)
        && StringComparer.Ordinal.Equals(selection.AdapterId, candidate.AdapterId)
        && StringComparer.Ordinal.Equals(selection.ProfileId, candidate.ProfileId)
        && StringComparer.Ordinal.Equals(selection.Channel, candidate.Channel);

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class AsyncCommand(Func<CancellationToken, Task> execute) : ICommand
    {
        private int executing;

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => Volatile.Read(ref executing) == 0;

        public async void Execute(object? parameter)
        {
            if (Interlocked.CompareExchange(ref executing, 1, 0) != 0)
            {
                return;
            }

            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                await execute(CancellationToken.None);
            }
            finally
            {
                Volatile.Write(ref executing, 0);
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}

internal static partial class ModelSourceMappingReviewLog
{
    [LoggerMessage(6630, LogLevel.Information,
        "Model source mappings discovered: total={CandidateCount}; pending={PendingCount}")]
    internal static partial void Discovered(ILogger logger, int candidateCount, int pendingCount);

    [LoggerMessage(6631, LogLevel.Information,
        "Model source mapping reviewed: adapter={AdapterId}; enabled={IsEnabled}")]
    internal static partial void Decided(ILogger logger, string adapterId, bool isEnabled);
}
