using System.Text;
using DevTools.Core.Text;

namespace DevTools.Core.Json;

/// <summary>What the parser is willing to accept beyond strict RFC 8259.</summary>
public sealed record JsonReaderOptions
{
    /// <summary>Accepts a comma before <c>}</c> or <c>]</c>.</summary>
    public bool AllowTrailingCommas { get; init; }

    /// <summary>Accepts <c>//</c> and <c>/* */</c> comments. They are dropped, not preserved.</summary>
    public bool AllowComments { get; init; }

    /// <summary>Refuses input nested deeper than this rather than overflowing the stack.</summary>
    public int MaxDepth { get; init; } = Limits.MaxJsonDepth;

    public static JsonReaderOptions Default { get; } = new();

    public static JsonReaderOptions Tolerant { get; } = new()
    {
        AllowTrailingCommas = true,
        AllowComments = true,
    };
}

/// <summary>A parsed document plus anything the parser wants to mention about it.</summary>
public sealed record JsonParseResult(JsonNode Root, IReadOnlyList<string> Warnings);

/// <summary>
/// A hand-written recursive-descent JSON parser.
/// </summary>
/// <remarks>
/// Hand-written rather than delegating to <see cref="System.Text.Json"/> because this model
/// needs three things that API will not give up: the source text of every number, duplicate
/// member names, and a position for every node.
/// </remarks>
public static class JsonReader
{
    public static OperationResult<JsonParseResult> Parse(string? json, JsonReaderOptions? options = null)
    {
        var opts = options ?? JsonReaderOptions.Default;

        if (TextUtil.IsBlank(json))
        {
            return OperationResult<JsonParseResult>.Fail("There is nothing to parse.");
        }

        var text = TextUtil.StripBom(json);
        var state = new ParserState(text, opts);

        try
        {
            state.SkipTrivia();
            if (state.AtEnd)
            {
                return OperationResult<JsonParseResult>.Fail("There is nothing to parse.");
            }

            var root = state.ReadValue(0);
            state.SkipTrivia();

            if (!state.AtEnd)
            {
                return Fail(state, $"Unexpected content after the document ends: '{state.Current}'.");
            }

            return OperationResult<JsonParseResult>.Ok(new JsonParseResult(root, state.Warnings));
        }
        catch (JsonParseException ex)
        {
            var (line, column) = TextUtil.OffsetToLineColumn(text, ex.Offset);
            return OperationResult<JsonParseResult>.Fail(new ToolError(ex.Message, line, column, ex.Offset));
        }
    }

    private static OperationResult<JsonParseResult> Fail(ParserState state, string message)
    {
        var (line, column) = TextUtil.OffsetToLineColumn(state.Text, state.Index);
        return OperationResult<JsonParseResult>.Fail(new ToolError(message, line, column, state.Index));
    }

    private sealed class JsonParseException(string message, int offset) : Exception(message)
    {
        public int Offset { get; } = offset;
    }

    private sealed class ParserState(string text, JsonReaderOptions options)
    {
        private readonly List<string> _warnings = [];

        public string Text { get; } = text;

        public int Index { get; private set; }

        public IReadOnlyList<string> Warnings => _warnings;

        public bool AtEnd => Index >= Text.Length;

        public char Current => Text[Index];

        private void Warn(string message)
        {
            if (!_warnings.Contains(message, StringComparer.Ordinal))
            {
                _warnings.Add(message);
            }
        }

        private JsonParseException Error(string message) => new(message, Index);

        private JsonParseException ErrorAt(string message, int offset) => new(message, offset);

        private JsonPosition PositionHere()
        {
            var (line, column) = TextUtil.OffsetToLineColumn(Text, Index);
            return new JsonPosition(line, column, Index);
        }

        public void SkipTrivia()
        {
            while (!AtEnd)
            {
                var c = Current;
                if (c is ' ' or '\t' or '\r' or '\n')
                {
                    Index++;
                    continue;
                }

                if (c == '/' && options.AllowComments && Index + 1 < Text.Length)
                {
                    var next = Text[Index + 1];
                    if (next == '/')
                    {
                        Index += 2;
                        while (!AtEnd && Current is not ('\n' or '\r'))
                        {
                            Index++;
                        }

                        continue;
                    }

                    if (next == '*')
                    {
                        var start = Index;
                        Index += 2;
                        while (true)
                        {
                            if (Index + 1 >= Text.Length)
                            {
                                throw ErrorAt("A block comment was opened but never closed.", start);
                            }

                            if (Text[Index] == '*' && Text[Index + 1] == '/')
                            {
                                Index += 2;
                                break;
                            }

                            Index++;
                        }

                        continue;
                    }
                }

                break;
            }
        }

