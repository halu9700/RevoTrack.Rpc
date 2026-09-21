using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

namespace RevoTrack.Rpc.Tests;

internal sealed class TestRpcServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task<TcpClient> _acceptTask;
    private TcpClient? _acceptedClient;

    public TestRpcServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = _listener.AcceptTcpClientAsync(_cancellation.Token).AsTask();
    }

    public int Port { get; }

    public async Task<TcpClient> AcceptClientAsync(CancellationToken cancellationToken)
    {
        _acceptedClient = await _acceptTask.WaitAsync(cancellationToken);
        return _acceptedClient;
    }

    public static async Task<byte[]> ReadFrameAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var reader = PipeReader.Create(
            stream,
            new StreamPipeReaderOptions(leaveOpen: true));

        try
        {
            await foreach (var body in RevoTrackRpcFrameCodec.ReadFramesAsync(
                               reader,
                               new RevoTrackRpcOptions(),
                               cancellationToken))
            {
                return body;
            }

            throw new EndOfStreamException("The test client disconnected before sending a frame.");
        }
        finally
        {
            reader.Complete();
        }
    }

    public static async Task WriteFrameAsync(
        TcpClient client,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        var frame = RevoTrackRpcFrameCodec.Encode(body.Span);
        await client.GetStream().WriteAsync(frame, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _listener.Stop();
        _acceptedClient?.Dispose();

        try
        {
            await _acceptTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }

        _cancellation.Dispose();
    }
}
