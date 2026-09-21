using System.Text;
using System.Text.Json;

namespace RevoTrack.Rpc.Tests;

public sealed class RevoTrackRpcClientTests
{
    [Fact]
    public async Task InvokeAsync_MatchesResponseById()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new TestRpcServer();
        await using var client = CreateClient();

        await client.ConnectAsync("127.0.0.1", server.Port, timeout.Token);
        var serverClient = await server.AcceptClientAsync(timeout.Token);

        var serverTask = Task.Run(
            async () =>
            {
                var request = await TestRpcServer.ReadFrameAsync(serverClient, timeout.Token);
                using var document = JsonDocument.Parse(request);
                var root = document.RootElement;

                Assert.Equal(RevoTrackRpcMethods.ScannerState, root.GetProperty("method").GetString());

                var response = Encoding.UTF8.GetBytes(
                    $$"""
                    {
                      "jsonrpc": "2.0",
                      "result": { "connectState": "connected" },
                      "id": {{root.GetProperty("id").GetInt64()}}
                    }
                    """);
                await TestRpcServer.WriteFrameAsync(serverClient, response, timeout.Token);
            },
            timeout.Token);

        var result = await client.InvokeAsync<JsonElement>(
            RevoTrackRpcMethods.ScannerState,
            cancellationToken: timeout.Token);

