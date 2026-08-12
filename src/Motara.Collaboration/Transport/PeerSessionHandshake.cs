using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Motara.Collaboration.Identity;
using Motara.Collaboration.Invites;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Motara.Collaboration.Transport;

public sealed record PeerHandshakeOffer(
    PeerMemberCredential LocalCredential,
    PeerMemberCredential RemoteCredential,
    ImmutableArray<byte> EphemeralPublicKey,
    ImmutableArray<byte> Signature);

public sealed record PeerHandshakeResponse(
    ImmutableArray<byte> EphemeralPublicKey,
    ImmutableArray<byte> Signature);

public sealed class PeerHandshakeException(string message) : Exception(message);

public sealed class PeerHandshakeOfferHandle : IDisposable
{
    private byte[]? privateKey;

    internal PeerHandshakeOfferHandle(PeerHandshakeOffer offer, byte[] privateKey)
    {
        Offer = offer;
        this.privateKey = privateKey;
    }

    public PeerHandshakeOffer Offer { get; }

    internal byte[] TakePrivateKey() =>
        Interlocked.Exchange(ref privateKey, null)
        ?? throw new ObjectDisposedException(nameof(PeerHandshakeOfferHandle));

    public void Dispose()
    {
        byte[]? owned = Interlocked.Exchange(ref privateKey, null);
        if (owned is not null)
        {
            CryptographicOperations.ZeroMemory(owned);
        }
    }
}

public sealed class PeerHandshakeResponseHandle : IDisposable
{
    private byte[]? sharedSecret;
    private readonly DeviceId first;
    private readonly DeviceId second;
    private readonly CollaborationSessionId session;

    internal PeerHandshakeResponseHandle(
        PeerHandshakeResponse response,
        byte[] sharedSecret,
        DeviceId first,
        DeviceId second,
        CollaborationSessionId session)
    {
        Response = response;
        this.sharedSecret = sharedSecret;
        this.first = first;
        this.second = second;
        this.session = session;
    }

    public PeerHandshakeResponse Response { get; }

