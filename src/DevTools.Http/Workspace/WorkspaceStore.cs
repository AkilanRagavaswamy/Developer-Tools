using System.Text.Json;
using System.Text.Json.Serialization;
using DevTools.Core;

namespace DevTools.Http.Workspace;

/// <summary>
/// Reads and writes the workspace as JSON on disk (FR-A20).
/// </summary>
/// <remarks>
/// <para>
/// Writes go to a temporary file which then replaces the original, so an interrupted save
/// cannot leave a half-written collection behind. Reads tolerate a missing or corrupt file by
/// returning an empty workspace, because a bad file must never stop the app from starting.
/// </para>
/// <para>
/// Nothing written here is a secret: <see cref="Model.AuthSpec"/> stores a reference, and the
/// value it points at lives in the credential store (FR-A30).
/// </para>
/// </remarks>
public sealed class WorkspaceStore(string rootDirectory)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _root = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));

    public string WorkspacePath => Path.Combine(_root, "workspace.json");

    public OperationResult<ApiWorkspace> Load()
    {
        try
        {
            if (!File.Exists(WorkspacePath))
            {
                return OperationResult<ApiWorkspace>.Ok(ApiWorkspace.Empty);
            }

            var text = File.ReadAllText(WorkspacePath);

            if (string.IsNullOrWhiteSpace(text))
            {
                return OperationResult<ApiWorkspace>.Ok(ApiWorkspace.Empty);
            }

            var workspace = JsonSerializer.Deserialize<ApiWorkspace>(text, Json);

            if (workspace is null)
            {
                return OperationResult<ApiWorkspace>.Ok(ApiWorkspace.Empty, "The workspace file was empty; a new workspace was started.");
            }

            if (workspace.SchemaVersion > ApiWorkspace.CurrentSchemaVersion)
            {
                return OperationResult<ApiWorkspace>.Fail(
                    $"This workspace was written by a newer version of DevTools (format {workspace.SchemaVersion}, this build understands {ApiWorkspace.CurrentSchemaVersion}). " +
                    "Update DevTools rather than risk losing the file's contents.");
            }

            return OperationResult<ApiWorkspace>.Ok(workspace);
        }
        catch (JsonException ex)
        {
            return OperationResult<ApiWorkspace>.Ok(
                ApiWorkspace.Empty,
                $"The workspace file could not be read ({ex.Message}). It was left untouched and an empty workspace was opened.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult<ApiWorkspace>.Ok(
                ApiWorkspace.Empty,
                $"The workspace file could not be opened: {ex.Message}");
        }
    }

    public OperationResult<bool> Save(ApiWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        try
        {
            Directory.CreateDirectory(_root);

            var temp = WorkspacePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(workspace, Json));

            // Replace rather than delete-then-move: the original survives until the new file
            // is complete on disk.
            if (File.Exists(WorkspacePath))
            {
                File.Replace(temp, WorkspacePath, null);
            }
            else
            {
                File.Move(temp, WorkspacePath);
            }

            return OperationResult<bool>.Ok(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult<bool>.Fail($"The workspace could not be saved: {ex.Message}");
        }
    }

    /// <summary>Serialises one collection for export (FR-A29).</summary>
    public static OperationResult<string> ExportCollection(RequestCollection collection, bool includeSecrets = false)
    {
        ArgumentNullException.ThrowIfNull(collection);

        var exported = includeSecrets
            ? collection
            : collection with
            {
                // Secret variables are stripped unless the user asked for them, and even then
                // only the values they typed here — a credential-store reference is not a value.
                Variables = [.. collection.Variables.Select(v => v.Secret ? v with { Value = string.Empty } : v)],
            };

        try
        {
            return OperationResult<string>.Ok(JsonSerializer.Serialize(exported, Json));
        }
        catch (NotSupportedException ex)
        {
            return OperationResult<string>.Fail($"The collection could not be exported: {ex.Message}");
        }
    }

    public static OperationResult<RequestCollection> ImportCollection(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return OperationResult<RequestCollection>.Fail("There is nothing to import.");
        }

        try
        {
            var collection = JsonSerializer.Deserialize<RequestCollection>(json, Json);

            return collection is null
                ? OperationResult<RequestCollection>.Fail("The file did not contain a collection.")
                : OperationResult<RequestCollection>.Ok(collection);
        }
        catch (JsonException ex)
        {
            return OperationResult<RequestCollection>.Fail($"The file is not a DevTools collection: {ex.Message}");
        }
    }
}
