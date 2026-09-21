using System.Buffers;
using System.Text;

namespace RevoTrack.Rpc.Tests;

public sealed class FrameCodecTests
{
    [Fact]
    public void Encode_WritesProtocolHeaderAndUtf8ByteLength()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":\"你好\"}");

        var frame = RevoTrackRpcFrameCodec.Encode(body);
        var headerLength = frame.AsSpan().IndexOf("\r\n\r\n"u8);
        var header = Encoding.ASCII.GetString(frame.AsSpan(0, headerLength + 4));

        Assert.Contains("RevoTrack-RPC/1.0\r\n", header);
        Assert.Contains($"Content-Length: {body.Length}\r\n", header);
        Assert.EndsWith("\r\n\r\n", header);
    }

    [Fact]
    public void TryReadFrame_ReadsMultipleFramesFromOneBuffer()
    {
        var firstBody = Encoding.UTF8.GetBytes("{\"id\":1}");
        var secondBody = Encoding.UTF8.GetBytes("{\"id\":2}");
        var buffer = new ReadOnlySequence<byte>(
            RevoTrackRpcFrameCodec.Encode(firstBody)
                .Concat(RevoTrackRpcFrameCodec.Encode(secondBody))
                .ToArray());
        var options = new RevoTrackRpcOptions();

        Assert.True(RevoTrackRpcFrameCodec.TryReadFrame(ref buffer, options, out var first));
        Assert.Equal(firstBody, first);
        Assert.True(RevoTrackRpcFrameCodec.TryReadFrame(ref buffer, options, out var second));
        Assert.Equal(secondBody, second);
        Assert.False(RevoTrackRpcFrameCodec.TryReadFrame(ref buffer, options, out _));
    }

    [Fact]
    public void TryReadFrame_WaitsForPartialBody()
    {
        var body = Encoding.UTF8.GetBytes("{\"id\":1}");
        var frame = RevoTrackRpcFrameCodec.Encode(body);
        var buffer = new ReadOnlySequence<byte>(frame[..^1]);

        var complete = RevoTrackRpcFrameCodec.TryReadFrame(
            ref buffer,
            new RevoTrackRpcOptions(),
            out _);

        Assert.False(complete);
        Assert.Equal(frame.Length - 1, buffer.Length);
    }

    [Fact]
    public void TryReadFrame_RejectsOversizedFrame()
    {
        var body = Encoding.UTF8.GetBytes("{\"id\":1}");
        var frame = RevoTrackRpcFrameCodec.Encode(body);
        var buffer = new ReadOnlySequence<byte>(frame);
        var options = new RevoTrackRpcOptions
        {
            MaxFrameSize = body.Length - 1,
        };

        var exception = Assert.Throws<RevoTrackRpcException>(
            () => RevoTrackRpcFrameCodec.TryReadFrame(ref buffer, options, out _));

        Assert.Contains("exceeded", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
