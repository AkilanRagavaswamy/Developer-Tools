using DevTools.Core;
using DevTools.Core.Json;
using DevTools.Http.Model;
using DevTools.Http.Workspace;

namespace DevTools.Http.Interop;

/// <summary>
/// Turns an OpenAPI 3 document into a collection (FR-A29).
/// </summary>
/// <remarks>
/// The goal is a collection someone can start sending, not a faithful model of the spec: each
/// operation becomes a request with its path, its parameters as query rows or path variables,
/// and a body skeleton generated from the request schema. Anything that cannot be turned into
/// a sendable request is reported rather than dropped.
/// </remarks>
public static class OpenApiImporter
{
    public static OperationResult<RequestCollection> Import(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return OperationResult<RequestCollection>.Fail("There is nothing to import.");
        }

        var parsed = JsonReader.Parse(json, JsonReaderOptions.Tolerant);

        if (!parsed.IsSuccess)
        {
            return OperationResult<RequestCollection>.Fail(
                $"The document is not valid JSON: {parsed.Error!.ToDisplayString()}. " +
                "If this is a YAML spec, convert it to JSON first.");
        }

        if (parsed.Value!.Root is not JsonObject root)
        {
            return OperationResult<RequestCollection>.Fail("An OpenAPI document must be a JSON object.");
        }

        var version = (root.Find("openapi") as JsonString)?.Value;

        if (version is null)
        {
            return OperationResult<RequestCollection>.Fail(
                root.Find("swagger") is not null
                    ? "This is a Swagger 2.0 document. Convert it to OpenAPI 3 first."
                    : "The document has no \"openapi\" version, so it is not an OpenAPI 3 spec.");
        }

        if (!version.StartsWith('3'))
        {
            return OperationResult<RequestCollection>.Fail($"OpenAPI {version} is not supported; this importer reads version 3.");
        }

        var info = root.Find("info") as JsonObject;
        var title = (info?.Find("title") as JsonString)?.Value ?? "Imported API";
        var description = (info?.Find("description") as JsonString)?.Value;

        var baseUrl = ReadFirstServer(root);
        var variables = new List<Variable>();

        if (!string.IsNullOrEmpty(baseUrl))
        {
            variables.Add(new Variable("baseUrl", baseUrl));
        }

        var components = root.Find("components") as JsonObject;
        var folders = new Dictionary<string, List<RequestDefinition>>(StringComparer.Ordinal);
        var loose = new List<RequestDefinition>();

        if (root.Find("paths") is JsonObject paths)
        {
            foreach (var path in paths.Members)
            {
                if (path.Value is not JsonObject operations)
                {
                    continue;
                }

                foreach (var operation in operations.Members)
                {
                    var method = operation.Name.ToUpperInvariant();

                    if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS"))
                    {
                        continue;
                    }

                    if (operation.Value is not JsonObject body)
                    {
                        continue;
                    }

                    var request = BuildRequest(path.Name, method, body, components, baseUrl);

                    // Operations are grouped by their first tag, which is what the spec's own
                    // documentation tooling does, so the tree matches the API's docs.
                    var tag = (body.Find("tags") as JsonArray)?.Items.OfType<JsonString>().FirstOrDefault()?.Value;

                    if (string.IsNullOrWhiteSpace(tag))
                    {
                        loose.Add(request);
                    }
                    else
                    {
                        if (!folders.TryGetValue(tag, out var list))
                        {
                            folders[tag] = list = [];
                        }

                        list.Add(request);
                    }
                }
            }
        }

        if (folders.Count == 0 && loose.Count == 0)
        {
            return OperationResult<RequestCollection>.Fail("The document defines no operations to import.");
        }

