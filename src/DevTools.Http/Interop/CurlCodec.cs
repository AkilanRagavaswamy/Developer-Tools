using System.Text;
using DevTools.Core;
using DevTools.Http.Execution;
using DevTools.Http.Model;

namespace DevTools.Http.Interop;

/// <summary>
/// Converts between a request and a <c>curl</c> command line, in both directions (FR-A29).
/// </summary>
/// <remarks>
/// Import is the half that matters: "copy as cURL" is in every browser's network panel, and
/// pasting that string is how most requests get into a client in the first place. The parser
/// therefore accepts the messy reality of those strings — line continuations, single or double
/// quotes, <c>$'…'</c>, and the long and short spelling of every flag.
/// </remarks>
public static class CurlCodec
{
    // ---- export -------------------------------------------------------------------------

    public static string ToCurl(RequestDefinition request, bool multiline = true)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parts = new List<string> { "curl" };
        var url = RequestFactory.BuildUrl(request);

        if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"-X {request.Method.ToUpperInvariant()}");
        }

        parts.Add(Quote(url.IsSuccess ? url.Value!.ToString() : request.Url));

        foreach (var header in request.Headers.Where(static h => h.Enabled && !string.IsNullOrWhiteSpace(h.Name)))
        {
            parts.Add($"-H {Quote($"{header.Name}: {header.Value}")}");
        }

        switch (request.Auth.Kind)
        {
            case AuthKind.Basic when !string.IsNullOrEmpty(request.Auth.Username):
                // The password is deliberately not written out: it lives in the credential store.
                parts.Add($"-u {Quote($"{request.Auth.Username}:$PASSWORD")}");
                break;

            case AuthKind.Bearer:
                parts.Add($"-H {Quote("Authorization: Bearer $TOKEN")}");
                break;

            case AuthKind.ApiKey when !string.IsNullOrWhiteSpace(request.Auth.ApiKeyName):
                if (request.Auth.ApiKeyIn == ApiKeyLocation.Header)
                {
                    parts.Add($"-H {Quote($"{request.Auth.ApiKeyName}: $API_KEY")}");
                }

                break;
        }

        switch (request.Body.Kind)
        {
            case BodyKind.Json:
                parts.Add($"-H {Quote("Content-Type: application/json")}");
                parts.Add($"--data {Quote(request.Body.Text ?? string.Empty)}");
                break;

            case BodyKind.Raw:
                if (!string.IsNullOrWhiteSpace(request.Body.ContentType))
                {
                    parts.Add($"-H {Quote($"Content-Type: {request.Body.ContentType}")}");
                }

                parts.Add($"--data {Quote(request.Body.Text ?? string.Empty)}");
                break;

            case BodyKind.FormUrlEncoded:
                foreach (var field in request.Body.Form.Where(static f => f.Enabled))
                {
                    parts.Add($"--data-urlencode {Quote($"{field.Name}={field.Value}")}");
                }

                break;

            case BodyKind.Multipart:
                foreach (var part in request.Body.Parts.Where(static p => p.Enabled))
                {
                    parts.Add(string.IsNullOrWhiteSpace(part.FilePath)
                        ? $"-F {Quote($"{part.Name}={part.Value}")}"
                        : $"-F {Quote($"{part.Name}=@{part.FilePath}")}");
                }

                break;

            case BodyKind.BinaryFile when !string.IsNullOrWhiteSpace(request.Body.FilePath):
                parts.Add($"--data-binary {Quote("@" + request.Body.FilePath)}");
                break;
        }

        if (!request.Options.FollowRedirects)
        {
            // curl does not follow redirects by default, so the flag is the opposite one.
        }
        else
        {
            parts.Add("-L");
        }

        if (request.Options.IgnoreCertificateErrors)
        {
            parts.Add("-k");
        }

        return multiline
            ? string.Join(" \\\n  ", parts)
            : string.Join(' ', parts);
    }

    private static string Quote(string value)
    {
        // Single quotes are literal in POSIX shells, which is what a copied curl line assumes;
        // an embedded single quote has to leave and re-enter the quoting.
        var escaped = value.Replace("'", "'\\''", StringComparison.Ordinal);
        return $"'{escaped}'";
    }

    // ---- import -------------------------------------------------------------------------

    public static OperationResult<RequestDefinition> FromCurl(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return OperationResult<RequestDefinition>.Fail("There is no command to import.");
        }

        var tokens = Tokenize(command);

        if (tokens.Count == 0)
        {
            return OperationResult<RequestDefinition>.Fail("There is no command to import.");
        }

        if (string.Equals(tokens[0], "curl", StringComparison.OrdinalIgnoreCase))
        {
            tokens.RemoveAt(0);
        }

        string? url = null;
        string? method = null;
        var headers = new List<KeyValueItem>();
        var formFields = new List<FormField>();
        var parts = new List<MultipartPart>();
        var auth = AuthSpec.None;
        var options = RequestOptions.Default;
        string? rawBody = null;
        string? binaryFile = null;
        var urlEncodedData = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            string? Next()
            {
                if (i + 1 >= tokens.Count)
                {
                    return null;
                }

                i++;
                return tokens[i];
            }

            switch (token)
            {
                case "-X" or "--request":
                    method = Next()?.ToUpperInvariant();
                    break;

                case "-H" or "--header":
                {
                    var header = Next();
                    if (header is null)
                    {
                        break;
                    }

                    var colon = header.IndexOf(':', StringComparison.Ordinal);
                    if (colon > 0)
                    {
                        headers.Add(new KeyValueItem(header[..colon].Trim(), header[(colon + 1)..].Trim()));
                    }

                    break;
                }

                case "-d" or "--data" or "--data-raw" or "--data-ascii":
                    rawBody = Next() ?? string.Empty;
                    break;

                case "--data-urlencode":
                {
                    var field = Next();
                    if (field is null)
                    {
                        break;
                    }

                    urlEncodedData = true;
                    var equals = field.IndexOf('=', StringComparison.Ordinal);

                    formFields.Add(equals > 0
                        ? new FormField(field[..equals], field[(equals + 1)..])
                        : new FormField(field, string.Empty));

                    break;
                }

                case "--data-binary":
                {
                    var value = Next();
                    if (value is null)
                    {
                        break;
                    }

                    if (value.StartsWith('@'))
                    {
                        binaryFile = value[1..];
                    }
                    else
                    {
                        rawBody = value;
                    }

                    break;
                }

                case "-F" or "--form":
                {
                    var field = Next();
                    if (field is null)
                    {
                        break;
                    }

                    var equals = field.IndexOf('=', StringComparison.Ordinal);
                    if (equals <= 0)
                    {
                        break;
                    }

                    var name = field[..equals];
                    var value = field[(equals + 1)..];

                    parts.Add(value.StartsWith('@')
                        ? new MultipartPart(name, FilePath: value[1..])
                        : new MultipartPart(name, Value: value));

                    break;
                }

                case "-u" or "--user":
                {
                    var credentials = Next();
                    if (credentials is null)
                    {
                        break;
                    }

                    var colon = credentials.IndexOf(':', StringComparison.Ordinal);

                    auth = new AuthSpec
                    {
                        Kind = AuthKind.Basic,
                        Username = colon > 0 ? credentials[..colon] : credentials,
                    };

                    break;
                }

                case "-L" or "--location":
                    options = options with { FollowRedirects = true };
                    break;

                case "-k" or "--insecure":
                    options = options with { IgnoreCertificateErrors = true };
                    break;

                case "-I" or "--head":
                    method ??= "HEAD";
                    break;

                case "--compressed" or "-s" or "--silent" or "-v" or "--verbose" or "-i" or "--include":
                    break;

                case "-m" or "--max-time":
                {
                    var seconds = Next();
                    if (int.TryParse(seconds, out var value))
                    {
                        options = options with { TimeoutSeconds = value };
                    }

                    break;
                }

                case "-x" or "--proxy":
                    options = options with { ProxyUrl = Next() };
                    break;

                case "--url":
                    url = Next();
                    break;

                default:
                    // Anything that is not a flag, and is not the argument of one, is the URL.
                    if (!token.StartsWith('-') && url is null)
                    {
                        url = token;
                    }
                    else if (token.StartsWith('-') && token.Length > 1)
                    {
                        // An unknown flag with an argument would otherwise swallow the URL.
                        if (token is "-o" or "--output" or "-e" or "--referer" or "-A" or "--user-agent"
                            or "-b" or "--cookie" or "-c" or "--cookie-jar" or "--cert" or "--key")
                        {
                            Next();
                        }
                    }

                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return OperationResult<RequestDefinition>.Fail("The command has no URL.");
        }

        var body = BuildBody(rawBody, binaryFile, formFields, parts, urlEncodedData, headers);

        // curl infers POST from the presence of a body; so does everyone reading the command.
        method ??= body.Kind == BodyKind.None ? "GET" : "POST";

        // Basic credentials given on the command line are recognised but not stored as a value.
        var authHeader = headers.FirstOrDefault(static h =>
            string.Equals(h.Name, "Authorization", StringComparison.OrdinalIgnoreCase));

        if (authHeader is not null && auth.Kind == AuthKind.None &&
            authHeader.Value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            auth = new AuthSpec { Kind = AuthKind.Bearer };
            headers.Remove(authHeader);
        }

        return OperationResult<RequestDefinition>.Ok(new RequestDefinition
        {
            Name = DeriveName(url!),
            Method = method,
            Url = RequestFactory.StripQuery(url),
            Query = RequestFactory.SplitQuery(url),
            Headers = headers,
            Body = body,
            Auth = auth,
            Options = options,
        });
    }

    private static BodySpec BuildBody(
        string? rawBody,
        string? binaryFile,
        List<FormField> formFields,
        List<MultipartPart> parts,
        bool urlEncodedData,
        List<KeyValueItem> headers)
    {
        if (parts.Count > 0)
        {
            return new BodySpec { Kind = BodyKind.Multipart, Parts = parts };
        }

        if (!string.IsNullOrEmpty(binaryFile))
        {
            return new BodySpec { Kind = BodyKind.BinaryFile, FilePath = binaryFile };
        }

        if (urlEncodedData && formFields.Count > 0)
        {
            return new BodySpec { Kind = BodyKind.FormUrlEncoded, Form = formFields };
        }

        if (rawBody is null)
        {
            return BodySpec.None;
        }

        var contentType = headers
            .FirstOrDefault(static h => string.Equals(h.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        // A -d payload that is JSON is treated as JSON whether or not a header said so; that
        // is what the user meant, and it lights up the body editor's validation.
        if (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true ||
            (contentType is null && Core.Json.JsonFormatter.IsValid(rawBody)))
        {
            return new BodySpec { Kind = BodyKind.Json, Text = rawBody };
        }

        if (contentType?.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) == true)
        {
            var fields = rawBody
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(static pair =>
                {
                    var equals = pair.IndexOf('=', StringComparison.Ordinal);
                    return equals > 0
                        ? new FormField(Uri.UnescapeDataString(pair[..equals]), Uri.UnescapeDataString(pair[(equals + 1)..]))
                        : new FormField(Uri.UnescapeDataString(pair), string.Empty);
                })
                .ToList();

            return new BodySpec { Kind = BodyKind.FormUrlEncoded, Form = fields };
        }

        return new BodySpec { Kind = BodyKind.Raw, Text = rawBody, ContentType = contentType };
    }

    private static string DeriveName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "Imported request";
        }

        var segment = uri.Segments.LastOrDefault()?.Trim('/');
        return string.IsNullOrWhiteSpace(segment) ? uri.Host : segment;
    }

    /// <summary>
    /// Splits a shell command line the way a POSIX shell would, which is what a copied curl
    /// command assumes: line continuations, both quoting styles, and <c>$'…'</c>.
    /// </summary>
    internal static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var builder = new StringBuilder();
        var inToken = false;
        var i = 0;

        while (i < command.Length)
        {
            var c = command[i];

            // A backslash before a newline continues the line; anything else it escapes.
            if (c == '\\' && i + 1 < command.Length)
            {
                var next = command[i + 1];

                if (next is '\n' or '\r')
                {
                    i += next == '\r' && i + 2 < command.Length && command[i + 2] == '\n' ? 3 : 2;
                    continue;
                }

                builder.Append(next);
                inToken = true;
                i += 2;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    tokens.Add(builder.ToString());
                    builder.Clear();
                    inToken = false;
                }

                i++;
                continue;
            }

            if (c == '\'')
            {
                inToken = true;
                i++;

                while (i < command.Length && command[i] != '\'')
                {
                    builder.Append(command[i]);
                    i++;
                }

                i++;
                continue;
            }

            if (c == '"')
            {
                inToken = true;
                i++;

                while (i < command.Length && command[i] != '"')
                {
                    if (command[i] == '\\' && i + 1 < command.Length)
                    {
                        builder.Append(command[i + 1]);
                        i += 2;
                        continue;
                    }

                    builder.Append(command[i]);
                    i++;
                }

                i++;
                continue;
            }

            // $'…' — the ANSI-C quoting Chrome emits when a header contains a newline.
            if (c == '$' && i + 1 < command.Length && command[i + 1] == '\'')
            {
                inToken = true;
                i += 2;

                while (i < command.Length && command[i] != '\'')
                {
                    if (command[i] == '\\' && i + 1 < command.Length)
                    {
                        builder.Append(command[i + 1] switch
                        {
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
                            var other => other,
                        });

                        i += 2;
                        continue;
                    }

                    builder.Append(command[i]);
                    i++;
                }

                i++;
                continue;
            }

            builder.Append(c);
            inToken = true;
            i++;
        }

        if (inToken)
        {
            tokens.Add(builder.ToString());
        }

        return tokens;
    }
}
