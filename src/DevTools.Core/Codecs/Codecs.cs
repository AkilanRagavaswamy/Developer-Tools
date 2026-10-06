using System.Buffers.Text;
using System.Globalization;
using System.Text;
using DevTools.Core.Text;

namespace DevTools.Core.Codecs;

/// <summary>Which way a codec runs.</summary>
public enum CodecDirection
{
    Encode,
    Decode,
}

/// <summary>Options for the Base64 text codec.</summary>
public sealed record Base64Options
{
    public TextEncodingKind TextEncoding { get; init; } = TextEncodingKind.Utf8;

    /// <summary>The RFC 4648 §5 alphabet: <c>-</c> and <c>_</c> instead of <c>+</c> and <c>/</c>, no padding.</summary>
    public bool UrlSafe { get; init; }

    /// <summary>Wraps encoded output at 76 characters, as MIME does.</summary>
    public bool WrapLines { get; init; }

    /// <summary>Encodes or decodes each line on its own.</summary>
    public bool EachLine { get; init; }

    public static Base64Options Default { get; } = new();
}

/// <summary>
/// Base64 to and from text.
/// </summary>
/// <remarks>
/// Decoding is deliberately forgiving about form and strict about content: whitespace and line
/// breaks are ignored, missing padding is restored, and either alphabet is accepted, because
/// that is the shape Base64 arrives in from headers, JWTs and e-mail. What it will not do is
/// pretend binary bytes are text: if the result is not valid in the chosen encoding, it says
/// how many bytes there were rather than showing replacement characters.
/// </remarks>
public static class Base64Codec
{
    public static OperationResult<string> Run(string? input, CodecDirection direction, Base64Options? options = null)
    {
        var opts = options ?? Base64Options.Default;
        input ??= string.Empty;

        if (input.Length == 0)
        {
            return OperationResult<string>.Ok(string.Empty);
        }

        if (!opts.EachLine)
        {
            return direction == CodecDirection.Encode
                ? OperationResult<string>.Ok(Encode(input, opts))
                : Decode(input, opts);
        }

        var lines = TextUtil.NormalizeNewlines(input).Split('\n');
        var output = new StringBuilder(input.Length * 2);

        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                output.Append('\n');
            }

            if (lines[i].Length == 0)
            {
                continue;
            }

            if (direction == CodecDirection.Encode)
            {
                output.Append(Encode(lines[i], opts with { WrapLines = false }));
                continue;
            }

            var decoded = Decode(lines[i], opts);
            if (!decoded.IsSuccess)
            {
                return OperationResult<string>.Fail($"Line {i + 1}: {decoded.Error!.Message}");
            }

