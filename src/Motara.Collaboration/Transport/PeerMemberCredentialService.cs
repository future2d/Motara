using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Motara.Collaboration.Identity;
using Motara.Collaboration.Invites;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Motara.Collaboration.Transport;

public sealed class PeerMemberCredentialService
{
    public static PeerMemberCredential Issue(
        DeviceIdentityHandle host,
        CollaborationSessionId session,
        DeviceIdentity member,
        PeerMemberPermissions permissions,
        DateTimeOffset expires)
    {
        ArgumentNullException.ThrowIfNull(host);
        byte[] data = CreateSignedData(
            session,
            host.Identity.DeviceId,
            host.Identity.PublicKey,
            member.DeviceId,
            member.PublicKey,
            permissions,
            expires);
        try
        {
            return new PeerMemberCredential(
                SchemaVersion: 1,
                SessionId: session,
                IssuerDeviceId: host.Identity.DeviceId,
                IssuerPublicKey: host.Identity.PublicKey,
                MemberDeviceId: member.DeviceId,
                MemberPublicKey: member.PublicKey,
                Permissions: permissions,
                ExpiresAtUtc: expires,
                Signature: host.Sign(data).ToImmutableArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    public static bool Validate(
        PeerMemberCredential credential,
        DeviceIdentity expectedIssuer,
        DeviceId expectedMember,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return credential.SchemaVersion == 1
            && credential.ExpiresAtUtc > now
            && credential.IssuerDeviceId == expectedIssuer.DeviceId
            && credential.IssuerPublicKey.AsSpan().SequenceEqual(expectedIssuer.PublicKey.AsSpan())
            && credential.MemberDeviceId == expectedMember
            && IsValidPublicKey(credential.IssuerPublicKey, credential.IssuerDeviceId)
            && IsValidPublicKey(credential.MemberPublicKey, credential.MemberDeviceId)
            && credential.Signature.Length == Ed25519SignatureLength
            && VerifySignature(credential);
    }

    private const int Ed25519SignatureLength = 64;

    private static bool IsValidPublicKey(ImmutableArray<byte> publicKey, DeviceId deviceId) =>
        publicKey.Length == 32
        && DeviceId.FromEd25519PublicKey(publicKey.AsSpan()) == deviceId;

    private static bool VerifySignature(PeerMemberCredential credential)
    {
        byte[] data = CreateSignedData(
            credential.SessionId,
            credential.IssuerDeviceId,
            credential.IssuerPublicKey,
            credential.MemberDeviceId,
            credential.MemberPublicKey,
            credential.Permissions,
            credential.ExpiresAtUtc);
        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(credential.IssuerPublicKey.ToArray()));
            verifier.BlockUpdate(data, 0, data.Length);
            return verifier.VerifySignature(credential.Signature.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    private static byte[] CreateSignedData(
        CollaborationSessionId session,
        DeviceId issuerDeviceId,
        ImmutableArray<byte> issuerPublicKey,
        DeviceId memberDeviceId,
        ImmutableArray<byte> memberPublicKey,
        PeerMemberPermissions permissions,
        DateTimeOffset expires) =>
        Encoding.UTF8.GetBytes($"1|{session}|{issuerDeviceId}|{Convert.ToHexString(issuerPublicKey.AsSpan())}|{memberDeviceId}|{Convert.ToHexString(memberPublicKey.AsSpan())}|{(int)permissions}|{expires.UtcTicks}");
}
