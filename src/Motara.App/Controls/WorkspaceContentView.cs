using Avalonia.Controls;
using Motara.App.Localization;
using Motara.App.ViewModels;
using Motara.App.Collaboration;

namespace Motara.App.Controls;

internal enum WorkspaceScrollMode
{
    HostManaged,
    ContentManaged,
}

internal sealed record WorkspaceContentDescriptor(
    Control Content,
    string TitleResourceKey,
    double Width,
    double MaxWidth,
    Control? InitialFocus,
    Action Detach,
    bool ExpandToAvailableWidth = false,
    WorkspaceScrollMode ScrollMode = WorkspaceScrollMode.HostManaged);

internal static class WorkspaceContentFactory
{
    public static WorkspaceContentDescriptor? Create(
        object payload,
        LocalizationManager localization,
        Action close)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(close);

        return payload switch
        {
            SceneNamePromptViewModel prompt => CreateSceneName(prompt, localization),
            SceneDeleteConfirmationViewModel confirmation => CreateSceneDelete(confirmation, localization),
            IFacialMocapConfigurationViewModel configuration => CreateIFacialMocap(
                configuration,
                localization,
                close),
            OpenSeeFaceConfigurationViewModel configuration => CreateOpenSeeFace(
                configuration,
                localization,
                close),
            SourceMappingEditorHostViewModel mappingHost => CreateSourceMapping(
                mappingHost,
                localization,
                close),
            SourceMappingEditorViewModel mapping => CreateSourceMapping(mapping, localization, close),
            ModelParameterMappingEditorViewModel modelMapping => CreateModelMapping(
                modelMapping,
                localization,
                close),
            ModelPhysicsSettingsViewModel physics => CreateModelPhysics(
                physics,
                localization,
                close),
            ModelBasicSettingsViewModel basic => CreateModelBasic(basic, localization, close),
            ModelAdvancedSettingsViewModel => CreateModelAdvanced(),
            ParameterPriorityWorkspaceViewModel priority => CreateParameterPriority(
                priority,
                localization,
                close),
            SceneEffectEditorViewModel effect => CreateSceneEffect(effect, localization, close),
            ScreenshotWorkspaceViewModel screenshot => CreateScreenshot(screenshot, localization),
            CubismEditorOutputSettingsWorkspaceViewModel cubismEditor => CreateCubismEditor(
                cubismEditor,
                localization),
            CompositionVideoOutputSettingsViewModel videoOutput => CreateVideoOutput(
                videoOutput,
                localization),
            SceneAttachmentEditorViewModel attachmentEditor => CreateSceneAttachment(
                attachmentEditor,
                localization),
            SignalAttachmentEditorViewModel signalAttachment => CreateSignalAttachment(
                signalAttachment,
                localization),
            WindowPresentationSettingsViewModel presentation => CreateWindowPresentation(
                presentation,
                localization),
            BackgroundEditorViewModel background => CreateBackgroundEditor(
                background,
                localization),
            FriendInviteGenerationViewModel generation => CreateFriendInviteGeneration(
                generation,
                localization),
            FriendInviteAcceptanceViewModel acceptance => CreateFriendInviteAcceptance(
                acceptance,
                localization),
            FriendDetailsViewModel friend => CreateFriendDetails(friend, localization),
            IdentityMigrationViewModel migration => CreateIdentityMigration(migration, localization),
            LocalProfileSettingsViewModel profile => CreateLocalProfileSettings(profile, localization),
            SessionInviteGenerationViewModel sessionGeneration => CreateSessionInviteGeneration(sessionGeneration, localization),
            SessionInviteEntryViewModel sessionEntry => CreateSessionInviteEntry(sessionEntry, localization),
            SessionInviteAcceptanceViewModel sessionAcceptance => CreateSessionInviteAcceptance(sessionAcceptance, localization),
            StartupInvitationErrorViewModel startupError => CreateStartupInvitationError(startupError, localization),
            ModelRenderingFallbackNotice fallback => CreateModelRenderingFallback(fallback, localization),
            _ => null,
        };
    }

    private static WorkspaceContentDescriptor CreateSceneName(
        SceneNamePromptViewModel prompt,
        LocalizationManager localization)
    {
        var control = new SceneNamePromptControl();
        control.Attach(prompt, localization);
        return new WorkspaceContentDescriptor(
            control,
            prompt.IsRename ? "Workspace.Scene.RenameTitle" : "Workspace.Scene.CreateTitle",
            640,
            720,
            control.InitialFocus,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateSceneDelete(
        SceneDeleteConfirmationViewModel confirmation,
        LocalizationManager localization)
    {
        var control = new SceneDeleteConfirmationControl();
        control.Attach(confirmation, localization);
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.Scene.DeleteTitle",
            640,
            720,
            control.InitialFocus,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateIFacialMocap(
        IFacialMocapConfigurationViewModel configuration,
        LocalizationManager localization,
        Action close)
    {
        var control = new IFacialMocapConfigurationControl();
        control.Attach(configuration, localization, close);
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.Tracking.IFacialMocap.Title",
            640,
            720,
            control.InitialFocus,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateOpenSeeFace(
        OpenSeeFaceConfigurationViewModel configuration,
        LocalizationManager localization,
        Action close)
    {
        var control = new OpenSeeFaceConfigurationControl();
        control.Attach(configuration, localization, close);
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.Tracking.OpenSeeFace.Title",
            720,
            720,
            control.InitialFocus,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateSourceMapping(
        SourceMappingEditorViewModel editor,
        LocalizationManager localization,
        Action close)
    {
        var control = new SourceMappingEditor();
        control.Attach(editor, localization);
        control.CloseApproved += (_, _) => close();
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.SourceMapping.Title",
            1100,
            1680,
            null,
            () => control.Detach(),
            ExpandToAvailableWidth: true,
            ScrollMode: WorkspaceScrollMode.ContentManaged);
    }

    private static WorkspaceContentDescriptor CreateSourceMapping(
        SourceMappingEditorHostViewModel host,
        LocalizationManager localization,
        Action close)
    {
        var control = new SourceMappingEditor();
        control.Attach(host, localization);
        control.CloseApproved += (_, _) => close();
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.SourceMapping.Title",
            1100,
            1680,
            null,
            () =>
            {
                control.Detach();
                host.Dispose();
            },
            ExpandToAvailableWidth: true,
            ScrollMode: WorkspaceScrollMode.ContentManaged);
    }

    private static WorkspaceContentDescriptor CreateModelMapping(
        ModelParameterMappingEditorViewModel editor,
        LocalizationManager localization,
        Action close)
    {
        var control = new ModelParameterMappingEditor();
        control.Attach(editor, localization, close);
        return new WorkspaceContentDescriptor(
            control,
            editor.IsExternalOutputMapping
                ? "Workspace.CubismMapping.Title"
                : "Workspace.ModelMapping.Title",
            1060,
            1680,
            null,
            control.Detach,
            ExpandToAvailableWidth: true,
            ScrollMode: WorkspaceScrollMode.ContentManaged);
    }

    private static WorkspaceContentDescriptor CreateParameterPriority(
        ParameterPriorityWorkspaceViewModel workspace,
        LocalizationManager localization,
        Action close)
    {
        var control = new ParameterPriorityWorkspaceControl();
        control.Attach(workspace, localization, close);
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.ParameterPriority.Title",
            620,
            720,
            null,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateModelPhysics(
        ModelPhysicsSettingsViewModel settings,
        LocalizationManager localization,
        Action close)
    {
        var control = new ModelPhysicsSettingsControl();
        control.Attach(settings, localization, close);
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.ModelPhysics.Title",
            620,
            800,
            null,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateModelBasic(
        ModelBasicSettingsViewModel settings,
        LocalizationManager localization,
        Action close)
    {
        var control = new ModelBasicSettingsControl();
        control.Attach(settings, localization, close);
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.ModelBasic.Title",
            620,
            800,
            null,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateModelAdvanced() => new(
        new ModelAdvancedSettingsControl(),
        "Workspace.ModelAdvanced.Title",
        620,
        800,
        null,
        () => { });

    private static WorkspaceContentDescriptor CreateSceneEffect(
        SceneEffectEditorViewModel editor,
        LocalizationManager localization,
        Action close)
    {
        var control = new SceneEffectEditor();
        control.Attach(editor, localization, close);
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.SceneEffect.Title",
            620,
            760,
            null,
            () => { });
    }

    private static WorkspaceContentDescriptor CreateWindowPresentation(
        WindowPresentationSettingsViewModel settings,
        LocalizationManager localization)
    {
        var control = new WindowPresentationSettingsControl();
        control.Attach(settings, localization);
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.WindowPresentation.Title",
            620,
            720,
            null,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateScreenshot(
        ScreenshotWorkspaceViewModel settings,
        LocalizationManager localization)
    {
        var control = new ScreenshotWorkspaceControl();
        control.Attach(settings, localization);
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.Screenshot.Title",
            620,
            720,
            control.InitialFocus,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateBackgroundEditor(
        BackgroundEditorViewModel editor,
        LocalizationManager localization)
    {
        var control = new BackgroundEditorControl();
        control.Attach(editor, localization);
        return new WorkspaceContentDescriptor(
            control,
            editor.Scope.Kind == BackgroundEditorScopeKind.Global
                ? "Workspace.Background.GlobalTitle"
                : "Workspace.Background.SceneTitle",
            640,
            760,
            control.InitialFocus,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateCubismEditor(
        CubismEditorOutputSettingsWorkspaceViewModel settings,
        LocalizationManager localization)
    {
        var control = new CubismEditorOutputSettingsControl();
        control.Attach(settings, localization);
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.CubismEditor.Title",
            620,
            720,
            control.InitialFocus,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateVideoOutput(
        CompositionVideoOutputSettingsViewModel settings,
        LocalizationManager localization)
    {
        var control = new CompositionVideoOutputSettingsControl();
        control.Attach(settings, localization);
        return new WorkspaceContentDescriptor(
            control,
            settings.Protocol == Motara.Media.VideoSignalProtocol.Spout2
                ? "Workspace.VideoOutput.Spout2Title"
                : "Workspace.VideoOutput.NdiTitle",
            560,
            640,
            control.InitialFocus,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateSignalAttachment(
        SignalAttachmentEditorViewModel editor,
        LocalizationManager localization)
    {
        var control = new SignalAttachmentEditorControl();
        control.Attach(editor, localization);
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.SignalAttachment.Title",
            560,
            680,
            control.InitialFocus,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateSceneAttachment(
        SceneAttachmentEditorViewModel editor,
        LocalizationManager localization)
    {
        var control = new SceneAttachmentEditorControl();
        control.Attach(editor, localization);
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.SceneAttachment.Title",
            640,
            760,
            control.InitialFocus,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateFriendInviteGeneration(
        FriendInviteGenerationViewModel workspace,
        LocalizationManager localization)
    {
        var control = new FriendInviteGenerationControl();
        control.Attach(workspace, localization);
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.Collaboration.GenerateTitle",
            640,
            720,
            control.InitialFocus,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateFriendInviteAcceptance(
        FriendInviteAcceptanceViewModel workspace,
        LocalizationManager localization)
    {
        var control = new FriendInviteAcceptanceControl();
        control.Attach(workspace, localization);
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.Collaboration.AcceptTitle",
            640,
            720,
            control.InitialFocus,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateFriendDetails(
        FriendDetailsViewModel workspace,
        LocalizationManager localization)
    {
        var control = new FriendDetailsControl();
        control.Attach(workspace, localization);
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.Collaboration.Friend.Title",
            700,
            780,
            control.InitialFocus,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateIdentityMigration(
        IdentityMigrationViewModel workspace,
        LocalizationManager localization)
    {
        var control = new IdentityMigrationControl();
        control.Attach(workspace, localization);
        return new WorkspaceContentDescriptor(
            control,
            workspace.Mode == IdentityMigrationMode.Export
                ? "Workspace.Collaboration.Identity.ExportTitle"
                : "Workspace.Collaboration.Identity.ImportTitle",
            640,
            720,
            control.InitialFocus,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateLocalProfileSettings(
        LocalProfileSettingsViewModel workspace,
        LocalizationManager localization)
    {
        var control = new LocalProfileSettingsControl();
        control.Attach(workspace, localization);
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.Collaboration.Profile.Title",
            640,
            720,
            control.InitialFocus,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateSessionInviteGeneration(
        SessionInviteGenerationViewModel workspace,
        LocalizationManager localization)
    {
        var control = new SessionInviteGenerationControl();
        control.Attach(workspace, localization);
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.Collaboration.Session.GenerateTitle",
            640,
            720,
            control.InitialFocus,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateSessionInviteAcceptance(
        SessionInviteAcceptanceViewModel workspace,
        LocalizationManager localization)
    {
        var control = new SessionInviteAcceptanceControl();
        control.Attach(workspace, localization);
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.Collaboration.Session.AcceptTitle",
            640,
            720,
            control.InitialFocus,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateSessionInviteEntry(
        SessionInviteEntryViewModel workspace,
        LocalizationManager localization)
    {
        var control = new SessionInviteEntryControl();
        control.Attach(workspace, localization);
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.Collaboration.Session.AcceptEntryTitle",
            640,
            720,
            control.InitialFocus,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateStartupInvitationError(
        StartupInvitationErrorViewModel workspace,
        LocalizationManager localization)
    {
        var control = new StartupInvitationErrorControl();
        control.Attach(workspace, localization);
        return new WorkspaceContentDescriptor(
            control,
            "Workspace.Collaboration.Startup.Title",
            560,
            640,
            control.InitialFocus,
            control.Detach);
    }

    private static WorkspaceContentDescriptor CreateModelRenderingFallback(
        ModelRenderingFallbackNotice notice,
        LocalizationManager localization)
    {
        var control = new ModelRenderingFallbackNoticeControl();
        control.Attach(notice, localization);
        return new WorkspaceContentDescriptor(
            control,
            "Dialog.ModelRenderingFallback.Title",
            640,
            720,
            null,
            () => { });
    }
}
