using System.Security.Cryptography;
using System.Text;

namespace DevTools.Core.Generators;

/// <summary>The UUID layouts the generator produces (RFC 9562).</summary>
public enum UuidVersion
{
    /// <summary>Random — the usual choice.</summary>
    V4,

    /// <summary>Unix-time ordered: sorts by creation time, so database indexes stay compact.</summary>
    V7,

    /// <summary>Gregorian-time based, with a random node rather than a network card's address.</summary>
    V1,

    /// <summary>All zeros.</summary>
    Nil,
}

/// <summary>How each UUID is written out.</summary>
public enum UuidFormat
{
    /// <summary><c>0f8fad5b-d9cb-469f-a165-70867728950e</c></summary>
    Hyphenated,

    /// <summary><c>0f8fad5bd9cb469fa16570867728950e</c></summary>
    Compact,

    /// <summary><c>{0f8fad5b-d9cb-469f-a165-70867728950e}</c>, as the Windows registry and COM write them.</summary>
    Braces,

    /// <summary><c>urn:uuid:0f8fad5b-d9cb-469f-a165-70867728950e</c></summary>
    Urn,
}

public sealed record UuidOptions
{
    public UuidVersion Version { get; init; } = UuidVersion.V4;

    public UuidFormat Format { get; init; } = UuidFormat.Hyphenated;

    public bool Uppercase { get; init; }

    public int Count { get; init; } = 1;

    public static UuidOptions Default { get; } = new();
}

/// <summary>
/// Generates UUIDs from the operating system's cryptographic random source.
/// </summary>
/// <remarks>
/// <para>
/// Version 7 within one batch is strictly increasing: when several are made in the same
/// millisecond the 12-bit sequence field counts up (RFC 9562 §6.2, method 1) instead of being
/// random, so a batch pasted into a table sorts in the order it was generated. DevToys'
/// version 7 is random in that field and can sort out of order.
/// </para>
/// <para>
/// Version 1 uses a random node with the multicast bit set, as RFC 9562 §6.10 asks, so no MAC
/// address leaves the machine and the value cannot collide with one made from a real card.
/// </para>
/// </remarks>
public static class UuidGenerator
{
    public const int MaxCount = 10_000;

    private static readonly long GregorianOffset = new DateTime(1582, 10, 15, 0, 0, 0, DateTimeKind.Utc).Ticks;

    public static string Generate(UuidOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var count = Math.Clamp(options.Count, 1, MaxCount);
        var builder = new StringBuilder(count * 46);
        var state = new V7State();

        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            var bytes = options.Version switch
            {
                UuidVersion.V7 => state.Next(),
                UuidVersion.V1 => NewV1(),
                UuidVersion.Nil => new byte[16],
                _ => NewV4(),
            };

            builder.Append(Format(bytes, options.Format, options.Uppercase));
        }

        return builder.ToString();
    }

    /// <summary>Formats sixteen bytes, in RFC (network) order, as text.</summary>
    public static string Format(ReadOnlySpan<byte> bytes, UuidFormat format, bool uppercase)
    {
        var hex = uppercase ? Convert.ToHexString(bytes) : Convert.ToHexStringLower(bytes);
        var hyphenated = $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";

        return format switch
        {
            UuidFormat.Compact => hex,
            UuidFormat.Braces => "{" + hyphenated + "}",
            UuidFormat.Urn => (uppercase ? "URN:UUID:" : "urn:uuid:") + hyphenated,
            _ => hyphenated,
        };
    }

    private static byte[] NewV4()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return bytes;
    }

    private static byte[] NewV1()
    {
        var timestamp = DateTime.UtcNow.Ticks - GregorianOffset; // 100 ns intervals since 1582
        var bytes = RandomNumberGenerator.GetBytes(16);

        // time_low, time_mid, time_hi — most significant byte first within each field.
        var low = (uint)(timestamp & 0xFFFFFFFF);
        var mid = (ushort)((timestamp >> 32) & 0xFFFF);
        var high = (ushort)((timestamp >> 48) & 0x0FFF);

        bytes[0] = (byte)(low >> 24);
        bytes[1] = (byte)(low >> 16);
        bytes[2] = (byte)(low >> 8);
        bytes[3] = (byte)low;
        bytes[4] = (byte)(mid >> 8);
        bytes[5] = (byte)mid;
        bytes[6] = (byte)(0x10 | (high >> 8));
        bytes[7] = (byte)high;

        // Bytes 8–9 are a random clock sequence; 10–15 a random node, multicast bit set.
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        bytes[10] |= 0x01;
        return bytes;
    }

    /// <summary>Remembers the last millisecond and sequence, so a batch never goes backwards.</summary>
    private sealed class V7State
    {
        private long _lastMilliseconds = -1;
        private int _sequence;

        public byte[] Next()
        {
            var milliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (milliseconds <= _lastMilliseconds)
            {
                milliseconds = _lastMilliseconds;
                _sequence++;

                // 12 bits exhausted within one millisecond: borrow the next millisecond.
                if (_sequence > 0xFFF)
                {
                    milliseconds++;
                    _sequence = RandomNumberGenerator.GetInt32(0x800);
                }
            }
            else
            {
                // Start low in the range so there is room to count up.
                _sequence = RandomNumberGenerator.GetInt32(0x800);
            }

            _lastMilliseconds = milliseconds;

            var bytes = RandomNumberGenerator.GetBytes(16);
            bytes[0] = (byte)(milliseconds >> 40);
            bytes[1] = (byte)(milliseconds >> 32);
            bytes[2] = (byte)(milliseconds >> 24);
            bytes[3] = (byte)(milliseconds >> 16);
            bytes[4] = (byte)(milliseconds >> 8);
            bytes[5] = (byte)milliseconds;
            bytes[6] = (byte)(0x70 | ((_sequence >> 8) & 0x0F));
            bytes[7] = (byte)_sequence;
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
            return bytes;
        }
    }

    /// <summary>What a UUID's version and variant bits say it is, for the inspector line.</summary>
    public static string? Describe(string? uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return null;
        }

        var text = uuid.Trim();
        if (text.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[9..];
        }

        if (!Guid.TryParse(text, out var guid))
        {
            return null;
        }

        var bytes = guid.ToByteArray(bigEndian: true);
        if (bytes.All(static b => b == 0))
        {
            return "Nil UUID";
        }

        var version = bytes[6] >> 4;
        var variant = (bytes[8] & 0xC0) == 0x80 ? "RFC 9562" : "non-standard variant";

        if (version == 7)
        {
            var ms = ((long)bytes[0] << 40) | ((long)bytes[1] << 32) | ((long)bytes[2] << 24) |
                     ((long)bytes[3] << 16) | ((long)bytes[4] << 8) | bytes[5];
            return $"Version 7 ({variant}), created {DateTimeOffset.FromUnixTimeMilliseconds(ms):yyyy-MM-dd HH:mm:ss.fff} UTC";
        }

        return $"Version {version} ({variant})";
    }
}