        public JsonNode ReadValue(int depth)
        {
            if (depth > options.MaxDepth)
            {
                throw Error($"The document nests deeper than {options.MaxDepth} levels, which is refused rather than risking a stack overflow.");
            }

            SkipTrivia();
            if (AtEnd)
            {
                throw Error("A value was expected but the document ended.");
            }

            var position = PositionHere();

            return Current switch
            {
                '{' => ReadObject(depth, position),
                '[' => ReadArray(depth, position),
                '"' => new JsonString(ReadString(), position),
                't' => ReadLiteral("true", position, static p => new JsonBool(true, p)),
                'f' => ReadLiteral("false", position, static p => new JsonBool(false, p)),
                'n' => ReadLiteral("null", position, static p => new JsonNull(p)),
                _ => ReadNumber(position),
            };
        }

        private JsonNode ReadLiteral(string literal, JsonPosition position, Func<JsonPosition, JsonNode> make)
        {
            if (Index + literal.Length > Text.Length ||
                !Text.AsSpan(Index, literal.Length).SequenceEqual(literal))
            {
                throw Error($"Expected '{literal}'.");
            }

            Index += literal.Length;
            return make(position);
        }

        private JsonObject ReadObject(int depth, JsonPosition position)
        {
            Index++; // '{'
            var members = new List<JsonMember>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            SkipTrivia();
            if (!AtEnd && Current == '}')
            {
                Index++;
                return new JsonObject(members, position);
            }

            while (true)
            {
                SkipTrivia();

                if (AtEnd)
                {
                    throw Error("The object was opened but never closed.");
                }

                if (Current == '}')
                {
                    if (options.AllowTrailingCommas)
                    {
                        Index++;
                        return new JsonObject(members, position);
                    }

                    throw Error("A trailing comma is not allowed here. Turn on 'Allow trailing commas' to accept it.");
                }

                if (Current != '"')
                {
                    throw Error($"A member name must be a quoted string, but '{Current}' was found.");
                }

                var namePosition = PositionHere();
                var name = ReadString();

                if (!seen.Add(name))
                {
                    // Legal JSON, and silently merging it is how a differ reports a false 'equal'.
                    Warn($"The object at {namePosition} has more than one member named \"{name}\". Every one is kept.");
                }

                SkipTrivia();
                if (AtEnd || Current != ':')
                {
                    throw Error($"Expected ':' after the member name \"{name}\".");
                }

                Index++;
                var value = ReadValue(depth + 1);
                members.Add(new JsonMember(name, value, namePosition));

                SkipTrivia();
                if (AtEnd)
                {
                    throw Error("The object was opened but never closed.");
                }

                if (Current == ',')
                {
                    Index++;
                    continue;
                }

                if (Current == '}')
                {
                    Index++;
                    return new JsonObject(members, position);
                }

                throw Error($"Expected ',' or '}}' but found '{Current}'.");
            }
        }

        private JsonArray ReadArray(int depth, JsonPosition position)
        {
            Index++; // '['
            var items = new List<JsonNode>();

            SkipTrivia();
            if (!AtEnd && Current == ']')
            {
                Index++;
                return new JsonArray(items, position);
            }

            while (true)
            {
                SkipTrivia();

                if (AtEnd)
                {
                    throw Error("The array was opened but never closed.");
                }

                if (Current == ']')
                {
                    if (options.AllowTrailingCommas)
                    {
                        Index++;
                        return new JsonArray(items, position);
                    }

                    throw Error("A trailing comma is not allowed here. Turn on 'Allow trailing commas' to accept it.");
                }

                items.Add(ReadValue(depth + 1));

                SkipTrivia();
                if (AtEnd)
                {
                    throw Error("The array was opened but never closed.");
                }

                if (Current == ',')
                {
                    Index++;
                    continue;
                }

                if (Current == ']')
                {
                    Index++;
                    return new JsonArray(items, position);
                }

                throw Error($"Expected ',' or ']' but found '{Current}'.");
            }
        }

        private string ReadString()
        {
            var openedAt = Index;
            Index++; // opening quote
            var builder = new StringBuilder();

            while (true)
            {
                if (AtEnd)
                {
                    throw ErrorAt("The string was opened but never closed.", openedAt);
                }

                var c = Current;

                if (c == '"')
                {
                    Index++;
                    return builder.ToString();
                }

                if (c == '\\')
                {
                    Index++;
                    if (AtEnd)
                    {
                        throw ErrorAt("The string was opened but never closed.", openedAt);
                    }

                    var escape = Current;
                    Index++;

                    switch (escape)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            AppendUnicodeEscape(builder);
                            break;
                        default:
                            throw Error($"'\\{escape}' is not a valid JSON escape sequence.");
                    }

                    continue;
                }

                if (char.IsControl(c) && c is not ('\t' or '\n' or '\r'))
                {
                    throw Error($"A raw control character (U+{(int)c:X4}) must be escaped inside a string.");
                }

                builder.Append(c);
                Index++;
            }
        }

