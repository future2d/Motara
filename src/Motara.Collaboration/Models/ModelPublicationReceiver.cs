using Motara.Collaboration.Identity;
using Motara.Collaboration.Transport;

namespace Motara.Collaboration.Models;

/// <summary>
/// Receives direct model publications from multiple authenticated members.
/// Every sender owns an independent ready-package slot so one member's transfer
/// can never replace or release another member's displayed model.
/// </summary>
public sealed class ModelPublicationReceiver : IAsyncDisposable
{
    private readonly Func<DeviceId, EncryptedPeerFrameCodec> codecFactory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<DeviceId, EncryptedPeerFrameCodec> codecs = [];
    private readonly Dictionary<DeviceId, SenderPublication> publications = [];
    private bool disposed;

    public ModelPublicationReceiver(Func<DeviceId, EncryptedPeerFrameCodec> codecs) =>
        codecFactory = codecs ?? throw new ArgumentNullException(nameof(codecs));

    public event Action<DeviceId, RemoteModelPackage?>? PackageChanged;

    public RemoteModelPackage? GetCurrent(DeviceId sender)
    {
        lock (publications)
        {
            return publications.TryGetValue(sender, out SenderPublication? publication)
                ? publication.Slot.Current
                : null;
        }
    }

    public async Task AcceptAsync(ReceivedModelFrame received, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(received);
        cancellationToken.ThrowIfCancellationRequested();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            byte[] payload = GetCodec(received.Sender).Open(received.Frame);
            if (payload.Length == 0)
            {
                throw new ArgumentException("An empty publication message is invalid.", nameof(received));
            }

            switch (payload[0])
            {
                case ModelPublicationMessages.ManifestKind:
                    await AcceptManifestAsync(received.Sender, ModelPublicationMessages.DecodeManifest(payload)).ConfigureAwait(false);
                    break;
                case ModelPublicationMessages.ChunkKind:
                    await AcceptChunkAsync(received.Sender, ModelPublicationMessages.DecodeChunk(payload), cancellationToken).ConfigureAwait(false);
                    break;
                case ModelPublicationMessages.WithdrawalKind:
                    await AcceptWithdrawalAsync(received.Sender, ModelPublicationMessages.DecodeWithdrawal(payload)).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentException("The publication message kind is invalid.", nameof(received));
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            SenderPublication[] owned;
            lock (publications)
            {
                owned = publications.Values.ToArray();
                publications.Clear();
            }
            codecs.Clear();

            foreach (SenderPublication publication in owned)
            {
                if (publication.Pending is not null)
                {
                    await publication.Pending.DisposeAsync().ConfigureAwait(false);
                }

                await publication.Slot.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }

    private async Task AcceptManifestAsync(DeviceId sender, ModelPackageManifest manifest)
    {
        SenderPublication publication = GetOrCreate(sender);
        if (manifest.Generation.Value < publication.Generation.Value)
        {
            return;
        }

        if (publication.Pending is not null)
        {
            await publication.Pending.DisposeAsync().ConfigureAwait(false);
        }

        publication.Generation = manifest.Generation;
        publication.Pending = RemoteModelPackageReceiver.Begin(manifest, ModelPackageLimits.Default);
    }

    private async Task AcceptChunkAsync(DeviceId sender, ModelPackageChunk chunk, CancellationToken cancellationToken)
    {
        if (!TryGet(sender, out SenderPublication? publication) || publication is null)
        {
            throw new InvalidOperationException("No manifest was received for this member.");
        }

        if (publication.Pending is not RemoteModelPackageReceiver pending)
        {
            throw new InvalidOperationException("No manifest was received for this member.");
        }
        await pending.AcceptChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
        if (!pending.IsComplete)
        {
            return;
        }

        RemoteModelPackageReceiver completed = pending;
        publication.Pending = null;
        try
        {
            RemoteModelPackage package = await completed.CompleteAsync(cancellationToken).ConfigureAwait(false);
            await publication.Slot.ReplaceAsync(package).ConfigureAwait(false);
            PackageChanged?.Invoke(sender, package);
        }
        catch
        {
            await completed.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task AcceptWithdrawalAsync(DeviceId sender, ModelGeneration generation)
    {
        SenderPublication publication = GetOrCreate(sender);
        if (generation.Value < publication.Generation.Value)
        {
            return;
        }

        publication.Generation = generation;
        if (publication.Pending is not null)
        {
            await publication.Pending.DisposeAsync().ConfigureAwait(false);
            publication.Pending = null;
        }

        await publication.Slot.ReleaseAsync().ConfigureAwait(false);
        PackageChanged?.Invoke(sender, null);
    }

    private SenderPublication GetOrCreate(DeviceId sender)
    {
        lock (publications)
        {
            if (!publications.TryGetValue(sender, out SenderPublication? publication))
            {
                publication = new SenderPublication();
                publications.Add(sender, publication);
            }

            return publication;
        }
    }

    private EncryptedPeerFrameCodec GetCodec(DeviceId sender)
    {
        if (!codecs.TryGetValue(sender, out EncryptedPeerFrameCodec? codec))
        {
            codec = codecFactory(sender);
            codecs.Add(sender, codec);
        }

        return codec;
    }

    private bool TryGet(DeviceId sender, out SenderPublication? publication)
    {
        lock (publications)
        {
            return publications.TryGetValue(sender, out publication);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed class SenderPublication
    {
        public ModelGeneration Generation { get; set; } = new(1);

        public RemoteModelPackageReceiver? Pending { get; set; }

        public RemoteModelPackageSlot Slot { get; } = new();
    }
}
