namespace DevTools.Core.Vector;

/// <summary>What a line of the conversion report is telling you.</summary>
public enum SvgNoteKind
{
    /// <summary>What did come across. One of these is always emitted, even for a clean run.</summary>
    Converted,

    /// <summary>A feature of the input that is not in the output, and will not be.</summary>
    Dropped,

    /// <summary>The output is there but constrained — a limit of the target dialect or shape.</summary>
    Limitation,
}

/// <summary>
/// One line of the conversion report: what happened, and why.
/// </summary>
/// <remarks>
/// The converter's contract is that nothing is dropped silently, because the user finds out
/// from the rendered picture otherwise. That contract needs the reason as well as the fact —
/// "text was left out" is not actionable, "text was left out, convert it to outlines first"
/// is. So a note carries the two separately, and the flat <see cref="Text"/> for callers that
/// only want a sentence.
/// </remarks>
public sealed record SvgNote(SvgNoteKind Kind, string Headline, string Detail)
{
    public SvgNote(SvgNoteKind kind, string headline)
        : this(kind, headline, string.Empty)
    {
    }

    /// <summary>The whole note as one sentence.</summary>
    public string Text => Detail.Length == 0 ? Headline : $"{Headline} {Detail}";
}
