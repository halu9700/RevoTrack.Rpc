using System.Text.Json;

namespace RevoTrack.Rpc;

/// <summary>
/// Sends requests and receives notifications over the RevoTrack-RPC/1.0 protocol.
/// </summary>
public interface IRevoTrackRpcClient : IAsyncDisposable
{
    event EventHandler<RevoTrackRpcConnectionStateChangedEventArgs>? ConnectionStateChanged;

    event EventHandler<RevoTrackRpcNotification>? NotificationReceived;

    event EventHandler<RevoTrackRpcTransportErrorEventArgs>? TransportError;

    RevoTrackRpcConnectionState State { get; }

    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task<JsonElement?> InvokeAsync(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default);

    Task<TResult> InvokeAsync<TResult>(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default);

    Task NotifyAsync(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<RevoTrackRpcNotification> ReadNotificationsAsync(
        CancellationToken cancellationToken = default);
}
