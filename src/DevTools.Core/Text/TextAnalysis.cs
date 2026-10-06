using System.Globalization;
using System.Text;

namespace DevTools.Core.Text;

/// <summary>How often one word or character occurs.</summary>
public sealed record FrequencyEntry(string Text, int Count, double Percent);

/// <summary>Counts describing a piece of text.</summary>
public sealed record TextStatistics(
    int Characters,
    int CharactersWithoutWhitespace,
    int Utf16Units,
    int Utf8Bytes,
    int Words,
    int UniqueWords,
    int Sentences,
    int Paragraphs,
    int Lines,
    int Letters,
    int Digits,
    int Whitespace,
    int Punctuation,
    int LongestLine,
    double AverageWordLength,
    NewlineStyle LineEndings,
    TimeSpan ReadingTime,
    TimeSpan SpeakingTime,
    IReadOnlyList<FrequencyEntry> TopWords,
    IReadOnlyList<FrequencyEntry> CharacterFrequency)
{
    public static TextStatistics Empty { get; } = new(
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, NewlineStyle.None, TimeSpan.Zero, TimeSpan.Zero, [], []);
}

/// <summary>
/// Text statistics in one pass.
/// </summary>
/// <remarks>
/// <para>
/// Characters are counted as people count them — grapheme clusters, so an emoji with a skin
/// tone is one — with the UTF-16 and UTF-8 sizes reported beside them for anyone fitting a
/// column or a payload limit. A word is a run of letters and digits; an apostrophe or hyphen
/// between two letters keeps "don't" and "well-known" whole. DevToys splits on both, and also
/// drops the last word of the text from its frequency table.
/// </para>
/// <para>
/// Reading time assumes 238 words a minute and speaking time 150, the commonly cited averages
/// for adult silent reading and for presentation speech.
/// </para>
/// </remarks>
public static class TextAnalysis
{
    private const double ReadingWordsPerMinute = 238;
    private const double SpeakingWordsPerMinute = 150;

    public static TextStatistics Analyze(string? text, int topWords = 50, int topCharacters = 100)
    {
        if (string.IsNullOrEmpty(text))
        {
            return TextStatistics.Empty;
        }

        var graphemes = 0;
        var nonWhitespace = 0;
        var letters = 0;
        var digits = 0;
        var whitespace = 0;
        var punctuation = 0;
        var characterCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            graphemes++;

            var c = element[0];
            if (char.IsWhiteSpace(c))
            {
                whitespace++;
            }
            else
            {
                nonWhitespace++;

                if (char.IsLetter(c))
                {
                    letters++;
                }
                else if (char.IsDigit(c))
                {
                    digits++;
                }
                else if (char.IsPunctuation(c))
                {
                    punctuation++;
                }
            }

            characterCounts[element] = characterCounts.GetValueOrDefault(element) + 1;
        }

        // Words, sentences, lines and paragraphs share one walk over the UTF-16 text.
        var wordCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var words = 0;
        var wordLetters = 0L;
        var sentences = 0;
        var sentenceHasContent = false;
        var lines = 1;
        var longestLine = 0;
        var lineLength = 0;
        var paragraphs = 0;
        var lineHasContent = false;
        var inParagraph = false;
        var word = new StringBuilder();

        void EndWord()
        {
            if (word.Length == 0)
            {
                return;
            }

            words++;
            wordLetters += word.Length;
            var key = word.ToString().ToLowerInvariant();
            wordCounts[key] = wordCounts.GetValueOrDefault(key) + 1;
            word.Clear();
        }

        void EndLine()
        {
            if (lineHasContent && !inParagraph)
            {
                paragraphs++;
            }

            inParagraph = lineHasContent;
            longestLine = Math.Max(longestLine, lineLength);
            lineLength = 0;
            lineHasContent = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c is '\r' or '\n')
            {
                EndWord();
                EndLine();
                lines++;

                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                continue;
            }

