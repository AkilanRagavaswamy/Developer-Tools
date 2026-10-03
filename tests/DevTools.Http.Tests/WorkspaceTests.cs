using DevTools.Http.Model;
using DevTools.Http.Workspace;
using Xunit;

namespace DevTools.Http.Tests;

public sealed class VariableResolverTests
{
    private static ApiWorkspace Workspace(
        IEnumerable<Variable>? globals = null,
        IEnumerable<Variable>? environment = null)
    {
        var environments = environment is null
            ? []
            : new List<ApiEnvironment> { new() { Id = "env", Name = "Local", Variables = [.. environment] } };

        return new ApiWorkspace
        {
            Globals = [.. globals ?? []],
            Environments = environments,
            ActiveEnvironmentId = environment is null ? null : "env",
        };
    }

    [Fact]
    public void A_placeholder_is_replaced_with_its_value()
    {
        var resolver = new VariableResolver(Workspace(globals: [new Variable("host", "example.com")]));

        Assert.Equal("https://example.com/api", resolver.Substitute("https://{{host}}/api").Text);
    }

    [Fact]
    public void Whitespace_inside_the_braces_is_tolerated()
    {
        var resolver = new VariableResolver(Workspace(globals: [new Variable("a", "1")]));

        Assert.Equal("1", resolver.Substitute("{{ a }}").Text);
    }

    // FR-A26 — most specific wins.
    [Fact]
    public void Precedence_runs_environment_then_collection_then_global()
    {
        var workspace = Workspace(
            globals: [new Variable("who", "global")],
            environment: [new Variable("who", "environment")]);

        var collection = new RequestCollection { Variables = [new Variable("who", "collection")] };

        Assert.Equal("environment", new VariableResolver(workspace, collection).Substitute("{{who}}").Text);

        // With no active environment, the collection's value wins over the global one.
        var noEnvironment = Workspace(globals: [new Variable("who", "global")]);
        Assert.Equal("collection", new VariableResolver(noEnvironment, collection).Substitute("{{who}}").Text);

        Assert.Equal("global", new VariableResolver(noEnvironment).Substitute("{{who}}").Text);
    }

    [Fact]
    public void The_source_of_each_resolved_value_is_reported()
    {
        var workspace = Workspace(
            globals: [new Variable("g", "1")],
            environment: [new Variable("e", "2")]);

        var result = new VariableResolver(workspace).Substitute("{{g}}/{{e}}");

        Assert.Equal(VariableSource.Global, result.Used.Single(static v => v.Name == "g").Source);
        Assert.Equal(VariableSource.Environment, result.Used.Single(static v => v.Name == "e").Source);
    }

    [Fact]
    public void A_disabled_variable_is_not_in_scope()
    {
        var resolver = new VariableResolver(Workspace(globals: [new Variable("a", "1", Enabled: false)]));

        Assert.True(resolver.Substitute("{{a}}").HasMissing);
    }

    /// <summary>
    /// An unknown placeholder is left visible rather than becoming an empty string — a request
    /// that quietly sends "https:///api" is far harder to diagnose than one that shows the
    /// placeholder it could not fill.
    /// </summary>
    [Fact]
    public void An_unknown_placeholder_is_left_in_place_and_reported()
    {
        var result = new VariableResolver(Workspace()).Substitute("https://{{host}}/api");

        Assert.Equal("https://{{host}}/api", result.Text);
        Assert.True(result.HasMissing);
        Assert.Equal("host", result.Missing.Single());
    }

    [Fact]
    public void A_variable_may_refer_to_another_one()
    {
        var resolver = new VariableResolver(Workspace(globals:
        [
            new Variable("base", "https://{{host}}"),
            new Variable("host", "example.com"),
        ]));

        Assert.Equal("https://example.com/v1", resolver.Substitute("{{base}}/v1").Text);
    }

