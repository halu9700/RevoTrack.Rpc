using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;

namespace RevoTrack.Rpc;

internal static class RevoTrackRpcFrameCodec
{
    private const string ProtocolVersion = "RevoTrack-RPC/1.0";
    private const string ContentLengthHeader = "Content-Length";
    private static readonly byte[] HeaderDelimiter = "\r\n\r\n"u8.ToArray();

    public static byte[] Encode(ReadOnlySpan<byte> body)
    {
        var headerText = string.Create(
            CultureInfo.InvariantCulture,
            $"{ProtocolVersion}\r\n{ContentLengthHeader}: {body.Length}\r\n\r\n");
        var header = Encoding.ASCII.GetBytes(headerText);
        var frame = new byte[header.Length + body.Length];

        header.CopyTo(frame, 0);
        body.CopyTo(frame.AsSpan(header.Length));

        return frame;
    }

    public static async IAsyncEnumerable<byte[]> ReadFramesAsync(
        PipeReader reader,
        RevoTrackRpcOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            while (TryReadFrame(ref buffer, options, out var body))
            {
                yield return body;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                break;
            }
        }
    }

    internal static bool TryReadFrame(
        ref ReadOnlySequence<byte> buffer,
        RevoTrackRpcOptions options,
        out byte[] body)
    {
        body = [];

        var sequenceReader = new SequenceReader<byte>(buffer);
        if (!sequenceReader.TryReadTo(
                out ReadOnlySequence<byte> headerSequence,
                HeaderDelimiter,
                advancePastDelimiter: true))
        {
            if (buffer.Length > options.MaxHeaderSize)
            {
                throw new RevoTrackRpcException(
                    "The RPC header exceeded the configured maximum size.");
            }

            return false;
        }

        if (headerSequence.Length > options.MaxHeaderSize)
        {
            throw new RevoTrackRpcException(
                "The RPC header exceeded the configured maximum size.");
        }

        var contentLength = ParseContentLength(headerSequence);
        if (contentLength > options.MaxFrameSize)
        {
            throw new RevoTrackRpcException(
                $"The RPC frame size {contentLength} exceeded the configured maximum "
                + $"{options.MaxFrameSize}.");
        }

        if (sequenceReader.Remaining < contentLength)
        {
            return false;
        }

        var bodyStart = sequenceReader.Position;
        body = buffer.Slice(bodyStart, contentLength).ToArray();
        buffer = buffer.Slice(bodyStart).Slice(contentLength);
        return true;
    }

    private static int ParseContentLength(ReadOnlySequence<byte> headerSequence)
    {
        var headerBytes = headerSequence.ToArray();
        var headerText = Encoding.UTF8.GetString(headerBytes);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);

        if (lines.Length < 2
            || !string.Equals(lines[0], ProtocolVersion, StringComparison.Ordinal))
        {
            throw new RevoTrackRpcException(
                $"Invalid protocol header. Expected the first line to be '{ProtocolVersion}'.");
        }

        int? contentLength = null;

        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index];
            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                throw new RevoTrackRpcException($"Invalid RPC header line: '{line}'.");
            }

            var name = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            if (!string.Equals(name, ContentLengthHeader, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!int.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedLength)
                || parsedLength < 0)
            {
                throw new RevoTrackRpcException($"Invalid Content-Length value: '{value}'.");
            }

            contentLength = parsedLength;
        }

        return contentLength
            ?? throw new RevoTrackRpcException(
                "The RPC header did not contain a Content-Length field.");
    }
}
