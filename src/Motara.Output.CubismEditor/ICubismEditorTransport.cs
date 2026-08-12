namespace Motara.Output.CubismEditor;

/// <summary>Provides one ordered text WebSocket connection to the Cubism Editor API.</summary>
public interface ICubismEditorTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken);

    Task SendTextAsync(string message, CancellationToken cancellationToken);

    Task<string> ReceiveTextAsync(CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}
