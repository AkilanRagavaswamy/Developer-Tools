using System.Buffers;
using System.Text;

namespace DevTools.Http.Capture;

/// <summary>A request line and headers, exactly as they arrived.</summary>
public sealed record WireRequest(
    string Method,
    string Target,
    string Version,
    IReadOnlyList<CapturedHeader> Headers)
{
    public string? Header(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
}

/// <summary>
/// Reads HTTP/1.1 off a stream, byte by byte, without assuming anything about what is on it.
/// </summary>
/// <remarks>
/// A proxy cannot use a parser that needs the whole message up front, because it has to decide
/// what to do with a request — tunnel it or read it — from the first line alone. So the head is
/// read a line at a time and the body only afterwards, once the framing headers say how.
/// </remarks>
public static class HttpWire
{
    /// <summary>The largest head we will read before deciding the peer is not speaking HTTP.</summary>
    private const int MaxHeadBytes = 64 * 1024;

    /// <summary>
    /// Reads the request line and headers. Returns <see langword="null"/> at a clean end of
    /// stream, which is how a client says it has finished with the connection.
    /// </summary>
    public static async Task<WireRequest?> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        var first = await ReadLineAsync(stream, token);

        if (string.IsNullOrEmpty(first))
        {
            return null;
        }

        var parts = first.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
        {
            return null;
        }

        var headers = await ReadHeadersAsync(stream, token);

        return new WireRequest(
            parts[0],
            parts[1],
            parts.Length > 2 ? parts[2] : "HTTP/1.1",
            headers);
    }

    public static async Task<List<CapturedHeader>> ReadHeadersAsync(Stream stream, CancellationToken token)
    {
        var headers = new List<CapturedHeader>();
        var read = 0;

        while (true)
        {
            var line = await ReadLineAsync(stream, token);

            if (string.IsNullOrEmpty(line))
            {
                break;
            }

            read += line.Length;

            if (read > MaxHeadBytes)
            {
                break;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);

            if (colon > 0)
            {
                headers.Add(new CapturedHeader(line[..colon].Trim(), line[(colon + 1)..].Trim()));
            }
        }

        return headers;
    }

    /// <summary>
    /// Reads the body the headers describe: a counted body, a chunked one, or nothing at all.
    /// </summary>
    public static async Task<byte[]> ReadBodyAsync(
        Stream stream,
        IReadOnlyList<CapturedHeader> headers,
        CancellationToken token)
    {
        var encoding = Value(headers, "Transfer-Encoding");

        if (encoding is not null && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadChunkedAsync(stream, token);
        }

        if (Value(headers, "Content-Length") is { } lengthText &&
            long.TryParse(lengthText, out var length) &&
            length > 0)
        {
            return await ReadExactlyAsync(stream, length, token);
        }

        return [];
    }

    private static string? Value(IReadOnlyList<CapturedHeader> headers, string name) =>
        headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, long length, CancellationToken token)
    {
        var buffer = new byte[length];
        var offset = 0;

        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, (int)(length - offset)), token);

            if (read == 0)
            {
                // The peer went away mid-body; keep what arrived rather than losing it.
                Array.Resize(ref buffer, offset);
                break;
            }

            offset += read;
        }

        return buffer;
    }

    private static async Task<byte[]> ReadChunkedAsync(Stream stream, CancellationToken token)
    {
        using var body = new MemoryStream();

        while (true)
        {
            var header = await ReadLineAsync(stream, token);

            if (header is null)
            {
                break;
            }

            // A chunk size may carry extensions after a semicolon; the size is what matters.
            var semicolon = header.IndexOf(';', StringComparison.Ordinal);
            var sizeText = semicolon >= 0 ? header[..semicolon] : header;

            if (!int.TryParse(sizeText.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var size) ||
                size <= 0)
            {
                break;
            }

            var chunk = await ReadExactlyAsync(stream, size, token);
            body.Write(chunk, 0, chunk.Length);

            // Each chunk is followed by its own CRLF, which is not part of the body.
            _ = await ReadLineAsync(stream, token);
        }

        // Trailers, if any, run to the blank line that ends the message.
        while (await ReadLineAsync(stream, token) is { Length: > 0 })
        {
        }

        return body.ToArray();
    }

    /// <summary>
    /// Reads one CRLF-terminated line. Returns <see langword="null"/> at end of stream, which
    /// the caller has to tell apart from the empty line that ends a header block.
    /// </summary>
    /// <remarks>
    /// One byte at a time, which is a syscall per byte and slower than it looks. It is also the
    /// only way to stop at exactly the end of the head: any buffered read would swallow the
    /// first bytes of the body, and a proxy that loses them corrupts the traffic it is meant to
    /// be observing. Heads are a few hundred bytes, so the cost stays well under the network
    /// round trip that follows.
    /// </remarks>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        var length = 0;

        try
        {
            var single = new byte[1];

            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(single.AsMemory(0, 1), token);

                if (read == 0)
                {
                    return length == 0 ? null : Encoding.ASCII.GetString(buffer, 0, length);
                }

                if (single[0] == (byte)'\n')
                {
                    if (length > 0 && buffer[length - 1] == (byte)'\r')
                    {
                        length--;
                    }

                    return Encoding.ASCII.GetString(buffer, 0, length);
                }

                buffer[length++] = single[0];
            }

            return Encoding.ASCII.GetString(buffer, 0, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
