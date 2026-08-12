using Motara.Collaboration.Invites;

namespace Motara.Collaboration.Handshake;

public static class FriendshipHandshakeTextCodec
{
    private const int MaximumMessageLength = 16 * 1024;

    public static string Encode(ReadOnlySpan<byte> message)
    {
        if (message.Length is 0 or > MaximumMessageLength)
        {
            throw new ArgumentException("The handshake message size is invalid.", nameof(message));
        }

        return Base64Url.Encode(message);
    }

    public static bool TryDecode(string? text, out byte[] message) =>
        Base64Url.TryDecode(text, MaximumMessageLength, out message)
        && message.Length > 0;
}
