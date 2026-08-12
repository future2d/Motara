using System.Collections.Immutable;
using Motara.ModelLibrary;
using Motara.ModelRuntime.Abstractions;
using Motara.Scene;

namespace Motara.App.Shortcuts;

internal sealed record ShortcutTargetOption(string Id, string DisplayName);

internal sealed record ShortcutTargetContext(
    ModelDescriptor? ActiveModel,
    SceneWorkspace? Workspace,
    ImmutableArray<ShortcutTargetOption> ModelTargets,
    ImmutableArray<ShortcutTargetOption> TrackingSourceTargets,
    ImmutableArray<ShortcutTargetOption> BackgroundTargets)
{
    internal static ShortcutTargetContext Create(
        ModelDescriptor? activeModel,
        SceneWorkspace? workspace) => new(activeModel, workspace, [], [], []);
}

internal static class ShortcutTargetCatalog
{
    internal const string NoTrackingSourceId = "tracking:none";
    internal const string SharedBackgroundId = "background:shared";

    internal static string ImageBackgroundId(string assetId) => $"background:image:{assetId}";

    internal static string VideoBackgroundId(string assetId) => $"background:video:{assetId}";

    internal static ImmutableArray<ShortcutTargetOption> Build(
        ShortcutActionDefinition action,
        ModelDescriptor? model,
        SceneWorkspace? workspace) => Build(
            action,
            ShortcutTargetContext.Create(model, workspace));

    internal static ImmutableArray<ShortcutTargetOption> Build(
        ShortcutActionDefinition action,
        ShortcutTargetContext context,
        Func<string, string>? localize = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);
        localize ??= static key => key;
        return action.TargetKind switch
        {
            ShortcutTargetKind.Transform =>
            [
                new("transform:move:left", localize("Shortcut.Target.Transform.MoveLeft")),
                new("transform:move:right", localize("Shortcut.Target.Transform.MoveRight")),
                new("transform:move:up", localize("Shortcut.Target.Transform.MoveUp")),
                new("transform:move:down", localize("Shortcut.Target.Transform.MoveDown")),
                new("transform:rotate:left", localize("Shortcut.Target.Transform.RotateLeft")),
                new("transform:rotate:right", localize("Shortcut.Target.Transform.RotateRight")),
                new("transform:scale:up", localize("Shortcut.Target.Transform.ScaleUp")),
                new("transform:scale:down", localize("Shortcut.Target.Transform.ScaleDown")),
            ],
            ShortcutTargetKind.Motion => WithOptionalNone(
                Assets(context.ActiveModel?.Motions ?? [], "motions", ".motion3.json"),
                action.TargetPolicy == ShortcutTargetPolicy.RequiredWithNone,
                "motion:none",
                localize("Shortcut.Target.NoneMotion")),
            ShortcutTargetKind.Expression => Assets(context.ActiveModel?.Expressions ?? [], "exps", ".exp3.json"),
            ShortcutTargetKind.Scene => WithOptionalNone(
                context.Workspace is null
                    ? []
                    : context.Workspace.Scenes.Select(scene => new ShortcutTargetOption(
                        scene.Id.Value.ToString("N"), scene.DisplayName)).ToImmutableArray(),
                action.TargetPolicy == ShortcutTargetPolicy.RequiredWithNone,
                "scene:none",
                localize("Shortcut.Target.NoneScene")),
            ShortcutTargetKind.SceneSource => SceneSources(context.Workspace),
            ShortcutTargetKind.Model => context.ModelTargets,
            ShortcutTargetKind.TrackingSource => context.TrackingSourceTargets,
            ShortcutTargetKind.Background => context.BackgroundTargets,
            _ => [],
        };
    }

    private static ImmutableArray<ShortcutTargetOption> Assets(
        ImmutableArray<ModelAuxiliaryAsset> assets,
        string canonicalDirectory,
        string suffix) => assets
        .Where(asset => IsCanonicalAssetPath(asset.AssetId, canonicalDirectory, suffix))
        .Select(asset => new ShortcutTargetOption(asset.AssetId, asset.Name))
        .ToImmutableArray();

    private static bool IsCanonicalAssetPath(string assetId, string directory, string suffix)
    {
        string normalized = assetId.Replace('\\', '/');
        return normalized.StartsWith(directory + "/", StringComparison.Ordinal)
            && normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && !normalized.Contains("../", StringComparison.Ordinal)
            && !normalized.Contains("/./", StringComparison.Ordinal);
    }

    private static ImmutableArray<ShortcutTargetOption> WithOptionalNone(
        ImmutableArray<ShortcutTargetOption> options,
        bool includeNone,
        string noneId,
        string noneName) => includeNone
            ? [new ShortcutTargetOption(noneId, noneName), .. options]
            : options;

    private static ImmutableArray<ShortcutTargetOption> SceneSources(SceneWorkspace? workspace)
    {
        if (workspace is null) return [];
        SceneDocument scene = workspace.ActiveScene;
        var targets = ImmutableArray.CreateBuilder<ShortcutTargetOption>();
        if (scene.MainModel is { } mainModel)
            targets.Add(new ShortcutTargetOption(mainModel.SourceId.ToString("N"), mainModel.ModelAssetId));
        targets.AddRange(scene.Attachments.Select(attachment => new ShortcutTargetOption(
            attachment.SourceId.ToString("N"), attachment.DisplayName)));
        return targets.ToImmutable();
    }
}
