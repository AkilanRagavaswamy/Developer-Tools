using DevTools.Http.Interop;
using DevTools.Http.Model;
using Xunit;

namespace DevTools.Http.Tests;

public sealed class CurlCodecTests
{
    private static RequestDefinition Import(string command)
    {
        var result = CurlCodec.FromCurl(command);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        return result.Value!;
    }

    // ---- tokenizing ------------------------------------------------------------------

    [Fact]
    public void Single_and_double_quotes_hold_a_token_together()
    {
        Assert.Equal(["curl", "a b", "c d"], CurlCodec.Tokenize("""curl 'a b' "c d" """));
    }

    [Fact]
    public void Line_continuations_are_joined()
    {
        Assert.Equal(["curl", "-X", "POST", "https://x/"], CurlCodec.Tokenize("curl \\\n  -X POST \\\n  https://x/"));
    }

    [Fact]
    public void An_embedded_quote_survives_escaping()
    {
        Assert.Equal(["a'b"], CurlCodec.Tokenize("""'a'\''b'"""));
    }

    // Chrome emits $'…' when a header value contains a newline.
    [Fact]
    public void Ansi_c_quoting_decodes_escapes()
    {
        Assert.Equal(["a\nb"], CurlCodec.Tokenize("""$'a\nb'"""));
    }

    // ---- importing -------------------------------------------------------------------

    [Fact]
    public void A_bare_url_becomes_a_get()
    {
        var request = Import("curl https://api.example.com/things");

        Assert.Equal("GET", request.Method);
        Assert.Equal("https://api.example.com/things", request.Url);
    }

    [Fact]
    public void The_query_string_is_split_into_rows()
    {
        var request = Import("curl 'https://api.example.com/search?q=hello%20world&page=2'");

        Assert.Equal("https://api.example.com/search", request.Url);
        Assert.Equal(2, request.Query.Count);
        Assert.Equal("hello world", request.Query[0].Value);
    }

    [Fact]
    public void Headers_are_imported()
    {
        var request = Import("""curl https://x/ -H 'Accept: application/json' -H 'X-Trace: 1'""");

        Assert.Equal(2, request.Headers.Count);
        Assert.Equal("Accept", request.Headers[0].Name);
        Assert.Equal("application/json", request.Headers[0].Value);
    }

    // curl infers POST from the presence of a body, and so does anyone reading the command.
    [Fact]
    public void A_data_flag_implies_post()
    {
        var request = Import("""curl https://x/ --data '{"a":1}'""");

        Assert.Equal("POST", request.Method);
        Assert.Equal(BodyKind.Json, request.Body.Kind);
        Assert.Equal("""{"a":1}""", request.Body.Text);
    }

    [Fact]
    public void An_explicit_method_wins_over_the_inferred_one()
    {
        Assert.Equal("PUT", Import("""curl -X PUT https://x/ -d 'body'""").Method);
    }

    [Fact]
    public void A_json_content_type_header_makes_the_body_json_even_when_it_is_not_valid()
    {
        var request = Import("""curl https://x/ -H 'Content-Type: application/json' -d 'not json'""");
        Assert.Equal(BodyKind.Json, request.Body.Kind);
    }

    [Fact]
    public void A_form_content_type_splits_the_body_into_fields()
    {
        var request = Import("""curl https://x/ -H 'Content-Type: application/x-www-form-urlencoded' -d 'a=1&b=two%20words'""");

        Assert.Equal(BodyKind.FormUrlEncoded, request.Body.Kind);
        Assert.Equal(2, request.Body.Form.Count);
        Assert.Equal("two words", request.Body.Form[1].Value);
    }

    [Fact]
    public void Data_urlencode_flags_become_form_fields()
    {
        var request = Import("""curl https://x/ --data-urlencode 'a=1' --data-urlencode 'b=2'""");

        Assert.Equal(BodyKind.FormUrlEncoded, request.Body.Kind);
        Assert.Equal(2, request.Body.Form.Count);
    }

    [Fact]
    public void Form_flags_become_multipart_parts_including_files()
    {
        var request = Import("""curl https://x/ -F 'name=value' -F 'file=@/tmp/a.png'""");

        Assert.Equal(BodyKind.Multipart, request.Body.Kind);
        Assert.Equal(2, request.Body.Parts.Count);
        Assert.Equal("value", request.Body.Parts[0].Value);
        Assert.Equal("/tmp/a.png", request.Body.Parts[1].FilePath);
    }

    [Fact]
    public void A_binary_file_body_is_recognised()
    {
        var request = Import("""curl -X PUT https://x/ --data-binary '@/tmp/blob.bin'""");

        Assert.Equal(BodyKind.BinaryFile, request.Body.Kind);
        Assert.Equal("/tmp/blob.bin", request.Body.FilePath);
    }

