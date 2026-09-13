using System.Runtime.InteropServices;

namespace PocketModem.Client.Wintun;

/// <summary>
/// A live Wintun adapter and session (DESIGN.md §7.1).
///
/// Owns the driver handles and exposes packet read/write over the ring buffer.
/// Reads block on the driver's wait event rather than spinning, so an idle
/// tunnel costs no CPU.
/// </summary>
public sealed class WintunAdapter : Platform.ITunAdapter
{
    private IntPtr _adapter;
    private IntPtr _session;
    private IntPtr _readEvent;
    private bool _disposed;

    public ulong Luid { get; private set; }
    public string Name { get; }

    private WintunAdapter(IntPtr adapter, string name)
    {
        _adapter = adapter;
        Name = name;
        WintunInterop.GetAdapterLuid(adapter, out var luid);
        Luid = luid;
    }

    /// <summary>
    /// Create (or reopen) the adapter. Requires elevation — creating a network
    /// adapter is a privileged operation (§7.4).
    /// </summary>
    public static WintunAdapter Create(string name, string tunnelType = "PocketModem")
    {
        // A fixed GUID means Windows treats this as the same adapter across
        // runs, so firewall rules and interface settings persist rather than
        // accumulating a new "Unidentified network" each launch.
        var guid = new Guid("f9a3c2d1-4b7e-4a91-9c3d-8e5f1a2b6c74");

        var handle = WintunInterop.CreateAdapter(name, tunnelType, ref guid);
        if (handle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            // Reopening an existing adapter is the normal path after a crash
            // that left one behind.
            handle = WintunInterop.OpenAdapter(name);
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"Could not create or open the Wintun adapter (error {err}). " +
                    "Run as Administrator, and make sure wintun.dll sits next to the exe.");
            }
        }

        return new WintunAdapter(handle, name);
    }

    /// <summary>Windows: the adapter already has a session open.</summary>
    private const int ErrorAlreadyInitialized = 1247;

    public void StartSession(uint capacity = WintunInterop.RingCapacity)
    {
        // Starting twice would leak the first session and fail anyway.
        if (_session != IntPtr.Zero) return;

        // An adapter left by a process that has just exited can still hold a
        // session for a moment after the process is gone, and Create() will
        // happily reopen it in that window - so the adapter is ours while the
        // session is not. Waiting it out beats failing, because the alternative
        // is a reconnect loop that retries into the same window forever.
        for (int attempt = 0; ; attempt++)
        {
            _session = WintunInterop.StartSession(_adapter, capacity);
            if (_session != IntPtr.Zero) break;

            int err = Marshal.GetLastWin32Error();
            if (err != ErrorAlreadyInitialized || attempt >= 10)
            {
                throw new InvalidOperationException(
                    $"WintunStartSession failed (error {err})" +
                    (err == ErrorAlreadyInitialized
                        ? ". Another copy of PocketModem is using the adapter - close it and try again."
                        : "."));
            }

            Thread.Sleep(300);
        }

        _readEvent = WintunInterop.GetReadWaitEvent(_session);
    }

    /// <summary>
    /// Read one outbound IP packet, blocking up to <paramref name="timeoutMs"/>.
    /// Returns null on timeout so the caller can check for cancellation.
    /// </summary>
    public byte[]? ReceivePacket(uint timeoutMs = 250)
    {
        while (true)
        {
            var ptr = WintunInterop.ReceivePacket(_session, out uint size);
            if (ptr != IntPtr.Zero)
            {
                var packet = new byte[size];
                Marshal.Copy(ptr, packet, 0, (int)size);
                WintunInterop.ReleaseReceivePacket(_session, ptr);
                return packet;
            }

            if (Marshal.GetLastWin32Error() != WintunInterop.ErrorNoMoreItems)
                return null;

            // Ring is empty: sleep on the driver's event instead of spinning.
            uint wait = WintunInterop.WaitForSingleObject(_readEvent, timeoutMs);
            if (wait != 0) return null;   // timeout or abandoned
        }
    }

    /// <summary>Inject one inbound IP packet into the Windows stack.</summary>
    public bool SendPacket(ReadOnlySpan<byte> packet)
    {
        var ptr = WintunInterop.AllocateSendPacket(_session, (uint)packet.Length);
        if (ptr == IntPtr.Zero)
        {
            // ERROR_BUFFER_OVERFLOW means the ring is full: the PC is producing
            // faster than the tunnel drains. Dropping is correct - TCP will
            // retransmit, and blocking here would stall every other stream.
            return false;
        }

        unsafe
        {
            fixed (byte* src = packet)
            {
                Buffer.MemoryCopy(src, ptr.ToPointer(), packet.Length, packet.Length);
            }
        }
        WintunInterop.SendPacket(_session, ptr);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_session != IntPtr.Zero)
        {
            WintunInterop.EndSession(_session);
            _session = IntPtr.Zero;
        }
        if (_adapter != IntPtr.Zero)
        {
            WintunInterop.CloseAdapter(_adapter);
            _adapter = IntPtr.Zero;
        }
    }
}
