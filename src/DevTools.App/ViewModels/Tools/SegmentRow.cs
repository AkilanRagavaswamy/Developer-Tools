namespace DevTools.App.ViewModels.Tools;

/// <summary>
/// One row of a timing breakdown: a named phase and how long it took.
/// </summary>
/// <remarks>
/// Formatting lives here rather than in the pages because the same durations are shown by more
/// than one tool, and "64 ms" against "0.064 s" in two places reads as two different numbers.
/// </remarks>
public sealed record SegmentRow(string Name, TimeSpan Value, bool HandshakeOnly = false)
{
    public string Mean => Format(Value);

    public bool HasData => Value > TimeSpan.Zero;

    internal static string Format(TimeSpan value) => value.TotalMilliseconds switch
    {
        <= 0 => "—",
        < 1 => $"{value.TotalMilliseconds:0.##} ms",
        < 1000 => $"{value.TotalMilliseconds:0.#} ms",
        _ => $"{value.TotalSeconds:0.##} s",
    };
}