            output.Append(decoded.Value);
        }

        return OperationResult<string>.Ok(output.ToString());
    }

    public static string Encode(string text, Base64Options options)
    {
        var bytes = EncodingCatalog.GetBytes(text, options.TextEncoding);

        if (options.TextEncoding == TextEncodingKind.Utf8Bom)
        {
            bytes = [0xEF, 0xBB, 0xBF, .. bytes];
        }

        return EncodeBytes(bytes, options.UrlSafe, options.WrapLines);
    }

    public static string EncodeBytes(ReadOnlySpan<byte> bytes, bool urlSafe = false, bool wrapLines = false)
    {
        if (urlSafe)
        {
            return Base64Url.EncodeToString(bytes);
        }

        return Convert.ToBase64String(bytes, wrapLines ? Base64FormattingOptions.InsertLineBreaks : Base64FormattingOptions.None)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public static OperationResult<string> Decode(string base64, Base64Options options)
    {
        var bytes = DecodeBytes(base64);
        if (!bytes.IsSuccess)
        {
            return OperationResult<string>.Fail(bytes.Error!);
        }

        var data = bytes.Value!;
        var encoding = options.TextEncoding;

        // A BOM in the data names its own encoding, whatever was chosen.
        var span = data.AsSpan();
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            span = span[3..];
            encoding = TextEncodingKind.Utf8;
        }

        if (!IsValidText(span, encoding))
        {
            return OperationResult<string>.Fail(
                $"This decodes to {data.Length:N0} bytes that are not valid {EncodingCatalog.DisplayName(encoding)} text — " +
                "it is probably binary data, such as an image or a compressed file.");
        }

        return OperationResult<string>.Ok(EncodingCatalog.GetString(span, encoding));
    }

    /// <summary>
    /// Decodes Base64 in any of the forms it turns up in: either alphabet, with or without
    /// padding, broken across lines, or behind a <c>data:</c> URI prefix.
    /// </summary>
    public static OperationResult<byte[]> DecodeBytes(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return OperationResult<byte[]>.Ok([]);
        }

        var text = base64.Trim();

        // "data:image/png;base64,iVBOR…" — everything up to the comma is the label.
        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = text.IndexOf(',', StringComparison.Ordinal);
            if (comma < 0)
            {
                return OperationResult<byte[]>.Fail("The data URI has no comma, so there is no data after its header.");
            }

            text = text[(comma + 1)..];
        }

        var clean = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            switch (c)
            {
                case '-':
                    clean.Append('+');
                    break;
                case '_':
                    clean.Append('/');
                    break;
                case ' ' or '\t' or '\r' or '\n':
                    break;
                case '=':
                    clean.Append(c);
                    break;
                default:
                    if (char.IsAsciiLetterOrDigit(c) || c is '+' or '/')
                    {
                        clean.Append(c);
                        break;
                    }

                    return OperationResult<byte[]>.Fail(
                        $"'{c}' at position {i + 1} is not a Base64 character.");
            }
        }

        var padding = clean.Length % 4;
        if (padding == 1)
        {
            return OperationResult<byte[]>.Fail("The input is one character too long or too short to be Base64.");
        }

        if (padding > 0)
        {
            clean.Append('=', 4 - padding);
        }

        try
        {
            return OperationResult<byte[]>.Ok(Convert.FromBase64String(clean.ToString()));
        }
        catch (FormatException)
        {
            return OperationResult<byte[]>.Fail("The padding ('=') is in the wrong place, so this is not valid Base64.");
        }
    }

    private static bool IsValidText(ReadOnlySpan<byte> bytes, TextEncodingKind kind)
    {
        try
        {
            var strict = kind switch
            {
                TextEncodingKind.Utf8 or TextEncodingKind.Utf8Bom => new UTF8Encoding(false, throwOnInvalidBytes: true),
                TextEncodingKind.Utf16Le => new UnicodeEncoding(false, false, throwOnInvalidBytes: true),
                TextEncodingKind.Utf16Be => new UnicodeEncoding(true, false, throwOnInvalidBytes: true),
                TextEncodingKind.Utf32Le => new UTF32Encoding(false, false, throwOnInvalidCharacters: true),
                TextEncodingKind.Ascii => System.Text.Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
                _ => (System.Text.Encoding?)null,
            };

            var decoded = strict is null ? EncodingCatalog.GetString(bytes, kind) : strict.GetString(bytes);

            // Raw control bytes other than tab and newlines mean binary, even when they are legal.
            foreach (var c in decoded)
            {
                if (c < ' ' && c is not ('\t' or '\n' or '\r'))
                {
                    return false;
                }
            }

            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

/// <summary>An image recognised from its first bytes.</summary>
public sealed record ImageFormatInfo(string Name, string MimeType, string Extension);

/// <summary>Base64 and data URIs to and from image bytes.</summary>
public static class Base64Image
{
    public static IReadOnlyList<ImageFormatInfo> Formats { get; } =
    [
        new("PNG", "image/png", ".png"),
        new("JPEG", "image/jpeg", ".jpg"),
        new("GIF", "image/gif", ".gif"),
        new("BMP", "image/bmp", ".bmp"),
        new("WebP", "image/webp", ".webp"),
        new("ICO", "image/x-icon", ".ico"),
        new("SVG", "image/svg+xml", ".svg"),
        new("TIFF", "image/tiff", ".tif"),
    ];

    /// <summary>
    /// Recognises an image by its signature, not by any label it came with — a data URI that
    /// says <c>image/png</c> over JPEG bytes is common, and the bytes are what will render.
    /// </summary>
    public static ImageFormatInfo? Detect(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return Formats[0];
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return Formats[1];
        }

        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8))
        {
            return Formats[2];
        }

        if (data.StartsWith("BM"u8) && data.Length > 14)
        {
            return Formats[3];
        }

        if (data.Length > 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            return Formats[4];
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0x00]))
        {
            return Formats[5];
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0x49, 0x49, 0x2A, 0x00]) || data.StartsWith((ReadOnlySpan<byte>)[0x4D, 0x4D, 0x00, 0x2A]))
        {
            return Formats[7];
        }

        // SVG is text: look for an <svg element near the start, past any declaration or BOM.
        var head = System.Text.Encoding.UTF8.GetString(data[..Math.Min(data.Length, 1024)]);
        if (head.TrimStart('﻿', ' ', '\t', '\r', '\n').StartsWith('<') &&
            head.Contains("<svg", StringComparison.OrdinalIgnoreCase))
        {
            return Formats[6];
        }

        return null;
    }

    public static ImageFormatInfo? FromExtension(string? extension) =>
        Formats.FirstOrDefault(f => string.Equals(f.Extension, extension, StringComparison.OrdinalIgnoreCase)) ??
        (extension?.ToLowerInvariant() switch
        {
            ".jpeg" or ".jfif" => Formats[1],
            ".tiff" => Formats[7],
            _ => null,
        });

    /// <summary>The image as Base64, optionally behind a <c>data:</c> prefix ready for HTML or CSS.</summary>
    public static string Encode(ReadOnlySpan<byte> data, bool asDataUri, ImageFormatInfo? format = null)
    {
        var base64 = Convert.ToBase64String(data);
        var mime = (format ?? Detect(data))?.MimeType ?? "application/octet-stream";
        return asDataUri ? $"data:{mime};base64,{base64}" : base64;
    }

    /// <summary>Decodes Base64 or a data URI and confirms the bytes are an image.</summary>
    public static OperationResult<(byte[] Data, ImageFormatInfo Format)> Decode(string? input)
    {
        var bytes = Base64Codec.DecodeBytes(input);
        if (!bytes.IsSuccess)
        {
            return OperationResult<(byte[], ImageFormatInfo)>.Fail(bytes.Error!);
        }

        if (bytes.Value!.Length == 0)
        {
            return OperationResult<(byte[], ImageFormatInfo)>.Fail("There is nothing to decode.");
        }

        var format = Detect(bytes.Value);
        return format is null
            ? OperationResult<(byte[], ImageFormatInfo)>.Fail(
                $"This decodes to {bytes.Value.Length:N0} bytes, but they are not a PNG, JPEG, GIF, BMP, WebP, ICO, TIFF or SVG image.")
            : OperationResult<(byte[], ImageFormatInfo)>.Ok((bytes.Value, format));
    }
}