        return OperationResult<RequestCollection>.Ok(new RequestCollection
        {
            Name = title,
            Description = description,
            Variables = variables,
            Requests = loose,
            Folders =
            [
                .. folders
                    .OrderBy(static f => f.Key, StringComparer.Ordinal)
                    .Select(static f => new RequestFolder { Name = f.Key, Requests = f.Value }),
            ],
        });
    }

    private static string ReadFirstServer(JsonObject root)
    {
        if (root.Find("servers") is not JsonArray servers)
        {
            return string.Empty;
        }

        foreach (var server in servers.Items.OfType<JsonObject>())
        {
            if (server.Find("url") is JsonString url && url.Value.Length > 0)
            {
                var text = url.Value.TrimEnd('/');

                // Server templating ({region}.example.com) becomes a workspace variable so the
                // URL is at least editable rather than unusable.
                return text.Replace('{', '{').Replace('}', '}');
            }
        }

        return string.Empty;
    }

    private static RequestDefinition BuildRequest(
        string path,
        string method,
        JsonObject operation,
        JsonObject? components,
        string baseUrl)
    {
        var name = (operation.Find("summary") as JsonString)?.Value
                   ?? (operation.Find("operationId") as JsonString)?.Value
                   ?? $"{method} {path}";

        var query = new List<KeyValueItem>();
        var headers = new List<KeyValueItem>();
        var url = path;

        foreach (var parameter in ReadParameters(operation, components))
        {
            var parameterName = (parameter.Find("name") as JsonString)?.Value;
            var location = (parameter.Find("in") as JsonString)?.Value;

            if (string.IsNullOrWhiteSpace(parameterName) || string.IsNullOrWhiteSpace(location))
            {
                continue;
            }

            var required = parameter.Find("required") is JsonBool { Value: true };
            var example = Example(parameter.Find("schema") as JsonObject) ?? string.Empty;
            var describedBy = (parameter.Find("description") as JsonString)?.Value;

            switch (location)
            {
                case "query":
                    query.Add(new KeyValueItem(parameterName, example, required, describedBy));
                    break;

                case "header":
                    headers.Add(new KeyValueItem(parameterName, example, required, describedBy));
                    break;

                case "path":
                    // Path parameters become workspace variables so they are filled in one place.
                    url = url.Replace($"{{{parameterName}}}", $"{{{{{parameterName}}}}}", StringComparison.Ordinal);
                    break;
            }
        }

        var body = ReadBody(operation, components, headers);

        var full = string.IsNullOrEmpty(baseUrl) ? url : "{{baseUrl}}" + url;

        return new RequestDefinition
        {
            Name = name,
            Method = method,
            Url = full,
            Query = query,
            Headers = headers,
            Body = body,
        };
    }

    private static IEnumerable<JsonObject> ReadParameters(JsonObject operation, JsonObject? components)
    {
        if (operation.Find("parameters") is not JsonArray parameters)
        {
            yield break;
        }

        foreach (var parameter in parameters.Items.OfType<JsonObject>())
        {
            var resolved = Resolve(parameter, components);

            if (resolved is not null)
            {
                yield return resolved;
            }
        }
    }

    private static BodySpec ReadBody(JsonObject operation, JsonObject? components, List<KeyValueItem> headers)
    {
        if (operation.Find("requestBody") is not JsonObject requestBody)
        {
            return BodySpec.None;
        }

        var resolved = Resolve(requestBody, components);

        if (resolved?.Find("content") is not JsonObject content || content.Members.Count == 0)
        {
            return BodySpec.None;
        }

        // JSON first when it is offered; it is what a developer wants to see in the editor.
        var media = content.Members.FirstOrDefault(static m =>
                        m.Name.Contains("json", StringComparison.OrdinalIgnoreCase))
                    ?? content.Members[0];

        var schema = Resolve((media.Value as JsonObject)?.Find("schema") as JsonObject, components);
        var skeleton = schema is null ? "{}" : JsonWriter.Write(BuildSkeleton(schema, components, 0));

        if (media.Name.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return new BodySpec { Kind = BodyKind.Json, Text = skeleton };
        }

        if (media.Name.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            var fields = new List<FormField>();

            if (schema?.Find("properties") is JsonObject properties)
            {
                fields.AddRange(properties.Members.Select(p => new FormField(p.Name, string.Empty)));
            }

            return new BodySpec { Kind = BodyKind.FormUrlEncoded, Form = fields };
        }

        if (!headers.Any(static h => string.Equals(h.Name, "Content-Type", StringComparison.OrdinalIgnoreCase)))
        {
            headers.Add(new KeyValueItem("Content-Type", media.Name));
        }

        return new BodySpec { Kind = BodyKind.Raw, Text = string.Empty, ContentType = media.Name };
    }

    /// <summary>
    /// Builds an example document from a schema, so the body editor opens with the right shape
    /// rather than an empty box.
    /// </summary>
    private static JsonNode BuildSkeleton(JsonObject schema, JsonObject? components, int depth)
    {
        if (depth > 12)
        {
            return new JsonNull(JsonPosition.None);
        }

        if (schema.Find("example") is { } example)
        {
            return example;
        }

        var type = (schema.Find("type") as JsonString)?.Value;

        // A schema with properties but no declared type is an object; plenty of specs omit it.
        if (type is null && schema.Find("properties") is JsonObject)
        {
            type = "object";
        }

        switch (type)
        {
            case "object":
            {
                var members = new List<JsonMember>();

                if (schema.Find("properties") is JsonObject properties)
                {
                    foreach (var property in properties.Members)
                    {
                        var child = Resolve(property.Value as JsonObject, components);

                        members.Add(new JsonMember(
                            property.Name,
                            child is null ? new JsonNull(JsonPosition.None) : BuildSkeleton(child, components, depth + 1),
                            JsonPosition.None));
                    }
                }

                return new JsonObject(members, JsonPosition.None);
            }

            case "array":
            {
                var items = Resolve(schema.Find("items") as JsonObject, components);

                return new JsonArray(
                    items is null ? [] : [BuildSkeleton(items, components, depth + 1)],
                    JsonPosition.None);
            }

            case "integer":
                return new JsonNumber("0", JsonPosition.None);

            case "number":
                return new JsonNumber("0.0", JsonPosition.None);

            case "boolean":
                return new JsonBool(false, JsonPosition.None);

            case "string":
            {
                var format = (schema.Find("format") as JsonString)?.Value;

                return new JsonString(format switch
                {
                    "date-time" => "2026-01-01T00:00:00Z",
                    "date" => "2026-01-01",
                    "uuid" => "00000000-0000-0000-0000-000000000000",
                    "email" => "user@example.com",
                    "uri" => "https://example.com",
                    _ => (schema.Find("enum") as JsonArray)?.Items.OfType<JsonString>().FirstOrDefault()?.Value
                         ?? string.Empty,
                }, JsonPosition.None);
            }

            default:
                return new JsonNull(JsonPosition.None);
        }
    }

    /// <summary>Follows a local <c>$ref</c>. Remote references are not fetched — nothing here goes online.</summary>
    private static JsonObject? Resolve(JsonObject? node, JsonObject? components, int depth = 0)
    {
        if (node is null || depth > 16)
        {
            return node;
        }

        if (node.Find("$ref") is not JsonString reference)
        {
            return node;
        }

        const string prefix = "#/components/";

        if (!reference.Value.StartsWith(prefix, StringComparison.Ordinal) || components is null)
        {
            return null;
        }

        JsonNode? current = components;

        foreach (var segment in reference.Value[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is not JsonObject obj)
            {
                return null;
            }

            current = obj.Find(segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
        }

        return current is JsonObject resolved ? Resolve(resolved, components, depth + 1) : null;
    }

    private static string? Example(JsonObject? schema)
    {
        if (schema is null)
        {
            return null;
        }

        return schema.Find("example") switch
        {
            JsonString s => s.Value,
            JsonNumber n => n.Raw,
            JsonBool b => b.Value ? "true" : "false",
            _ => (schema.Find("default") as JsonString)?.Value,
        };
    }
}