    public PeerSessionKeys Complete()
    {
        byte[] secret = Interlocked.Exchange(ref sharedSecret, null)
            ?? throw new ObjectDisposedException(nameof(PeerHandshakeResponseHandle));
        try
        {
            return PeerSessionKeys.CreatePair(secret, session, first, second).Second;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public void Dispose()
    {
        byte[]? owned = Interlocked.Exchange(ref sharedSecret, null);
        if (owned is not null)
        {
            CryptographicOperations.ZeroMemory(owned);
        }
    }
}

public static class PeerSessionHandshake
{
    private const int X25519KeyLength = 32;

    public static PeerHandshakeOfferHandle CreateOffer(
        DeviceIdentityHandle local,
        DeviceIdentity remote,
        DeviceIdentity sessionHost,
        PeerMemberCredential ownCredential,
        PeerMemberCredential remoteCredential,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(local);
        ValidateCredentials(local.Identity, remote, sessionHost, ownCredential, remoteCredential, timeProvider);
        byte[] privateKey = RandomNumberGenerator.GetBytes(X25519KeyLength);
        try
        {
            byte[] publicKey = new X25519PrivateKeyParameters(privateKey).GeneratePublicKey().GetEncoded();
            byte[] signature = local.Sign(CreateTranscript(ownCredential, remoteCredential, publicKey));
            return new PeerHandshakeOfferHandle(
                new PeerHandshakeOffer(ownCredential, remoteCredential, [.. publicKey], [.. signature]),
                privateKey);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(privateKey);
            throw;
        }
    }

    public static PeerHandshakeResponseHandle AcceptOffer(
        DeviceIdentityHandle local,
        DeviceIdentity remote,
        DeviceIdentity sessionHost,
        PeerMemberCredential ownCredential,
        PeerMemberCredential remoteCredential,
        PeerHandshakeOffer offer,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(offer);
        ValidateCredentials(local.Identity, remote, sessionHost, ownCredential, remoteCredential, timeProvider);
        if (!Equals(offer.LocalCredential, remoteCredential)
            || !Equals(offer.RemoteCredential, ownCredential)
            || !Verify(remote.PublicKey, CreateTranscript(remoteCredential, ownCredential, offer.EphemeralPublicKey.AsSpan()), offer.Signature))
        {
            throw new PeerHandshakeException("Offer authentication failed.");
        }

        byte[] privateKey = RandomNumberGenerator.GetBytes(X25519KeyLength);
        try
        {
            byte[] publicKey = new X25519PrivateKeyParameters(privateKey).GeneratePublicKey().GetEncoded();
            byte[] signature = local.Sign(CreateTranscript(
                remoteCredential,
                ownCredential,
                offer.EphemeralPublicKey.AsSpan(),
                publicKey));
            byte[] sharedSecret = DeriveSharedSecret(privateKey, offer.EphemeralPublicKey.AsSpan());
            return new PeerHandshakeResponseHandle(
                new PeerHandshakeResponse([.. publicKey], [.. signature]),
                sharedSecret,
                remote.DeviceId,
                local.Identity.DeviceId,
                ownCredential.SessionId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    public static PeerSessionKeys Complete(
        PeerHandshakeOfferHandle offer,
        PeerHandshakeResponse response,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(response);
        ValidateEphemeralKey(response.EphemeralPublicKey.AsSpan());
        if (!Verify(
                offer.Offer.RemoteCredential.MemberPublicKey,
                CreateTranscript(
                    offer.Offer.LocalCredential,
                    offer.Offer.RemoteCredential,
                    offer.Offer.EphemeralPublicKey.AsSpan(),
                    response.EphemeralPublicKey.AsSpan()),
                response.Signature))
        {
            throw new PeerHandshakeException("Response authentication failed.");
        }

        byte[] privateKey = offer.TakePrivateKey();
        byte[] secret;
        try
        {
            secret = DeriveSharedSecret(privateKey, response.EphemeralPublicKey.AsSpan());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
        try
        {
            return PeerSessionKeys.CreatePair(
                secret,
                offer.Offer.LocalCredential.SessionId,
                offer.Offer.LocalCredential.MemberDeviceId,
                offer.Offer.RemoteCredential.MemberDeviceId).First;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static void ValidateCredentials(
        DeviceIdentity local,
        DeviceIdentity remote,
        DeviceIdentity sessionHost,
        PeerMemberCredential ownCredential,
        PeerMemberCredential remoteCredential,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (ownCredential.SessionId != remoteCredential.SessionId
            || !PeerMemberCredentialService.Validate(
                ownCredential,
                sessionHost,
                local.DeviceId,
                timeProvider.GetUtcNow())
            || !PeerMemberCredentialService.Validate(
                remoteCredential,
                sessionHost,
                remote.DeviceId,
                timeProvider.GetUtcNow()))
        {
            throw new PeerHandshakeException("Credential validation failed.");
        }
    }

    private static bool Verify(
        ImmutableArray<byte> publicKey,
        byte[] data,
        ImmutableArray<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64)
        {
            return false;
        }

        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicKey.ToArray()));
        verifier.BlockUpdate(data, 0, data.Length);
        return verifier.VerifySignature(signature.ToArray());
    }

    private static byte[] DeriveSharedSecret(byte[] privateKey, ReadOnlySpan<byte> publicKey)
    {
        ValidateEphemeralKey(publicKey);
        byte[] secret = new byte[X25519KeyLength];
        new X25519PrivateKeyParameters(privateKey)
            .GenerateSecret(new X25519PublicKeyParameters(publicKey.ToArray()), secret, 0);
        return secret;
    }

    private static void ValidateEphemeralKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != X25519KeyLength)
        {
            throw new PeerHandshakeException("The ephemeral public key is invalid.");
        }
    }

    private static byte[] CreateTranscript(
        PeerMemberCredential first,
        PeerMemberCredential second,
        ReadOnlySpan<byte> firstEphemeralKey,
        ReadOnlySpan<byte> secondEphemeralKey = default) =>
        Encoding.UTF8.GetBytes(
            $"1|{first.SessionId}|{first.IssuerDeviceId}|{first.MemberDeviceId}|{second.MemberDeviceId}|{Convert.ToHexString(firstEphemeralKey)}|{Convert.ToHexString(secondEphemeralKey)}");
}
