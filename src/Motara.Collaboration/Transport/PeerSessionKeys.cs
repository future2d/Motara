using System.Security.Cryptography;
using System.Text;
using Motara.Collaboration.Identity;
using Motara.Collaboration.Invites;

namespace Motara.Collaboration.Transport;

public enum PeerChannelKind : byte { Model = 1, Control = 2, Drive = 3 }

public sealed class PeerSessionKeys : IDisposable
{
    private readonly byte[][] send = new byte[3][];
    private readonly byte[][] receive = new byte[3][];

    private PeerSessionKeys(byte[][] send, byte[][] receive)
    {
        this.send = send;
        this.receive = receive;
    }

    public static (PeerSessionKeys First, PeerSessionKeys Second) CreatePair(
        byte[] sharedSecret, CollaborationSessionId sessionId, DeviceId first, DeviceId second)
    {
        ArgumentNullException.ThrowIfNull(sharedSecret);
        if (sharedSecret.Length != 32) throw new ArgumentException("A shared secret must contain 32 bytes.", nameof(sharedSecret));
        byte[] salt = sessionId.Value.ToByteArray();
        byte[][] forward = new byte[3][];
        byte[][] reverse = new byte[3][];
        try
        {
            foreach (PeerChannelKind channel in Enum.GetValues<PeerChannelKind>())
            {
                forward[(int)channel - 1] = Derive(sharedSecret, salt, $"motara-peer-v1|{first}|{second}|{channel}");
                reverse[(int)channel - 1] = Derive(sharedSecret, salt, $"motara-peer-v1|{second}|{first}|{channel}");
            }
            return (new PeerSessionKeys(forward, reverse), new PeerSessionKeys(reverse, forward));
        }
        finally { CryptographicOperations.ZeroMemory(salt); }
    }

    public byte[] CopyKeyMaterial(PeerChannelKind channel) => Get(send, channel).ToArray();
    public byte[] CopyReceiveKeyMaterial(PeerChannelKind channel) => Get(receive, channel).ToArray();
    internal byte[] GetSend(PeerChannelKind channel) => Get(send, channel);
    internal byte[] GetReceive(PeerChannelKind channel) => Get(receive, channel);

    public void Dispose()
    {
        foreach (byte[]? key in send.Concat(receive).Distinct()) if (key is not null) CryptographicOperations.ZeroMemory(key);
    }

    private static byte[] Derive(byte[] secret, byte[] salt, string info)
    {
        byte[] output = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, output, salt, Encoding.UTF8.GetBytes(info));
        return output;
    }
    private static byte[] Get(byte[][] keys, PeerChannelKind channel) => keys[(int)channel - 1] ?? throw new ObjectDisposedException(nameof(PeerSessionKeys));
}
