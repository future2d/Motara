using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Persistence;
using Motara.App.Models;

namespace Motara.App.Shortcuts;

internal enum ShortcutDispatchState
{
    Dispatched,
    Unavailable,
    UnknownAction,
    Failed,
}

internal readonly record struct ShortcutDispatchResult(ShortcutDispatchState State, string? Reason = null);

internal sealed class ShortcutRuntimeContext
{
    internal ShortcutRuntimeContext(
        ActiveModelAnimationSource? animationSource = null,
        IReadOnlyDictionary<string, Func<CancellationToken, Task>>? commands = null)
    {
        AnimationSource = animationSource;
        Commands = commands ?? new Dictionary<string, Func<CancellationToken, Task>>(StringComparer.Ordinal);
    }

    internal ActiveModelAnimationSource? AnimationSource { get; }
    internal IReadOnlyDictionary<string, Func<CancellationToken, Task>> Commands { get; }
}

internal sealed class ShortcutDispatcher
{
    private const string PlayMotionPrefix = "Model.Motion.Play/";
    private const string SetIdlePrefix = "Model.Motion.SetIdle/";
    private const string ToggleExpressionPrefix = "Model.Expression.Toggle/";
    private const string ClearExpressionsAction = "Model.Expression.ClearAll";
    private readonly ILogger<ShortcutDispatcher> logger;

    internal ShortcutDispatcher(ILogger<ShortcutDispatcher>? logger = null) =>
        this.logger = logger ?? NullLogger<ShortcutDispatcher>.Instance;

    internal async Task<ShortcutDispatchResult> DispatchAsync(
        string actionId,
        ShortcutRuntimeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            bool? modelResult = DispatchModelAction(actionId, context.AnimationSource);
            if (modelResult.HasValue)
                return Complete(actionId, modelResult.Value);

            if (!context.Commands.TryGetValue(actionId, out Func<CancellationToken, Task>? command))
            {
                ShortcutDispatcherLog.Unknown(logger, actionId);
                return new(ShortcutDispatchState.UnknownAction, "The shortcut action is not available.");
            }

            await command(cancellationToken).ConfigureAwait(false);
            ShortcutDispatcherLog.Dispatched(logger, actionId);
            return new(ShortcutDispatchState.Dispatched);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ShortcutDispatcherLog.Failed(logger, exception, actionId);
            return new(ShortcutDispatchState.Failed, exception.Message);
        }
    }

    internal Task<ShortcutDispatchResult> DispatchAsync(
        ShortcutEntry entry,
        ShortcutRuntimeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return DispatchAsync(entry.RuntimeActionId, context, cancellationToken);
    }

    private ShortcutDispatchResult Complete(string actionId, bool dispatched)
    {
        if (dispatched)
        {
            ShortcutDispatcherLog.Dispatched(logger, actionId);
            return new(ShortcutDispatchState.Dispatched);
        }
        ShortcutDispatcherLog.Unavailable(logger, actionId);
        return new(ShortcutDispatchState.Unavailable, "The current model does not provide this asset.");
    }

    private static bool? DispatchModelAction(string actionId, ActiveModelAnimationSource? source)
    {
        if (actionId.StartsWith(PlayMotionPrefix, StringComparison.Ordinal))
            return source?.TryPlay(actionId[PlayMotionPrefix.Length..]) ?? false;
        if (actionId.StartsWith(SetIdlePrefix, StringComparison.Ordinal))
        {
            if (StringComparer.Ordinal.Equals(actionId[SetIdlePrefix.Length..], "motion:none"))
                return source?.ClearIdle() ?? false;
            return source?.TrySetIdle(actionId[SetIdlePrefix.Length..]) ?? false;
        }
        if (actionId.StartsWith(ToggleExpressionPrefix, StringComparison.Ordinal))
            return source?.TryToggleExpression(actionId[ToggleExpressionPrefix.Length..]) ?? false;
        if (StringComparer.Ordinal.Equals(actionId, ClearExpressionsAction))
            return source?.ClearExpressions() ?? false;
        return null;
    }
}

internal static partial class ShortcutDispatcherLog
{
    [LoggerMessage(6860, LogLevel.Debug, "Shortcut action dispatched: {ActionId}")]
    internal static partial void Dispatched(ILogger logger, string actionId);
    [LoggerMessage(6861, LogLevel.Warning, "Shortcut action unavailable: {ActionId}")]
    internal static partial void Unavailable(ILogger logger, string actionId);
    [LoggerMessage(6862, LogLevel.Warning, "Unknown shortcut action ignored: {ActionId}")]
    internal static partial void Unknown(ILogger logger, string actionId);
    [LoggerMessage(6863, LogLevel.Error, "Shortcut action failed: {ActionId}")]
    internal static partial void Failed(ILogger logger, Exception exception, string actionId);
}
