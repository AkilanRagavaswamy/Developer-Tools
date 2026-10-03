using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace DevTools.Profiler.Agent;

/// <summary>
/// The agent's end of the pipe back to DevTools.
/// </summary>
/// <remarks>
/// Everything here is best-effort and must never disturb the host application. Capturing is a
/// side activity: if the pipe is not there, breaks, or falls behind, the agent drops what it
/// cannot send and the app runs on untouched. One background thread owns the pipe so the app's
/// request threads only ever hand off a buffer and return.
/// </remarks>
internal sealed class ExchangeChannel : IDisposable
{
    // Bounded, so a flood of calls can never grow memory without limit inside the host app.
    private const int MaxQueued = 2048;

    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly BlockingCollection<byte[]> _queue = new(MaxQueued);
    private readonly NamedPipeClientStream _pipe;
    private readonly Thread _worker;
    private volatile bool _faulted;

    private ExchangeChannel(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _worker = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = "DevTools.Profiler.ExchangeChannel",
        };
        _worker.Start();
    }

    /// <summary>
    /// Connects to the DevTools pipe, or returns <see langword="null"/> when it cannot — in which
    /// case the agent simply does nothing, which is the right outcome for a tool that must not
    /// break the app it is watching.
    /// </summary>
    public static ExchangeChannel? TryConnect(string pipeName, int timeoutMs = 5000)
    {
        try
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            pipe.Connect(timeoutMs);
            return new ExchangeChannel(pipe);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Queues one exchange. Returns at once; the send happens on the worker thread.</summary>
    public void Send(WireExchange exchange)
    {
        if (_faulted)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(exchange, SerializerOptions);

            // Drop rather than block the app's thread when the consumer has fallen behind.
            _queue.TryAdd(json);
        }
        catch (Exception)
        {
            // A single exchange that will not serialise is not worth a word to anyone.
        }
    }

    private void PumpLoop()
    {
        try
        {
            foreach (var payload in _queue.GetConsumingEnumerable())
            {
                var header = new byte[4];
                header[0] = (byte)payload.Length;
                header[1] = (byte)(payload.Length >> 8);
                header[2] = (byte)(payload.Length >> 16);
                header[3] = (byte)(payload.Length >> 24);

                _pipe.Write(header, 0, header.Length);
                _pipe.Write(payload, 0, payload.Length);
                _pipe.Flush();
            }
        }
        catch (Exception)
        {
            // The far end went away. Stop trying; the host app carries on regardless.
            _faulted = true;
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();

        try
        {
            _worker.Join(TimeSpan.FromMilliseconds(500));
        }
        catch (Exception)
        {
            // Nothing useful to do while the process is tearing down.
        }

        _pipe.Dispose();
        _queue.Dispose();
    }
}
