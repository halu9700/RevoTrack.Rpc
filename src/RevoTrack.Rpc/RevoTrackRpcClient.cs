using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace RevoTrack.Rpc;

/// <summary>
/// Thread-safe client for the RevoTrack-RPC/1.0 TCP protocol.
/// </summary>
public sealed class RevoTrackRpcClient : IRevoTrackRpcClient
{
    private readonly RevoTrackRpcOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<long, PendingRequest> _pendingRequests = new();
    private readonly Channel<RevoTrackRpcNotification> _notificationChannel;

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private PipeReader? _reader;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveTask;
    private long _connectionGeneration;
    private long _nextRequestId;
    private int _state = (int)RevoTrackRpcConnectionState.Disconnected;
    private bool _disposed;

    public RevoTrackRpcClient()
        : this(new RevoTrackRpcOptions())
    {
    }

    public RevoTrackRpcClient(RevoTrackRpcOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();

        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        _notificationChannel = Channel.CreateBounded<RevoTrackRpcNotification>(
            new BoundedChannelOptions(_options.NotificationQueueCapacity)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = _options.DropOldestNotifications
                    ? BoundedChannelFullMode.DropOldest
                    : BoundedChannelFullMode.DropWrite,
            });
    }

    public event EventHandler<RevoTrackRpcConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public event EventHandler<RevoTrackRpcNotification>? NotificationReceived;

    public event EventHandler<RevoTrackRpcTransportErrorEventArgs>? TransportError;

    public RevoTrackRpcConnectionState State =>
        (RevoTrackRpcConnectionState)Volatile.Read(ref _state);

    public bool IsConnected =>
        State == RevoTrackRpcConnectionState.Connected && Volatile.Read(ref _stream) is not null;

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        ConnectAsync(_options.Host, _options.Port, cancellationToken);

    public async Task ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync(cancellationToken, waitForReceiveTask: true)
                .ConfigureAwait(false);
            SetState(RevoTrackRpcConnectionState.Connecting);

            var tcpClient = new TcpClient
            {
                NoDelay = _options.NoDelay,
            };

            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutCancellation.CancelAfter(_options.ConnectTimeout);

            try
            {
                await tcpClient.ConnectAsync(host, port, timeoutCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                tcpClient.Dispose();
                SetState(RevoTrackRpcConnectionState.Disconnected);
                throw;
            }

            var stream = tcpClient.GetStream();
            var reader = PipeReader.Create(stream);
            var receiveCancellation = new CancellationTokenSource();
            var generation = Interlocked.Increment(ref _connectionGeneration);

            Volatile.Write(ref _tcpClient, tcpClient);
            Volatile.Write(ref _stream, stream);
            Volatile.Write(ref _reader, reader);
            Volatile.Write(ref _receiveCancellation, receiveCancellation);
            _receiveTask = Task.Run(
                () => ReceiveLoopAsync(reader, generation, receiveCancellation.Token),
                CancellationToken.None);

            SetState(RevoTrackRpcConnectionState.Connected);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync(cancellationToken, waitForReceiveTask: true)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<JsonElement?> InvokeAsync(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        EnsureConnected();

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var pendingRequest = new PendingRequest();

        if (!_pendingRequests.TryAdd(requestId, pendingRequest))
        {
            throw new RevoTrackRpcException($"A request with id {requestId} is already pending.");
        }

        using var registration = cancellationToken.Register(
            static state =>
            {
                var pending = (PendingRequest)state!;
                pending.Completion.TrySetCanceled();
            },
            pendingRequest);

        try
        {
            var request = new RpcRequest("2.0", method, parameters, requestId);
            var body = JsonSerializer.SerializeToUtf8Bytes(request, _jsonOptions);
            await SendFrameAsync(body, cancellationToken).ConfigureAwait(false);

            var result = await pendingRequest.Completion.Task.ConfigureAwait(false);
            return result;
        }
        catch
        {
            _pendingRequests.TryRemove(requestId, out _);
            throw;
        }
    }

    public async Task<TResult> InvokeAsync<TResult>(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        if (result is null || result.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default!;
        }

        return result.Value.Deserialize<TResult>(_jsonOptions)
            ?? throw new RevoTrackRpcException(
                $"The result for '{method}' could not be deserialized.");
    }

    public async Task NotifyAsync(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        EnsureConnected();

        var notification = new RpcNotification("2.0", method, parameters);
        var body = JsonSerializer.SerializeToUtf8Bytes(notification, _jsonOptions);
        await SendFrameAsync(body, cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<RevoTrackRpcNotification> ReadNotificationsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _notificationChannel.Reader.ReadAllAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync(CancellationToken.None, waitForReceiveTask: true)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
            _lifecycleLock.Dispose();
            _sendLock.Dispose();
            _notificationChannel.Writer.TryComplete();
        }
    }

    private async Task ReceiveLoopAsync(
        PipeReader reader,
        long generation,
        CancellationToken cancellationToken)
    {
        Exception? transportException = null;

        try
        {
            await foreach (var body in RevoTrackRpcFrameCodec.ReadFramesAsync(
                               reader,
                               _options,
                               cancellationToken).ConfigureAwait(false))
            {
                ProcessMessage(body);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            transportException = exception;
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await HandleConnectionLostAsync(generation, transportException)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task HandleConnectionLostAsync(long generation, Exception? exception)
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != Volatile.Read(ref _connectionGeneration))
            {
                return;
            }

            await DisconnectCoreAsync(CancellationToken.None, waitForReceiveTask: false)
                .ConfigureAwait(false);

            if (exception is not null)
            {
                RaiseTransportError(exception);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task DisconnectCoreAsync(
        CancellationToken cancellationToken,
        bool waitForReceiveTask)
    {
        Interlocked.Increment(ref _connectionGeneration);

        var receiveCancellation = Interlocked.Exchange(ref _receiveCancellation, null);
        var reader = Interlocked.Exchange(ref _reader, null);
        var stream = Interlocked.Exchange(ref _stream, null);
        var tcpClient = Interlocked.Exchange(ref _tcpClient, null);
        var receiveTask = Interlocked.Exchange(ref _receiveTask, null);

        receiveCancellation?.Cancel();
        reader?.CancelPendingRead();

        if (reader is not null)
        {
            reader.Complete();
        }

        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        tcpClient?.Dispose();
        receiveCancellation?.Dispose();

        if (waitForReceiveTask && receiveTask is not null)
        {
            try
            {
                await receiveTask.WaitAsync(_options.DisconnectTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        FailPendingRequests(new RevoTrackRpcException("The RevoTrack connection was closed."));
        SetState(RevoTrackRpcConnectionState.Disconnected);
    }

    private async Task SendFrameAsync(byte[] body, CancellationToken cancellationToken)
    {
        var frame = RevoTrackRpcFrameCodec.Encode(body);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = Volatile.Read(ref _stream)
                ?? throw new RevoTrackRpcException("The RevoTrack client is not connected.");

            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void ProcessMessage(byte[] body)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new RevoTrackRpcException(
                RevoTrackRpcErrorCode.ParseError,
                "The RPC body was not valid JSON.",
                innerException: exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new RevoTrackRpcException(
                    RevoTrackRpcErrorCode.InvalidRequest,
                    "The JSON-RPC message must be an object.");
            }

            if (!root.TryGetProperty("id", out var idElement))
            {
                ProcessNotification(root);
                return;
            }

            if (idElement.ValueKind != JsonValueKind.Number
                || !idElement.TryGetInt64(out var requestId))
            {
                throw new RevoTrackRpcException(
                    RevoTrackRpcErrorCode.InvalidRequest,
                    "The JSON-RPC response contained an invalid id.");
            }

            if (!_pendingRequests.TryRemove(requestId, out var pendingRequest))
            {
                return;
            }

            if (root.TryGetProperty("error", out var error))
            {
                var errorCode = error.TryGetProperty("code", out var codeElement)
                    && codeElement.TryGetInt32(out var parsedCode)
                        ? parsedCode
                        : RevoTrackRpcErrorCode.Unknown;
                var message = error.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString() ?? "Unknown JSON-RPC error."
                    : "Unknown JSON-RPC error.";
                var errorData = error.TryGetProperty("data", out var dataElement)
                    ? dataElement.Clone()
                    : (JsonElement?)null;

                pendingRequest.Completion.TrySetException(
                    new RevoTrackRpcException(errorCode, message, errorData));
                return;
            }

            if (!root.TryGetProperty("result", out var resultElement))
            {
                pendingRequest.Completion.TrySetException(
                    new RevoTrackRpcException(
                        RevoTrackRpcErrorCode.InvalidRequest,
                        "The JSON-RPC response did not contain a result or error field."));
                return;
            }

            pendingRequest.Completion.TrySetResult(resultElement.Clone());
        }
    }

    private void ProcessNotification(JsonElement root)
    {
        if (!root.TryGetProperty("method", out var methodElement)
            || methodElement.GetString() is not { Length: > 0 } method)
        {
            throw new RevoTrackRpcException(
                RevoTrackRpcErrorCode.InvalidRequest,
                "The JSON-RPC notification did not contain a valid method.");
        }

        var parameters = root.TryGetProperty("params", out var parametersElement)
            ? parametersElement.Clone()
            : (JsonElement?)null;
        var notification = new RevoTrackRpcNotification(
            method,
            parameters,
            root.Clone());

        _notificationChannel.Writer.TryWrite(notification);
        RaiseNotification(notification);
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
        {
            throw new RevoTrackRpcException("The RevoTrack client is not connected.");
        }
    }

    private void SetState(RevoTrackRpcConnectionState state)
    {
        var previousState = (RevoTrackRpcConnectionState)Interlocked.Exchange(
            ref _state,
            (int)state);

        if (previousState != state)
        {
            RaiseSafely(
                ConnectionStateChanged,
                new RevoTrackRpcConnectionStateChangedEventArgs(previousState, state));
        }
    }

    private void FailPendingRequests(Exception exception)
    {
        foreach (var pendingRequest in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(pendingRequest.Key, out var removed))
            {
                removed.Completion.TrySetException(exception);
            }
        }
    }

    private void RaiseNotification(RevoTrackRpcNotification notification) =>
        RaiseSafely(NotificationReceived, notification);

    private void RaiseTransportError(Exception exception) =>
        RaiseSafely(TransportError, new RevoTrackRpcTransportErrorEventArgs(exception));

    private void RaiseSafely<TEventArgs>(
        EventHandler<TEventArgs>? handlers,
        TEventArgs eventArgs)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch
            {
            }
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class PendingRequest
    {
        public TaskCompletionSource<JsonElement?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record RpcRequest(
        [property: JsonPropertyName("jsonrpc")] string JsonRpc,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object? Parameters,
        [property: JsonPropertyName("id")] long Id);

    private sealed record RpcNotification(
        [property: JsonPropertyName("jsonrpc")] string JsonRpc,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object? Parameters);
}
