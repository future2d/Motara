using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Animation;
using Motara.App.Parameters;
using Motara.ModelRuntime.Abstractions;

namespace Motara.App.Collaboration;

internal sealed class RemoteMemberAnimationDriver : IAsyncDisposable
{
    private readonly IModelRuntime runtime;
    private readonly ModelCapabilities capabilities;
    private readonly CubismAnimationEvaluator evaluator;
    private readonly ILogger logger;
    private readonly TimeProvider timeProvider;
    private readonly long startTimestamp;
    private readonly CancellationTokenSource cancellation = new();
    private Task? driveTask;
    private long sequence;
    private int disposed;

    private RemoteMemberAnimationDriver(
        IModelRuntime runtime,
        ModelCapabilities capabilities,
        CubismAnimationSet definitions,
        ILogger logger,
        TimeProvider timeProvider)
    {
        this.runtime = runtime;
        this.capabilities = capabilities;
        evaluator = new CubismAnimationEvaluator(definitions, logger);
        this.logger = logger;
        this.timeProvider = timeProvider;
        startTimestamp = timeProvider.GetTimestamp();
    }

    internal static async Task<RemoteMemberAnimationDriver?> CreateAsync(
        IModelRuntime runtime,
        IModelAssetSource assets,
        ImmutableArray<ModelAuxiliaryAsset> definitions,
        CancellationToken cancellationToken,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(assets);
        if (runtime.Capabilities is not { } capabilities || definitions.IsDefaultOrEmpty)
        {
            return null;
        }

        ILogger activeLogger = logger ?? NullLogger.Instance;
        CubismAnimationSet set = await CubismAnimationParser.LoadAsync(
            assets,
            definitions,
            capabilities,
            cancellationToken,
            activeLogger).ConfigureAwait(false);
        if (set.Clips.IsEmpty && set.Expressions.IsEmpty && set.PoseGroups.IsEmpty)
        {
            return null;
        }

        var driver = new RemoteMemberAnimationDriver(
            runtime,
            capabilities,
            set,
            activeLogger,
            timeProvider ?? TimeProvider.System);
        try
        {
            await driver.ApplyAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            driver.Start();
            RemoteMemberAnimationDriverLog.Started(
                activeLogger,
                set.Clips.Length,
                set.Expressions.Length,
                set.PoseGroups.Length);
            return driver;
        }
        catch
        {
            await driver.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            if (driveTask is not null)
            {
                await driveTask.ConfigureAwait(false);
            }
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task DriveAsync(CancellationToken cancellationToken)
    {
        TimeSpan previousElapsed = TimeSpan.Zero;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000d / 60d), timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                TimeSpan elapsed = timeProvider.GetElapsedTime(startTimestamp);
                await ApplyAsync(elapsed - previousElapsed, cancellationToken).ConfigureAwait(false);
                previousElapsed = elapsed;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RemoteMemberAnimationDriverLog.Failed(logger, exception, exception.GetType().Name);
        }
        finally
        {
            RemoteMemberAnimationDriverLog.Stopped(logger);
        }
    }

    private void Start() => driveTask = DriveAsync(cancellation.Token);

    private async Task ApplyAsync(TimeSpan elapsed, CancellationToken cancellationToken)
    {
        CubismAnimationFrame frame = evaluator.Advance(
            elapsed,
            capabilities.Parameters.Select(static parameter => parameter.Default).ToArray());
        if (frame.Contributions.IsEmpty && frame.PartOpacities.IsEmpty)
        {
            return;
        }

        var values = new Dictionary<int, ModelParameterValue>();
        foreach (ParameterContribution contribution in frame.Contributions)
        {
            if ((uint)contribution.ParameterIndex >= (uint)capabilities.Parameters.Length)
            {
                continue;
            }

            ModelParameter parameter = capabilities.Parameters[contribution.ParameterIndex];
            values[contribution.ParameterIndex] = new ModelParameterValue(
                contribution.ParameterIndex,
                Math.Clamp(contribution.Value, parameter.Minimum, parameter.Maximum));
        }

        await runtime.ApplyParametersAsync(
            new ModelParameterUpdate(
                Interlocked.Increment(ref sequence),
                values.Values.ToArray(),
                frame.PartOpacities.AsSpan()),
            cancellationToken).ConfigureAwait(false);
    }
}

internal static partial class RemoteMemberAnimationDriverLog
{
    [LoggerMessage(8170, LogLevel.Information,
        "Remote member animation driver started with {ClipCount} clips, {ExpressionCount} expressions, and {PoseGroupCount} pose groups")]
    internal static partial void Started(ILogger logger, int clipCount, int expressionCount, int poseGroupCount);

    [LoggerMessage(8171, LogLevel.Warning,
        "Remote member animation driver stopped after {ErrorType}")]
    internal static partial void Failed(ILogger logger, Exception exception, string errorType);

    [LoggerMessage(8172, LogLevel.Information, "Remote member animation driver stopped")]
    internal static partial void Stopped(ILogger logger);
}
