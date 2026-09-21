using System.Text.Json;

namespace RevoTrack.Rpc.Tests;

internal sealed class StubRpcClient : IRevoTrackRpcClient
{
    public Func<string, object?, JsonElement?> InvokeHandler { get; init; } =
        (_, _) => null;

    public bool IsConnected { get; set; } = true;

    public bool ThrowProjectNotOpenOnBackToHome { get; set; }

    public List<(string Method, object? Parameters)> Invocations { get; } = [];

    public event EventHandler<RevoTrackRpcConnectionStateChangedEventArgs>? ConnectionStateChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<RevoTrackRpcNotification>? NotificationReceived
    {
        add { }
        remove { }
    }

    public event EventHandler<RevoTrackRpcTransportErrorEventArgs>? TransportError
    {
        add { }
        remove { }
    }

    public RevoTrackRpcConnectionState State =>
        IsConnected
            ? RevoTrackRpcConnectionState.Connected
            : RevoTrackRpcConnectionState.Disconnected;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<JsonElement?> InvokeAsync(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        Invocations.Add((method, parameters));

        if (ThrowProjectNotOpenOnBackToHome
            && method == RevoTrackRpcMethods.FileBackToHome)
        {
            throw new RevoTrackRpcException(
                RevoTrackRpcErrorCode.ProjectNotOpen,
                "Project is not open.");
        }

        return Task.FromResult(InvokeHandler(method, parameters));
    }

    public async Task<TResult> InvokeAsync<TResult>(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(method, parameters, cancellationToken);
        return result is null
            ? default!
            : result.Value.Deserialize<TResult>()!;
    }

    public Task NotifyAsync(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public async IAsyncEnumerable<RevoTrackRpcNotification> ReadNotificationsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
