using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace DevTools.Http.Capture;

/// <summary>A process the user can pick from the capture bar.</summary>
public sealed record ProcessEntry(int Id, string Name, bool HasNetworkActivity)
{
    public string Display => $"{Name} ({Id})";
}

/// <summary>
/// Answers "which process opened this connection?" from the Windows TCP table.
/// </summary>
/// <remarks>
/// This exists because a Windows proxy setting is per user, not per process: nothing can point
/// one application at a proxy and leave the rest of the machine alone. Everything arrives at
/// the capture proxy together, and the only way to tell the chosen app's calls from the rest is
/// to look up the client port in the same table Task Manager and Fiddler read. The answer is a
/// snapshot, so it has to be taken while the connection is still open — which is why the proxy
/// resolves it the moment it accepts, not when the exchange finishes.
/// </remarks>
public static class ProcessResolver
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidConnections = 4;

    /// <summary>Every process with a window or a name, the ones with open TCP connections first.</summary>
    public static IReadOnlyList<ProcessEntry> ListProcesses()
    {
        var active = new HashSet<int>(OwningProcessIds());
        var entries = new List<ProcessEntry>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                entries.Add(new ProcessEntry(process.Id, process.ProcessName, active.Contains(process.Id)));
            }
            catch (Exception)
            {
                // A process can exit between the enumeration and the read; skip it.
            }
            finally
            {
                process.Dispose();
            }
        }

        // Processes that are actually talking to the network first: that is nearly always the
        // one you came here to watch, and it saves scrolling past sixty background services.
        return [.. entries
            .OrderByDescending(e => e.HasNetworkActivity)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id)];
    }

    /// <summary>The process that owns the local endpoint of a connection, or 0 when unknown.</summary>
    public static int OwnerOf(IPEndPoint localEndPoint)
    {
        foreach (var row in ReadTable())
        {
            if (row.LocalPort == localEndPoint.Port)
            {
                return row.ProcessId;
            }
        }

        return 0;
    }

    /// <summary>The name of a process id, or an empty string once it has exited.</summary>
    public static string NameOf(int processId)
    {
        if (processId <= 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static IEnumerable<int> OwningProcessIds() => ReadTable().Select(r => r.ProcessId).Distinct();

    private static List<TcpRow> ReadTable()
    {
        var rows = new List<TcpRow>();
        var size = 0;

        // The first call is expected to fail with the required buffer size.
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidConnections, 0);

        if (size <= 0)
        {
            return rows;
        }

        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidConnections, 0) != 0)
            {
                return rows;
            }

            var count = Marshal.ReadInt32(buffer);
            var cursor = buffer + sizeof(int);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();

            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(cursor);
                rows.Add(new TcpRow(PortFromNetworkOrder(row.LocalPort), (int)row.OwningPid));
                cursor += rowSize;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return rows;
    }

    /// <summary>The table stores the port in network byte order inside a 32-bit field.</summary>
    private static int PortFromNetworkOrder(uint value) =>
        (int)(((value & 0xFF) << 8) | ((value & 0xFF00) >> 8));

    private readonly record struct TcpRow(int LocalPort, int ProcessId);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        bool order,
        int addressFamily,
        int tableClass,
        int reserved);
}