            lineLength++;

            if (!char.IsWhiteSpace(c))
            {
                lineHasContent = true;
            }

            if (char.IsLetterOrDigit(c) || char.IsSurrogate(c) ||
                CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                word.Append(c);
                sentenceHasContent = true;
                continue;
            }

            // "don't", "well-known": the joiner stays inside the word when letters surround it.
            if (c is '\'' or '’' or '-' && word.Length > 0 && i + 1 < text.Length && char.IsLetter(text[i + 1]))
            {
                word.Append(c);
                continue;
            }

            // "3.14" and "1,000" are one number, not two.
            if (c is '.' or ',' && word.Length > 0 && char.IsDigit(word[^1]) && i + 1 < text.Length && char.IsDigit(text[i + 1]))
            {
                word.Append(c);
                continue;
            }

            EndWord();

            if (c is '.' or '!' or '?' or '…' or '。' or '！' or '？')
            {
                // "?!" and "..." end one sentence, not three; "3.14" and "e.g.x" end none.
                var next = i + 1 < text.Length ? text[i + 1] : ' ';
                if (sentenceHasContent && (char.IsWhiteSpace(next) || next is '"' or '\'' or ')' or '’' or '”' || i + 1 == text.Length))
                {
                    sentences++;
                    sentenceHasContent = false;
                }
            }
        }

        EndWord();
        EndLine();

        if (sentenceHasContent)
        {
            sentences++;
        }

        var topWordList = Top(wordCounts, words, topWords);
        var topCharacterList = Top(characterCounts, graphemes, topCharacters);

        return new TextStatistics(
            Characters: graphemes,
            CharactersWithoutWhitespace: nonWhitespace,
            Utf16Units: text.Length,
            Utf8Bytes: System.Text.Encoding.UTF8.GetByteCount(text),
            Words: words,
            UniqueWords: wordCounts.Count,
            Sentences: sentences,
            Paragraphs: paragraphs,
            Lines: lines,
            Letters: letters,
            Digits: digits,
            Whitespace: whitespace,
            Punctuation: punctuation,
            LongestLine: longestLine,
            AverageWordLength: words == 0 ? 0 : (double)wordLetters / words,
            LineEndings: TextUtil.DetectNewline(text),
            ReadingTime: TimeSpan.FromMinutes(words / ReadingWordsPerMinute),
            SpeakingTime: TimeSpan.FromMinutes(words / SpeakingWordsPerMinute),
            TopWords: topWordList,
            CharacterFrequency: topCharacterList);
    }

    private static List<FrequencyEntry> Top(Dictionary<string, int> counts, int total, int take) =>
        [.. counts
            .OrderByDescending(static p => p.Value)
            .ThenBy(static p => p.Key, StringComparer.Ordinal)
            .Take(take)
            .Select(p => new FrequencyEntry(p.Key, p.Value, total == 0 ? 0 : 100.0 * p.Value / total))];

    /// <summary>A printable label for a character that would otherwise be invisible in a list.</summary>
    public static string Visible(string element) => element switch
    {
        " " => "␣ space",
        "\t" => "⇥ tab",
        "\r\n" or "\n" or "\r" => "↵ line break",
        " " => "⍽ no-break space",
        _ when element.Length == 1 && char.IsControl(element[0]) => $"U+{(int)element[0]:X4}",
        _ => element,
    };

    /// <summary>"under a minute", "4 min", "1 h 12 min".</summary>
    public static string DescribeDuration(TimeSpan span)
    {
        if (span.TotalSeconds < 1)
        {
            return "0 sec";
        }

        if (span.TotalMinutes < 1)
        {
            return $"{Math.Ceiling(span.TotalSeconds):0} sec";
        }

        if (span.TotalHours < 1)
        {
            return $"{Math.Round(span.TotalMinutes):0} min";
        }

        return $"{(int)span.TotalHours} h {span.Minutes} min";
    }
}
