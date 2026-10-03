using DevTools.Http.Model;

namespace DevTools.Http.Workspace;

/// <summary>One variable in an environment or collection.</summary>
public sealed record Variable(string Name, string Value, bool Enabled = true, bool Secret = false);

/// <summary>A named set of variables (FR-A26).</summary>
public sealed record ApiEnvironment
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = "New environment";

    public IReadOnlyList<Variable> Variables { get; init; } = [];
}

/// <summary>A folder inside a collection. Folders nest.</summary>
public sealed record RequestFolder
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = "New folder";

    public IReadOnlyList<RequestFolder> Folders { get; init; } = [];

    public IReadOnlyList<RequestDefinition> Requests { get; init; } = [];
}

/// <summary>A collection of requests, its own variables, and a default auth scheme.</summary>
public sealed record RequestCollection
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = "New collection";

    public string? Description { get; init; }

    public IReadOnlyList<Variable> Variables { get; init; } = [];

    public IReadOnlyList<RequestFolder> Folders { get; init; } = [];

    public IReadOnlyList<RequestDefinition> Requests { get; init; } = [];

    /// <summary>Every request in this collection, whatever folder it sits in.</summary>
    public IEnumerable<RequestDefinition> AllRequests()
    {
        foreach (var request in Requests)
        {
            yield return request;
        }

        foreach (var request in Folders.SelectMany(Walk))
        {
            yield return request;
        }

        static IEnumerable<RequestDefinition> Walk(RequestFolder folder)
        {
            foreach (var request in folder.Requests)
            {
                yield return request;
            }

            foreach (var request in folder.Folders.SelectMany(Walk))
            {
                yield return request;
            }
        }
    }
}

/// <summary>
/// Everything API Builder persists (FR-A20).
/// </summary>
/// <remarks>
/// <see cref="SchemaVersion"/> is present from the first release on purpose. A document format
/// without a version is a format that cannot be changed later without guessing what an older
/// file meant.
/// </remarks>
public sealed record ApiWorkspace
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public IReadOnlyList<RequestCollection> Collections { get; init; } = [];

    public IReadOnlyList<ApiEnvironment> Environments { get; init; } = [];

    /// <summary>Variables available everywhere, at the lowest precedence.</summary>
    public IReadOnlyList<Variable> Globals { get; init; } = [];

    public string? ActiveEnvironmentId { get; init; }

    public static ApiWorkspace Empty { get; } = new();

    public ApiEnvironment? ActiveEnvironment =>
        Environments.FirstOrDefault(e => string.Equals(e.Id, ActiveEnvironmentId, StringComparison.Ordinal));
}