    [Fact]
    public void Basic_auth_is_recognised_but_the_password_is_not_stored()
    {
        var request = Import("""curl https://x/ -u 'ada:hunter2'""");

        Assert.Equal(AuthKind.Basic, request.Auth.Kind);
        Assert.Equal("ada", request.Auth.Username);
        Assert.Null(request.Auth.SecretRef);
    }

    [Fact]
    public void A_bearer_header_becomes_the_bearer_scheme_without_keeping_the_token()
    {
        var request = Import("""curl https://x/ -H 'Authorization: Bearer super-secret-token'""");

        Assert.Equal(AuthKind.Bearer, request.Auth.Kind);
        Assert.DoesNotContain(request.Headers, static h => h.Name == "Authorization");
        Assert.Null(request.Auth.SecretRef);
    }

    [Fact]
    public void Transport_flags_are_imported()
    {
        var request = Import("curl -L -k -m 30 https://x/");

        Assert.True(request.Options.FollowRedirects);
        Assert.True(request.Options.IgnoreCertificateErrors);
        Assert.Equal(30, request.Options.TimeoutSeconds);
    }

    [Fact]
    public void Flags_that_take_an_argument_do_not_swallow_the_url()
    {
        Assert.Equal("https://x/wanted", Import("curl -o out.txt -A 'agent' https://x/wanted").Url);
    }

