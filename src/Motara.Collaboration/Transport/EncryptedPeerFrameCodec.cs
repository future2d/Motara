using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Motara.Collaboration.Transport;

public sealed class EncryptedPeerFrameCodec
{
    private readonly PeerSessionKeys keys;
    private readonly Dictionary<PeerChannelKind, ReplayWindow> replay = [];
    public EncryptedPeerFrameCodec(PeerSessionKeys keys) => this.keys = keys ?? throw new ArgumentNullException(nameof(keys));

    public EncryptedPeerFrame Seal(PeerChannelKind channel, ulong sequence, ReadOnlySpan<byte> payload)
    {
        byte[] cipher = new byte[payload.Length]; byte[] tag = new byte[16];
        using var aes = new AesGcm(keys.GetSend(channel), 16);
        aes.Encrypt(Nonce(channel, sequence), payload, cipher, tag, AssociatedData(channel, sequence));
        return new EncryptedPeerFrame(channel, sequence, cipher, tag);
    }
    public byte[] Open(EncryptedPeerFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Tag.Length != 16) throw new PeerFrameException("Invalid frame tag.");
        ReplayWindow window = replay.TryGetValue(frame.Channel, out ReplayWindow? existing) ? existing : replay[frame.Channel] = new ReplayWindow();
        if (!window.CanAccept(frame.Sequence)) throw new PeerFrameException("Repeated or stale frame.");
        byte[] plain = new byte[frame.Ciphertext.Length];
        try { using var aes = new AesGcm(keys.GetReceive(frame.Channel), 16); aes.Decrypt(Nonce(frame.Channel, frame.Sequence), frame.Ciphertext, frame.Tag, plain, AssociatedData(frame.Channel, frame.Sequence)); window.Accept(frame.Sequence); return plain; }
        catch (CryptographicException) { CryptographicOperations.ZeroMemory(plain); throw new PeerFrameException("Frame authentication failed."); }
    }
    private static byte[] Nonce(PeerChannelKind c, ulong s) { byte[] n = new byte[12]; n[0] = (byte)c; BinaryPrimitives.WriteUInt64BigEndian(n.AsSpan(4), s); return n; }
    private static byte[] AssociatedData(PeerChannelKind c, ulong s) { byte[] a = new byte[9]; a[0] = (byte)c; BinaryPrimitives.WriteUInt64BigEndian(a.AsSpan(1), s); return a; }
    private sealed class ReplayWindow
    {
        private ulong high;
        private ulong seen;
        internal bool CanAccept(ulong sequence) => sequence > high
            || high - sequence < 64 && (seen & (1UL << (int)(high - sequence))) == 0;
        internal void Accept(ulong sequence)
        {
            if (sequence > high)
            {
                ulong delta = sequence - high;
                seen = delta >= 64 ? 0 : seen << (int)delta;
                high = sequence;
            }

            seen |= sequence == high ? 1 : 1UL << (int)(high - sequence);
        }
    }
}
