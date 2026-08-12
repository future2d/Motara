using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Motara.Output.CubismEditor;

/// <summary>Uses the platform WebSocket client while enforcing text-only, bounded protocol messages.</summary>
internal sealed class ClientWebSocketCubismEditorTransport : ICubismEditorTransport
{
    private const int ReceiveBufferSize = 16 * 1024;
    private const int MaximumMessageBytes = 1024 * 1024;
    private readonly ClientWebSocket socket = new();

    public async Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
    }

    public Task SendTextAsync(string message, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    public async Task<string> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        try
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Cubism Editor closed the WebSocket connection.");
                if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > MaximumMessageBytes)
                    throw new WebSocketException("Cubism Editor sent an unsupported or oversized WebSocket message.");
                await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
            }
            while (!result.EndOfMessage);
            return Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Motara output stopped.", cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try { await CloseAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (WebSocketException) { }
        finally { socket.Dispose(); }
    }
}
