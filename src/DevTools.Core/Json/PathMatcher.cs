namespace DevTools.Core.Json;

/// <summary>
/// Matches a concrete node path such as <c>$.items[0].meta.etag</c> against user-written
/// glob patterns.
/// </summary>
/// <remarks>
/// Three wildcards, which is all anyone types in practice:
/// <list type="bullet">
/// <item><c>*</c> — exactly one segment (<c>$.meta.*</c>)</item>
/// <item><c>[*]</c> — any array index (<c>$.items[*].id</c>)</item>
/// <item><c>..</c> or <c>**</c> — any number of segments, including none (<c>$..timestamp</c>)</item>
/// </list>
/// </remarks>
public sealed class PathMatcher
{
    private readonly List<string[]> _patterns = [];

    public PathMatcher(IReadOnlyList<string>? patterns)
    {
        if (patterns is null)
        {
            return;
        }

        foreach (var pattern in patterns)
        {
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                _patterns.Add(Segment(pattern, forPattern: true));
            }
        }
    }

    public bool IsEmpty => _patterns.Count == 0;

    public bool Matches(string path)
    {
        if (_patterns.Count == 0)
        {
            return false;
        }

        var segments = Segment(path, forPattern: false);

        foreach (var pattern in _patterns)
        {
            if (Matches(pattern, 0, segments, 0))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Matches(string[] pattern, int p, string[] path, int s)
    {
        while (true)
        {
            if (p == pattern.Length)
            {
                return s == path.Length;
            }

            var token = pattern[p];

            if (token == "**")
            {
                // Any depth: try consuming zero or more path segments.
                for (var skip = s; skip <= path.Length; skip++)
                {
                    if (Matches(pattern, p + 1, path, skip))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (s == path.Length)
            {
                return false;
            }

            if (!SegmentMatches(token, path[s]))
            {
                return false;
            }

            p++;
            s++;
        }
    }

    private static bool SegmentMatches(string pattern, string segment)
    {
        if (pattern == "*")
        {
            return true;
        }

        if (pattern == "[*]")
        {
            return segment.Length > 1 && segment[0] == '[' && segment[^1] == ']';
        }

        return string.Equals(pattern, segment, StringComparison.Ordinal);
    }

    /// <summary>
    /// Splits <c>$.items[0].meta</c> into <c>items</c>, <c>[0]</c>, <c>meta</c>.
    /// In a pattern, <c>..</c> becomes the <c>**</c> any-depth token.
    /// </summary>
    private static string[] Segment(string path, bool forPattern)
    {
        var segments = new List<string>();
        var i = 0;

        if (i < path.Length && path[i] == '$')
        {
            i++;
        }

        while (i < path.Length)
        {
            var c = path[i];

            if (c == '.')
            {
                if (forPattern && i + 1 < path.Length && path[i + 1] == '.')
                {
                    segments.Add("**");
                    i += 2;
                    continue;
                }

                i++;
                continue;
            }

            if (c == '[')
            {
                var close = path.IndexOf(']', i);
                if (close < 0)
                {
                    segments.Add(path[i..]);
                    break;
                }

                var inner = path[(i + 1)..close];
                i = close + 1;

                // ['name'] is a member, [0] and [*] are elements.
                if (inner.Length >= 2 && ((inner[0] == '\'' && inner[^1] == '\'') || (inner[0] == '"' && inner[^1] == '"')))
                {
                    segments.Add(inner[1..^1]);
                }
                else
                {
                    segments.Add($"[{inner}]");
                }

                continue;
            }

            var start = i;
            while (i < path.Length && path[i] is not ('.' or '['))
            {
                i++;
            }

            var name = path[start..i];
            if (name.Length > 0)
            {
                segments.Add(forPattern && name == "**" ? "**" : name);
            }
        }

        return [.. segments];
    }
}
