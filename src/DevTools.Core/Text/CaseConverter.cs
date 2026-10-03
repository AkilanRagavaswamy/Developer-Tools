using System.Buffers;
using System.Text;

namespace DevTools.Core.Text;

/// <summary>The fifteen naming conventions the case converter can produce (FR-X02).</summary>
public enum LetterCase
{
    /// <summary><c>camelCase</c> — words joined with no separator, the first word lower case.</summary>
    Camel,

    /// <summary><c>PascalCase</c> — words joined with no separator, every word capitalised.</summary>
    Pascal,

    /// <summary><c>snake_case</c> — lower-case words joined with underscores.</summary>
    Snake,

    /// <summary><c>SCREAMING_SNAKE_CASE</c> — upper-case words joined with underscores.</summary>
    ScreamingSnake,

    /// <summary><c>kebab-case</c> — lower-case words joined with hyphens.</summary>
    Kebab,

    /// <summary><c>COBOL-CASE</c> — upper-case words joined with hyphens.</summary>
    Cobol,

    /// <summary><c>Train-Case</c> — capitalised words joined with hyphens.</summary>
    Train,

    /// <summary><c>dot.case</c> — lower-case words joined with full stops.</summary>
    Dot,

    /// <summary><c>path/case</c> — lower-case words joined with forward slashes.</summary>
    Path,

    /// <summary><c>Sentence case</c> — space-joined words with only the first one capitalised.</summary>
    Sentence,

    /// <summary><c>Title Case</c> — space-joined words capitalised by English title rules.</summary>
    Title,

    /// <summary><c>lowercase</c> — the original text lower-cased; spacing and punctuation untouched.</summary>
    Lower,

    /// <summary><c>UPPERCASE</c> — the original text upper-cased; spacing and punctuation untouched.</summary>
    Upper,

    /// <summary><c>aLtErNaTiNg cAsE</c> — the original text with case alternating by character position.</summary>
    Alternating,

    /// <summary><c>iNVERSE cASE</c> — the original text with every letter case swapped.</summary>
    Inverse,
}

/// <summary>Chooses which words count as acronyms once <see cref="CaseConverterOptions.PreserveAcronyms"/> is on.</summary>
public enum AcronymDetection
{
    /// <summary>Only runs that were already all upper case in the input, such as the <c>JSON</c> in <c>parseJSONData</c>.</summary>
    FromInput,

    /// <summary>Only words listed in <see cref="CaseConverterOptions.KnownAcronyms"/>.</summary>
    KnownList,

    /// <summary>Either of the above.</summary>
    Both,
}

/// <summary>
/// Knobs for <see cref="CaseConverter"/>. <c>new CaseConverterOptions()</c> gives the behaviour
/// most developers expect: digits split words, acronyms are normalised to <c>Json</c>, and each
/// line keeps its indentation.
/// </summary>
public sealed record CaseConverterOptions
{
    /// <summary>Splits <c>user2Name</c> into <c>user</c> + <c>2</c> + <c>Name</c> instead of <c>user2</c> + <c>Name</c>.</summary>
    public bool SplitOnDigits { get; init; } = true;

    /// <summary>Keeps acronyms upper case in the capitalised cases, so <c>XMLHttpRequest</c> survives a PascalCase round trip.</summary>
    public bool PreserveAcronyms { get; init; }

    /// <summary>Which words <see cref="PreserveAcronyms"/> applies to.</summary>
    public AcronymDetection AcronymDetection { get; init; } = AcronymDetection.Both;

    /// <summary>The acronym vocabulary used by <see cref="AcronymDetection.KnownList"/> and <see cref="AcronymDetection.Both"/>.</summary>
    public IReadOnlyList<string> KnownAcronyms { get; init; } = CaseConverter.DefaultAcronyms;

    /// <summary>Short words Title Case leaves lower case unless they are the first or last word.</summary>
    public IReadOnlyList<string> TitleCaseMinorWords { get; init; } = CaseConverter.DefaultMinorWords;

