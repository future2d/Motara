using Motara.Collaboration.Identity;
using Motara.Collaboration.Transport;

namespace Motara.Collaboration.Models;

public sealed record ReceivedModelFrame(DeviceId Sender, EncryptedPeerFrame Frame);

public interface IModelPeerTransport
{
    ValueTask SendModelAsync(DeviceId peer, EncryptedPeerFrame frame, CancellationToken cancellationToken);

    IAsyncEnumerable<ReceivedModelFrame> ReadModelFramesAsync(CancellationToken cancellationToken);
}