    [Fact]
    public void A_cycle_terminates_instead_of_hanging()
    {
        var resolver = new VariableResolver(Workspace(globals:
        [
            new Variable("a", "{{b}}"),
            new Variable("b", "{{a}}"),
        ]));

        // The assertion that matters is that this returns at all.
        Assert.NotNull(resolver.Substitute("{{a}}").Text);
    }

    [Fact]
    public void Text_with_no_placeholders_is_returned_untouched()
    {
        var resolver = new VariableResolver(Workspace(globals: [new Variable("a", "1")]));

        Assert.Equal("nothing to do here", resolver.Substitute("nothing to do here").Text);
        Assert.Equal(string.Empty, resolver.Substitute(null).Text);
    }

    [Fact]
    public void An_unclosed_brace_is_left_alone()
    {
        var resolver = new VariableResolver(Workspace(globals: [new Variable("a", "1")]));

        Assert.Equal("{{a", resolver.Substitute("{{a").Text);
    }

    [Fact]
    public void Substitution_reaches_every_part_of_a_request()
    {
        var workspace = Workspace(globals:
        [
            new Variable("host", "api.example.com"),
            new Variable("version", "v2"),
            new Variable("key", "abc"),
            new Variable("name", "Ada"),
        ]);

        var request = new RequestDefinition
        {
            Method = "POST",
            Url = "https://{{host}}/{{version}}/things",
            Headers = [new KeyValueItem("X-Key", "{{key}}")],
            Query = [new KeyValueItem("q", "{{name}}")],
            Body = BodySpec.FromJson("""{"name":"{{name}}"}"""),
            Auth = new AuthSpec { Kind = AuthKind.Basic, Username = "{{name}}" },
        };

        var (resolved, missing) = new VariableResolver(workspace).Apply(request);

        Assert.Empty(missing);
        Assert.Equal("https://api.example.com/v2/things", resolved.Url);
        Assert.Equal("abc", resolved.Headers[0].Value);
        Assert.Equal("Ada", resolved.Query[0].Value);
        Assert.Equal("""{"name":"Ada"}""", resolved.Body.Text);
        Assert.Equal("Ada", resolved.Auth.Username);
    }

    [Fact]
    public void Applying_a_request_reports_every_placeholder_it_could_not_fill()
    {
        var request = new RequestDefinition
        {
            Url = "https://{{host}}/{{version}}",
            Headers = [new KeyValueItem("X-Key", "{{key}}")],
        };

        var (_, missing) = new VariableResolver(Workspace()).Apply(request);

        Assert.Equal(3, missing.Count);
        Assert.Contains("host", missing);
        Assert.Contains("version", missing);
        Assert.Contains("key", missing);
    }
}

