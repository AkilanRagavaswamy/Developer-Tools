namespace DevTools.Core;

/// <summary>
/// A failure produced by a tool engine. <see cref="Line"/>, <see cref="Column"/> and
/// <see cref="Offset"/> are populated when the engine can locate the problem in the input,
/// which lets the UI point the user at the exact character.
/// </summary>
public sealed record ToolError(
    string Message,
    int? Line = null,
    int? Column = null,
    long? Offset = null)
{
    /// <summary>A single line combining the message with any position information.</summary>
    public string ToDisplayString()
    {
        if (Line is { } line && Column is { } column)
        {
            return $"{Message} (line {line}, column {column})";
        }

        if (Line is { } lineOnly)
        {
            return $"{Message} (line {lineOnly})";
        }

        if (Offset is { } offset)
        {
            return $"{Message} (offset {offset})";
        }

        return Message;
    }

    public override string ToString() => ToDisplayString();
}

/// <summary>
/// The uniform return type of every DevTools engine.
/// Engines never throw for invalid <em>input</em>; they return a failure carrying a message a
/// developer can act on. Exceptions are reserved for programmer error.
/// </summary>
public readonly record struct OperationResult<T>
{
    public bool IsSuccess { get; private init; }

    public T? Value { get; private init; }

    public ToolError? Error { get; private init; }

    /// <summary>Set on a successful result that the user should still be told something about.</summary>
    public string? Warning { get; private init; }

    public bool HasWarning => !string.IsNullOrEmpty(Warning);

    public static OperationResult<T> Ok(T value, string? warning = null) => new()
    {
        IsSuccess = true,
        Value = value,
        Warning = string.IsNullOrWhiteSpace(warning) ? null : warning,
    };

    public static OperationResult<T> Fail(string message) =>
        Fail(new ToolError(message));

    public static OperationResult<T> Fail(string message, int line, int column) =>
        Fail(new ToolError(message, line, column));

    public static OperationResult<T> Fail(string message, long offset) =>
        Fail(new ToolError(message, Offset: offset));

    public static OperationResult<T> Fail(ToolError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new OperationResult<T> { IsSuccess = false, Error = error };
    }

    /// <summary>The value on success, or <paramref name="fallback"/> on failure.</summary>
    public T? ValueOr(T? fallback) => IsSuccess ? Value : fallback;

    /// <summary>The failure text on failure, or an empty string on success.</summary>
    public string ErrorMessage => Error?.ToDisplayString() ?? string.Empty;

    /// <summary>Projects a successful value, propagating any failure unchanged.</summary>
    public OperationResult<TOut> Map<TOut>(Func<T, TOut> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return IsSuccess
            ? OperationResult<TOut>.Ok(selector(Value!), Warning)
            : OperationResult<TOut>.Fail(Error!);
    }
}

/// <summary>Non-generic helpers so call sites can write <c>OperationResult.Ok(x)</c>.</summary>
public static class OperationResult
{
    public static OperationResult<T> Ok<T>(T value, string? warning = null) =>
        OperationResult<T>.Ok(value, warning);

    public static OperationResult<T> Fail<T>(string message) =>
        OperationResult<T>.Fail(message);

    public static OperationResult<T> Fail<T>(ToolError error) =>
        OperationResult<T>.Fail(error);
}