    [Fact]
    public void A_command_with_no_url_is_reported()
    {
        var result = CurlCodec.FromCurl("curl -X POST -H 'a: b'");
        Assert.False(result.IsSuccess);
        Assert.Contains("no URL", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_input_is_reported()
    {
        Assert.False(CurlCodec.FromCurl("").IsSuccess);
        Assert.False(CurlCodec.FromCurl(null).IsSuccess);
    }

    // ---- exporting and round-tripping ---------------------------------------------------

    [Fact]
    public void Export_produces_a_command_that_imports_back_to_the_same_request()
    {
        var original = new RequestDefinition
        {
            Method = "POST",
            Url = "https://api.example.com/things",
            Query = [new KeyValueItem("page", "2")],
            Headers = [new KeyValueItem("Accept", "application/json")],
            Body = BodySpec.FromJson("""{"name":"Ada"}"""),
        };

        var round = Import(CurlCodec.ToCurl(original));

        Assert.Equal("POST", round.Method);
        Assert.Equal("https://api.example.com/things", round.Url);
        Assert.Equal("2", round.Query.Single(static q => q.Name == "page").Value);
        Assert.Contains(round.Headers, static h => h.Name == "Accept");
        Assert.Equal(BodyKind.Json, round.Body.Kind);
        Assert.Equal("""{"name":"Ada"}""", round.Body.Text);
    }

    [Fact]
    public void Export_never_writes_out_a_secret()
    {
        var request = RequestDefinition.Get("https://x/") with
        {
            Auth = new AuthSpec { Kind = AuthKind.Basic, Username = "ada", SecretRef = "vault-key" },
        };

        var command = CurlCodec.ToCurl(request);

        Assert.Contains("ada:$PASSWORD", command, StringComparison.Ordinal);
        Assert.DoesNotContain("vault-key", command, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_escapes_a_quote_inside_a_value()
    {
        var command = CurlCodec.ToCurl(
            new RequestDefinition { Method = "POST", Url = "https://x/", Body = new BodySpec { Kind = BodyKind.Raw, Text = "it's" } });

        Assert.Equal("it's", Import(command).Body.Text);
    }
}

public sealed class OpenApiImporterTests
{
    private const string Spec = """
        {
          "openapi": "3.0.3",
          "info": { "title": "Orders API", "description": "Test spec" },
          "servers": [ { "url": "https://api.example.com/v1" } ],
          "paths": {
            "/orders": {
              "get": {
                "summary": "List orders",
                "tags": ["Orders"],
                "parameters": [
                  { "name": "page", "in": "query", "required": true, "schema": { "type": "integer", "example": 1 } },
                  { "name": "X-Tenant", "in": "header", "schema": { "type": "string" } }
                ]
              },
              "post": {
                "summary": "Create an order",
                "tags": ["Orders"],
                "requestBody": {
                  "content": {
                    "application/json": {
                      "schema": { "$ref": "#/components/schemas/Order" }
                    }
                  }
                }
              }
            },
            "/orders/{orderId}": {
              "delete": { "summary": "Delete an order", "tags": ["Orders"],
                "parameters": [ { "name": "orderId", "in": "path", "required": true, "schema": { "type": "string" } } ] }
            },
            "/health": { "get": { "summary": "Health" } }
          },
          "components": {
            "schemas": {
              "Order": {
                "type": "object",
                "properties": {
                  "id": { "type": "string", "format": "uuid" },
                  "total": { "type": "number" },
                  "placedAt": { "type": "string", "format": "date-time" },
                  "lines": { "type": "array", "items": { "$ref": "#/components/schemas/Line" } }
                }
              },
              "Line": {
                "type": "object",
                "properties": { "sku": { "type": "string" }, "qty": { "type": "integer" } }
              }
            }
          }
        }
        """;

    private static Workspace.RequestCollection Import(string json)
    {
        var result = OpenApiImporter.Import(json);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        return result.Value!;
    }

    [Fact]
    public void The_collection_takes_its_name_from_the_spec()
    {
        var collection = Import(Spec);

        Assert.Equal("Orders API", collection.Name);
        Assert.Equal("Test spec", collection.Description);
    }

    [Fact]
    public void The_first_server_becomes_a_base_url_variable()
    {
        var collection = Import(Spec);

        var baseUrl = Assert.Single(collection.Variables, static v => v.Name == "baseUrl");
        Assert.Equal("https://api.example.com/v1", baseUrl.Value);

        Assert.All(collection.AllRequests(), static r =>
            Assert.StartsWith("{{baseUrl}}", r.Url, StringComparison.Ordinal));
    }

    [Fact]
    public void Operations_are_grouped_by_their_first_tag()
    {
        var collection = Import(Spec);

        var orders = Assert.Single(collection.Folders, static f => f.Name == "Orders");
        Assert.Equal(3, orders.Requests.Count);

        // The untagged /health operation stays at the top level.
        Assert.Single(collection.Requests);
    }

    [Fact]
    public void Query_and_header_parameters_become_rows_with_their_examples()
    {
        var list = Import(Spec).AllRequests().Single(static r => r.Name == "List orders");

        var page = Assert.Single(list.Query);
        Assert.Equal("page", page.Name);
        Assert.Equal("1", page.Value);
        Assert.True(page.Enabled);

        Assert.Single(list.Headers, static h => h.Name == "X-Tenant");
    }

    [Fact]
    public void Path_parameters_become_variables_in_the_url()
    {
        var delete = Import(Spec).AllRequests().Single(static r => r.Name == "Delete an order");

        Assert.Equal("{{baseUrl}}/orders/{{orderId}}", delete.Url);
        Assert.Equal("DELETE", delete.Method);
    }

    [Fact]
    public void A_request_body_schema_becomes_a_json_skeleton_with_refs_resolved()
    {
        var create = Import(Spec).AllRequests().Single(static r => r.Name == "Create an order");

        Assert.Equal(BodyKind.Json, create.Body.Kind);

        var body = create.Body.Text!;
        Assert.Contains("\"id\"", body, StringComparison.Ordinal);
        Assert.Contains("00000000-0000-0000-0000-000000000000", body, StringComparison.Ordinal);
        Assert.Contains("2026-01-01T00:00:00Z", body, StringComparison.Ordinal);

        // The nested $ref must be followed, not left as a literal.
        Assert.Contains("\"sku\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("$ref", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_swagger_2_document_is_refused_with_advice()
    {
        var result = OpenApiImporter.Import("""{"swagger":"2.0","info":{"title":"x"},"paths":{}}""");

        Assert.False(result.IsSuccess);
        Assert.Contains("Swagger 2.0", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_that_is_not_openapi_is_refused()
    {
        Assert.False(OpenApiImporter.Import("""{"hello":"world"}""").IsSuccess);
        Assert.False(OpenApiImporter.Import("not json").IsSuccess);
        Assert.False(OpenApiImporter.Import("").IsSuccess);
    }

    [Fact]
    public void A_spec_with_no_operations_is_refused()
    {
        Assert.False(OpenApiImporter.Import("""{"openapi":"3.0.0","info":{"title":"x"},"paths":{}}""").IsSuccess);
    }
}

public sealed class PostmanImporterTests
{
    private const string Collection = """
        {
          "info": {
            "name": "Sample",
            "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json",
            "description": "From Postman"
          },
          "variable": [
            { "key": "host", "value": "https://api.example.com" },
            { "key": "token", "value": "live-secret", "type": "secret" }
          ],
          "item": [
            {
              "name": "Things",
              "item": [
                {
                  "name": "List things",
                  "request": {
                    "method": "GET",
                    "url": { "raw": "{{host}}/things?page=1" },
                    "header": [
                      { "key": "Accept", "value": "application/json" },
                      { "key": "X-Off", "value": "no", "disabled": true }
                    ]
                  }
                },
                {
                  "name": "Create thing",
                  "request": {
                    "method": "POST",
                    "url": { "protocol": "https", "host": ["api","example","com"], "path": ["things"] },
                    "body": { "mode": "raw", "raw": "{\"name\":\"x\"}", "options": { "raw": { "language": "json" } } },
                    "auth": {
                      "type": "bearer",
                      "bearer": [ { "key": "token", "value": "another-live-secret" } ]
                    }
                  }
                }
              ]
            },
            {
              "name": "Upload",
              "request": {
                "method": "POST",
                "url": "https://api.example.com/upload",
                "body": {
                  "mode": "formdata",
                  "formdata": [
                    { "key": "field", "value": "v", "type": "text" },
                    { "key": "file", "src": "/tmp/a.png", "type": "file" }
                  ]
                }
              }
            }
          ]
        }
        """;

    private static Workspace.RequestCollection Import(string json)
    {
        var result = PostmanImporter.Import(json);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        return result.Value!;
    }

    [Fact]
    public void Folders_and_requests_keep_their_structure()
    {
        var collection = Import(Collection);

        Assert.Equal("Sample", collection.Name);

        var things = Assert.Single(collection.Folders, static f => f.Name == "Things");
        Assert.Equal(2, things.Requests.Count);

        Assert.Single(collection.Requests, static r => r.Name == "Upload");
        Assert.Equal(3, collection.AllRequests().Count());
    }

    [Fact]
    public void A_raw_url_is_split_into_a_url_and_query_rows()
    {
        var list = Import(Collection).AllRequests().Single(static r => r.Name == "List things");

        Assert.Equal("{{host}}/things", list.Url);
        Assert.Equal("page", list.Query.Single().Name);
    }

    [Fact]
    public void A_structured_url_is_rebuilt_when_there_is_no_raw_form()
    {
        var create = Import(Collection).AllRequests().Single(static r => r.Name == "Create thing");
        Assert.Equal("https://api.example.com/things", create.Url);
    }

    [Fact]
    public void Disabled_headers_are_imported_as_disabled_rather_than_dropped()
    {
        var list = Import(Collection).AllRequests().Single(static r => r.Name == "List things");

        Assert.Equal(2, list.Headers.Count);
        Assert.False(list.Headers.Single(static h => h.Name == "X-Off").Enabled);
    }

    [Fact]
    public void A_raw_json_body_is_recognised()
    {
        var create = Import(Collection).AllRequests().Single(static r => r.Name == "Create thing");

        Assert.Equal(BodyKind.Json, create.Body.Kind);
        Assert.Equal("""{"name":"x"}""", create.Body.Text);
    }

    [Fact]
    public void Form_data_becomes_multipart_parts()
    {
        var upload = Import(Collection).AllRequests().Single(static r => r.Name == "Upload");

        Assert.Equal(BodyKind.Multipart, upload.Body.Kind);
        Assert.Equal("v", upload.Body.Parts[0].Value);
        Assert.Equal("/tmp/a.png", upload.Body.Parts[1].FilePath);
    }

    /// <summary>
    /// Postman exports routinely contain live credentials. Copying one into a DevTools file
    /// would put a secret on disk in clear text and defeat the credential store (FR-A30).
    /// </summary>
    [Fact]
    public void No_secret_from_the_export_is_carried_into_the_collection()
    {
        var collection = Import(Collection);
        var serialised = Workspace.WorkspaceStore.ExportCollection(collection, includeSecrets: true);

        Assert.True(serialised.IsSuccess);
        Assert.DoesNotContain("live-secret", serialised.Value!, StringComparison.Ordinal);
        Assert.DoesNotContain("another-live-secret", serialised.Value, StringComparison.Ordinal);

        var create = collection.AllRequests().Single(static r => r.Name == "Create thing");
        Assert.Equal(AuthKind.Bearer, create.Auth.Kind);
        Assert.Null(create.Auth.SecretRef);
    }

    [Fact]
    public void A_secret_variable_is_imported_without_its_value()
    {
        var token = Import(Collection).Variables.Single(static v => v.Name == "token");

        Assert.True(token.Secret);
        Assert.Empty(token.Value);
    }

    [Fact]
    public void A_v1_collection_is_refused_with_advice()
    {
        var result = PostmanImporter.Import("""{"info":{"name":"x","schema":"https://schema.getpostman.com/json/collection/v1.0.0/collection.json"},"item":[]}""");

        Assert.False(result.IsSuccess);
        Assert.Contains("v2.1", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_a_postman_collection_is_refused()
    {
        Assert.False(PostmanImporter.Import("""{"hello":"world"}""").IsSuccess);
        Assert.False(PostmanImporter.Import("not json").IsSuccess);
        Assert.False(PostmanImporter.Import("").IsSuccess);
    }
}
