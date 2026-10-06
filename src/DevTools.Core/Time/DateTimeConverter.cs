using System.Globalization;
using DevTools.Core.Text;

namespace DevTools.Core.Time;

/// <summary>The unit a Unix timestamp is counted in.</summary>
public enum EpochUnit
{
    /// <summary>Decided from the number's size: seconds, milliseconds, microseconds or nanoseconds.</summary>
    Auto,
    Seconds,
    Milliseconds,
    Microseconds,
    Nanoseconds,
}

/// <summary>The order of day, month and year in a date written all in numbers, like 06-10-2026.</summary>
public enum DateOrder
{
    /// <summary>Whatever the Windows region uses: day first in India and Europe, month first in the US.</summary>
    Auto,
    DayMonthYear,
    MonthDayYear,
    YearMonthDay,
}

/// <summary>What an input was understood as.</summary>
/// <param name="ReadAs">For a date, how its numbers were taken — "6 October 2026 (day first)" — so the reading is never a guess the user cannot see.</param>
public sealed record DateParseResult(DateTimeOffset Instant, bool WasTimestamp, EpochUnit Unit, string? ReadAs = null);

/// <summary>One labelled representation of an instant.</summary>
public sealed record DateField(string Label, string Value);

/// <summary>
/// Converts between Unix timestamps and dates.
/// </summary>
/// <remarks>
/// <para>
/// One input box takes either form. A number is a timestamp; its unit is read from its size
/// unless one is chosen, because the same instant is 10 digits in seconds and 13 in
/// milliseconds and people rarely know which they have pasted. Fractional seconds are kept
/// exactly — the arithmetic is in <see cref="decimal"/>, never <see cref="double"/>.
/// </para>
/// <para>
/// A date written without an offset is read as wall-clock time in the chosen zone, which is
/// what someone typing "2024-03-10 02:30" means; a time that daylight saving skips over in that
/// zone is reported rather than silently shifted. DevToys only accepts an ISO string that
/// carries an offset, and counts only seconds and milliseconds.
/// </para>
/// </remarks>
public static class DateTimeConverter
{
    private static readonly decimal TicksPerSecond = TimeSpan.TicksPerSecond;

    public static OperationResult<DateParseResult> Parse(string? input, EpochUnit unit, TimeZoneInfo zone) =>
        Parse(input, unit, zone, DateOrder.Auto, CultureInfo.CurrentCulture);

    public static OperationResult<DateParseResult> Parse(string? input, EpochUnit unit, TimeZoneInfo zone, DateOrder order, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(zone);

        if (TextUtil.IsBlank(input))
        {
            return OperationResult<DateParseResult>.Fail("Enter a Unix timestamp or a date.");
        }

        var text = input!.Trim();

        if (decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            return FromTimestamp(number, unit);
        }

        return FromNumericDate(text, zone, order, culture) ?? FromText(text, zone);
    }

    public static OperationResult<DateParseResult> FromTimestamp(decimal value, EpochUnit unit)
    {
        var resolved = unit == EpochUnit.Auto ? Detect(value) : unit;

        var ticksPerUnit = resolved switch
        {
            EpochUnit.Milliseconds => TicksPerSecond / 1_000m,
            EpochUnit.Microseconds => TicksPerSecond / 1_000_000m,
            EpochUnit.Nanoseconds => TicksPerSecond / 1_000_000_000m,
            _ => TicksPerSecond,
        };

        decimal ticks;
        try
        {
            ticks = (value * ticksPerUnit) + DateTime.UnixEpoch.Ticks;
        }
        catch (OverflowException)
        {
            return OutOfRange(resolved);
        }

        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            return OutOfRange(resolved);
        }

