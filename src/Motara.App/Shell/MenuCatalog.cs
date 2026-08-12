using System.Collections.Immutable;
using System.Windows.Input;
using Motara.App.Collaboration;
using Motara.App.Shortcuts;
using Motara.App.Tracking;
using Motara.App.ViewModels;
using Motara.ModelLibrary;
using Motara.ModelRuntime.Abstractions;
using Motara.Media;
using Motara.Persistence;
using Motara.Scene;
using Motara.Tracking.Abstractions;
using Motara.Tracking.iFacialMocap;

namespace Motara.App.Shell;

/// <summary>Projects current application state into immutable business menu nodes.</summary>
public sealed class MenuCatalog
{
    private readonly MainWindowViewModel viewModel;

    public MenuCatalog(MainWindowViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    public ImmutableArray<MenuNode> GetRootNodes(NavigationDestination destination) => destination switch
    {
        NavigationDestination.Session => GetSessionNodes(),
        NavigationDestination.Model => GetModelNodes(),
        NavigationDestination.Scene => GetSceneNodes(),
        NavigationDestination.Tracking => GetTrackingNodes(),
        NavigationDestination.Mapping => GetMappingNodes(),
        NavigationDestination.Effects => GetEffectNodes(),
        NavigationDestination.Output => GetOutputNodes(),
        NavigationDestination.Shortcuts => GetShortcutNodes(),
        NavigationDestination.Settings =>
            [
                Submenu(
                    "settings.language",
                    "Menu.Settings.Language",
                    "Icon.Lucide.Languages",
                    [
                        Radio(
                            "settings.language.simplified-chinese",
                            "Menu.Settings.Language.SimplifiedChinese",
                            "Icon.Lucide.CircleDot",
                            viewModel.SetApplicationLanguageCommand,
                            ApplicationLanguage.SimplifiedChinese,
                            isSelected: viewModel.Localization.Culture.Name == "zh-CN"),
                        Radio(
                            "settings.language.english",
                            "Menu.Settings.Language.English",
                            "Icon.Lucide.CircleDot",
                            viewModel.SetApplicationLanguageCommand,
                            ApplicationLanguage.English,
                            isSelected: viewModel.Localization.Culture.Name == "en-US"),
                    ]),
                Separator("settings.developer"),
                Toggle(
                    "settings.developer-mode",
                    "Menu.Settings.DeveloperMode",
                    "Icon.Lucide.Wrench",
                    viewModel.IsDeveloperModeEnabled,
                    value => viewModel.TrySetDeveloperModeAsync(value, CancellationToken.None)),
            ],
        NavigationDestination.Developer => GetDeveloperNodes(),
        _ => [],
    };

    public MenuLevelGroup GetRootLevel(NavigationDestination destination)
    {
        if (destination == NavigationDestination.Session)
        {
            ImmutableArray<MenuNode> nodes = GetSessionNodes();
            MenuNode Find(string id) => nodes.Single(node => StringComparer.Ordinal.Equals(node.Id, id));
            return new MenuLevelGroup(
                "session.root",
                [
                    new MenuColumn(
                        "session.summary",
                        "Menu.Session.Column.Summary",
                        [
                            Find("session.scene"),
                            Find("session.main-model"),
                            Find("session.mapping"),
                            Find("session.output"),
                        ]),
                    new MenuColumn(
                        "session.activity",
                        "Menu.Session.Column.Activity",
                        [
                            Find("session.tracking.face"),
                            Find("session.tracking.hand"),
                            Find("session.tracking.body"),
                            Find("session.actions"),
                            Find("session.start"),
                            Find("session.stop"),
                        ]),
                ]);
        }

        if (destination == NavigationDestination.Scene)
        {
            ImmutableArray<MenuNode> nodes = GetSceneNodes();
            int sourceIndex = -1;
            for (int index = 0; index < nodes.Length; index++)
            {
                if (StringComparer.Ordinal.Equals(nodes[index].Id, "scene.main-model"))
                {
                    sourceIndex = index;
                    break;
                }
            }

            if (sourceIndex < 0)
            {
                return new MenuLevelGroup(
                    "scene.root",
                    [
                        new MenuColumn("scene.related", "Menu.Scene.Column.Scene", nodes),
                        new MenuColumn(
                            "scene.sources",
                            "Menu.Scene.Column.Sources",
                            [Status("scene.sources.empty", "Menu.Scene.NoActiveSource", "Icon.Lucide.Layers")]),
                    ]);
            }

            return new MenuLevelGroup(
                "scene.root",
                [
                    new MenuColumn("scene.related", "Menu.Scene.Column.Scene", nodes.Take(sourceIndex)),
                    new MenuColumn("scene.sources", "Menu.Scene.Column.Sources", nodes.Skip(sourceIndex + 1)),
                ]);
        }

        if (destination == NavigationDestination.Tracking)
        {
            ImmutableArray<MenuNode> nodes = GetTrackingNodes();
            return new MenuLevelGroup(
                "tracking.root",
                [
                    new MenuColumn("tracking.face", "Menu.Tracking.Column.Face", nodes[0].Children),
                    new MenuColumn(
                        "tracking.hand",
                        "Menu.Tracking.Column.Hand",
                        [Status("tracking.hand.empty", "Menu.Tracking.HandReserved", "Icon.Lucide.Hand")]),
                    new MenuColumn(
                        "tracking.body",
                        "Menu.Tracking.Column.Body",
                        [Status("tracking.body.empty", "Menu.Tracking.BodyReserved", "Icon.Lucide.PersonStanding")]),
                ]);
        }

        return MenuLevelGroup.SingleColumn(
            $"{destination.ToString().ToLowerInvariant()}.root",
            $"Navigation.{destination}",
            GetRootNodes(destination));
    }

    private ImmutableArray<MenuNode> GetSessionNodes()
    {
        string sceneLabel = viewModel.PresentedSceneId is SceneId sceneId
            ? viewModel.CurrentSceneWorkspace.Scenes
                .Single(scene => scene.Id == sceneId)
                .DisplayName
            : viewModel.Localization.GetString("Menu.Session.NoScene");
        string modelLabel = viewModel.CurrentMainModelId is ModelId modelId
            ? viewModel.ModelCatalog.Entries.FirstOrDefault(entry => entry.Id == modelId)?.DisplayName
                ?? modelId.Value
            : viewModel.Localization.GetString("Menu.Session.NoMainModel");

        return
        [
            InformationBlock(
                "session.scene",
                "Menu.Session.Scene",
                "Icon.Lucide.Layers",
                [
                    new MenuStatusField("Menu.Session.Field.Name", sceneLabel),
                    new MenuStatusField(
                        "Menu.Session.Field.Status",
                        viewModel.Localization.GetString(viewModel.PresentedSceneId is null
                            ? "Menu.Session.Status.Inactive"
                            : "Menu.Session.Status.Active")),
                ]),
            InformationBlock(
                "session.main-model",
                "Menu.Session.MainModel",
                "Icon.Lucide.User",
                [
                    new MenuStatusField("Menu.Session.Field.Name", modelLabel),
                    new MenuStatusField(
                        "Menu.Session.Field.Status",
                        viewModel.Localization.GetString(viewModel.CurrentMainModelId is null
                            ? "Menu.Session.Status.NotLoaded"
                            : "Menu.Session.Status.Loaded")),
                    new MenuStatusField(
                        "Menu.Session.Field.ModelFrameRate",
                        viewModel.CurrentMainModelFrameRate is double framesPerSecond
                            ? FormatFrameRate(framesPerSecond)
                            : "--"),
                ]),
            CreateSessionTrackingBlock(
                "session.tracking.face",
                "Menu.Session.FaceTracking",
                "Icon.Lucide.ScanFace",
                viewModel.FaceTrackingSourceStatus),
            CreateSessionTrackingBlock(
                "session.tracking.hand",
                "Menu.Session.HandTracking",
                "Icon.Lucide.Hand",
                viewModel.HandTrackingSourceStatus),
            InformationBlock(
                "session.mapping",
                "Menu.Session.Mapping",
                "Icon.Lucide.Waypoints",
                [
                    new MenuStatusField(
                        "Menu.Session.Field.SourceMapping",
                        viewModel.Localization.GetString(viewModel.FaceTrackingSourceStatus.IntendedSourceId is null
                            ? "Menu.Mapping.NoActiveSource"
                            : "Menu.Mapping.DefaultProfile")),
                    new MenuStatusField(
                        "Menu.Session.Field.ModelMapping",
                        GetCurrentModelMappingValue()),
                ]),
            InformationBlock(
                "session.output",
                "Menu.Session.Output",
                "Icon.Lucide.MonitorUp",
                [
                    new MenuStatusField(
                        "Menu.Session.Field.ParameterOutput",
                        viewModel.Localization.GetString("Menu.Output.NotEnabled")),
                    new MenuStatusField(
                        "Menu.Session.Field.VideoOutput",
                        viewModel.Localization.GetString("Menu.Output.NotEnabled")),
                    new MenuStatusField(
                        "Menu.Session.Field.WindowFrameRate",
                        viewModel.CurrentWindowPresentationFrameRate is double windowFramesPerSecond
                            ? FormatFrameRate(windowFramesPerSecond)
                            : "--"),
                ]),
            CreateSessionTrackingBlock(
                "session.tracking.body",
                "Menu.Session.BodyTracking",
                "Icon.Lucide.PersonStanding",
                TrackingSourceStatus.Empty),
            Separator("session.actions"),
            Command("session.start", "Menu.Session.Start", "Icon.Lucide.Play", viewModel.StartSessionCommand),
            Command("session.stop", "Menu.Session.Stop", "Icon.Lucide.Square", viewModel.StopSessionCommand),
        ];
    }

    private ImmutableArray<MenuNode> GetShortcutNodes()
    {
        InputBindingWorkspaceViewModel? workspace = viewModel.ShortcutMenuWorkspace;
        var nodes = ImmutableArray.CreateBuilder<MenuNode>();
        nodes.Add(MenuNode.CreateTextInput(
            "shortcuts.search",
            "Workspace.InputBindings.Search",
            workspace?.SearchQuery ?? string.Empty,
            "Workspace.InputBindings.Search",
            value => workspace?.SetSearchQuery(value)));

        foreach (ShortcutOwnerKind owner in new[]
                 {
                     ShortcutOwnerKind.Model,
                     ShortcutOwnerKind.Scene,
                     ShortcutOwnerKind.Software,
                 })
        {
            string ownerId = owner.ToString().ToLowerInvariant();
            string titleKey = owner switch
            {
                ShortcutOwnerKind.Model => "Workspace.InputBindings.Section.Model",
                ShortcutOwnerKind.Scene => "Workspace.InputBindings.Section.Scene",
                _ => "Workspace.InputBindings.Section.Software",
            };
            ShortcutSectionViewModel? section = workspace?.Sections.Single(item => item.Owner == owner);
            MenuNode heading = SectionHeading($"shortcuts.section.{ownerId}", titleKey);
            if (section is not null)
            {
                heading = heading with
                {
                    SectionActions = new MenuSectionActions(
                        section.TotalCount,
                        section.IsExpanded,
                        CanCreateShortcut(owner),
                        () => workspace!.ToggleSection(owner),
                        () => viewModel.CreateShortcutMenuEntry(owner)),
                };
            }
            nodes.Add(heading);

            if (section is not { IsExpanded: true }) continue;
            foreach (ShortcutRowViewModel row in section.Rows)
            {
                nodes.Add(CreateShortcutRow(workspace!, row));
            }

            if (workspace is { IsCreating: true, EditorOwner: { } editorOwner }
                && editorOwner == owner)
            {
                nodes.Add(CreateShortcutDraftRow(workspace, owner));
            }
        }

        return nodes.ToImmutable();
    }

    private bool CanCreateShortcut(ShortcutOwnerKind owner) => owner switch
    {
        ShortcutOwnerKind.Model => viewModel.CurrentMainModelId is not null,
        ShortcutOwnerKind.Scene => viewModel.PresentedSceneId is not null,
        ShortcutOwnerKind.Software => true,
        _ => false,
    };

    private MenuNode CreateShortcutRow(
        InputBindingWorkspaceViewModel workspace,
        ShortcutRowViewModel row)
    {
        bool isSelected = workspace.SelectedEntryId == row.Id;
        ImmutableArray<MenuNode> children = isSelected
            ? CreateShortcutEditorNodes(workspace, $"shortcuts.entry.{row.Id:N}")
            : [Status(
                $"shortcuts.entry.{row.Id:N}.select",
                "Workspace.InputBindings.SelectPrompt",
                "Icon.Lucide.Keyboard")];
        return Submenu(
            $"shortcuts.entry.{row.Id:N}",
            row.Name,
            "Icon.Lucide.Keyboard",
            children) with
        {
            IsLiteralLabel = true,
            SecondaryText = row.GestureText,
            InformationState = row.IsSuppressed
                ? MenuInformationState.Warning
                : MenuInformationState.Neutral,
            BeforeOpen = () => workspace.Select(row.Id),
        };
    }

    private MenuNode CreateShortcutDraftRow(
        InputBindingWorkspaceViewModel workspace,
        ShortcutOwnerKind owner)
    {
        string id = $"shortcuts.draft.{owner.ToString().ToLowerInvariant()}";
        return Submenu(
            id,
            workspace.EditorName,
            "Icon.Lucide.Keyboard",
            CreateShortcutEditorNodes(workspace, id)) with
        {
            IsLiteralLabel = true,
            SecondaryText = workspace.EditorGesture is { } gesture
                ? ShortcutGestureFormatter.Format(gesture)
                : viewModel.Localization.GetString("Workspace.InputBindings.GestureUnset"),
        };
    }

    private ImmutableArray<MenuNode> CreateShortcutEditorNodes(
        InputBindingWorkspaceViewModel workspace,
        string menuNodeId)
    {
        var nodes = ImmutableArray.CreateBuilder<MenuNode>();
        nodes.Add(MenuNode.CreateTextInput(
            "shortcuts.name",
            "Workspace.InputBindings.Name",
            workspace.EditorName,
            "Workspace.InputBindings.NamePlaceholder",
            workspace.SetEditorName));
        nodes.Add(MenuNode.CreateChoice(
            "shortcuts.action",
            "Workspace.InputBindings.Action",
            workspace.EditorActions.Select(action => new MenuChoiceOption(
                action.ActionKind,
                viewModel.Localization.GetString(action.NameResourceKey))),
            workspace.EditorActionKind,
            workspace.SelectEditorAction));

        ShortcutActionDefinition? selectedAction = workspace.EditorActions.FirstOrDefault(action =>
            StringComparer.Ordinal.Equals(action.ActionKind, workspace.EditorActionKind));
        if (selectedAction?.TargetKind != ShortcutTargetKind.None)
        {
            nodes.Add(MenuNode.CreateChoice(
                "shortcuts.target",
                "Workspace.InputBindings.Target",
                workspace.EditorTargets.Select(target => new MenuChoiceOption(target.Id, target.DisplayName)),
                workspace.EditorTargetId,
                workspace.SelectEditorTarget));
        }

        nodes.Add(MenuNode.CreateInputCapture(
            "shortcuts.capture",
            "Workspace.InputBindings.Gesture",
            workspace.EditorGesture,
            workspace.SetEditorGesture));
        nodes.Add(Toggle(
            "shortcuts.global",
            "Workspace.InputBindings.GlobalToggle",
            "Icon.Lucide.Settings",
            workspace.EditorGlobal,
            value =>
            {
                workspace.SetEditorGlobal(value);
                return Task.FromResult(true);
            },
            isEnabled: selectedAction?.AllowsGlobalRegistration == true));

        ShortcutEditorError displayedError = workspace.EditorError != ShortcutEditorError.None
            ? workspace.EditorError
            : workspace.EditorIsSuppressed
                ? ShortcutEditorError.GestureConflict
                : ShortcutEditorError.None;
        if (displayedError != ShortcutEditorError.None)
        {
            nodes.Add(Status(
                "shortcuts.error",
                $"Workspace.InputBindings.Error.{displayedError}",
                "Icon.Lucide.Bug"));
        }

        nodes.Add(Separator("shortcuts.actions"));
        nodes.Add(Command(
            "shortcuts.cancel",
            "Command.Cancel",
            "Icon.Lucide.X") with
        {
            ActionAsync = () =>
            {
                viewModel.CloseShortcutMenuEditor();
                return Task.FromResult(true);
            },
        });
        nodes.Add(Command(
            "shortcuts.confirm",
            "Command.Confirm",
            "Icon.Lucide.CircleDot") with
        {
            ActionAsync = async () =>
            {
                bool saved = await workspace.ConfirmEditorAsync(CancellationToken.None);
                if (saved && workspace.SelectedEntryId is Guid savedId
                    && menuNodeId.StartsWith("shortcuts.draft.", StringComparison.Ordinal))
                {
                    CloseShortcutSubmenu(menuNodeId);
                    viewModel.Navigation.SelectMenuNode(0, $"shortcuts.entry.{savedId:N}");
                }
                return saved;
            },
        });
        if (!workspace.IsCreating)
        {
            nodes.Add(Command(
                "shortcuts.delete",
                "Command.Delete",
                "Icon.Lucide.Trash2") with
            {
                ActionAsync = async () =>
                {
                    await workspace.DeleteSelectedAsync(CancellationToken.None);
                    CloseShortcutSubmenu(menuNodeId);
                    return true;
                },
            });
        }
        return nodes.ToImmutable();
    }

    private void CloseShortcutSubmenu(string menuNodeId)
    {
        if (!viewModel.Navigation.SelectedMenuPath.IsEmpty
            && StringComparer.Ordinal.Equals(viewModel.Navigation.SelectedMenuPath[0], menuNodeId))
        {
            viewModel.Navigation.SelectMenuNode(0, menuNodeId);
        }
    }

    private MenuNode CreateSessionTrackingBlock(
        string id,
        string titleResourceKey,
        string iconResourceKey,
        TrackingSourceStatus status) =>
        InformationBlock(
            id,
            titleResourceKey,
            iconResourceKey,
            GetTrackingStatusFields(status),
            GetInformationState(status));

    private static MenuInformationState GetInformationState(TrackingSourceStatus status) => status.State switch
    {
        TrackingSourceRunState.Running => MenuInformationState.Positive,
        TrackingSourceRunState.Switching or TrackingSourceRunState.Stopping => MenuInformationState.Warning,
        TrackingSourceRunState.Faulted => MenuInformationState.Error,
        _ => MenuInformationState.Neutral,
    };

    private ImmutableArray<MenuNode> GetEffectNodes()
    {
        bool hasSelectedSource = viewModel.SelectedSceneSourceId.HasValue;
        string sourceLabel = "Menu.Effects.SourceUnavailable";
        bool isLiteralLabel = false;
        if (hasSelectedSource && viewModel.CurrentMainModelId is ModelId modelId)
        {
            sourceLabel = viewModel.ModelCatalog.Entries
                .FirstOrDefault(entry => entry.Id == modelId)?.DisplayName
                ?? modelId.Value;
            isLiteralLabel = true;
        }

        return
        [
            Submenu(
                "effects.global",
                "Menu.Effects.Global",
                "Icon.Lucide.WandSparkles",
                CreateEffectScopeNodes("effects.global")),
            Submenu(
                "effects.scene",
                "Menu.Effects.Scene",
                "Icon.Lucide.Layers",
                CreateSceneEffectScopeNodes()),
            Submenu(
                "effects.source",
                "Menu.Effects.Source",
                "Icon.Lucide.User",
                [
                    Status(
                        "effects.source.current",
                        sourceLabel,
                        "Icon.Lucide.User",
                        isLiteralLabel: isLiteralLabel),
                    Status("effects.source.applied", "Menu.Effects.NoneApplied", "Icon.Lucide.WandSparkles"),
                    Command(
                        "effects.source.add",
                        "Menu.Effects.Add",
                        "Icon.Lucide.Plus",
                        isEnabled: false,
                        helpTextResourceKey: "Menu.Common.NotImplemented"),
                    Command(
                        "effects.source.edit",
                        "Menu.Effects.Edit",
                        "Icon.Lucide.Pencil",
                        isEnabled: false,
                        helpTextResourceKey: "Menu.Common.NotImplemented"),
                ],
                isEnabled: hasSelectedSource),
        ];
    }

    private static ImmutableArray<MenuNode> CreateEffectScopeNodes(string scopeId) =>
    [
        Status($"{scopeId}.applied", "Menu.Effects.NoneApplied", "Icon.Lucide.WandSparkles"),
        Command(
            $"{scopeId}.add",
            "Menu.Effects.Add",
            "Icon.Lucide.Plus",
            isEnabled: false,
            helpTextResourceKey: "Menu.Common.NotImplemented"),
        Command(
            $"{scopeId}.edit",
            "Menu.Effects.Edit",
            "Icon.Lucide.Pencil",
            isEnabled: false,
            helpTextResourceKey: "Menu.Common.NotImplemented"),
    ];

    private ImmutableArray<MenuNode> CreateSceneEffectScopeNodes()
    {
        SceneEffectInstance? blur = viewModel.CurrentSceneWorkspace.ActiveScene.Effects
            .FirstOrDefault(effect => effect.EffectId == "builtin.blur");
        return
        [
            Status(
                "effects.scene.applied",
                blur is null ? "Menu.Effects.NoneApplied" : "Menu.Effects.Blur",
                "Icon.Lucide.WandSparkles"),
            Command(
                "effects.scene.add",
                "Menu.Effects.Add",
                "Icon.Lucide.Plus",
                viewModel.OpenSceneEffectEditorCommand,
                isEnabled: blur is null),
            Command(
                "effects.scene.edit",
                "Menu.Effects.Edit",
                "Icon.Lucide.Pencil",
                viewModel.OpenSceneEffectEditorCommand,
                isEnabled: blur is not null),
        ];
    }

    private ImmutableArray<MenuNode> GetMappingNodes()
    {
        bool hasSource = viewModel.FaceTrackingSourceStatus.IntendedSourceId is not null;
        string model = viewModel.CurrentMainModelId is ModelId modelId
            ? viewModel.ModelCatalog.Entries.FirstOrDefault(entry => entry.Id == modelId)?.DisplayName
                ?? modelId.Value
            : viewModel.Localization.GetString("Menu.Mapping.NoCurrentModel");
        return
        [
            InformationBlock(
                "mapping.source",
                "Menu.Mapping.Source",
                "Icon.Lucide.Activity",
                [
                    new MenuStatusField("Menu.Mapping.Field.ActiveSource", GetCurrentMappingSourceValue()),
                    new MenuStatusField(
                        "Menu.Mapping.Field.ActiveProfile",
                        viewModel.Localization.GetString("Menu.Mapping.DefaultProfile")),
                    new MenuStatusField(
                        "Menu.Mapping.Field.Configuration",
                        viewModel.Localization.GetString(hasSource
                            ? "Menu.Mapping.Status.Ready"
                            : "Menu.Mapping.Status.Unavailable")),
                ],
                hasSource ? MenuInformationState.Positive : MenuInformationState.Neutral,
                hasSource ? null : "Menu.Mapping.NoActiveSource"),
            Command(
                "mapping.source.edit",
                "Menu.Mapping.EditSource",
                "Icon.Lucide.Pencil",
                viewModel.OpenSourceMappingEditorCommand),
            Separator("mapping.separator.model"),
            InformationBlock(
                "mapping.model",
                "Menu.Mapping.Model",
                "Icon.Lucide.User",
                [
                    new MenuStatusField("Menu.Mapping.Field.ModelName", model),
                    new MenuStatusField(
                        "Menu.Mapping.Field.ModelProfile",
                        GetCurrentModelMappingValue()),
                ],
                viewModel.CurrentModelMappingBindingCount > 0
                    ? MenuInformationState.Positive
                    : MenuInformationState.Neutral,
                viewModel.CurrentMainModelId is null ? "Menu.Mapping.RequiresModel" : null),
            Command("mapping.model.edit", "Menu.Mapping.EditModel", "Icon.Lucide.Pencil",
                viewModel.OpenModelParameterMappingCommand,
                isEnabled: viewModel.CanEditCurrentModelMapping,
                helpTextResourceKey: viewModel.CanEditCurrentModelMapping ? null : "Menu.Mapping.RequiresModel"),
            Command("mapping.model.auto-match", "Menu.Mapping.AutoMatch", "Icon.Lucide.WandSparkles",
                viewModel.OpenModelParameterMappingCommand,
                isEnabled: viewModel.CanEditCurrentModelMapping,
                helpTextResourceKey: viewModel.CanEditCurrentModelMapping ? null : "Menu.Mapping.RequiresModel"),
            Separator("mapping.separator.status"),
            InformationBlock(
                "mapping.status",
                "Menu.Mapping.Status",
                "Icon.Lucide.Activity",
                [
                    new MenuStatusField(
                        "Menu.Mapping.Field.Connected",
                        viewModel.CurrentSessionSnapshot.Parameters.Count(parameter =>
                            parameter.Validity == ParameterValidity.Valid).ToString(viewModel.Localization.Culture)),
                    new MenuStatusField(
                        "Menu.Mapping.Field.Missing",
                        viewModel.CurrentSessionSnapshot.Parameters.Count(parameter =>
                            parameter.Validity != ParameterValidity.Valid).ToString(viewModel.Localization.Culture)),
                ]),
            Command("mapping.status.issues", "Menu.Mapping.ViewIssues", "Icon.Lucide.ChartSpline",
                isEnabled: false, helpTextResourceKey: "Menu.Common.NotImplemented"),
        ];
    }

    private string GetCurrentMappingSourceValue()
    {
        string? sourceId = viewModel.FaceTrackingSourceStatus.IntendedSourceId;
        if (sourceId is null)
        {
            return viewModel.Localization.GetString("Menu.Mapping.NoActiveSource");
        }

        return StringComparer.Ordinal.Equals(sourceId, IFacialMocapTrackingSource.SourceId)
            ? viewModel.Localization.GetString("Menu.Mapping.CurrentIFacialMocap")
            : sourceId;
    }

    private string GetCurrentModelMappingValue() => viewModel.CurrentModelMappingBindingCount > 0
        ? string.Format(
            viewModel.Localization.Culture,
            viewModel.Localization.GetString("Menu.Mapping.ConfiguredFormat"),
            viewModel.CurrentModelMappingBindingCount)
        : viewModel.Localization.GetString("Menu.Mapping.NotConfigured");

    private ImmutableArray<MenuNode> GetOutputNodes() =>
    [
        Command(
            "output.screenshot",
            "Menu.Output.Screenshot",
            "Icon.Lucide.Camera",
            viewModel.OpenScreenshotWorkspaceCommand),
        Separator("output.capture-settings"),
        Command(
            "output.window-presentation",
            "Menu.Output.WindowPresentation",
            "Icon.Lucide.AppWindow",
            viewModel.OpenWindowPresentationSettingsCommand),
        Toggle(
            "output.lock-window-size",
            "Menu.Output.LockWindowSize",
            "Icon.Lucide.Lock",
            viewModel.IsWindowSizeLocked,
            value => viewModel.TrySetWindowSizeLockedAsync(value, CancellationToken.None)),
        Separator("output.parameter-separator"),
        Submenu(
            "output.cubism-editor",
            "Menu.Output.CubismEditor",
            "Icon.Lucide.MonitorUp",
            [
                Toggle(
                    "output.cubism-editor.enabled",
                    "Menu.Output.Enabled",
                    "Icon.Lucide.CircleDot",
                    viewModel.IsCubismEditorOutputEnabled,
                    value => viewModel.TrySetCubismEditorOutputEnabledAsync(value, CancellationToken.None)),
                Command(
                    "output.cubism-editor.connection",
                    "Menu.Output.Connection",
                    "Icon.Lucide.Settings",
                    viewModel.OpenCubismEditorOutputSettingsCommand),
                Command(
                    "output.cubism-editor.mapping",
                    "Menu.Output.ParameterMapping",
                    "Icon.Lucide.Waypoints",
                    viewModel.OpenCubismEditorMappingCommand),
                Toggle(
                    "output.cubism-editor.always-output",
                    "Menu.Output.Cubism.AlwaysOutput",
                    "Icon.Lucide.Activity",
                    viewModel.IsCubismEditorAlwaysOutput,
                    value => viewModel.TrySetCubismEditorAlwaysOutputAsync(value, CancellationToken.None)),
                InformationBlock(
                    "output.cubism-editor.information",
                    "Menu.Output.CubismEditor",
                    "Icon.Lucide.Activity",
                    [
                        new MenuStatusField(
                            "Menu.Output.Cubism.Field.Status",
                            viewModel.CubismEditorOutputStatusText),
                        new MenuStatusField(
                            "Menu.Output.Cubism.Field.Endpoint",
                            viewModel.CubismEditorOutputEndpointText),
                        new MenuStatusField(
                            "Menu.Output.Cubism.Field.EditorModel",
                            viewModel.CubismEditorOutputModelUidText),
                    ],
                    viewModel.CubismEditorOutputInformationState),
            ]),
        Command(
            "output.background",
            "Menu.Output.Background",
            "Icon.Lucide.Layers",
            viewModel.OpenGlobalBackgroundEditorCommand),
        Submenu(
            "output.video",
            "Menu.Output.Video",
            "Icon.Lucide.MonitorUp",
            [
                Command(
                    "output.video.independent-window",
                    "Menu.Output.IndependentWindow",
                    "Icon.Lucide.PanelLeft",
                    isEnabled: false,
                    helpTextResourceKey: "Menu.Common.NotImplemented"),
                Submenu(
                    "output.video.spout2",
                    "Menu.Output.Spout2",
                    "Icon.Lucide.MonitorUp",
                    [
                        Toggle(
                            "output.video.spout2.enabled",
                            "Menu.Output.Enabled",
                            "Icon.Lucide.CircleDot",
                            viewModel.IsSpout2VideoOutputEnabled,
                            value => viewModel.TrySetVideoSignalOutputEnabledAsync(
                                VideoSignalProtocol.Spout2,
                                value,
                                CancellationToken.None)),
                        Command(
                            "output.video.spout2.resolution",
                            "Menu.Output.CustomResolution",
                            "Icon.Lucide.MoveDiagonal2",
                            viewModel.OpenSpout2VideoOutputSettingsCommand),
                    ]),
                Submenu(
                    "output.video.ndi",
                    "Menu.Output.Ndi",
                    "Icon.Lucide.MonitorUp",
                    [
                        Toggle(
                            "output.video.ndi.enabled",
                            "Menu.Output.Enabled",
                            "Icon.Lucide.CircleDot",
                            viewModel.IsNdiVideoOutputEnabled,
                            value => viewModel.TrySetVideoSignalOutputEnabledAsync(
                                VideoSignalProtocol.Ndi,
                                value,
                                CancellationToken.None)),
                        Command(
                            "output.video.ndi.resolution",
                            "Menu.Output.CustomResolution",
                            "Icon.Lucide.MoveDiagonal2",
                            viewModel.OpenNdiVideoOutputSettingsCommand),
                    ]),
        ]),
    ];

    private string FormatFrameRate(double framesPerSecond) => string.Format(
        viewModel.Localization.Culture,
        "{0:F1} FPS",
        framesPerSecond);

    private static MenuNode DisabledToggle(string id, string labelResourceKey, string iconResourceKey) =>
        Toggle(
            id,
            labelResourceKey,
            iconResourceKey,
            value: false,
            _ => Task.FromResult(false),
            isEnabled: false,
            helpTextResourceKey: "Menu.Common.NotImplemented");

    private static MenuNode DisabledRadio(string id, string labelResourceKey) =>
        Radio(
            id,
            labelResourceKey,
            "Icon.Lucide.CircleDot",
            command: null,
            isEnabled: false,
            helpTextResourceKey: "Menu.Common.NotImplemented");

    private ImmutableArray<MenuNode> GetDeveloperNodes() =>
    [
        Command(
            "developer.parameter-priority",
            "Menu.Developer.ParameterPriority",
            "Icon.Lucide.SlidersHorizontal",
            viewModel.OpenParameterPriorityWorkspaceCommand),
        Submenu(
            "developer.identity-management",
            "Menu.Developer.IdentityManagement",
            "Icon.Lucide.User",
            [
                Command(
                    "developer.identity-management.export",
                    "Command.ExportCollaborationIdentity",
                    "Icon.Lucide.MonitorUp",
                    viewModel.OpenIdentityMigrationWorkspaceCommand,
                    IdentityMigrationMode.Export),
                Command(
                    "developer.identity-management.import",
                    "Command.ImportCollaborationIdentity",
                    "Icon.Lucide.FolderOpen",
                    viewModel.OpenIdentityMigrationWorkspaceCommand,
                    IdentityMigrationMode.Import),
            ],
            isEnabled: viewModel.CollaborationWorkspace is not null),
        Separator("developer.inspection"),
        Command("developer.raw-frame-inspection", "Menu.Developer.RawFrameInspection", "Icon.Lucide.FileScan"),
        Command("developer.pipeline-inspection", "Menu.Developer.PipelineInspection", "Icon.Lucide.ChartSpline"),
        Command("developer.trace-capture", "Menu.Developer.TraceCapture", "Icon.Lucide.ScrollText"),
        Separator("developer.diagnostics"),
        Toggle(
            "developer.model-rendering.gpu",
            "Menu.Developer.ModelRendering.Gpu",
            "Icon.Lucide.MonitorUp",
            viewModel.ModelRenderingBackendPreference == ModelRenderingBackendPreference.Gpu,
            value => viewModel.TrySetModelRenderingBackendPreferenceAsync(
                value ? ModelRenderingBackendPreference.Gpu : ModelRenderingBackendPreference.Cpu,
                CancellationToken.None)),
        Command("developer.fault-injection", "Menu.Developer.FaultInjection", "Icon.Lucide.Bug"),
        Command("developer.diagnostic-sampling", "Menu.Developer.DiagnosticSampling", "Icon.Lucide.Gauge"),
        Separator("developer.logging"),
        Submenu(
            "developer.debug-logging",
            "Menu.Developer.DebugLogging",
            "Icon.Lucide.Terminal",
            [
                Radio(
                    "developer.debug-logging.information",
                    "Menu.Developer.Logging.Information",
                    "Icon.Lucide.CircleDot",
                    viewModel.SetDiagnosticLogLevelCommand,
                    DiagnosticLogLevel.Information,
                    viewModel.DiagnosticLogLevel == DiagnosticLogLevel.Information),
                Radio(
                    "developer.debug-logging.debug",
                    "Menu.Developer.Logging.Debug",
                    "Icon.Lucide.CircleDot",
                    viewModel.SetDiagnosticLogLevelCommand,
                    DiagnosticLogLevel.Debug,
                    viewModel.DiagnosticLogLevel == DiagnosticLogLevel.Debug),
                Radio(
                    "developer.debug-logging.trace",
                    "Menu.Developer.Logging.Trace",
                    "Icon.Lucide.CircleDot",
                    viewModel.SetDiagnosticLogLevelCommand,
                    DiagnosticLogLevel.Trace,
                    viewModel.DiagnosticLogLevel == DiagnosticLogLevel.Trace),
                Separator("developer.debug-logging.actions"),
                Command(
                    "developer.debug-logging.open-folder",
                    "Menu.Developer.Logging.OpenFolder",
                    "Icon.Lucide.FolderOpen",
                    viewModel.OpenLogsFolderCommand),
                Command(
                    "developer.debug-logging.export",
                    "Menu.Developer.Logging.Export",
                    "Icon.Lucide.ScrollText",
                    viewModel.ExportDiagnosticLogsCommand),
            ]),
    ];

    private ImmutableArray<MenuNode> GetTrackingNodes()
    {
        TrackingSourceStatus status = viewModel.FaceTrackingSourceStatus;
        return
        [
            Submenu(
                "tracking.face",
                "Menu.Tracking.Face",
                "Icon.Lucide.ScanFace",
                CreateTrackingChannelNodes(TrackingChannel.Face, status)),
            Status(
                "tracking.hand",
                "Menu.Tracking.HandReserved",
                "Icon.Lucide.Hand"),
            Status(
                "tracking.body",
                "Menu.Tracking.BodyReserved",
                "Icon.Lucide.PersonStanding"),
        ];
    }

    private ImmutableArray<MenuNode> CreateTrackingChannelNodes(
        TrackingChannel channel,
        TrackingSourceStatus status)
    {
        string channelId = channel.ToString().ToLowerInvariant();
        string? intendedSourceId = status.IntendedSourceId
            ?? viewModel.TrackingChannelSelections.GetSourceId(channel);
        var nodes = new List<MenuNode>
        {
            Radio(
                $"tracking.{channelId}.source.none",
                "Menu.Tracking.None",
                "Icon.Lucide.CircleDot",
                viewModel.SelectTrackingSourceCommand,
                new MainWindowViewModel.TrackingSourceSelection(channel, null),
                isSelected: intendedSourceId is null),
        };

        if (channel == TrackingChannel.Face)
        {
            nodes.Add(CreateIFacialMocapNode(channelId, intendedSourceId));
            nodes.Add(CreateReservedFaceSourceNode(channelId, "facemotion3d", "Menu.Tracking.Source.FaceMotion3D"));
            nodes.Add(CreateReservedFaceSourceNode(channelId, "maxine", "Menu.Tracking.Source.Maxine"));
            nodes.Add(CreateReservedFaceSourceNode(channelId, "mediapipe", "Menu.Tracking.Source.MediaPipe"));
            if (viewModel.TrackingSourceRegistry is TrackingSourceRegistry faceRegistry
                && faceRegistry.TryGetFactory(
                    Motara.App.Tracking.OpenSeeFaceLocalTrackingSourceFactory.SourceId,
                    out ITrackingSourceFactory? openSeeFaceFactory)
                && openSeeFaceFactory is not null)
            {
                nodes.Add(CreateTrackingTransportNode(
                    channel,
                    channelId,
                    openSeeFaceFactory.Descriptor,
                    intendedSourceId));
            }
        }

        if (channel != TrackingChannel.Face && viewModel.TrackingSourceRegistry is TrackingSourceRegistry registry)
        {
            foreach (TrackingTechnologyDescriptor technology in registry.GetTechnologies(
                channel,
                viewModel.IsDeveloperModeEnabled))
            {
                ImmutableArray<MenuNode> transports = registry
                    .GetDescriptors(channel, viewModel.IsDeveloperModeEnabled)
                    .Where(descriptor => descriptor.TechnologyId == technology.Id)
                    .Select(descriptor => CreateTrackingTransportNode(
                        channel,
                        channelId,
                        descriptor,
                        intendedSourceId))
                    .ToImmutableArray();
                nodes.Add(Submenu(
                    $"tracking.{channelId}.technology.{technology.Id}",
                    technology.DisplayNameResourceKey,
                    technology.IconResourceKey,
                    transports));
            }
        }

        nodes.Add(Separator($"tracking.{channelId}.status"));
        if (channel == TrackingChannel.Face)
        {
            nodes.Add(InformationBlock(
                "tracking.face.input-status",
                "Menu.Session.FaceTracking",
                "Icon.Lucide.Activity",
                GetTrackingStatusFields(status),
                GetInformationState(status)));
            nodes.Add(Command(
                "tracking.face.source-settings",
                "Menu.Tracking.SourceSettings",
                "Icon.Lucide.Settings",
                intendedSourceId == Motara.Tracking.iFacialMocap.IFacialMocapTrackingSource.SourceId
                    ? viewModel.OpenIFacialMocapConfigurationCommand
                    : intendedSourceId == Motara.App.Tracking.OpenSeeFaceLocalTrackingSourceFactory.SourceId
                        ? viewModel.OpenOpenSeeFaceConfigurationCommand
                        : null,
                isEnabled: intendedSourceId
                    == Motara.Tracking.iFacialMocap.IFacialMocapTrackingSource.SourceId
                    || intendedSourceId
                    == Motara.App.Tracking.OpenSeeFaceLocalTrackingSourceFactory.SourceId));
            nodes.Add(Toggle(
                "tracking.face.remember-source",
                "Menu.Tracking.RememberFaceTracking",
                "Icon.Lucide.History",
                viewModel.RememberFaceTrackingOnStartup,
                value => viewModel.TrySetRememberFaceTrackingOnStartupAsync(
                    value,
                    CancellationToken.None)));
            nodes.Add(Separator("tracking.face.calibration-group"));
            nodes.Add(Command(
                "tracking.face.calibration",
                "Menu.Tracking.Calibration",
                "Icon.Lucide.ScanFace",
                viewModel.CalibrateFaceTrackingCommand,
                isEnabled: status.State == TrackingSourceRunState.Running));
        }
        else
        {
            nodes.Add(InformationBlock(
                $"tracking.{channelId}.input-status",
                channel == TrackingChannel.Hand
                    ? "Menu.Tracking.Hand"
                    : "Menu.Tracking.BodyReserved",
                "Icon.Lucide.Activity",
                GetTrackingStatusFields(status),
                GetInformationState(status)));
        }

        return [.. nodes];
    }

    private MenuNode CreateIFacialMocapNode(
        string channelId,
        string? intendedSourceId) =>
        Radio(
            $"tracking.{channelId}.source.{IFacialMocapTrackingSource.SourceId}",
            "Menu.Tracking.Source.IFacialMocap",
            "Icon.Lucide.ScanFace",
            viewModel.SelectTrackingSourceCommand,
            new MainWindowViewModel.TrackingSourceSelection(
                TrackingChannel.Face,
                IFacialMocapTrackingSource.SourceId),
            isSelected: intendedSourceId == IFacialMocapTrackingSource.SourceId,
            helpTextResourceKey: "Menu.Tracking.Source.IFacialMocap.NotConfigured");

    private static MenuNode CreateReservedFaceSourceNode(
        string channelId,
        string sourceId,
        string labelResourceKey) =>
        Radio(
            $"tracking.{channelId}.source.{sourceId}",
            labelResourceKey,
            "Icon.Lucide.ScanFace",
            command: null,
            isEnabled: false,
            helpTextResourceKey: "Menu.Common.NotImplemented");

    private MenuNode CreateTrackingTransportNode(
        TrackingChannel channel,
        string channelId,
        TrackingSourceDescriptor descriptor,
        string? intendedSourceId)
    {
        System.Windows.Input.ICommand command =
            descriptor.Id == Motara.Tracking.iFacialMocap.IFacialMocapTrackingSource.SourceId
                ? viewModel.OpenIFacialMocapConfigurationCommand
                : viewModel.SelectTrackingSourceCommand;
        object? commandParameter = ReferenceEquals(command, viewModel.SelectTrackingSourceCommand)
            ? new MainWindowViewModel.TrackingSourceSelection(channel, descriptor.Id)
            : null;
        return Radio(
            $"tracking.{channelId}.source.{descriptor.Id}",
            descriptor.DisplayNameResourceKey,
            descriptor.IconResourceKey,
            command,
            commandParameter,
            isSelected: intendedSourceId == descriptor.Id,
            helpTextResourceKey: descriptor.Id
                == Motara.Tracking.iFacialMocap.IFacialMocapTrackingSource.SourceId
                ? "Menu.Tracking.Source.IFacialMocap.NotConfigured"
                : null);
    }

    private string GetTrackingStatusText(TrackingSourceStatus status)
    {
        string state = viewModel.Localization.GetString(status.State switch
        {
            TrackingSourceRunState.Switching => "Menu.Tracking.Status.Switching",
            TrackingSourceRunState.Running => "Menu.Tracking.Status.Running",
            TrackingSourceRunState.Stopping => "Menu.Tracking.Status.Stopping",
            TrackingSourceRunState.Stopped => "Menu.Tracking.Status.Stopped",
            TrackingSourceRunState.Faulted => "Menu.Tracking.Status.Faulted",
            _ => "Menu.Tracking.Status.None",
        });
        string sourceName = GetTrackingSourceName(status.IntendedSourceId);
        if (status.State != TrackingSourceRunState.Running)
        {
            return string.Format(
                viewModel.Localization.Culture,
                viewModel.Localization.GetString("Menu.Tracking.StatusFormat"),
                state,
                sourceName);
        }

        string lastInput = status.LastFrameReceivedAtUtc?.ToLocalTime().ToString(
            "T",
            viewModel.Localization.Culture) ?? "--";
        return string.Format(
            viewModel.Localization.Culture,
            viewModel.Localization.GetString("Menu.Tracking.Status.RunningFormat"),
            state,
            sourceName,
            status.FramesPerSecond,
            status.ReceivedFrameCount,
            lastInput);
    }

    private ImmutableArray<MenuStatusField> GetTrackingStatusFields(TrackingSourceStatus status)
    {
        string state = viewModel.Localization.GetString(status.State switch
        {
            TrackingSourceRunState.Switching => "Menu.Tracking.Status.Switching",
            TrackingSourceRunState.Running => "Menu.Tracking.Status.Running",
            TrackingSourceRunState.Stopping => "Menu.Tracking.Status.Stopping",
            TrackingSourceRunState.Stopped => "Menu.Tracking.Status.Stopped",
            TrackingSourceRunState.Faulted => "Menu.Tracking.Status.Faulted",
            _ => "Menu.Tracking.Status.None",
        });
        var fields = ImmutableArray.CreateBuilder<MenuStatusField>();
        fields.Add(new MenuStatusField(
            "Menu.Tracking.StatusField.InputStatus",
            state));
        fields.Add(new MenuStatusField(
            "Menu.Tracking.StatusField.Source",
            GetTrackingSourceName(status.IntendedSourceId)));
        if (status.State == TrackingSourceRunState.Running)
        {
            fields.Add(new MenuStatusField(
                "Menu.Tracking.StatusField.FrameRate",
                string.Format(viewModel.Localization.Culture, "{0:0.0} FPS", status.FramesPerSecond)));
            fields.Add(new MenuStatusField(
                "Menu.Tracking.StatusField.ReceivedFrames",
                status.ReceivedFrameCount.ToString(viewModel.Localization.Culture)));
            fields.Add(new MenuStatusField(
                "Menu.Tracking.StatusField.LastInput",
                status.LastFrameReceivedAtUtc?.ToLocalTime().ToString(
                    "T",
                    viewModel.Localization.Culture) ?? "--"));
        }
        else if (status.State == TrackingSourceRunState.Faulted
            && !string.IsNullOrWhiteSpace(status.ErrorCode))
        {
            fields.Add(new MenuStatusField(
                "Menu.Tracking.StatusField.Error",
                status.ErrorCode));
        }

        return fields.ToImmutable();
    }

    private string GetTrackingSourceName(string? sourceId)
    {
        if (sourceId is null)
        {
            return viewModel.Localization.GetString("Menu.Tracking.None");
        }

        if (viewModel.FaceTrackingController?.Registry.TryGetFactory(
                sourceId,
                out ITrackingSourceFactory? factory) == true
            && factory is not null)
        {
            return viewModel.Localization.GetString(factory.Descriptor.DisplayNameResourceKey);
        }

        return viewModel.Localization.GetString("Menu.Tracking.Source.Unknown");
    }

    private ImmutableArray<MenuNode> GetSceneNodes()
    {
        SceneWorkspace workspace = viewModel.CurrentSceneWorkspace;
        SceneDocument? presentedScene = viewModel.PresentedSceneId is SceneId sceneId
            ? workspace.Scenes.Single(scene => scene.Id == sceneId)
            : null;
        MainModelInstance? mainModel = presentedScene?.MainModel;
        ModelId? currentModelId = mainModel is null
            ? null
            : ModelId.Create(mainModel.ModelAssetId);
        string? modelName = currentModelId is ModelId id
            ? viewModel.ModelCatalog.Entries.FirstOrDefault(entry => entry.Id == id)?.DisplayName
            : null;
        var nodes = new List<MenuNode>
        {
            Radio(
                "scene.none",
                "Menu.Scene.None",
                "Icon.Lucide.EyeOff",
                viewModel.SelectNoSceneCommand,
                isSelected: viewModel.PresentedSceneId is null),
        };
        nodes.AddRange(workspace.Scenes
            .Select((scene, index) => Radio(
                $"scene.entry.{index}",
                scene.DisplayName,
                "Icon.Lucide.Layers",
                viewModel.ActivateSceneCommand,
                scene.Id,
                scene.Id == viewModel.PresentedSceneId,
                isLiteralLabel: true,
                automationName: scene.DisplayName))
            .ToList());
        nodes.AddRange(
        [
            Separator("scene.actions"),
            Command("scene.create", "Menu.Scene.Create", "Icon.Lucide.Plus", viewModel.CreateSceneCommand),
            Command(
                "scene.rename",
                "Menu.Scene.Rename",
                "Icon.Lucide.Pencil",
                viewModel.RenameActiveSceneCommand,
                isEnabled: presentedScene is not null),
            Command(
                "scene.delete",
                "Menu.Scene.Delete",
                "Icon.Lucide.Trash2",
                viewModel.DeleteActiveSceneCommand,
                isEnabled: presentedScene is not null && workspace.Scenes.Length > 1),
            Command(
                "scene.organize",
                "Menu.Scene.Organize",
                "Icon.Lucide.FolderOpen",
                viewModel.OrganizeActiveSceneCommand,
                isEnabled: presentedScene is not null),
            Separator("scene.settings"),
            Toggle(
                "scene.restore-on-startup",
                "Menu.Scene.RestoreOnStartup",
                "Icon.Lucide.RotateCcw",
                viewModel.RestoreActiveSceneOnStartup,
                value => viewModel.TrySetRestoreActiveSceneOnStartupAsync(
                    value,
                    CancellationToken.None)),
        ]);
        if (viewModel.SceneOrganizationStatusText is string organizationStatus)
        {
            nodes.Insert(
                nodes.FindIndex(static node => node.Id == "scene.settings"),
                Status(
                    "scene.organize.status",
                    organizationStatus,
                    "Icon.Lucide.Activity",
                    isLiteralLabel: true));
        }
        if (presentedScene is not null)
        {
            nodes.Add(Separator("scene.main-model"));
            nodes.AddRange(CreateSceneSourceNodes(presentedScene, mainModel, modelName));
            if (mainModel is null)
            {
                nodes.Add(Command(
                    "scene.main-model.go-to-models",
                    "Menu.Scene.GoToModels",
                    "Icon.Lucide.User",
                    viewModel.SelectDestinationCommand,
                    NavigationDestination.Model));
            }

            nodes.Add(Command(
                "scene.add-attachment",
                "Menu.Scene.AddAttachment",
                "Icon.Lucide.Plus",
                viewModel.OpenSceneAttachmentWorkspaceCommand,
                isEnabled: viewModel.PresentedSceneId is not null));
            nodes.Add(Separator("scene.source-settings"));
            if (mainModel is not null)
            {
                nodes.Add(Submenu(
                    "scene.main-model.tracking",
                    "Menu.Scene.MainModelTracking",
                    "Icon.Lucide.Radio",
                    CreateTrackingModeNodes(mainModel)));
            }
            nodes.Add(Submenu(
                "scene.background",
                "Menu.Scene.Background",
                "Icon.Lucide.Layers",
                CreateSceneBackgroundNodes()));
        }

        return [.. nodes];
    }

    private ImmutableArray<MenuNode> CreateSceneBackgroundNodes()
    {
        bool isCustom = viewModel.CurrentSceneBackgroundOverride is not null;
        return
        [
            Radio(
                "scene.background.shared",
                "Menu.Scene.Background.Shared",
                "Icon.Lucide.CircleDot",
                viewModel.SetSceneBackgroundModeCommand,
                false,
                isSelected: !isCustom),
            Radio(
                "scene.background.custom",
                "Menu.Scene.Background.Custom",
                "Icon.Lucide.CircleDot",
                viewModel.SetSceneBackgroundModeCommand,
                true,
                isSelected: isCustom),
            Command(
                "scene.background.edit-custom",
                "Menu.Scene.Background.EditCustom",
                "Icon.Lucide.Pencil",
                viewModel.OpenSceneBackgroundEditorCommand,
                isEnabled: isCustom),
        ];
    }

    private ImmutableArray<MenuNode> CreateTrackingModeNodes(MainModelInstance mainModel) =>
    [
        Radio(
            "scene.main-model.tracking.shared",
            "Menu.Scene.TrackingMode.Shared",
            "Icon.Lucide.CircleDot",
            viewModel.SetMainModelTrackingCommand,
            MainModelTrackingMode.SharedTracking,
            isSelected: mainModel.TrackingMode == MainModelTrackingMode.SharedTracking,
            isEnabled: true),
        Radio(
            "scene.main-model.tracking.idle",
            "Menu.Scene.TrackingMode.Idle",
            "Icon.Lucide.CircleDot",
            command: null,
            isSelected: mainModel.TrackingMode == MainModelTrackingMode.IdleAnimation,
            isEnabled: false,
            helpTextResourceKey: "Menu.Scene.TrackingMode.EditorUnavailable"),
        Radio(
            "scene.main-model.tracking.manual",
            "Menu.Scene.TrackingMode.Manual",
            "Icon.Lucide.CircleDot",
            viewModel.SetMainModelTrackingCommand,
            MainModelTrackingMode.Manual,
            isSelected: mainModel.TrackingMode == MainModelTrackingMode.Manual,
            isEnabled: true),
        Separator("scene.main-model.tracking.channels"),
        Toggle(
            "scene.main-model.tracking.face",
            "Menu.Tracking.Face",
            "Icon.Lucide.ScanFace",
            mainModel.TrackingChannels.Face,
            value => viewModel.TrySetMainModelTrackingChannelAsync(
                TrackingChannel.Face,
                value,
                CancellationToken.None),
            isEnabled: mainModel.TrackingMode == MainModelTrackingMode.SharedTracking),
        Toggle(
            "scene.main-model.tracking.hand",
            "Menu.Tracking.Hand",
            "Icon.Lucide.PersonStanding",
            mainModel.TrackingChannels.Hand,
            value => viewModel.TrySetMainModelTrackingChannelAsync(
                TrackingChannel.Hand,
                value,
                CancellationToken.None),
            isEnabled: mainModel.TrackingMode == MainModelTrackingMode.SharedTracking),
        Toggle(
            "scene.main-model.tracking.body",
            "Menu.Tracking.BodyReserved",
            "Icon.Lucide.PersonStanding",
            mainModel.TrackingChannels.Body,
            _ => Task.FromResult(false),
            isEnabled: false,
            helpTextResourceKey: "Menu.Scene.TrackingMode.EditorUnavailable"),
    ];

    private ImmutableArray<MenuNode> CreateSceneSourceNodes(
        SceneDocument scene,
        MainModelInstance? mainModel,
        string? modelName)
    {
        var nodes = new List<MenuNode>();
        IEnumerable<AttachmentInstance> front = scene.Attachments
            .Where(static attachment => attachment.Placement == AttachmentPlacement.AfterMainModel)
            .Reverse();
        IEnumerable<AttachmentInstance> back = scene.Attachments
            .Where(static attachment => attachment.Placement == AttachmentPlacement.BeforeMainModel)
            .Reverse();
        int frontIndex = 0;
        foreach (AttachmentInstance attachment in front)
        {
            nodes.Add(CreateAttachmentSourceNode(attachment, frontIndex++));
        }

        if (mainModel is not null)
        {
            MenuNode source = Command(
                "scene.main-model.current",
                modelName ?? "Menu.Scene.MainModelAssigned",
                "Icon.Lucide.User",
                viewModel.SelectSceneSourceCommand,
                mainModel.SourceId,
                isSelected: viewModel.IsMainModelSourceSelected,
                isLiteralLabel: modelName is not null,
                sourceActions: new MenuSourceActions(
                    mainModel.IsVisible,
                    mainModel.IsLocked,
                    value => viewModel.TrySetMainModelVisibilityAsync(value, CancellationToken.None),
                    value => viewModel.TrySetMainModelLockAsync(value, CancellationToken.None))
                {
                    SourceId = mainModel.SourceId,
                    IsMainModel = true,
                    MoveMainToAsync = frontAttachmentCount => viewModel.TryMoveMainModelAsync(
                        frontAttachmentCount,
                        CancellationToken.None),
                });
            nodes.Add(source with
            {
                Children = CreateMainModelSourceDetails(mainModel),
            });
        }
        else
        {
            nodes.Add(Status("scene.main-model.empty", "Menu.Scene.NoMainModel", "Icon.Lucide.User"));
        }

        int backIndex = 0;
        foreach (AttachmentInstance attachment in back)
        {
            nodes.Add(CreateAttachmentSourceNode(attachment, backIndex++));
        }

        return [.. nodes];
    }

    private MenuNode CreateAttachmentSourceNode(AttachmentInstance attachment, int orderIndex)
    {
        string labelResourceKey = SceneSourceRegistry.Default.TryGet(
            attachment.SourceTypeId,
            out SceneSourceDescriptor? descriptor)
            ? descriptor!.DisplayNameResourceKey
            : "Menu.Scene.Attachment.Unknown";
        return Command(
            $"scene.attachment.{attachment.SourceId:N}",
            attachment.DisplayName,
            "Icon.Lucide.Layers",
            viewModel.SelectSceneSourceCommand,
            attachment.SourceId,
            isSelected: viewModel.SelectedSceneSourceId == attachment.SourceId,
            isLiteralLabel: true,
            sourceActions: new MenuSourceActions(
                attachment.IsVisible,
                attachment.IsLocked,
                value => viewModel.TrySetSceneAttachmentVisibilityAsync(
                    attachment.SourceId,
                    value,
                    CancellationToken.None),
                value => viewModel.TrySetSceneAttachmentLockAsync(
                    attachment.SourceId,
                    value,
                    CancellationToken.None))
            {
                SourceId = attachment.SourceId,
                IsMainModel = false,
                OrderIndex = orderIndex,
                Placement = attachment.Placement,
                MoveAsync = destinationIndex => viewModel.TryMoveSceneAttachmentAsync(
                    attachment.SourceId,
                    attachment.Placement,
                    destinationIndex,
                    CancellationToken.None),
                MoveToAsync = (placement, destinationIndex) => viewModel.TryMoveSceneAttachmentAsync(
                    attachment.SourceId,
                    placement,
                    destinationIndex,
                    CancellationToken.None),
                DisplayName = attachment.DisplayName,
                SetDisplayNameAsync = displayName => viewModel.TrySetSceneAttachmentDisplayNameAsync(
                    attachment.SourceId,
                    displayName,
                    CancellationToken.None),
                DeleteAsync = () => viewModel.TryRemoveSceneAttachmentAsync(
                    attachment.SourceId,
                    CancellationToken.None),
            }) with
            {
                Children = CreateAttachmentSourceDetails(attachment, labelResourceKey),
            };
    }

    private ImmutableArray<MenuNode> CreateMainModelSourceDetails(MainModelInstance mainModel)
    {
        string modelName = viewModel.ModelCatalog.Entries
            .FirstOrDefault(entry => StringComparer.Ordinal.Equals(entry.Id.Value, mainModel.ModelAssetId))
            ?.DisplayName
            ?? mainModel.ModelAssetId;
        return
        [
            InformationBlock(
                "scene.source.main-model.information",
                "Menu.Scene.Source.Information",
                "Icon.Lucide.User",
                [
                    new MenuStatusField("Menu.Scene.Source.Type", viewModel.Localization.GetString("Menu.Scene.Source.MainModel")),
                    new MenuStatusField("Menu.Scene.Source.Resource", modelName),
                    new MenuStatusField("Menu.Scene.Source.Layer", viewModel.Localization.GetString("Menu.Scene.Source.MainModel")),
                ]),
            Toggle(
                "scene.source.main-model.visibility",
                "Menu.Scene.Source.Visibility",
                "Icon.Lucide.Eye",
                mainModel.IsVisible,
                value => viewModel.TrySetMainModelVisibilityAsync(value, CancellationToken.None)),
            Toggle(
                "scene.source.main-model.lock",
                "Menu.Scene.Source.Lock",
                "Icon.Lucide.Lock",
                mainModel.IsLocked,
                value => viewModel.TrySetMainModelLockAsync(value, CancellationToken.None)),
        ];
    }

    private ImmutableArray<MenuNode> CreateAttachmentSourceDetails(
        AttachmentInstance attachment,
        string typeResourceKey)
    {
        string layer = attachment.Placement == AttachmentPlacement.AfterMainModel
            ? viewModel.Localization.GetString("Menu.Scene.Source.AfterModel")
            : viewModel.Localization.GetString("Menu.Scene.Source.BeforeModel");
        return
        [
            InformationBlock(
                $"scene.source.{attachment.SourceId:N}.information",
                "Menu.Scene.Source.Information",
                "Icon.Lucide.Layers",
                [
                    new MenuStatusField("Menu.Scene.Source.Type", viewModel.Localization.GetString(typeResourceKey)),
                    new MenuStatusField("Menu.Scene.Source.Resource", attachment.ResourceReference),
                    new MenuStatusField("Menu.Scene.Source.Layer", layer),
                ],
                sourceActions: new MenuSourceActions(
                    attachment.IsVisible,
                    attachment.IsLocked,
                    value => viewModel.TrySetSceneAttachmentVisibilityAsync(
                        attachment.SourceId,
                        value,
                        CancellationToken.None),
                    value => viewModel.TrySetSceneAttachmentLockAsync(
                        attachment.SourceId,
                        value,
                        CancellationToken.None))
                {
                    SourceId = attachment.SourceId,
                    DisplayName = attachment.DisplayName,
                    SetDisplayNameAsync = displayName => viewModel.TrySetSceneAttachmentDisplayNameAsync(
                        attachment.SourceId,
                        displayName,
                        CancellationToken.None),
                }),
            Toggle(
                $"scene.source.{attachment.SourceId:N}.visibility",
                "Menu.Scene.Source.Visibility",
                "Icon.Lucide.Eye",
                attachment.IsVisible,
                value => viewModel.TrySetSceneAttachmentVisibilityAsync(
                    attachment.SourceId,
                    value,
                    CancellationToken.None)),
            Toggle(
                $"scene.source.{attachment.SourceId:N}.lock",
                "Menu.Scene.Source.Lock",
                "Icon.Lucide.Lock",
                attachment.IsLocked,
                value => viewModel.TrySetSceneAttachmentLockAsync(
                    attachment.SourceId,
                    value,
                    CancellationToken.None)),
            Toggle(
                $"scene.source.{attachment.SourceId:N}.follow-main-model",
                "Menu.Scene.Source.FollowMainModel",
                "Icon.Lucide.User",
                attachment.MountMode == AttachmentMountMode.MainModelAnchor,
                value => viewModel.TrySetSceneAttachmentMountModeAsync(
                    attachment.SourceId,
                    value ? AttachmentMountMode.MainModelAnchor : AttachmentMountMode.Canvas,
                    CancellationToken.None),
                isEnabled: viewModel.CurrentSceneWorkspace.ActiveScene.MainModel is not null),
            Command(
                $"scene.source.{attachment.SourceId:N}.remove",
                "Menu.Scene.Source.Remove",
                "Icon.Lucide.Trash2",
                command: null,
                isEnabled: true) with
            {
                ActionAsync = () => viewModel.TryRemoveSceneAttachmentAsync(
                    attachment.SourceId,
                    CancellationToken.None),
            },
        ];
    }

    private ImmutableArray<MenuNode> GetModelNodes()
    {
        var nodes = new List<MenuNode>
        {
            Status(
                "model.status",
                viewModel.ModelCatalog.StatusText,
                "Icon.Lucide.Activity",
                isLiteralLabel: true),
            Separator("model.catalog"),
        };
        for (int index = 0; index < viewModel.ModelCatalog.Entries.Length; index++)
        {
            ModelCatalogViewModel.ModelCatalogEntryViewModel model = viewModel.ModelCatalog.Entries[index];
            nodes.Add(Command(
                $"model.entry.{index}",
                model.DisplayName,
                "Icon.Lucide.User",
                viewModel.ModelCatalog.SelectModelCommand,
                model.Id,
                model.IsSelectable,
                model.IsCurrentMainModel,
                isLiteralLabel: true,
                automationName: string.Format(
                    viewModel.Localization.Culture,
                    viewModel.Localization.GetString("Accessibility.ModelEntryFormat"),
                    model.DisplayName)));
        }

        nodes.Add(Separator("model.actions"));
        nodes.Add(Command(
            "model.import",
            "Menu.Model.Import",
            "Icon.Lucide.Plus",
            viewModel.ModelCatalog.ImportCommand));
        nodes.Add(Command(
            "model.refresh",
            "Menu.Model.Refresh",
            "Icon.Lucide.RotateCcw",
            viewModel.ModelCatalog.RefreshCommand));
        nodes.Add(Command(
            "model.open-folder",
            "Menu.Model.OpenFolder",
            "Icon.Lucide.FolderOpen",
            viewModel.ModelCatalog.OpenModelsFolderCommand));
        return [.. nodes];
    }

    private static MenuNode Command(
        string id,
        string labelResourceKey,
        string iconResourceKey,
        ICommand? command = null,
        object? commandParameter = null,
        bool isEnabled = true,
        bool isSelected = false,
        bool isLiteralLabel = false,
        string? automationName = null,
        string? helpTextResourceKey = null,
        MenuSourceActions? sourceActions = null) =>
        MenuNode.CreateCommand(
            id,
            labelResourceKey,
            iconResourceKey,
            command,
            commandParameter,
            isEnabled,
            isSelected,
            isLiteralLabel,
            automationName,
            helpTextResourceKey,
            sourceActions);

    private static MenuNode Submenu(
        string id,
        string labelResourceKey,
        string iconResourceKey,
        IEnumerable<MenuNode> children,
        bool isEnabled = true,
        string? helpTextResourceKey = null) =>
        MenuNode.CreateSubmenu(
            id,
            labelResourceKey,
            iconResourceKey,
            children,
            isEnabled,
            helpTextResourceKey: helpTextResourceKey);

    private static MenuNode Toggle(
        string id,
        string labelResourceKey,
        string iconResourceKey,
        bool value,
        Func<bool, Task<bool>> changeAsync,
        bool isEnabled = true,
        string? helpTextResourceKey = null) =>
        MenuNode.CreateToggle(
            id,
            labelResourceKey,
            iconResourceKey,
            value,
            changeAsync,
            isEnabled,
            helpTextResourceKey: helpTextResourceKey);

    private static MenuNode Radio(
        string id,
        string labelResourceKey,
        string iconResourceKey,
        ICommand? command,
        object? commandParameter = null,
        bool isSelected = false,
        bool isEnabled = true,
        bool isLiteralLabel = false,
        string? automationName = null,
        string? helpTextResourceKey = null) =>
        MenuNode.CreateRadioChoice(
            id,
            labelResourceKey,
            iconResourceKey,
            command,
            commandParameter,
            isSelected,
            isEnabled,
            automationName,
            helpTextResourceKey,
            isLiteralLabel);

    private static MenuNode Status(
        string id,
        string labelResourceKey,
        string iconResourceKey,
        bool isLiteralLabel = false,
        string? automationName = null) =>
        MenuNode.CreateStatus(
            id,
            labelResourceKey,
            iconResourceKey,
            isLiteralLabel,
            automationName);

    private static MenuNode InformationBlock(
        string id,
        string titleResourceKey,
        string? iconResourceKey,
        IEnumerable<MenuStatusField> fields,
        MenuInformationState informationState = MenuInformationState.Neutral,
        string? unavailableReasonResourceKey = null,
        string? emptyValueResourceKey = "Menu.Common.NoData",
        MenuSourceActions? sourceActions = null) =>
        MenuNode.CreateInformationBlock(
            id,
            titleResourceKey,
            iconResourceKey,
            fields,
            informationState,
            unavailableReasonResourceKey,
            emptyValueResourceKey) with
        {
            SourceActions = sourceActions,
        };

    private static MenuNode Separator(string id) => MenuNode.CreateSeparator(id);

    private static MenuNode SectionHeading(string id, string labelResourceKey) =>
        MenuNode.CreateSectionHeading(id, labelResourceKey);
}
