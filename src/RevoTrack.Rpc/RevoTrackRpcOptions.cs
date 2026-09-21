namespace RevoTrack.Rpc;

/// <summary>
/// Configures the RevoTrack RPC client.
/// </summary>
public sealed class RevoTrackRpcOptions
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 53665;
    public const int DefaultMaxFrameSize = 16 * 1024 * 1024;
    public const int DefaultMaxHeaderSize = 8 * 1024;
    public const int DefaultNotificationQueueCapacity = 1024;

    public string Host { get; set; } = DefaultHost;

    public int Port { get; set; } = DefaultPort;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan DisconnectTimeout { get; set; } = TimeSpan.FromSeconds(2);

    public int MaxFrameSize { get; set; } = DefaultMaxFrameSize;

    public int MaxHeaderSize { get; set; } = DefaultMaxHeaderSize;

    public int NotificationQueueCapacity { get; set; } = DefaultNotificationQueueCapacity;

    public bool DropOldestNotifications { get; set; } = true;

    public bool NoDelay { get; set; } = true;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        ArgumentOutOfRangeException.ThrowIfLessThan(Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Port, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ConnectTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(DisconnectTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxFrameSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxHeaderSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(NotificationQueueCapacity, 0);
    }
}

/// <summary>
/// Describes the client connection lifecycle.
/// </summary>
public enum RevoTrackRpcConnectionState
{
    Disconnected,
    Connecting,
    Connected,
}

public sealed class RevoTrackRpcConnectionStateChangedEventArgs(
    RevoTrackRpcConnectionState previousState,
    RevoTrackRpcConnectionState currentState) : EventArgs
{
    public RevoTrackRpcConnectionState PreviousState { get; } = previousState;

    public RevoTrackRpcConnectionState CurrentState { get; } = currentState;
}

public sealed class RevoTrackRpcTransportErrorEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}
