using System.Net.Sockets;
using System.Security.Authentication;

namespace DevTools.Http.Execution;

/// <summary>
/// Turns a transport exception into a sentence a developer can act on (edge case 19).
/// </summary>
/// <remarks>
/// Separate from <see cref="HttpExecutor"/> so the mapping can be tested directly. Asserting it
/// by actually failing to reach a host would make the suite depend on the machine's DNS and
/// firewall — and plenty of networks hijack a non-existent name rather than returning NXDOMAIN,
/// which would make such a test pass or fail for reasons that have nothing to do with the code.
/// </remarks>
public static class TransportDiagnostics
{
    public static string Describe(Exception exception, string? url)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url ?? "the server";

        for (Exception? inner = exception; inner is not null; inner = inner.InnerException)
        {
            switch (inner)
            {
                case SocketException socket:
                    return Describe(socket.SocketErrorCode, host);

                case AuthenticationException auth:
                    return $"The TLS handshake with \"{host}\" failed: {auth.Message} " +
                           "If this is a development server with a self-signed certificate, turn on " +
                           "\"Ignore certificate errors\" for this request.";
            }
        }

        return $"The request to \"{host}\" failed: {exception.Message}";
    }

    public static string Describe(SocketError error, string host) => error switch
    {
        SocketError.HostNotFound or SocketError.NoData =>
            $"The host \"{host}\" could not be resolved.",

        SocketError.ConnectionRefused =>
            $"\"{host}\" refused the connection. Is the port right, and is the server running?",

        SocketError.TimedOut =>
            $"Connecting to \"{host}\" timed out.",

        SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
            $"\"{host}\" is unreachable from this machine.",

        SocketError.ConnectionReset =>
            $"\"{host}\" closed the connection unexpectedly.",

        SocketError.AccessDenied =>
            $"The connection to \"{host}\" was blocked — check a firewall or proxy policy.",

        _ => $"The connection to \"{host}\" failed: {error}.",
    };

    /// <summary>Classifies a failure, for statistics and for choosing an icon.</summary>
    public static Model.TransportFailure Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? inner = exception; inner is not null; inner = inner.InnerException)
        {
            switch (inner)
            {
                case SocketException socket:
                    return socket.SocketErrorCode switch
                    {
                        SocketError.HostNotFound or SocketError.NoData => Model.TransportFailure.DnsFailure,
                        SocketError.ConnectionRefused => Model.TransportFailure.ConnectionRefused,
                        SocketError.TimedOut => Model.TransportFailure.Timeout,
                        _ => Model.TransportFailure.Other,
                    };

                case AuthenticationException:
                    return Model.TransportFailure.TlsFailure;

                case OperationCanceledException:
                    return Model.TransportFailure.Cancelled;
            }
        }

        return Model.TransportFailure.Other;
    }
}
