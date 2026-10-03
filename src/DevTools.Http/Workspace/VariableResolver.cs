using System.Text;
using DevTools.Http.Model;

namespace DevTools.Http.Workspace;

/// <summary>Where a resolved value came from, so the preview can explain itself.</summary>
public enum VariableSource
{
    Unresolved,
    Environment,
    Collection,
    Global,
}

public sealed record ResolvedVariable(string Name, string? Value, VariableSource Source)
{
    public bool IsResolved => Source != VariableSource.Unresolved;
}

/// <summary>The outcome of substituting into one string.</summary>
public sealed record SubstitutionResult(
    string Text,
    IReadOnlyList<ResolvedVariable> Used)
{
    public IReadOnlyList<string> Missing =>
        [.. Used.Where(static v => !v.IsResolved).Select(static v => v.Name).Distinct(StringComparer.Ordinal)];

    public bool HasMissing => Missing.Count > 0;
}

/// <summary>
/// Substitutes <c>{{name}}</c> placeholders (FR-A26).
/// </summary>
/// <remarks>
/// Precedence is environment, then collection, then global — most specific wins. Resolution
/// is iterative so a variable may refer to another, with a depth cap because
/// <c>a = {{b}}</c> and <c>b = {{a}}</c> is a thing people write by accident.
/// </remarks>
public sealed class VariableResolver
{
    private const int MaxDepth = 8;

    private readonly Dictionary<string, (string Value, VariableSource Source)> _map = new(StringComparer.Ordinal);

    public VariableResolver(ApiWorkspace workspace, RequestCollection? collection = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        // Added lowest precedence first: each layer overwrites the one beneath it.
        foreach (var variable in workspace.Globals.Where(static v => v.Enabled))
        {
            _map[variable.Name] = (variable.Value, VariableSource.Global);
        }

        if (collection is not null)
        {
            foreach (var variable in collection.Variables.Where(static v => v.Enabled))
            {
                _map[variable.Name] = (variable.Value, VariableSource.Collection);
            }
        }

        if (workspace.ActiveEnvironment is { } environment)
        {
            foreach (var variable in environment.Variables.Where(static v => v.Enabled))
            {
                _map[variable.Name] = (variable.Value, VariableSource.Environment);
            }
        }
    }

    /// <summary>The variables in scope, highest precedence already applied.</summary>
    public IReadOnlyDictionary<string, string> Effective =>
        _map.ToDictionary(static kv => kv.Key, static kv => kv.Value.Value, StringComparer.Ordinal);

    public SubstitutionResult Substitute(string? text)
    {
        var used = new List<ResolvedVariable>();

        if (string.IsNullOrEmpty(text))
        {
            return new SubstitutionResult(text ?? string.Empty, used);
        }

        var current = text;

        for (var depth = 0; depth < MaxDepth; depth++)
        {
            var (next, replaced) = SubstituteOnce(current, used);

            if (!replaced)
            {
                return new SubstitutionResult(next, used);
            }

            current = next;
        }

        return new SubstitutionResult(current, used);
    }

    private (string Text, bool Replaced) SubstituteOnce(string text, List<ResolvedVariable> used)
    {
        var builder = new StringBuilder(text.Length);
        var replaced = false;
        var i = 0;

        while (i < text.Length)
        {
            if (i + 1 < text.Length && text[i] == '{' && text[i + 1] == '{')
            {
                var close = text.IndexOf("}}", i + 2, StringComparison.Ordinal);

                if (close > 0)
                {
                    var name = text[(i + 2)..close].Trim();

                    if (name.Length > 0)
                    {
                        if (_map.TryGetValue(name, out var hit))
                        {
                            builder.Append(hit.Value);
                            used.Add(new ResolvedVariable(name, hit.Value, hit.Source));
                            replaced = true;
                        }
                        else
                        {
                            // An unknown placeholder is left in place, so it is visible in the
                            // preview rather than silently becoming an empty string.
                            builder.Append(text, i, close + 2 - i);
                            used.Add(new ResolvedVariable(name, null, VariableSource.Unresolved));
                        }

                        i = close + 2;
                        continue;
                    }
                }
            }

            builder.Append(text[i]);
            i++;
        }

        return (builder.ToString(), replaced);
    }

    /// <summary>Applies substitution across a whole request (FR-A26).</summary>
    public (RequestDefinition Request, IReadOnlyList<string> Missing) Apply(RequestDefinition request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var missing = new List<string>();

        string Sub(string? value)
        {
            var result = Substitute(value);
            missing.AddRange(result.Missing);
            return result.Text;
        }

        var resolved = request with
        {
            Url = Sub(request.Url),
            Headers = [.. request.Headers.Select(h => h with { Name = Sub(h.Name), Value = Sub(h.Value) })],
            Query = [.. request.Query.Select(q => q with { Name = Sub(q.Name), Value = Sub(q.Value) })],
            Body = request.Body with
            {
                Text = request.Body.Text is null ? null : Sub(request.Body.Text),
                Form = [.. request.Body.Form.Select(f => f with { Name = Sub(f.Name), Value = Sub(f.Value) })],
                Parts = [.. request.Body.Parts.Select(p => p with
                {
                    Name = Sub(p.Name),
                    Value = p.Value is null ? null : Sub(p.Value),
                })],
            },
            Auth = request.Auth with
            {
                Username = request.Auth.Username is null ? null : Sub(request.Auth.Username),
                ApiKeyName = request.Auth.ApiKeyName is null ? null : Sub(request.Auth.ApiKeyName),
                TokenUrl = request.Auth.TokenUrl is null ? null : Sub(request.Auth.TokenUrl),
                ClientId = request.Auth.ClientId is null ? null : Sub(request.Auth.ClientId),
                Scope = request.Auth.Scope is null ? null : Sub(request.Auth.Scope),
            },
        };

        return (resolved, [.. missing.Distinct(StringComparer.Ordinal)]);
    }
}
