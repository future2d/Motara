namespace Motara.Collaboration.Transport;

public sealed record EncryptedPeerFrame(PeerChannelKind Channel, ulong Sequence, byte[] Ciphertext, byte[] Tag);
public sealed class PeerFrameException(string message) : Exception(message);