    /// <summary>Converts each line on its own so a block of code or a list keeps its shape.</summary>
    public bool ProcessLineByLine { get; init; } = true;

    /// <summary>Re-emits the white space each line started with, so indented blocks stay indented.</summary>
    public bool PreserveIndentation { get; init; } = true;

    /// <summary>Forces a newline convention on the output; <see langword="null"/> keeps the one the input used.</summary>
    public NewlineStyle? OutputNewline { get; init; }

    /// <summary>The defaults, offered as a singleton so call sites need not allocate.</summary>
    public static CaseConverterOptions Default { get; } = new();
}

/// <summary>Every case rendering of one input, so the UI can show the full table from a single call.</summary>
public sealed record CaseVariants(
    string Camel,
    string Pascal,
    string Snake,
    string ScreamingSnake,
    string Kebab,
    string Cobol,
    string Train,
    string Dot,
    string Path,
    string Sentence,
    string Title,
    string Lower,
    string Upper,
    string Alternating,
    string Inverse)
{
    /// <summary>The all-empty set returned for blank input.</summary>
    public static CaseVariants Empty { get; } = new(
        string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
        string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
        string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

    /// <summary>Looks one variant up by enum value, which is how a data-bound table row reads it.</summary>
    public string ForCase(LetterCase letterCase) => letterCase switch
    {
        LetterCase.Camel => Camel,
        LetterCase.Pascal => Pascal,
        LetterCase.Snake => Snake,
        LetterCase.ScreamingSnake => ScreamingSnake,
        LetterCase.Kebab => Kebab,
        LetterCase.Cobol => Cobol,
        LetterCase.Train => Train,
        LetterCase.Dot => Dot,
        LetterCase.Path => Path,
        LetterCase.Sentence => Sentence,
        LetterCase.Title => Title,
        LetterCase.Lower => Lower,
        LetterCase.Upper => Upper,
        LetterCase.Alternating => Alternating,
        LetterCase.Inverse => Inverse,
        _ => string.Empty,
    };
}

/// <summary>The outcome of one conversion: the requested rendering, every other one, and the words behind them.</summary>
public sealed record CaseConversionResult(
    LetterCase RequestedCase,
    string Converted,
    CaseVariants Variants,
    IReadOnlyList<string> Words,
    int LineCount);

/// <summary>
/// Converts identifiers and prose between naming conventions (FR-X02). The interesting part is
/// word splitting: delimiters, camel humps, acronym runs and digit boundaries all start a new word,
/// which is what lets <c>parseJSONData</c> become <c>parse_json_data</c>.
/// </summary>
/// <remarks>
/// <para>
/// All casing uses the invariant culture on purpose. Under a Turkish locale <c>"I".ToLower()</c>
/// yields a dotless <c>ı</c>, which silently corrupts identifiers; the invariant mapping keeps
/// <c>I</c> and <c>i</c> paired no matter what the machine locale is.
/// </para>
/// <para>
/// <see cref="LetterCase.Lower"/>, <see cref="LetterCase.Upper"/>, <see cref="LetterCase.Alternating"/>
/// and <see cref="LetterCase.Inverse"/> are literal transforms of the line and keep every space and
/// punctuation mark. The other eleven cases are rebuilt from the detected words; characters that are
/// neither a delimiter nor a letter or digit — emoji and astral-plane characters included — stay
/// inside the word they were found in and pass through untouched.
/// </para>
/// </remarks>
public static class CaseConverter
{
    private const string DelimiterChars = "_-./\\";

    /// <summary>Acronyms recognised by <see cref="AcronymDetection.KnownList"/> out of the box.</summary>
    public static IReadOnlyList<string> DefaultAcronyms { get; } =
    [
        "ID", "URL", "URI", "URN", "HTTP", "HTTPS", "XML", "JSON", "HTML", "CSS", "API", "SQL",
        "UUID", "GUID", "IO", "UI", "UX", "DB", "CPU", "GPU", "RAM", "PDF", "CSV", "TSV", "YAML",
        "TOML", "INI", "JWT", "SSL", "TLS", "TCP", "UDP", "IP", "DNS", "FTP", "SSH", "REST",
        "SOAP", "CRUD", "OS", "PC", "USB", "SDK", "CLI", "GUI", "AI", "ML", "UTC", "ISO", "RFC",
        "ASCII", "UTF", "BOM", "MD5", "SHA", "AES", "RSA", "JS", "TS", "PHP", "GC", "JIT", "IL",
        "LINQ", "WPF", "MVVM", "MVC", "XAML", "DTO", "ORM", "CDN", "SMTP", "IMAP", "POP", "RGB",
        "RGBA", "HSL", "SVG", "PNG", "JPEG", "GIF", "ZIP", "GZIP", "CRC",
    ];

    /// <summary>The articles, conjunctions and prepositions Title Case keeps lower case mid-title.</summary>
    public static IReadOnlyList<string> DefaultMinorWords { get; } =
    [
        "a", "an", "the", "and", "but", "or", "for", "nor", "on", "at", "to", "from", "by", "in",
        "of", "with",
    ];

    /// <summary>Every case the UI offers, in the order the table shows them.</summary>
    public static IReadOnlyList<LetterCase> AllCases { get; } =
    [
        LetterCase.Camel, LetterCase.Pascal, LetterCase.Snake, LetterCase.ScreamingSnake,
        LetterCase.Kebab, LetterCase.Cobol, LetterCase.Train, LetterCase.Dot, LetterCase.Path,
        LetterCase.Sentence, LetterCase.Title, LetterCase.Lower, LetterCase.Upper,
        LetterCase.Alternating, LetterCase.Inverse,
    ];

    /// <summary>A human label for one case, shown as the row heading in the variants table.</summary>
    public static string DisplayName(LetterCase letterCase) => letterCase switch
    {
        LetterCase.Camel => "camelCase",
        LetterCase.Pascal => "PascalCase",
        LetterCase.Snake => "snake_case",
        LetterCase.ScreamingSnake => "SCREAMING_SNAKE_CASE",
        LetterCase.Kebab => "kebab-case",
        LetterCase.Cobol => "COBOL-CASE",
        LetterCase.Train => "Train-Case",
        LetterCase.Dot => "dot.case",
        LetterCase.Path => "path/case",
        LetterCase.Sentence => "Sentence case",
        LetterCase.Title => "Title Case",
        LetterCase.Lower => "lowercase",
        LetterCase.Upper => "UPPERCASE",
        LetterCase.Alternating => "aLtErNaTiNg cAsE",
        LetterCase.Inverse => "iNVERSE cASE",
        _ => letterCase.ToString(),
    };

    /// <summary>Converts text to one case and hands back every other rendering alongside it.</summary>
    public static OperationResult<CaseConversionResult> Convert(
        string? input,
        LetterCase target,
        CaseConverterOptions? options = null)
    {
        var opts = options ?? CaseConverterOptions.Default;

        if (SizeError(input) is { } tooBig)
        {
            return OperationResult<CaseConversionResult>.Fail(tooBig);
        }

        var text = TextUtil.StripBom(input);
        if (TextUtil.IsBlank(text))
        {
            return OperationResult<CaseConversionResult>.Ok(
                new CaseConversionResult(target, string.Empty, CaseVariants.Empty, [], 0));
        }

        var variants = BuildVariants(text, opts, out var words, out var lineCount);
        return OperationResult<CaseConversionResult>.Ok(
            new CaseConversionResult(target, variants.ForCase(target), variants, words, lineCount));
    }

    /// <summary>Renders every case at once, for the side-by-side table.</summary>
    public static OperationResult<CaseVariants> ConvertAll(string? input, CaseConverterOptions? options = null) =>
        Convert(input, LetterCase.Pascal, options).Map(r => r.Variants);

    /// <summary>
    /// Splits text into the words every case rendering is built from: on <c>_ - . / \</c> and white
    /// space, at lower-to-upper humps, at acronym boundaries and — optionally — at digit boundaries.
    /// </summary>
    public static IReadOnlyList<string> SplitWords(string? text, CaseConverterOptions? options = null) =>
        SplitDetected(text, options ?? CaseConverterOptions.Default).Select(w => w.Text).ToArray();

    private static CaseVariants BuildVariants(
        string text,
        CaseConverterOptions options,
        out IReadOnlyList<string> allWords,
        out int lineCount)
    {
        var newline = options.OutputNewline ?? TextUtil.DetectNewline(text);
        var joiner = newline switch
        {
            NewlineStyle.CrLf => "\r\n",
            NewlineStyle.Cr => "\r",
            _ => "\n",
        };

        string[] lines = options.ProcessLineByLine
            ? TextUtil.SplitLines(text, keepTrailingEmpty: true)
            : [TextUtil.NormalizeNewlines(text).Replace("\n", " ", StringComparison.Ordinal)];

        lineCount = lines.Length;

        var known = BuildKnownSet(options);
        var minor = BuildMinorSet(options);
        var collected = new List<string>();

        var buffers = new StringBuilder[AllCases.Count];
        for (var i = 0; i < buffers.Length; i++)
        {
            buffers[i] = new StringBuilder();
        }

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var indentLength = 0;
            while (indentLength < line.Length && char.IsWhiteSpace(line[indentLength]))
            {
                indentLength++;
            }

            var indent = options.PreserveIndentation ? line[..indentLength] : string.Empty;
            var content = line[indentLength..];
            var words = SplitDetected(content, options);
            collected.AddRange(words.Select(w => w.Text));

            for (var caseIndex = 0; caseIndex < AllCases.Count; caseIndex++)
            {
                if (lineIndex > 0)
                {
                    buffers[caseIndex].Append(joiner);
                }

                buffers[caseIndex].Append(indent);
                buffers[caseIndex].Append(RenderLine(AllCases[caseIndex], content, words, options, known, minor));
            }
        }

        allWords = collected;
        return new CaseVariants(
            buffers[0].ToString(), buffers[1].ToString(), buffers[2].ToString(), buffers[3].ToString(),
            buffers[4].ToString(), buffers[5].ToString(), buffers[6].ToString(), buffers[7].ToString(),
            buffers[8].ToString(), buffers[9].ToString(), buffers[10].ToString(), buffers[11].ToString(),
            buffers[12].ToString(), buffers[13].ToString(), buffers[14].ToString());
    }

    private static string RenderLine(
        LetterCase letterCase,
        string content,
        IReadOnlyList<Word> words,
        CaseConverterOptions options,
        HashSet<string> known,
        HashSet<string> minor)
    {
        switch (letterCase)
        {
            case LetterCase.Lower:
                return content.ToLowerInvariant();
            case LetterCase.Upper:
                return content.ToUpperInvariant();
            case LetterCase.Alternating:
                return Alternate(content);
            case LetterCase.Inverse:
                return Invert(content);
            default:
                break;
        }

        if (words.Count == 0)
        {
            return string.Empty;
        }

        return letterCase switch
        {
            LetterCase.Camel => JoinCamel(words, options, known),
            LetterCase.Pascal => Join(words, string.Empty, WordForm.Capital, options, known),
            LetterCase.Snake => Join(words, "_", WordForm.Lower, options, known),
            LetterCase.ScreamingSnake => Join(words, "_", WordForm.Upper, options, known),
            LetterCase.Kebab => Join(words, "-", WordForm.Lower, options, known),
            LetterCase.Cobol => Join(words, "-", WordForm.Upper, options, known),
            LetterCase.Train => Join(words, "-", WordForm.Capital, options, known),
            LetterCase.Dot => Join(words, ".", WordForm.Lower, options, known),
            LetterCase.Path => Join(words, "/", WordForm.Lower, options, known),
            LetterCase.Sentence => JoinSentence(words, options, known),
            LetterCase.Title => JoinTitle(words, options, known, minor),
            _ => string.Empty,
        };
    }

    private static string Join(
        IReadOnlyList<Word> words,
        string separator,
        WordForm form,
        CaseConverterOptions options,
        HashSet<string> known)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(separator);
            }

            builder.Append(Render(words[i], form, options, known));
        }

        return builder.ToString();
    }

    private static string JoinCamel(IReadOnlyList<Word> words, CaseConverterOptions options, HashSet<string> known)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            builder.Append(Render(words[i], i == 0 ? WordForm.Lower : WordForm.Capital, options, known));
        }

        return builder.ToString();
    }

    private static string JoinSentence(IReadOnlyList<Word> words, CaseConverterOptions options, HashSet<string> known)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(Render(words[i], i == 0 ? WordForm.Capital : WordForm.Prose, options, known));
        }

        return builder.ToString();
    }

    private static string JoinTitle(
        IReadOnlyList<Word> words,
        CaseConverterOptions options,
        HashSet<string> known,
        HashSet<string> minor)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            var isEdge = i == 0 || i == words.Count - 1;
            var isMinor = !isEdge && minor.Contains(words[i].Text);
            builder.Append(Render(words[i], isMinor ? WordForm.Prose : WordForm.Capital, options, known));
        }

        return builder.ToString();
    }

    private static string Render(Word word, WordForm form, CaseConverterOptions options, HashSet<string> known)
    {
        var acronym = IsAcronym(word, options, known);
        return form switch
        {
            WordForm.Lower => word.Text.ToLowerInvariant(),
            WordForm.Upper => word.Text.ToUpperInvariant(),
            WordForm.Capital => acronym ? word.Text.ToUpperInvariant() : Capitalize(word.Text),
            _ => acronym ? word.Text.ToUpperInvariant() : word.Text.ToLowerInvariant(),
        };
    }

    private static bool IsAcronym(Word word, CaseConverterOptions options, HashSet<string> known)
    {
        if (!options.PreserveAcronyms)
        {
            return false;
        }

        if (options.AcronymDetection is AcronymDetection.FromInput or AcronymDetection.Both
            && word.WasAllUpper
            && word.LetterCount >= 2)
        {
            return true;
        }

        return options.AcronymDetection is AcronymDetection.KnownList or AcronymDetection.Both
            && known.Contains(word.Text);
    }

    /// <summary>Lower-cases the word, then upper-cases its first cased letter — surrogate pairs included.</summary>
    private static string Capitalize(string word)
    {
        if (word.Length == 0)
        {
            return word;
        }

        var lowered = word.ToLowerInvariant();
        var index = 0;
        while (index < lowered.Length)
        {
            var status = Rune.DecodeFromUtf16(lowered.AsSpan(index), out var rune, out var consumed);
            if (status == OperationStatus.Done && Rune.IsLetter(rune))
            {
                var upper = Rune.ToUpperInvariant(rune);
                return string.Concat(
                    lowered.AsSpan(0, index),
                    upper.ToString(),
                    lowered.AsSpan(index + consumed));
            }

            index += consumed;
        }

        return lowered;
    }

    private static string Alternate(string content)
    {
        var builder = new StringBuilder(content.Length);
        var position = 0;
        var index = 0;
        while (index < content.Length)
        {
            var status = Rune.DecodeFromUtf16(content.AsSpan(index), out var rune, out var consumed);
            if (status == OperationStatus.Done)
            {
                var mapped = position % 2 == 0 ? Rune.ToLowerInvariant(rune) : Rune.ToUpperInvariant(rune);
                builder.Append(mapped.ToString());
            }
            else
            {
                builder.Append(content, index, consumed);
            }

            position++;
            index += consumed;
        }

        return builder.ToString();
    }

    private static string Invert(string content)
    {
        var builder = new StringBuilder(content.Length);
        var index = 0;
        while (index < content.Length)
        {
            var status = Rune.DecodeFromUtf16(content.AsSpan(index), out var rune, out var consumed);
            if (status == OperationStatus.Done)
            {
                var mapped = Rune.IsUpper(rune)
                    ? Rune.ToLowerInvariant(rune)
                    : Rune.IsLower(rune) ? Rune.ToUpperInvariant(rune) : rune;
                builder.Append(mapped.ToString());
            }
            else
            {
                builder.Append(content, index, consumed);
            }

            index += consumed;
        }

        return builder.ToString();
    }

    private static List<Word> SplitDetected(string? text, CaseConverterOptions options)
    {
        var words = new List<Word>();
        if (string.IsNullOrEmpty(text))
        {
            return words;
        }

        var classes = new List<CharClass>();
        var starts = new List<int>();
        var lengths = new List<int>();

        var index = 0;
        while (index < text.Length)
        {
            var status = Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed);
            classes.Add(status == OperationStatus.Done ? Classify(rune) : CharClass.Other);
            starts.Add(index);
            lengths.Add(consumed);
            index += consumed;
        }

        var builder = new StringBuilder();
        var allUpper = true;
        var letters = 0;

        void Flush()
        {
            if (builder.Length > 0)
            {
                words.Add(new Word(builder.ToString(), allUpper && letters >= 1, letters));
                builder.Clear();
            }

            allUpper = true;
            letters = 0;
        }

        for (var i = 0; i < classes.Count; i++)
        {
            var current = classes[i];
            if (current == CharClass.Delimiter)
            {
                Flush();
                continue;
            }

            if (builder.Length > 0)
            {
                var previous = classes[i - 1];
                var next = i + 1 < classes.Count ? classes[i + 1] : CharClass.Other;
                var split =
                    (current == CharClass.Upper && previous == CharClass.Lower)
                    || (current == CharClass.Upper && previous == CharClass.Upper && next == CharClass.Lower)
                    || (options.SplitOnDigits && current == CharClass.Digit && previous != CharClass.Digit)
                    || (options.SplitOnDigits && current != CharClass.Digit && previous == CharClass.Digit);

                if (split)
                {
                    Flush();
                }
            }

            builder.Append(text, starts[i], lengths[i]);
            if (current is CharClass.Upper or CharClass.Lower or CharClass.CaselessLetter)
            {
                letters++;
                if (current != CharClass.Upper)
                {
                    allUpper = false;
                }
            }
        }

        Flush();
        return words;
    }

    private static CharClass Classify(Rune rune)
    {
        if (rune.Value < 128 && DelimiterChars.Contains((char)rune.Value, StringComparison.Ordinal))
        {
            return CharClass.Delimiter;
        }

        if (Rune.IsWhiteSpace(rune))
        {
            return CharClass.Delimiter;
        }

        if (Rune.IsUpper(rune))
        {
            return CharClass.Upper;
        }

        if (Rune.IsLower(rune))
        {
            return CharClass.Lower;
        }

        if (Rune.IsLetter(rune))
        {
            return CharClass.CaselessLetter;
        }

        return Rune.IsDigit(rune) ? CharClass.Digit : CharClass.Other;
    }

    private static HashSet<string> BuildKnownSet(CaseConverterOptions options) =>
        new(options.KnownAcronyms ?? DefaultAcronyms, StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> BuildMinorSet(CaseConverterOptions options) =>
        new(options.TitleCaseMinorWords ?? DefaultMinorWords, StringComparer.OrdinalIgnoreCase);

    private static string? SizeError(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetByteCount(input);
        return bytes > Limits.MaxInputBytes
            ? $"Input is {Limits.Describe(bytes)}, which exceeds the {Limits.Describe(Limits.MaxInputBytes)} limit for the case converter."
            : null;
    }

    private enum CharClass
    {
        Upper,
        Lower,
        CaselessLetter,
        Digit,
        Delimiter,
        Other,
    }

    private enum WordForm
    {
        Lower,
        Upper,
        Capital,
        Prose,
    }

    private sealed record Word(string Text, bool WasAllUpper, int LetterCount);
}
