using System.Net.Http.Headers;

namespace Motara.Collaboration.Presence;

public readonly record struct PresenceLookupKey
{
    private PresenceLookupKey(string value) => Value = value;

    public string Value { get; }

    public static PresenceLookupKey Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length is < 16 or > 128 || !value.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A presence lookup key must be opaque hexadecimal text.", nameof(value));
        }

        return new PresenceLookupKey(value);
    }
}

public sealed record EncryptedPresenceRecord(
    PresenceLookupKey LookupKey,
    byte[] Payload,
    TimeSpan TimeToLive)
{
    public const int MaximumPayloadBytes = 64 * 1024;
}

public sealed record EncryptedInviteEnvelope(
    PresenceLookupKey MailboxKey,
    byte[] Payload,
    TimeSpan TimeToLive)
{
    public const int MaximumPayloadBytes = 64 * 1024;
}

public interface IPresenceClient
{
    Task PublishAsync(EncryptedPresenceRecord record, CancellationToken cancellationToken);

    Task<byte[]> QueryAsync(PresenceLookupKey key, CancellationToken cancellationToken);

    Task SendInviteAsync(EncryptedInviteEnvelope envelope, CancellationToken cancellationToken);

    Task<byte[]> ReadInviteMailboxAsync(PresenceLookupKey key, CancellationToken cancellationToken);

    Task ClearInviteMailboxAsync(PresenceLookupKey key, CancellationToken cancellationToken);
}

public sealed class HttpPresenceClient : IPresenceClient, IDisposable
{
    private static readonly Uri BaseAddress = new("https://presence.motara.org/");
    private readonly HttpClient client;

    public HttpPresenceClient(HttpClient client)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.client.BaseAddress ??= BaseAddress;
    }

    public async Task PublishAsync(EncryptedPresenceRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Payload is null || record.Payload.Length > EncryptedPresenceRecord.MaximumPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(record));
        }

        if (record.TimeToLive <= TimeSpan.Zero || record.TimeToLive > TimeSpan.FromMinutes(3))
        {
            throw new ArgumentOutOfRangeException(nameof(record));
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, $"v1/presence/{record.LookupKey.Value}")
        {
            Content = new ByteArrayContent(record.Payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("X-Motara-Presence-Ttl-Seconds", ((int)record.TimeToLive.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<byte[]> QueryAsync(PresenceLookupKey key, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(
            $"v1/presence/{key.Value}",
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        byte[] payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (payload.Length > EncryptedPresenceRecord.MaximumPayloadBytes)
        {
            throw new InvalidDataException("The presence response exceeds the configured payload limit.");
        }

        return payload;
    }

    public async Task SendInviteAsync(EncryptedInviteEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Payload is null || envelope.Payload.Length > EncryptedInviteEnvelope.MaximumPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(envelope));
        }

        if (envelope.TimeToLive <= TimeSpan.Zero || envelope.TimeToLive > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(envelope));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"v1/invite/{envelope.MailboxKey.Value}")
        {
            Content = new ByteArrayContent(envelope.Payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("X-Motara-Invite-Ttl-Seconds", ((int)envelope.TimeToLive.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public Task<byte[]> ReadInviteMailboxAsync(PresenceLookupKey key, CancellationToken cancellationToken) =>
        ReadOpaquePayloadAsync(HttpMethod.Get, $"v1/invite/{key.Value}", cancellationToken);

    public async Task ClearInviteMailboxAsync(PresenceLookupKey key, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"v1/invite/{key.Value}");
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task<byte[]> ReadOpaquePayloadAsync(HttpMethod method, string relativeUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativeUri);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        byte[] payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (payload.Length > EncryptedPresenceRecord.MaximumPayloadBytes)
        {
            throw new InvalidDataException("The presence response exceeds the configured payload limit.");
        }

        return payload;
    }

    public void Dispose() { }
}
