using DevTools.Core;
using DevTools.Core.Json;
using DevTools.Http.Model;
using DevTools.Http.Workspace;

namespace DevTools.Http.Interop;

/// <summary>
/// Reads a Postman collection v2.1 export (FR-A29).
/// </summary>
/// <remarks>
/// Postman's format is what most people already have, so importing it is the shortest path off
/// their existing tooling. Only the parts that describe a request are read: scripts, tests and
/// the collection runner are outside v1's scope and are reported rather than silently lost.
/// </remarks>
public static class PostmanImporter
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
                $"The document is not valid JSON: {parsed.Error!.ToDisplayString()}");
        }

        if (parsed.Value!.Root is not JsonObject root)
        {
            return OperationResult<RequestCollection>.Fail("A Postman collection must be a JSON object.");
        }

        if (root.Find("info") is not JsonObject info)
        {
            return OperationResult<RequestCollection>.Fail("The file has no \"info\" block, so it is not a Postman collection.");
        }

        var schema = (info.Find("schema") as JsonString)?.Value ?? string.Empty;

        if (!schema.Contains("v2", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<RequestCollection>.Fail(
                "Only Postman collection format v2 and v2.1 can be imported. Re-export the collection as v2.1.");
        }

        var name = (info.Find("name") as JsonString)?.Value ?? "Imported collection";
        var description = (info.Find("description") as JsonString)?.Value;

        var variables = ReadVariables(root.Find("variable") as JsonArray);
        var (folders, requests) = ReadItems(root.Find("item") as JsonArray);

        if (folders.Count == 0 && requests.Count == 0)
        {
            return OperationResult<RequestCollection>.Fail("The collection contains no requests.");
        }

        return OperationResult<RequestCollection>.Ok(new RequestCollection
        {
            Name = name,
            Description = description,
            Variables = variables,
            Folders = folders,
            Requests = requests,
        });
    }

    private static List<Variable> ReadVariables(JsonArray? array)
    {
        var variables = new List<Variable>();

        if (array is null)
        {
            return variables;
        }

        foreach (var entry in array.Items.OfType<JsonObject>())
        {
            var key = (entry.Find("key") as JsonString)?.Value;

            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var value = entry.Find("value") switch
            {
                JsonString s => s.Value,
                JsonNumber n => n.Raw,
                JsonBool b => b.Value ? "true" : "false",
                _ => string.Empty,
            };

            var secret = (entry.Find("type") as JsonString)?.Value
                is "secret" or "password";

            variables.Add(new Variable(key, secret ? string.Empty : value, Enabled: true, Secret: secret));
        }

        return variables;
    }

    private static (List<RequestFolder> Folders, List<RequestDefinition> Requests) ReadItems(JsonArray? items)
    {
        var folders = new List<RequestFolder>();
        var requests = new List<RequestDefinition>();

        if (items is null)
        {
            return (folders, requests);
        }

        foreach (var item in items.Items.OfType<JsonObject>())
        {
            var name = (item.Find("name") as JsonString)?.Value ?? "Untitled";

            // An item with its own "item" array is a folder; one with "request" is a request.
            if (item.Find("item") is JsonArray children)
            {
                var (nested, nestedRequests) = ReadItems(children);

                folders.Add(new RequestFolder
                {
                    Name = name,
                    Folders = nested,
                    Requests = nestedRequests,
                });

                continue;
            }

            if (item.Find("request") is { } request)
            {
                var built = ReadRequest(name, request);

                if (built is not null)
                {
                    requests.Add(built);
                }
            }
        }

        return (folders, requests);
    }

    private static RequestDefinition? ReadRequest(string name, JsonNode node)
    {
        // The shorthand form is a bare URL string.
        if (node is JsonString shorthand)
        {
            return new RequestDefinition
            {
                Name = name,
                Method = "GET",
                Url = Execution.RequestFactory.StripQuery(shorthand.Value),
                Query = Execution.RequestFactory.SplitQuery(shorthand.Value),
            };
        }

        if (node is not JsonObject request)
        {
            return null;
        }

        var method = (request.Find("method") as JsonString)?.Value?.ToUpperInvariant() ?? "GET";
        var url = ReadUrl(request.Find("url"));

        var headers = new List<KeyValueItem>();

        if (request.Find("header") is JsonArray headerArray)
        {
            foreach (var header in headerArray.Items.OfType<JsonObject>())
            {
                var key = (header.Find("key") as JsonString)?.Value;

                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                headers.Add(new KeyValueItem(
                    key,
                    (header.Find("value") as JsonString)?.Value ?? string.Empty,
                    header.Find("disabled") is not JsonBool { Value: true },
                    (header.Find("description") as JsonString)?.Value));
            }
        }

        return new RequestDefinition
        {
            Name = name,
            Method = method,
            Url = Execution.RequestFactory.StripQuery(url),
            Query = Execution.RequestFactory.SplitQuery(url),
            Headers = headers,
            Body = ReadBody(request.Find("body") as JsonObject),
            Auth = ReadAuth(request.Find("auth") as JsonObject),
        };
    }

    private static string ReadUrl(JsonNode? node) => node switch
    {
        JsonString text => text.Value,
        JsonObject obj => (obj.Find("raw") as JsonString)?.Value ?? BuildUrl(obj),
        _ => string.Empty,
    };

    /// <summary>Rebuilds a URL from Postman's structured form when there is no "raw".</summary>
    private static string BuildUrl(JsonObject url)
    {
        var protocol = (url.Find("protocol") as JsonString)?.Value ?? "https";

        var host = url.Find("host") switch
        {
            JsonString s => s.Value,
            JsonArray a => string.Join('.', a.Items.OfType<JsonString>().Select(static s => s.Value)),
            _ => string.Empty,
        };

        var path = url.Find("path") switch
        {
            JsonString s => s.Value,
            JsonArray a => string.Join('/', a.Items.OfType<JsonString>().Select(static s => s.Value)),
            _ => string.Empty,
        };

        if (host.Length == 0)
        {
            return path;
        }

        var port = (url.Find("port") as JsonString)?.Value;
        var authority = string.IsNullOrEmpty(port) ? host : $"{host}:{port}";

        return $"{protocol}://{authority}/{path.TrimStart('/')}";
    }

    private static BodySpec ReadBody(JsonObject? body)
    {
        if (body is null)
        {
            return BodySpec.None;
        }

        var mode = (body.Find("mode") as JsonString)?.Value;

        switch (mode)
        {
            case "raw":
            {
                var text = (body.Find("raw") as JsonString)?.Value ?? string.Empty;

                var language = ((body.Find("options") as JsonObject)?.Find("raw") as JsonObject)
                    ?.Find("language") as JsonString;

                var isJson = language?.Value == "json" || JsonFormatter.IsValid(text);

                return isJson
                    ? new BodySpec { Kind = BodyKind.Json, Text = text }
                    : new BodySpec { Kind = BodyKind.Raw, Text = text };
            }

            case "urlencoded":
            {
                var fields = new List<FormField>();

                if (body.Find("urlencoded") is JsonArray array)
                {
                    foreach (var field in array.Items.OfType<JsonObject>())
                    {
                        var key = (field.Find("key") as JsonString)?.Value;

                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            fields.Add(new FormField(
                                key,
                                (field.Find("value") as JsonString)?.Value ?? string.Empty,
                                field.Find("disabled") is not JsonBool { Value: true }));
                        }
                    }
                }

                return new BodySpec { Kind = BodyKind.FormUrlEncoded, Form = fields };
            }

            case "formdata":
            {
                var parts = new List<MultipartPart>();

                if (body.Find("formdata") is JsonArray array)
                {
                    foreach (var part in array.Items.OfType<JsonObject>())
                    {
                        var key = (part.Find("key") as JsonString)?.Value;

                        if (string.IsNullOrWhiteSpace(key))
                        {
                            continue;
                        }

                        var type = (part.Find("type") as JsonString)?.Value;

                        parts.Add(type == "file"
                            ? new MultipartPart(key, FilePath: (part.Find("src") as JsonString)?.Value)
                            : new MultipartPart(key, Value: (part.Find("value") as JsonString)?.Value));
                    }
                }

                return new BodySpec { Kind = BodyKind.Multipart, Parts = parts };
            }

            case "file":
                return new BodySpec
                {
                    Kind = BodyKind.BinaryFile,
                    FilePath = ((body.Find("file") as JsonObject)?.Find("src") as JsonString)?.Value,
                };

            default:
                return BodySpec.None;
        }
    }

    /// <summary>
    /// Reads the auth scheme but never a credential: Postman exports frequently contain live
    /// secrets, and copying one into a DevTools file would defeat the credential store (FR-A30).
    /// </summary>
    private static AuthSpec ReadAuth(JsonObject? auth)
    {
        if (auth is null)
        {
            return AuthSpec.None;
        }

        var type = (auth.Find("type") as JsonString)?.Value;

        static string? Field(JsonObject? auth, string section, string key)
        {
            if (auth?.Find(section) is not JsonArray array)
            {
                return null;
            }

            foreach (var entry in array.Items.OfType<JsonObject>())
            {
                if ((entry.Find("key") as JsonString)?.Value == key)
                {
                    return (entry.Find("value") as JsonString)?.Value;
                }
            }

            return null;
        }

        return type switch
        {
            "basic" => new AuthSpec { Kind = AuthKind.Basic, Username = Field(auth, "basic", "username") },
            "bearer" => new AuthSpec { Kind = AuthKind.Bearer },
            "apikey" => new AuthSpec
            {
                Kind = AuthKind.ApiKey,
                ApiKeyName = Field(auth, "apikey", "key"),
                ApiKeyIn = Field(auth, "apikey", "in") == "query" ? ApiKeyLocation.Query : ApiKeyLocation.Header,
            },
            "oauth2" => new AuthSpec
            {
                Kind = AuthKind.OAuth2ClientCredentials,
                TokenUrl = Field(auth, "oauth2", "accessTokenUrl"),
                ClientId = Field(auth, "oauth2", "clientId"),
                Scope = Field(auth, "oauth2", "scope"),
            },
            _ => AuthSpec.None,
        };
    }
}