public sealed class WorkspaceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"devtools-workspace-{Guid.NewGuid():N}");

    private WorkspaceStore Store => new(_root);

    private static ApiWorkspace Sample() => new()
    {
        Globals = [new Variable("host", "example.com")],
        Environments = [new ApiEnvironment { Id = "env", Name = "Local", Variables = [new Variable("port", "5001")] }],
        ActiveEnvironmentId = "env",
        Collections =
        [
            new RequestCollection
            {
                Name = "Orders",
                Requests = [RequestDefinition.Get("https://{{host}}/orders") with { Name = "List" }],
                Folders =
                [
                    new RequestFolder
                    {
                        Name = "Admin",
                        Requests = [new RequestDefinition { Name = "Purge", Method = "DELETE", Url = "https://{{host}}/purge" }],
                    },
                ],
            },
        ],
    };

    [Fact]
    public void A_missing_file_loads_as_an_empty_workspace_rather_than_failing()
    {
        var result = Store.Load();

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Collections);
    }

    [Fact]
    public void A_workspace_survives_a_save_and_load()
    {
        Assert.True(Store.Save(Sample()).IsSuccess);

        var loaded = Store.Load();
        Assert.True(loaded.IsSuccess, loaded.ErrorMessage);

        var workspace = loaded.Value!;
        Assert.Equal("example.com", workspace.Globals.Single().Value);
        Assert.Equal("Local", workspace.ActiveEnvironment!.Name);

        var collection = Assert.Single(workspace.Collections);
        Assert.Equal("Orders", collection.Name);
        Assert.Equal(2, collection.AllRequests().Count());
        Assert.Equal("DELETE", collection.AllRequests().Single(static r => r.Name == "Purge").Method);
    }

    [Fact]
    public void Saving_twice_replaces_the_file_and_leaves_no_temporary_behind()
    {
        Assert.True(Store.Save(Sample()).IsSuccess);
        Assert.True(Store.Save(Sample() with { Collections = [] }).IsSuccess);

        Assert.Empty(Store.Load().Value!.Collections);
        Assert.False(File.Exists(Store.WorkspacePath + ".tmp"));
    }

    /// <summary>A corrupt file must never stop the app from starting.</summary>
    [Fact]
    public void A_corrupt_file_loads_as_empty_with_a_warning_and_is_not_destroyed()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Store.WorkspacePath, "{ this is not json");

        var result = Store.Load();

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Collections);
        Assert.True(result.HasWarning);
        Assert.True(File.Exists(Store.WorkspacePath));
    }

    [Fact]
    public void An_empty_file_loads_as_an_empty_workspace()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Store.WorkspacePath, "   ");

        Assert.True(Store.Load().IsSuccess);
    }

    /// <summary>
    /// A file from a newer build is refused rather than silently read as far as this build
    /// understands it — which is the reason the schema version exists at all.
    /// </summary>
    [Fact]
    public void A_workspace_from_a_newer_build_is_refused_rather_than_partially_read()
    {
        Assert.True(Store.Save(Sample() with { SchemaVersion = ApiWorkspace.CurrentSchemaVersion + 1 }).IsSuccess);

        var result = Store.Load();

        Assert.False(result.IsSuccess);
        Assert.Contains("newer version", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_saved_file_records_the_schema_version()
    {
        Assert.True(Store.Save(Sample()).IsSuccess);

        Assert.Contains(
            $"\"schemaVersion\": {ApiWorkspace.CurrentSchemaVersion}",
            File.ReadAllText(Store.WorkspacePath),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_collection_round_trips_through_export_and_import()
    {
        var collection = Sample().Collections[0];

        var exported = WorkspaceStore.ExportCollection(collection);
        Assert.True(exported.IsSuccess, exported.ErrorMessage);

        var imported = WorkspaceStore.ImportCollection(exported.Value);
        Assert.True(imported.IsSuccess, imported.ErrorMessage);

        Assert.Equal("Orders", imported.Value!.Name);
        Assert.Equal(2, imported.Value.AllRequests().Count());
    }

    [Fact]
    public void Export_leaves_out_secret_variable_values_by_default()
    {
        var collection = new RequestCollection
        {
            Name = "With secrets",
            Variables =
            [
                new Variable("public", "fine"),
                new Variable("apiKey", "super-secret", Secret: true),
            ],
        };

        var withoutSecrets = WorkspaceStore.ExportCollection(collection);
        Assert.True(withoutSecrets.IsSuccess);
        Assert.DoesNotContain("super-secret", withoutSecrets.Value!, StringComparison.Ordinal);
        Assert.Contains("fine", withoutSecrets.Value, StringComparison.Ordinal);

        var withSecrets = WorkspaceStore.ExportCollection(collection, includeSecrets: true);
        Assert.True(withSecrets.IsSuccess);
        Assert.Contains("super-secret", withSecrets.Value!, StringComparison.Ordinal);
    }

    [Fact]
    public void Importing_something_that_is_not_a_collection_is_reported()
    {
        Assert.False(WorkspaceStore.ImportCollection("not json").IsSuccess);
        Assert.False(WorkspaceStore.ImportCollection("").IsSuccess);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
