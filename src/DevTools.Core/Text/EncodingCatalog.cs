using System.Text;

namespace DevTools.Core.Text;

/// <summary>The text encodings DevTools offers wherever bytes and text meet.</summary>
public enum TextEncodingKind
{
    Utf8,
    Utf8Bom,
    Utf16Le,
    Utf16Be,
    Utf32Le,
    Ascii,
    Latin1,
}

/// <summary>
/// Resolves <see cref="TextEncodingKind"/> to a concrete <see cref="Encoding"/>.
/// Every encoding returned uses replacement fallbacks rather than throwing, so malformed
/// bytes render as U+FFFD instead of crashing a transform (edge case 4).
/// </summary>
public static class EncodingCatalog
{
    public static IReadOnlyList<TextEncodingKind> All { get; } =
    [
        TextEncodingKind.Utf8,
        TextEncodingKind.Utf8Bom,
        TextEncodingKind.Utf16Le,
        TextEncodingKind.Utf16Be,
        TextEncodingKind.Utf32Le,
        TextEncodingKind.Ascii,
        TextEncodingKind.Latin1,
    ];

    public static string DisplayName(TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8 => "UTF-8",
        TextEncodingKind.Utf8Bom => "UTF-8 with BOM",
        TextEncodingKind.Utf16Le => "UTF-16 LE",
        TextEncodingKind.Utf16Be => "UTF-16 BE",
        TextEncodingKind.Utf32Le => "UTF-32 LE",
        TextEncodingKind.Ascii => "ASCII",
        TextEncodingKind.Latin1 => "Latin-1 (ISO-8859-1)",
        _ => kind.ToString(),
    };

    public static Encoding Resolve(TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8 => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
        TextEncodingKind.Utf8Bom => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: false),
        TextEncodingKind.Utf16Le => new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: false),
        TextEncodingKind.Utf16Be => new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: false),
        TextEncodingKind.Utf32Le => new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: false),
        TextEncodingKind.Ascii => Encoding.ASCII,
        TextEncodingKind.Latin1 => Encoding.Latin1,
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
    };

    /// <summary>Encodes text, never throwing on unmappable characters.</summary>
    public static byte[] GetBytes(string? text, TextEncodingKind kind) =>
        Resolve(kind).GetBytes(text ?? string.Empty);

    /// <summary>Decodes bytes, substituting U+FFFD for anything malformed.</summary>
    public static string GetString(ReadOnlySpan<byte> bytes, TextEncodingKind kind) =>
        Resolve(kind).GetString(bytes);
}