        Assert.Equal("connected", result.GetProperty("connectState").GetString());
        await serverTask;
    }

    [Fact]
    public async Task InvokeAsync_MatchesOutOfOrderResponses()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new TestRpcServer();
        await using var client = CreateClient();

        await client.ConnectAsync("127.0.0.1", server.Port, timeout.Token);
        var serverClient = await server.AcceptClientAsync(timeout.Token);

        var serverTask = Task.Run(
            async () =>
            {
                var first = await TestRpcServer.ReadFrameAsync(serverClient, timeout.Token);
                var second = await TestRpcServer.ReadFrameAsync(serverClient, timeout.Token);

                await WriteStringResultAsync(serverClient, second, "second", timeout.Token);
                await WriteStringResultAsync(serverClient, first, "first", timeout.Token);
            },
            timeout.Token);

        var firstTask = client.InvokeAsync<string>("first/method", cancellationToken: timeout.Token);
        var secondTask = client.InvokeAsync<string>("second/method", cancellationToken: timeout.Token);

        Assert.Equal("first", await firstTask);
        Assert.Equal("second", await secondTask);
        await serverTask;
    }

    [Fact]
    public async Task InvokeAsync_HandlesFrameSplitAcrossTcpWrites()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new TestRpcServer();
        await using var client = CreateClient();

        await client.ConnectAsync("127.0.0.1", server.Port, timeout.Token);
        var serverClient = await server.AcceptClientAsync(timeout.Token);

        var serverTask = Task.Run(
            async () =>
            {
                var request = await TestRpcServer.ReadFrameAsync(serverClient, timeout.Token);
                using var document = JsonDocument.Parse(request);
                var response = Encoding.UTF8.GetBytes(
                    $$"""
                    {
                      "jsonrpc": "2.0",
                      "result": "connected",
                      "id": {{document.RootElement.GetProperty("id").GetInt64()}}
                    }
                    """);
                var frame = RevoTrackRpcFrameCodec.Encode(response);
                var splitIndex = frame.Length / 2;
                var stream = serverClient.GetStream();

                await stream.WriteAsync(frame.AsMemory(0, splitIndex), timeout.Token);
                await Task.Delay(20, timeout.Token);
                await stream.WriteAsync(frame.AsMemory(splitIndex), timeout.Token);
            },
            timeout.Token);

        var result = await client.InvokeAsync<string>(
            RevoTrackRpcMethods.ScannerState,
            cancellationToken: timeout.Token);

        Assert.Equal("connected", result);
        await serverTask;
    }

    [Fact]
    public async Task InvokeAsync_MapsJsonRpcError()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new TestRpcServer();
        await using var client = CreateClient();

        await client.ConnectAsync("127.0.0.1", server.Port, timeout.Token);
        var serverClient = await server.AcceptClientAsync(timeout.Token);

        var serverTask = Task.Run(
            async () =>
            {
                var request = await TestRpcServer.ReadFrameAsync(serverClient, timeout.Token);
                using var document = JsonDocument.Parse(request);

                var response = Encoding.UTF8.GetBytes(
                    $$"""
                    {
                      "jsonrpc": "2.0",
                      "error": { "code": 50528275, "message": "Scan not ready" },
                      "id": {{document.RootElement.GetProperty("id").GetInt64()}}
                    }
                    """);
                await TestRpcServer.WriteFrameAsync(serverClient, response, timeout.Token);
            },
            timeout.Token);

        var exception = await Assert.ThrowsAsync<RevoTrackRpcException>(
            () => client.InvokeAsync(RevoTrackRpcMethods.ScanStart, cancellationToken: timeout.Token));

        Assert.Equal(RevoTrackRpcErrorCode.ScanNotReady, exception.ErrorCode);
        Assert.Equal("Scan not ready", exception.Message);
        await serverTask;
    }

    [Fact]
    public async Task SetScannerAutoOptionsAsync_SendsAutoExposureTypes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new TestRpcServer();
        await using var client = CreateClient();

        await client.ConnectAsync("127.0.0.1", server.Port, timeout.Token);
        var serverClient = await server.AcceptClientAsync(timeout.Token);

        var serverTask = Task.Run(
            async () =>
            {
                var request = await TestRpcServer.ReadFrameAsync(serverClient, timeout.Token);
                using var document = JsonDocument.Parse(request);
                var root = document.RootElement;
                var scanner = root.GetProperty("params").GetProperty("scanner");

                Assert.Equal("scanner/setOptions", root.GetProperty("method").GetString());
                Assert.Equal("auto", scanner.GetProperty("depthExposureType").GetString());
                Assert.Equal("auto", scanner.GetProperty("rgbExposureType").GetString());
                Assert.Equal(12, scanner.GetProperty("laserBrightness").GetInt32());
                Assert.False(scanner.TryGetProperty("depthExposure", out _));
                Assert.False(scanner.TryGetProperty("rgbExposure", out _));

                var response = Encoding.UTF8.GetBytes(
                    $$"""
                    {
                      "jsonrpc": "2.0",
                      "result": null,
                      "id": {{root.GetProperty("id").GetInt64()}}
                    }
                    """);
                await TestRpcServer.WriteFrameAsync(serverClient, response, timeout.Token);
            },
            timeout.Token);

        await client.SetScannerAutoOptionsAsync(
            laserBrightness: 12,
            cancellationToken: timeout.Token);

        await serverTask;
    }

    [Fact]
    public async Task NotificationReceived_ReceivesServerNotification()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new TestRpcServer();
        await using var client = CreateClient();
        var notificationSource =
            new TaskCompletionSource<RevoTrackRpcNotification>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        client.NotificationReceived += (_, notification) =>
            notificationSource.TrySetResult(notification);

        await client.ConnectAsync("127.0.0.1", server.Port, timeout.Token);
        var serverClient = await server.AcceptClientAsync(timeout.Token);

        var notification = Encoding.UTF8.GetBytes(
            """
            {
              "jsonrpc": "2.0",
              "method": "scan/stateChanged",
              "params": { "state": "ready" }
            }
            """);
        await TestRpcServer.WriteFrameAsync(serverClient, notification, timeout.Token);

        var received = await notificationSource.Task.WaitAsync(timeout.Token);

        Assert.Equal("scan/stateChanged", received.Method);
        Assert.Equal("ready", received.Parameters?.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ReadNotificationsAsync_ReceivesServerNotification()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new TestRpcServer();
        await using var client = CreateClient();
        var notificationTask = ReadFirstNotificationAsync(
            client.ReadNotificationsAsync(timeout.Token),
            timeout.Token);

        await client.ConnectAsync("127.0.0.1", server.Port, timeout.Token);
        var serverClient = await server.AcceptClientAsync(timeout.Token);

        var notification = Encoding.UTF8.GetBytes(
            """
            {
              "jsonrpc": "2.0",
              "method": "scan/stateChanged",
              "params": { "state": "started" }
            }
            """);
        await TestRpcServer.WriteFrameAsync(serverClient, notification, timeout.Token);

        var received = await notificationTask;

        Assert.Equal("scan/stateChanged", received.Method);
        Assert.Equal("started", received.Parameters?.GetProperty("state").GetString());
    }

    [Fact]
    public async Task InvokeAsync_WhenDisconnected_Throws()
    {
        await using var client = CreateClient();

        var exception = await Assert.ThrowsAsync<RevoTrackRpcException>(
            () => client.InvokeAsync(RevoTrackRpcMethods.ScannerState));

        Assert.Contains("not connected", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static RevoTrackRpcClient CreateClient() =>
        new(
            new RevoTrackRpcOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                DisconnectTimeout = TimeSpan.FromSeconds(1),
            });

    private static async Task WriteStringResultAsync(
        System.Net.Sockets.TcpClient client,
        byte[] request,
        string result,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(request);
        var response = Encoding.UTF8.GetBytes(
            $$"""
            {
              "jsonrpc": "2.0",
              "result": "{{result}}",
              "id": {{document.RootElement.GetProperty("id").GetInt64()}}
            }
            """);
        await TestRpcServer.WriteFrameAsync(client, response, cancellationToken);
    }

    private static async Task<RevoTrackRpcNotification> ReadFirstNotificationAsync(
        IAsyncEnumerable<RevoTrackRpcNotification> notifications,
        CancellationToken cancellationToken)
    {
        await foreach (var notification in notifications.WithCancellation(cancellationToken))
        {
            return notification;
        }

        throw new InvalidOperationException("The notification sequence completed unexpectedly.");
    }
}