/// <summary>How much of the text URL encoding escapes.</summary>
public enum UrlEncodeMode
{
    /// <summary>Everything but the RFC 3986 unreserved characters — for a query value or path segment.</summary>
    Component,

    /// <summary>Only what a URL cannot contain, keeping <c>:/?#[]@!$&amp;'()*+,;=</c> — for a whole URL.</summary>
    FullUrl,

    /// <summary><c>application/x-www-form-urlencoded</c>: as Component, but a space becomes <c>+</c>.</summary>
    Form,
}

/// <summary>Percent-encoding (RFC 3986).</summary>
public static class UrlCodec
{
    public static OperationResult<string> Run(string? input, CodecDirection direction, UrlEncodeMode mode, bool eachLine)
    {
        input ??= string.Empty;

        if (!eachLine)
        {
            return OperationResult<string>.Ok(direction == CodecDirection.Encode ? Encode(input, mode) : Decode(input, mode));
        }

        var lines = TextUtil.NormalizeNewlines(input).Split('\n');
        return OperationResult<string>.Ok(string.Join('\n', lines.Select(line =>
            direction == CodecDirection.Encode ? Encode(line, mode) : Decode(line, mode))));
    }

    public static string Encode(string text, UrlEncodeMode mode)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var output = new StringBuilder(bytes.Length * 3);

