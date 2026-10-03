using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using HarmonyLib;

namespace DevTools.Profiler.Agent;

/// <summary>
/// The one patch the agent installs: a postfix on <see cref="HttpClient.SendAsync(HttpRequestMessage, HttpCompletionOption, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// On modern .NET every higher-level call — <c>GetAsync</c>, <c>PostAsync</c>, <c>WebClient</c>,
/// <c>HttpWebRequest</c> — funnels through this overload, so one patch sees them all. The postfix
/// wraps the returned task in a continuation that reads the exchange once it completes and then
/// hands back the <em>same</em> response, so the host application sees no difference. Bodies are
/// buffered (which leaves them re-readable by the app) only within a size cap and only where
/// doing so will not disturb a large streaming download.
/// </remarks>
internal static class HttpClientPatch
{
    /// <summary>Bodies larger than this are recorded as present but not captured.</summary>
    private const long MaxBodyBytes = 16L * 1024 * 1024;

    public static void Apply(Harmony harmony)
    {
        var target = typeof(HttpClient).GetMethod(
            nameof(HttpClient.SendAsync),
            [typeof(HttpRequestMessage), typeof(HttpCompletionOption), typeof(CancellationToken)]);

        if (target is null)
        {
            return;
        }

        var postfix = typeof(HttpClientPatch).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic);
        harmony.Patch(target, postfix: new HarmonyMethod(postfix));
    }

    private static void Postfix(HttpRequestMessage request, ref Task<HttpResponseMessage> __result)
    {
        // Snapshot the request now, synchronously: by the time the task completes the content may
        // have been consumed and the message disposed.
        var wire = new WireExchange
        {
            StartedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Method = request.Method.Method,
            Url = request.RequestUri?.ToString() ?? string.Empty,
            Host = request.RequestUri?.Host ?? string.Empty,
            PathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty,
            HttpVersion = request.Version.ToString(),
            IsSecure = string.Equals(request.RequestUri?.Scheme, "https", StringComparison.OrdinalIgnoreCase),
            RequestHeaders = Collect(request.Headers, request.Content?.Headers),
        };

        var startedAt = Stopwatch.GetTimestamp();
        __result = Wrap(wire, request, startedAt, __result);
    }

    private static async Task<HttpResponseMessage> Wrap(
        WireExchange wire,
        HttpRequestMessage request,
        long startedAt,
        Task<HttpResponseMessage> inner)
    {
        try
        {
            var response = await inner.ConfigureAwait(false);

            wire.DurationMs = Elapsed(startedAt);
            wire.StatusCode = (int)response.StatusCode;
            wire.ReasonPhrase = response.ReasonPhrase ?? string.Empty;
            wire.ResponseHeaders = Collect(response.Headers, response.Content?.Headers);
            wire.ResponseMediaType = response.Content?.Headers.ContentType?.MediaType;

            wire.RequestBody = await TryReadRequestBodyAsync(request.Content).ConfigureAwait(false);
            wire.ResponseBody = await TryReadResponseBodyAsync(response.Content).ConfigureAwait(false);

            AgentBootstrap.Report(wire);
            return response;
        }
        catch (Exception ex)
        {
            wire.DurationMs = Elapsed(startedAt);
            wire.Failed = true;
            wire.Error = ex.Message;
            AgentBootstrap.Report(wire);
            throw;
        }
    }

    private static double Elapsed(long startedAt) =>
        (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency;

    private static WireHeader[] Collect(HttpHeaders headers, HttpHeaders? contentHeaders)
    {
        var list = new List<WireHeader>();

        foreach (var header in headers)
        {
            list.Add(new WireHeader { Name = header.Key, Value = string.Join(", ", header.Value) });
        }

        if (contentHeaders is not null)
        {
            foreach (var header in contentHeaders)
            {
                list.Add(new WireHeader { Name = header.Key, Value = string.Join(", ", header.Value) });
            }
        }

        return [.. list];
    }

    /// <summary>
    /// Reads the request body after the send has completed, when the handler is no longer touching
    /// it. Returns an empty array for anything that cannot be read back without side effects.
    /// </summary>
    private static async Task<byte[]> TryReadRequestBodyAsync(HttpContent? content)
    {
        if (content is null)
        {
            return [];
        }

        try
        {
            var length = content.Headers.ContentLength;
            if (length is > MaxBodyBytes)
            {
                return [];
            }

            return await content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A one-shot stream body already consumed by the handler; nothing to recover.
            return [];
        }
    }

    /// <summary>
    /// Buffers the response body so it stays readable by the application, within a size cap, and
    /// only where buffering will not interfere with a large streaming download.
    /// </summary>
    private static async Task<byte[]> TryReadResponseBodyAsync(HttpContent? content)
    {
        if (content is null)
        {
            return [];
        }

        try
        {
            var length = content.Headers.ContentLength;

            if (length is { } known)
            {
                if (known > MaxBodyBytes)
                {
                    return [];
                }

                await content.LoadIntoBufferAsync().ConfigureAwait(false);
                return await content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }

            // Unknown length (for example a chunked response). Buffer only textual payloads, which
            // are the ones a profiler is for; a large binary stream is left to flow untouched.
            if (!IsTextual(content.Headers.ContentType?.MediaType))
            {
                return [];
            }

            await content.LoadIntoBufferAsync(MaxBodyBytes).ConfigureAwait(false);
            return await content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Too large for the cap, or not re-readable. The call is still recorded without a body.
            return [];
        }
    }

    private static bool IsTextual(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType))
        {
            return false;
        }

        return mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("text", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    }
}
