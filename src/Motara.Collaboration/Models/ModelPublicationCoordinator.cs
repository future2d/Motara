using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Collaboration.Identity;
using Motara.Collaboration.Transport;
using Motara.ModelRuntime.Abstractions;

namespace Motara.Collaboration.Models;

public sealed class ModelPublicationCoordinator
{
    private readonly object gate = new();
    private readonly IModelPeerTransport transport;
    private readonly Func<DeviceId, EncryptedPeerFrameCodec> codecs;
    private readonly ILogger<ModelPublicationCoordinator> logger;
    private ImmutableHashSet<DeviceId> peers = [];
    private int peersConfigured;
    private CancellationTokenSource? activePublication;
    private long sequence;
    private ModelPublicationState state = ModelPublicationState.Empty;

    public ModelPublicationCoordinator(
        IModelPeerTransport transport,
        Func<DeviceId, EncryptedPeerFrameCodec> codecs,
        ILogger<ModelPublicationCoordinator>? logger = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.codecs = codecs ?? throw new ArgumentNullException(nameof(codecs));
        this.logger = logger ?? NullLogger<ModelPublicationCoordinator>.Instance;
    }

    public void SetPeers(IEnumerable<DeviceId> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Volatile.Write(ref peers, values.ToImmutableHashSet());
        Volatile.Write(ref peersConfigured, 1);
    }

    public ModelPublicationState State => Volatile.Read(ref state);

    public async Task WithdrawAsync(ModelGeneration generation, CancellationToken cancellationToken)
    {
        ImmutableHashSet<DeviceId> targets = Volatile.Read(ref peers);
        CancelActivePublication();
        Volatile.Write(ref state, CreateState(ModelPublicationStatus.Withdrawn, generation, targets));
        ModelPublicationEvents.Withdrawn(logger, generation.Value, targets.Count);
        byte[] payload = ModelPublicationMessages.EncodeWithdrawal(generation);
        foreach (DeviceId peer in targets)
        {
            await SendAsync(peer, payload, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task PublishAsync(
        ModelPackageManifest manifest,
        IModelAssetSource source,
        IReadOnlyCollection<DeviceId> targets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(targets);
        DeviceId[] authorizedTargets = SelectAuthorizedTargets(targets);

        CancellationTokenSource publication = BeginPublication(manifest.Generation, authorizedTargets, cancellationToken);
        try
        {
            foreach (DeviceId peer in authorizedTargets)
            {
                await SendAsync(peer, ModelPublicationMessages.EncodeManifest(manifest), publication.Token)
                    .ConfigureAwait(false);
            }

            byte[] buffer = new byte[64 * 1024];
            foreach (ModelPackageFile file in manifest.Files)
            {
                await using Stream stream = await source.OpenReadAsync(file.AssetId, publication.Token)
                    .ConfigureAwait(false);
                long offset = 0;
                while (true)
                {
                    int read = await stream.ReadAsync(buffer, publication.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    byte[] data = buffer.AsSpan(0, read).ToArray();
                    byte[] hash = System.Security.Cryptography.SHA256.HashData(data);
                    var chunk = new ModelPackageChunk(
                        manifest.PackageContentId,
                        manifest.Generation,
                        file.AssetId,
                        offset,
                        data,
                        hash);
                    byte[] payload = ModelPublicationMessages.EncodeChunk(chunk);
                    foreach (DeviceId peer in authorizedTargets)
                    {
                        await SendAsync(peer, payload, publication.Token).ConfigureAwait(false);
                    }

                    offset += read;
                }
            }

            if (IsCurrent(publication))
            {
                Volatile.Write(ref state, CreateState(ModelPublicationStatus.Ready, manifest.Generation, authorizedTargets));
                ModelPublicationEvents.Completed(logger, manifest.Generation.Value, authorizedTargets.Length);
            }
        }
        catch (OperationCanceledException) when (publication.IsCancellationRequested)
        {
            ModelPublicationEvents.Cancelled(logger, manifest.Generation.Value);
            throw;
        }
        catch (Exception exception)
        {
            if (IsCurrent(publication))
            {
                Volatile.Write(ref state, CreateState(ModelPublicationStatus.Failed, manifest.Generation, authorizedTargets));
                ModelPublicationEvents.Failed(logger, manifest.Generation.Value, exception.GetType().Name);
            }

            throw;
        }
        finally
        {
            EndPublication(publication);
        }
    }

    private CancellationTokenSource BeginPublication(
        ModelGeneration generation,
        DeviceId[] targets,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource next = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous;
        lock (gate)
        {
            previous = activePublication;
            activePublication = next;
            Volatile.Write(ref state, CreateState(ModelPublicationStatus.Publishing, generation, targets));
        }

        ModelPublicationEvents.Started(logger, generation.Value, targets.Length);
        previous?.Cancel();
        return next;
    }

    private DeviceId[] SelectAuthorizedTargets(IReadOnlyCollection<DeviceId> targets)
    {
        ImmutableHashSet<DeviceId> configuredPeers = Volatile.Read(ref peers);
        return Volatile.Read(ref peersConfigured) == 0
            ? targets.Distinct().ToArray()
            : targets.Where(configuredPeers.Contains).Distinct().ToArray();
    }

    private void CancelActivePublication()
    {
        CancellationTokenSource? previous;
        lock (gate)
        {
            previous = activePublication;
            activePublication = null;
        }

        previous?.Cancel();
    }

    private bool IsCurrent(CancellationTokenSource publication)
    {
        lock (gate)
        {
            return ReferenceEquals(activePublication, publication);
        }
    }

    private void EndPublication(CancellationTokenSource publication)
    {
        lock (gate)
        {
            if (ReferenceEquals(activePublication, publication))
            {
                activePublication = null;
            }
        }

        publication.Dispose();
    }

    private async Task SendAsync(DeviceId peer, byte[] payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EncryptedPeerFrame frame = codecs(peer).Seal(
            PeerChannelKind.Model,
            checked((ulong)Interlocked.Increment(ref sequence)),
            payload);
        await transport.SendModelAsync(peer, frame, cancellationToken).ConfigureAwait(false);
    }

    private static ModelPublicationState CreateState(
        ModelPublicationStatus status,
        ModelGeneration generation,
        IEnumerable<DeviceId> targets) =>
        new(
            status,
            generation,
            targets.ToImmutableDictionary(static peer => peer, _ => status));
}
