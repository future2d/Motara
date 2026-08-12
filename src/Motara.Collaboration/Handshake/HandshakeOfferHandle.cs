using System.Collections.Immutable;
using System.Security.Cryptography;
using Motara.Collaboration.Identity;

namespace Motara.Collaboration.Handshake;

public sealed class HandshakeOfferHandle : IDisposable, IAsyncDisposable
{
    private byte[]? ephemeralPrivateKey;

    internal HandshakeOfferHandle(
        HandshakeOffer offer,
        byte[] messageBytes,
        byte[] ephemeralPrivateKey)
    {
        Offer = offer;
        MessageBytes = ImmutableArray.CreateRange(messageBytes);
        this.ephemeralPrivateKey = ephemeralPrivateKey;
    }

    internal HandshakeOffer Offer { get; }

    public ImmutableArray<byte> MessageBytes { get; }

    public DeviceId FriendDeviceId => Offer.ResponderDeviceId;

    public DateTimeOffset ExpiresAtUtc => Offer.ExpiresAtUtc;

    internal byte[] CopyEphemeralPrivateKey()
    {
        ObjectDisposedException.ThrowIf(ephemeralPrivateKey is null, this);
        return ephemeralPrivateKey.ToArray();
    }

    public void Dispose()
    {
        byte[]? value = Interlocked.Exchange(ref ephemeralPrivateKey, null);
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class HandshakeResponseHandle : IDisposable, IAsyncDisposable
{
    private byte[]? ephemeralPrivateKey;

    internal HandshakeResponseHandle(
        HandshakeOffer offer,
        byte[] offerMessageBytes,
        HandshakeResponse response,
        byte[] messageBytes,
        byte[] ephemeralPrivateKey)
    {
        Offer = offer;
        OfferMessageBytes = ImmutableArray.CreateRange(offerMessageBytes);
        Response = response;
        MessageBytes = ImmutableArray.CreateRange(messageBytes);
        this.ephemeralPrivateKey = ephemeralPrivateKey;
    }

    internal HandshakeResponse Response { get; }

    internal HandshakeOffer Offer { get; }

    internal ImmutableArray<byte> OfferMessageBytes { get; }

    internal ImmutableArray<byte> MessageBytes { get; }

    internal byte[] CopyEphemeralPrivateKey()
    {
        ObjectDisposedException.ThrowIf(ephemeralPrivateKey is null, this);
        return ephemeralPrivateKey.ToArray();
    }

    public void Dispose()
    {
        byte[]? value = Interlocked.Exchange(ref ephemeralPrivateKey, null);
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
