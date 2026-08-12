using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Persistence;

namespace Motara.App.Shortcuts;

internal sealed record ShortcutConflict(
    string Gesture,
    ShortcutEntry Winner,
    ShortcutEntry Suppressed);

internal interface ILayeredShortcutStore
{
    Task SaveEntriesAsync(ImmutableArray<ShortcutEntry> entries, CancellationToken cancellationToken);
}

internal sealed record LayeredShortcutSnapshot(
    ImmutableArray<ShortcutEntry> AllEntries,
    ShortcutProfile ActiveProfile,
    ImmutableArray<ShortcutConflict> Conflicts)
{
    internal static LayeredShortcutSnapshot Resolve(IEnumerable<ShortcutEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ImmutableArray<ShortcutEntry> allEntries = entries.ToImmutableArray();
        if (allEntries.Select(static entry => entry.Id).Distinct().Count() != allEntries.Length)
            throw new ArgumentException("Shortcut instance IDs must be unique across layers.", nameof(entries));

        var winnersByGesture = new Dictionary<string, ShortcutEntry>(StringComparer.Ordinal);
        var active = ImmutableArray.CreateBuilder<ShortcutEntry>();
        var conflicts = ImmutableArray.CreateBuilder<ShortcutConflict>();
        foreach (ShortcutEntry entry in allEntries
            .OrderBy(static entry => entry.Owner))
        {
            if (winnersByGesture.TryGetValue(entry.Gesture.CanonicalText, out ShortcutEntry? winner))
            {
                conflicts.Add(new ShortcutConflict(entry.Gesture.CanonicalText, winner, entry));
                continue;
            }

            winnersByGesture.Add(entry.Gesture.CanonicalText, entry);
            active.Add(entry);
        }

        return new LayeredShortcutSnapshot(
            allEntries,
            ShortcutProfile.Create(active),
            conflicts.ToImmutable());
    }
}

internal sealed class LayeredShortcutStore : IShortcutStore, ILayeredShortcutStore
{
    private readonly IShortcutStore software;
    private readonly IShortcutStore? scene;
    private readonly IShortcutStore? model;
    private readonly ILogger<LayeredShortcutStore> logger;

    internal LayeredShortcutStore(
        IShortcutStore software,
        IShortcutStore? scene,
        IShortcutStore? model,
        ILogger<LayeredShortcutStore>? logger = null)
    {
        this.software = software ?? throw new ArgumentNullException(nameof(software));
        this.scene = scene;
        this.model = model;
        this.logger = logger ?? NullLogger<LayeredShortcutStore>.Instance;
    }

    public async Task<ShortcutProfile> LoadAsync(CancellationToken cancellationToken)
        => (await LoadSnapshotAsync(cancellationToken).ConfigureAwait(false)).ActiveProfile;

    internal async Task<LayeredShortcutSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        ShortcutProfile softwareProfile = await software.LoadAsync(cancellationToken).ConfigureAwait(false);
        ShortcutProfile sceneProfile = scene is null
            ? ShortcutProfile.Default
            : await scene.LoadAsync(cancellationToken).ConfigureAwait(false);
        ShortcutProfile modelProfile = model is null
            ? ShortcutProfile.Default
            : await model.LoadAsync(cancellationToken).ConfigureAwait(false);
        LayeredShortcutSnapshot snapshot = LayeredShortcutSnapshot.Resolve(
            softwareProfile.Entries
                .Concat(sceneProfile.Entries)
                .Concat(modelProfile.Entries));
        LogConflicts(snapshot.Conflicts);
        LayeredShortcutStoreLog.Loaded(
            logger,
            softwareProfile.Entries.Length,
            sceneProfile.Entries.Length,
            modelProfile.Entries.Length,
            snapshot.ActiveProfile.Entries.Length,
            snapshot.Conflicts.Length);
        return snapshot;
    }

    public async Task SaveAsync(ShortcutProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await SaveEntriesAsync(profile.Entries, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveEntriesAsync(
        ImmutableArray<ShortcutEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.IsDefault)
            throw new ArgumentException("Shortcut entries must be initialized.", nameof(entries));
        LayeredShortcutSnapshot snapshot = LayeredShortcutSnapshot.Resolve(entries);
        LogConflicts(snapshot.Conflicts);
        await software.SaveAsync(Filter(entries, ShortcutOwnerKind.Software), cancellationToken).ConfigureAwait(false);
        if (scene is not null)
            await scene.SaveAsync(Filter(entries, ShortcutOwnerKind.Scene), cancellationToken).ConfigureAwait(false);
        if (model is not null)
            await model.SaveAsync(Filter(entries, ShortcutOwnerKind.Model), cancellationToken).ConfigureAwait(false);
        LayeredShortcutStoreLog.Saved(
            logger,
            entries.Length,
            snapshot.ActiveProfile.Entries.Length,
            snapshot.Conflicts.Length,
            scene is not null,
            model is not null);
    }

    private void LogConflicts(ImmutableArray<ShortcutConflict> conflicts)
    {
        foreach (ShortcutConflict conflict in conflicts)
        {
            LayeredShortcutStoreLog.Suppressed(
                logger,
                conflict.Gesture,
                conflict.Winner.Id,
                conflict.Winner.Owner.ToString(),
                conflict.Suppressed.Id,
                conflict.Suppressed.Owner.ToString());
        }
    }

    private static ShortcutProfile Filter(
        ImmutableArray<ShortcutEntry> entries,
        ShortcutOwnerKind owner) =>
        ShortcutProfile.Create(entries.Where(entry => entry.Owner == owner));
}

internal static partial class LayeredShortcutStoreLog
{
    [LoggerMessage(2053, LogLevel.Information,
        "Shortcut layers loaded: software {SoftwareCount}, scene {SceneCount}, model {ModelCount}; active {ActiveCount}, suppressed conflicts {SuppressedCount}")]
    internal static partial void Loaded(
        ILogger logger,
        int softwareCount,
        int sceneCount,
        int modelCount,
        int activeCount,
        int suppressedCount);

    [LoggerMessage(2054, LogLevel.Information,
        "Shortcut layers saved: {ConfiguredCount} configured, {ActiveCount} active, {SuppressedCount} suppressed; scene layer {HasScene}; model layer {HasModel}")]
    internal static partial void Saved(
        ILogger logger,
        int configuredCount,
        int activeCount,
        int suppressedCount,
        bool hasScene,
        bool hasModel);

    [LoggerMessage(2055, LogLevel.Warning,
        "Shortcut gesture {Gesture} kept {WinnerId} ({WinnerOwner}) and suppressed {SuppressedId} ({SuppressedOwner})")]
    internal static partial void Suppressed(
        ILogger logger,
        string gesture,
        Guid winnerId,
        string winnerOwner,
        Guid suppressedId,
        string suppressedOwner);
}