        var instant = new DateTimeOffset((long)decimal.Truncate(ticks), TimeSpan.Zero);
        return OperationResult<DateParseResult>.Ok(new DateParseResult(instant, true, resolved));
    }

    private static OperationResult<DateParseResult> OutOfRange(EpochUnit unit) =>
        OperationResult<DateParseResult>.Fail(
            $"As {Describe(unit).ToLowerInvariant()}, that timestamp falls outside the years 0001–9999.");

    /// <summary>The unit a timestamp of this size most plausibly is.</summary>
    /// <remarks>
    /// Seconds cover every year to 5138 within 11 digits; milliseconds take over beyond that,
    /// and so on by thousands. Pick a unit explicitly to read a date before 1973 as milliseconds.
    /// </remarks>
    public static EpochUnit Detect(decimal value)
    {
        var magnitude = Math.Abs(value);
        return magnitude switch
        {
            < 100_000_000_000m => EpochUnit.Seconds,
            < 100_000_000_000_000m => EpochUnit.Milliseconds,
            < 100_000_000_000_000_000m => EpochUnit.Microseconds,
            _ => EpochUnit.Nanoseconds,
        };
    }

    private static readonly System.Text.RegularExpressions.Regex NumericDate = new(
        @"^(?<a>\d{1,4})(?<sep>[-/.])(?<b>\d{1,2})\k<sep>(?<c>\d{1,4})(?<rest>(?:[T ].*)?)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads a date written all in numbers — 06-10-2026, 10/6/2026, 2026.10.06 — in the given
    /// order, or the Windows region's order when that is Auto.
    /// </summary>
    /// <remarks>
    /// Left to <see cref="DateTime.TryParse(string, out DateTime)"/>, "06-10-2026" is June
    /// under the invariant culture and October under an Indian or British one, and which you
    /// get depends on which is tried first. Here the order is decided explicitly, the result
    /// says which order it used, and a date that could be read either way says so.
    /// Returns null when the text is not of this shape, so the general parser takes it.
    /// </remarks>
    private static OperationResult<DateParseResult>? FromNumericDate(string text, TimeZoneInfo zone, DateOrder order, CultureInfo culture)
    {
        var match = NumericDate.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var a = match.Groups["a"].Value;
        var b = int.Parse(match.Groups["b"].Value, CultureInfo.InvariantCulture);
        var c = match.Groups["c"].Value;

        // A four-digit first number can only be a year, whatever the setting says.
        var resolved = a.Length == 4 ? DateOrder.YearMonthDay : order == DateOrder.Auto ? RegionOrder(culture) : order;

        if (resolved == DateOrder.YearMonthDay && a.Length != 4 && c.Length == 4)
        {
            // "06-10-2026" with year-first chosen cannot be year-first; fall back to the region.
            resolved = RegionOrder(culture) is var regional && regional != DateOrder.YearMonthDay ? regional : DateOrder.DayMonthYear;
        }

        int day, month, year;
        switch (resolved)
        {
            case DateOrder.YearMonthDay:
                (year, month, day) = (int.Parse(a, CultureInfo.InvariantCulture), b, int.Parse(c, CultureInfo.InvariantCulture));
                break;
            case DateOrder.MonthDayYear:
                (month, day, year) = (int.Parse(a, CultureInfo.InvariantCulture), b, int.Parse(c, CultureInfo.InvariantCulture));
                break;
            default:
                (day, month, year) = (int.Parse(a, CultureInfo.InvariantCulture), b, int.Parse(c, CultureInfo.InvariantCulture));
                break;
        }

        if (year < 100)
        {
            year = culture.Calendar.ToFourDigitYear(year);
        }

        if (month is < 1 or > 12)
        {
            return OperationResult<DateParseResult>.Fail(
                $"There is no month {month}. Read {DescribeOrder(resolved)} — if the date is written another way, change Date order.");
        }

        if (year is < 1 or > 9999 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return OperationResult<DateParseResult>.Fail(
                $"{CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month)} {year} has no day {day}. Read {DescribeOrder(resolved)} — if the date is written another way, change Date order.");
        }

        var iso = string.Create(CultureInfo.InvariantCulture, $"{year:0000}-{month:00}-{day:00}{match.Groups["rest"].Value}");
        var parsed = FromText(iso, zone);
        if (!parsed.IsSuccess)
        {
            return parsed;
        }

        var readAs = $"{new DateTime(year, month, day).ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture)} ({DescribeOrder(resolved)})";

        // 06-10 is either 6 October or 10 June; say which was chosen and how to get the other.
        string? warning = parsed.Warning;
        if (a.Length != 4 && day <= 12 && month <= 12 && day != month)
        {
            // Swapping day and month gives the other reading, whichever way round this one was.
            var other = new DateTime(year, day, month);
            warning ??= $"This date could also be {other.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}. " +
                        $"It was read {DescribeOrder(resolved)}{(order == DateOrder.Auto ? ", as your Windows region writes dates" : string.Empty)}; change Date order to read it the other way.";
        }

        return OperationResult<DateParseResult>.Ok(parsed.Value! with { ReadAs = readAs }, warning);
    }

    /// <summary>The order the region's short date pattern puts day, month and year in.</summary>
    public static DateOrder RegionOrder(CultureInfo culture)
    {
        var pattern = culture.DateTimeFormat.ShortDatePattern;
        var d = pattern.IndexOf('d', StringComparison.Ordinal);
        var m = pattern.IndexOf('M', StringComparison.Ordinal);
        var y = pattern.IndexOf('y', StringComparison.Ordinal);

        if (d < 0 || m < 0 || y < 0)
        {
            return DateOrder.DayMonthYear;
        }

        return y < m && y < d ? DateOrder.YearMonthDay : m < d ? DateOrder.MonthDayYear : DateOrder.DayMonthYear;
    }

    public static string DescribeOrder(DateOrder order) => order switch
    {
        DateOrder.MonthDayYear => "month first",
        DateOrder.YearMonthDay => "year first",
        DateOrder.DayMonthYear => "day first",
        _ => "as your Windows region writes dates",
    };

    private static OperationResult<DateParseResult> FromText(string text, TimeZoneInfo zone)
    {
        const DateTimeStyles Styles = DateTimeStyles.AllowWhiteSpaces;

        // An explicit offset or a Z settles the instant on its own.
        if (HasExplicitOffset(text) &&
            (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, Styles, out var withOffset) ||
             DateTimeOffset.TryParse(text, CultureInfo.CurrentCulture, Styles, out withOffset)))
        {
            return OperationResult<DateParseResult>.Ok(new DateParseResult(withOffset, false, EpochUnit.Auto));
        }

        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, Styles, out var wall) &&
            !DateTime.TryParse(text, CultureInfo.CurrentCulture, Styles, out wall))
        {
            return OperationResult<DateParseResult>.Fail(
                "That is neither a number nor a date this converter recognises. Try 1700000000, 2024-05-01T12:00:00Z or 2024-05-01 12:00.");
        }

        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(wall))
        {
            return OperationResult<DateParseResult>.Fail(
                $"{wall:yyyy-MM-dd HH:mm} does not exist in {zone.DisplayName}: the clocks skip over it when daylight saving starts.");
        }

        var utc = TimeZoneInfo.ConvertTimeToUtc(wall, zone);
        var instant = new DateTimeOffset(utc, TimeSpan.Zero);

        var warning = zone.IsAmbiguousTime(wall)
            ? $"{wall:yyyy-MM-dd HH:mm} happens twice in this zone as daylight saving ends; the first is shown."
            : null;

        return OperationResult<DateParseResult>.Ok(new DateParseResult(instant, false, EpochUnit.Auto), warning);
    }

    /// <summary>True when the text ends in <c>Z</c>, <c>GMT</c>, <c>UTC</c> or a <c>±hh:mm</c> offset.</summary>
    private static bool HasExplicitOffset(string text)
    {
        var t = text.TrimEnd();

        if (t.EndsWith('Z') || t.EndsWith('z') ||
            t.EndsWith("GMT", StringComparison.OrdinalIgnoreCase) || t.EndsWith("UTC", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // "+05:30", "-0800", "+05" after a time.
        var i = t.Length - 1;
        var digits = 0;
        while (i >= 0 && (char.IsAsciiDigit(t[i]) || t[i] == ':'))
        {
            if (char.IsAsciiDigit(t[i]))
            {
                digits++;
            }

            i--;
        }

        return i > 0 && t[i] is '+' or '-' && digits is 2 or 4 && t.AsSpan(0, i).Contains(':');
    }

    public static string Describe(EpochUnit unit) => unit switch
    {
        EpochUnit.Seconds => "Seconds",
        EpochUnit.Milliseconds => "Milliseconds",
        EpochUnit.Microseconds => "Microseconds",
        EpochUnit.Nanoseconds => "Nanoseconds",
        _ => "Auto-detect",
    };

    /// <summary>Every representation of the instant the converter shows.</summary>
    public static IReadOnlyList<DateField> Fields(DateTimeOffset instant, TimeZoneInfo zone, DateTimeOffset now)
    {
        var inv = CultureInfo.InvariantCulture;
        var utc = instant.ToUniversalTime();
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        var sinceEpoch = utc.Ticks - DateTime.UnixEpoch.Ticks;

        var seconds = Math.Floor((decimal)sinceEpoch / TicksPerSecond);
        var milliseconds = Math.Floor((decimal)sinceEpoch / TimeSpan.TicksPerMillisecond);
        var microseconds = Math.Floor((decimal)sinceEpoch / (TimeSpan.TicksPerMillisecond / 1000));
        var nanoseconds = (decimal)sinceEpoch * 100;

        var dst = zone.IsDaylightSavingTime(local) ? "daylight saving time" : zone.SupportsDaylightSavingTime ? "standard time" : "no daylight saving";

        var fields = new List<DateField>
        {
            new("Unix time (seconds)", seconds.ToString(inv)),
            new("Unix time (milliseconds)", milliseconds.ToString(inv)),
            new("Unix time (microseconds)", microseconds.ToString(inv)),
            new("Unix time (nanoseconds)", nanoseconds.ToString(inv)),
            new("ISO 8601 (UTC)", utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", inv).Replace(".0000000Z", "Z", StringComparison.Ordinal)),
            new("ISO 8601 (selected zone)", local.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", inv)),
            new("RFC 1123 / HTTP date", utc.ToString("R", inv)),
            new("Selected zone", $"{local.ToString("dddd, d MMMM yyyy, HH:mm:ss", inv)} (UTC{FormatOffset(local.Offset)}, {dst})"),
            new("UTC", utc.ToString("dddd, d MMMM yyyy, HH:mm:ss", inv)),
            new("Relative", Relative(utc, now.ToUniversalTime())),
            new("Day of year", $"{local.DayOfYear} of {(DateTime.IsLeapYear(local.Year) ? 366 : 365)}"),
            new("ISO week", $"{ISOWeek.GetYear(local.DateTime)}-W{ISOWeek.GetWeekOfYear(local.DateTime):00}"),
            new(".NET ticks (UTC)", utc.Ticks.ToString(inv)),
        };

        // FILETIME counts 100 ns intervals since 1601 and cannot express anything earlier.
        var fileTimeEpoch = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        if (utc.Ticks >= fileTimeEpoch)
        {
            fields.Add(new DateField("Windows FILETIME", (utc.Ticks - fileTimeEpoch).ToString(inv)));
        }

        return fields;
    }

    private static string FormatOffset(TimeSpan offset) =>
        (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    /// <summary>"3 hours ago", "in 2 days" — rounded to the largest unit that is at least one.</summary>
    public static string Relative(DateTimeOffset instant, DateTimeOffset now)
    {
        var delta = instant - now;
        var future = delta > TimeSpan.Zero;
        var span = delta.Duration();

        string amount;
        if (span.TotalSeconds < 1)
        {
            return "now";
        }

        if (span.TotalMinutes < 1)
        {
            amount = Plural((int)span.TotalSeconds, "second");
        }
        else if (span.TotalHours < 1)
        {
            amount = Plural((int)span.TotalMinutes, "minute");
        }
        else if (span.TotalDays < 1)
        {
            amount = Plural((int)span.TotalHours, "hour");
        }
        else if (span.TotalDays < 30)
        {
            amount = Plural((int)span.TotalDays, "day");
        }
        else if (span.TotalDays < 365)
        {
            amount = Plural((int)(span.TotalDays / 30.4375), "month");
        }
        else
        {
            amount = Plural((int)(span.TotalDays / 365.25), "year");
        }

        return future ? $"in {amount}" : $"{amount} ago";
    }

    private static string Plural(int value, string unit) => value == 1 ? $"1 {unit}" : $"{value:N0} {unit}s";
}
