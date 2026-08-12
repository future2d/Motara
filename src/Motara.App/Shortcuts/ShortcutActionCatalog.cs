using System.Collections.Immutable;
using Motara.App.Input;
using Motara.ModelLibrary;
using Motara.ModelRuntime.Abstractions;
using Motara.Persistence;

namespace Motara.App.Shortcuts;

internal enum ShortcutTargetKind
{
    None,
    Transform,
    Motion,
    Expression,
    Scene,
    SceneSource,
    Model,
    TrackingSource,
    Background,
}

internal enum ShortcutTargetPolicy
{
    None,
    Required,
    RequiredWithNone,
}

internal sealed record ShortcutActionDefinition(
    string ActionKind,
    ShortcutOwnerKind Owner,
    ShortcutTargetKind TargetKind,
    string NameResourceKey,
    bool AllowsGlobalRegistration,
    ShortcutTargetPolicy TargetPolicy);

internal static class ShortcutActionCatalog
{
    internal static ImmutableArray<ShortcutActionDefinition> BuildDefinitions(InputActionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var actions = ImmutableArray.CreateBuilder<ShortcutActionDefinition>();
        Add(ShortcutOwnerKind.Model, "Model.Motion.Play", ShortcutTargetKind.Motion, "Input.Action.Model.Motion.Play", ShortcutTargetPolicy.Required);
        Add(ShortcutOwnerKind.Model, "Model.Motion.SetIdle", ShortcutTargetKind.Motion, "Input.Action.Model.Motion.SetIdle", ShortcutTargetPolicy.RequiredWithNone);
        Add(ShortcutOwnerKind.Model, "Model.Expression.Toggle", ShortcutTargetKind.Expression, "Input.Action.Model.Expression.Toggle", ShortcutTargetPolicy.Required);
        Add(ShortcutOwnerKind.Model, "Model.Expression.ClearAll", ShortcutTargetKind.None, "Input.Action.Model.Expression.ClearAll", ShortcutTargetPolicy.None);
        Add(ShortcutOwnerKind.Scene, "Scene.Background.Change", ShortcutTargetKind.Background, "Input.Action.Scene.Background.Change", ShortcutTargetPolicy.Required);
        Add(ShortcutOwnerKind.Scene, "Scene.Model.Change", ShortcutTargetKind.Model, "Input.Action.Scene.Model.Change", ShortcutTargetPolicy.Required);
        Add(ShortcutOwnerKind.Scene, "Scene.Source.Toggle", ShortcutTargetKind.SceneSource, "Input.Action.Scene.Source.Toggle", ShortcutTargetPolicy.Required);
        Add(ShortcutOwnerKind.Software, "Software.TrackingSource.Switch", ShortcutTargetKind.TrackingSource, "Input.Action.Software.TrackingSource.Switch", ShortcutTargetPolicy.Required);
        Add(ShortcutOwnerKind.Software, "Software.Model.Transform", ShortcutTargetKind.Transform, "Input.Action.Software.Model.Transform", ShortcutTargetPolicy.Required);
        Add(ShortcutOwnerKind.Software, "Software.Scene.Change", ShortcutTargetKind.Scene, "Input.Action.Software.Scene.Change", ShortcutTargetPolicy.RequiredWithNone);
        Add(ShortcutOwnerKind.Software, "Software.Camera.Calibrate", ShortcutTargetKind.None, "Input.Action.Software.Camera.Calibrate", ShortcutTargetPolicy.None);
        Add(ShortcutOwnerKind.Software, "Software.Screenshot", ShortcutTargetKind.None, "Input.Action.Software.Screenshot", ShortcutTargetPolicy.None);

        foreach (InputActionDescriptor descriptor in registry.Descriptors)
        {
            if (!IsUserEditableDescriptor(descriptor.Id)) continue;
            if (actions.Any(action => StringComparer.Ordinal.Equals(action.ActionKind, descriptor.Id))) continue;
            actions.Add(new ShortcutActionDefinition(
                descriptor.Id,
                ShortcutOwnerKind.Software,
                ShortcutTargetKind.None,
                descriptor.NameResourceKey,
                descriptor.AllowsGlobalRegistration,
                ShortcutTargetPolicy.None));
        }
        return actions.ToImmutable();

        void Add(
            ShortcutOwnerKind owner,
            string actionKind,
            ShortcutTargetKind targetKind,
            string nameResourceKey,
            ShortcutTargetPolicy targetPolicy) => actions.Add(new ShortcutActionDefinition(
                actionKind,
                owner,
                targetKind,
                nameResourceKey,
                AllowsGlobalRegistration: true,
                targetPolicy));
    }

    internal static ImmutableArray<InputActionDescriptor> Build(
        InputActionRegistry registry,
        ModelDescriptor? model,
        Motara.Scene.SceneWorkspace? workspace) => Build(
            registry,
            ShortcutTargetContext.Create(model, workspace));