        foreach (var b in bytes)
        {
            var c = (char)b;

            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~'))
            {
                output.Append(c);
            }
            else if (mode == UrlEncodeMode.Form && c == ' ')
            {
                output.Append('+');
            }
            else if (mode == UrlEncodeMode.FullUrl && b < 0x80 && ":/?#[]@!$&'()*+,;=%".Contains(c, StringComparison.Ordinal))
            {
                output.Append(c);
            }
            else
            {
                output.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return output.ToString();
    }

    /// <summary>
    /// Decodes percent-escapes as UTF-8. A malformed escape is left exactly as written rather
    /// than failing the whole input, so one stray <c>%</c> does not hide the rest.
    /// </summary>
    public static string Decode(string text, UrlEncodeMode mode)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var bytes = new List<byte>(text.Length);
        var output = new StringBuilder(text.Length);

        void Flush()
        {
            if (bytes.Count > 0)
            {
                output.Append(System.Text.Encoding.UTF8.GetString([.. bytes]));
                bytes.Clear();
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '%' && i + 2 < text.Length &&
                char.IsAsciiHexDigit(text[i + 1]) && char.IsAsciiHexDigit(text[i + 2]))
            {
                bytes.Add(byte.Parse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 2;
                continue;
            }

            Flush();
            output.Append(c == '+' && mode == UrlEncodeMode.Form ? ' ' : c);
        }

        Flush();
        return output.ToString();
    }
}

/// <summary>HTML entity encoding.</summary>
public static class HtmlCodec
{
    public static OperationResult<string> Run(string? input, CodecDirection direction, bool encodeNonAscii)
    {
        input ??= string.Empty;
        return OperationResult<string>.Ok(direction == CodecDirection.Encode
            ? Encode(input, encodeNonAscii)
            : Decode(input));
    }

    /// <summary>
    /// Escapes the five characters that are markup — <c>&amp; &lt; &gt; " '</c> — and, when asked,
    /// every non-ASCII character as a numeric reference, for pages whose encoding is unknown.
    /// </summary>
    public static string Encode(string text, bool encodeNonAscii)
    {
        var output = new StringBuilder(text.Length + (text.Length / 8));

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            switch (c)
            {
                case '&': output.Append("&amp;"); break;
                case '<': output.Append("&lt;"); break;
                case '>': output.Append("&gt;"); break;
                case '"': output.Append("&quot;"); break;
                case '\'': output.Append("&#39;"); break;
                default:
                    if (encodeNonAscii && c > 0x7E)
                    {
                        // One reference for the whole code point, not one per surrogate half.
                        var codePoint = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                            ? char.ConvertToUtf32(c, text[++i])
                            : c;
                        output.Append("&#x").Append(codePoint.ToString("X", CultureInfo.InvariantCulture)).Append(';');
                    }
                    else
                    {
                        output.Append(c);
                    }

                    break;
            }
        }

        return output.ToString();
    }

    /// <summary>Resolves named (<c>&amp;eacute;</c>), decimal and hexadecimal references.</summary>
    public static string Decode(string text) => System.Web.HttpUtility.HtmlDecode(text);
}