        /// <summary>
        /// Reads a <c>\uXXXX</c> escape (the backslash and the 'u' are already consumed) and
        /// appends what it denotes.
        /// </summary>
        /// <remarks>
        /// Surrogate pairing is resolved here rather than by the caller, because a high
        /// surrogate is only meaningful together with the low half that follows it. An
        /// unpaired half is not representable, so it becomes U+FFFD and the document is
        /// flagged as no longer a byte-for-byte round trip (edge case 4).
        /// </remarks>
        private void AppendUnicodeEscape(StringBuilder builder)
        {
            var value = ReadFourHexDigits();

            if (char.IsHighSurrogate(value))
            {
                if (TryPeekUnicodeEscape(out var low) && char.IsLowSurrogate(low))
                {
                    Index += 6; // the "\uXXXX" of the low half
                    builder.Append(value).Append(low);
                    return;
                }

                Warn("An unpaired high surrogate was replaced with U+FFFD, so the output is not a byte-for-byte round trip of the input.");
                builder.Append('�');
                return;
            }

            if (char.IsLowSurrogate(value))
            {
                // A low surrogate reached here cannot have been preceded by a high one: the
                // branch above consumes both halves together.
                Warn("An unpaired low surrogate was replaced with U+FFFD, so the output is not a byte-for-byte round trip of the input.");
                builder.Append('�');
                return;
            }

            builder.Append(value);
        }

        private char ReadFourHexDigits()
        {
            if (Index + 4 > Text.Length)
            {
                throw Error("A '\\u' escape needs four hexadecimal digits.");
            }

            var span = Text.AsSpan(Index, 4);
            for (var i = 0; i < 4; i++)
            {
                if (!char.IsAsciiHexDigit(span[i]))
                {
                    throw Error($"'\\u{span}' is not a valid escape: '{span[i]}' is not a hexadecimal digit.");
                }
            }

            var value = (char)Convert.ToInt32(span.ToString(), 16);
            Index += 4;
            return value;
        }

        /// <summary>Looks ahead for a complete <c>\uXXXX</c> without consuming anything.</summary>
        private bool TryPeekUnicodeEscape(out char value)
        {
            value = '\0';

            if (Index + 6 > Text.Length || Text[Index] != '\\' || Text[Index + 1] != 'u')
            {
                return false;
            }

            var span = Text.AsSpan(Index + 2, 4);
            foreach (var c in span)
            {
                if (!char.IsAsciiHexDigit(c))
                {
                    return false;
                }
            }

            value = (char)Convert.ToInt32(span.ToString(), 16);
            return true;
        }

        private JsonNumber ReadNumber(JsonPosition position)
        {
            var start = Index;

            if (!AtEnd && Current == '-')
            {
                Index++;
            }

            if (AtEnd || !char.IsAsciiDigit(Current))
            {
                throw ErrorAt($"'{DescribeHere(start)}' is not a valid JSON value.", start);
            }

            if (Current == '0')
            {
                Index++;
            }
            else
            {
                while (!AtEnd && char.IsAsciiDigit(Current))
                {
                    Index++;
                }
            }

            if (!AtEnd && Current == '.')
            {
                Index++;
                if (AtEnd || !char.IsAsciiDigit(Current))
                {
                    throw Error("A decimal point must be followed by at least one digit.");
                }

                while (!AtEnd && char.IsAsciiDigit(Current))
                {
                    Index++;
                }
            }

            if (!AtEnd && (Current == 'e' || Current == 'E'))
            {
                Index++;
                if (!AtEnd && (Current == '+' || Current == '-'))
                {
                    Index++;
                }

                if (AtEnd || !char.IsAsciiDigit(Current))
                {
                    throw Error("An exponent must be followed by at least one digit.");
                }

                while (!AtEnd && char.IsAsciiDigit(Current))
                {
                    Index++;
                }
            }

            // The source text is kept verbatim. That is the whole point of this model.
            return new JsonNumber(Text[start..Index], position);
        }

        private string DescribeHere(int start)
        {
            var end = start;
            while (end < Text.Length && !char.IsWhiteSpace(Text[end]) && Text[end] is not (',' or '}' or ']' or ':'))
            {
                end++;
            }

            var token = end > start ? Text[start..end] : Text[start..Math.Min(start + 1, Text.Length)];
            return TextUtil.Ellipsize(token, 24);
        }
    }
}