    internal static ImmutableArray<InputActionDescriptor> Build(
        InputActionRegistry registry,
        ShortcutTargetContext context)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(context);
        var actions = ImmutableArray.CreateBuilder<InputActionDescriptor>();
        actions.AddRange(registry.Descriptors);
        actions.Add(CreateAction("Model.Expression.ClearAll", "Input.Action.Model.Expression.ClearAll", "Input.Category.Model"));
        actions.Add(CreateAction("Scene.Background.Change", "Input.Action.Scene.Background.Change", "Input.Category.Scene"));
        actions.Add(CreateAction("Scene.Model.Change", "Input.Action.Scene.Model.Change", "Input.Category.Scene"));
        actions.Add(CreateAction("Software.TrackingSource.Switch", "Input.Action.Software.TrackingSource.Switch", "Input.Category.Software"));
        actions.Add(CreateAction("Software.Camera.Calibrate", "Input.Action.Software.Camera.Calibrate", "Input.Category.Software"));
        foreach (string target in new[]
        {
            "move:left", "move:right", "move:up", "move:down",
            "rotate:left", "rotate:right", "scale:up", "scale:down",
        })
        {
            actions.Add(CreateAction(
                $"Software.Model.Transform/transform:{target}",
                "Input.Action.Software.Model.Transform",
                "Input.Category.Software"));
        }
        if (context.Workspace is not null)
        {
            foreach (Motara.Scene.SceneDocument scene in context.Workspace.Scenes)
                actions.Add(CreateAction($"Software.Scene.Change/{scene.Id.Value:N}", "Input.Action.Software.Scene.Change", "Input.Category.Software"));
            Motara.Scene.SceneDocument activeScene = context.Workspace.ActiveScene;
            if (activeScene.MainModel is { } mainModel)
                actions.Add(CreateAction($"Scene.Source.Toggle/{mainModel.SourceId:N}", "Input.Action.Scene.Source.Toggle", "Input.Category.Scene"));
            foreach (Motara.Scene.AttachmentInstance attachment in activeScene.Attachments)
                actions.Add(CreateAction($"Scene.Source.Toggle/{attachment.SourceId:N}", "Input.Action.Scene.Source.Toggle", "Input.Category.Scene"));
        }

        if (context.ActiveModel is not null)
        {
            foreach (ModelAuxiliaryAsset motion in context.ActiveModel.Motions)
            {
                actions.Add(CreateModelAction(
                    $"Model.Motion.Play/{motion.AssetId}",
                    "Input.Action.Model.Motion.Play"));
                actions.Add(CreateModelAction(
                    $"Model.Motion.SetIdle/{motion.AssetId}",
                    "Input.Action.Model.Motion.SetIdle"));
            }
            actions.Add(CreateModelAction(
                "Model.Motion.SetIdle/motion:none",
                "Input.Action.Model.Motion.SetIdle"));

            foreach (ModelAuxiliaryAsset expression in context.ActiveModel.Expressions)
            {
                actions.Add(CreateModelAction(
                    $"Model.Expression.Toggle/{expression.AssetId}",
                    "Input.Action.Model.Expression.Toggle"));
            }
        }

        foreach (ShortcutTargetOption target in context.ModelTargets)
            actions.Add(CreateAction(
                $"Scene.Model.Change/{target.Id}",
                "Input.Action.Scene.Model.Change",
                "Input.Category.Scene"));
        actions.Add(CreateAction(
            "Software.Scene.Change/scene:none",
            "Input.Action.Software.Scene.Change",
            "Input.Category.Software"));
        foreach (ShortcutTargetOption target in context.TrackingSourceTargets)
            actions.Add(CreateAction(
                $"Software.TrackingSource.Switch/{target.Id}",
                "Input.Action.Software.TrackingSource.Switch",
                "Input.Category.Software"));
        foreach (ShortcutTargetOption target in context.BackgroundTargets)
            actions.Add(CreateAction(
                $"Scene.Background.Change/{target.Id}",
                "Input.Action.Scene.Background.Change",
                "Input.Category.Scene"));

        return actions.ToImmutable();
    }

    private static bool IsUserEditableDescriptor(string id) =>
        !id.StartsWith("Menu.", StringComparison.Ordinal)
        && !id.StartsWith("MenuWorkspace.", StringComparison.Ordinal)
        && !id.StartsWith("MenuColumn.", StringComparison.Ordinal)
        && !id.StartsWith("Canvas.", StringComparison.Ordinal)
        && !StringComparer.Ordinal.Equals(id, BuiltInInputActions.CaptureScreenshot);

    private static InputActionDescriptor CreateModelAction(
        string id,
        string nameResourceKey) => CreateAction(id, nameResourceKey, "Input.Category.Model");

    private static InputActionDescriptor CreateAction(
        string id,
        string nameResourceKey,
        string categoryResourceKey,
        ImmutableArray<InputBinding> defaultBindings = default) =>
        new(
            id,
            nameResourceKey,
            categoryResourceKey,
            [InputBindingScope.Global, InputBindingScope.Application],
            defaultBindings.IsDefault ? [] : defaultBindings,
            allowsGlobalRegistration: true,
            allowsExternalSequence: false);
}
