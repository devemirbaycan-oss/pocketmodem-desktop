using System.Diagnostics;
using System.Text.Json;

namespace PocketModem.Client.Net;

/// <summary>
/// Owns the route-table changes that put PC traffic through the tunnel
/// (DESIGN.md §7.2), and guarantees they can be undone.
///
/// The worst failure in this whole design is a leftover default route pointing
/// at a tunnel that no longer exists: the machine then has no internet at all,
/// and the user has no obvious way to fix it. So every change is written to a
/// journal on disk BEFORE it is applied, and the journal is replayed in reverse
/// on the next start if we did not shut down cleanly.
///
/// Uses netsh/route rather than P/Invoke to the IP helper API. These are
/// one-shot control-plane operations, not a hot path, and shelling out keeps
/// the rollback readable by a human who needs to undo it by hand.
/// </summary>
public sealed class RouteManager : Platform.IRouteManager
{
    private readonly string _journalPath;
    private readonly List<string> _undoCommands = new();
    private bool _applied;

    /// <summary>
    /// Skip reverting the routes when this instance goes away.
    ///
    /// Windows removes them anyway once the adapter disappears with its owning
    /// process, so this saves the explicit undo rather than preserving
    /// anything: the incoming instance rebuilds in one step instead of waiting
    /// for the outgoing one to tear down first.
    /// </summary>
    public bool HandingOver { get; set; }

    public RouteManager()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PocketModem");
        Directory.CreateDirectory(dir);
        _journalPath = Path.Combine(dir, "route-journal.json");
    }

    /// <summary>
    /// Replay a journal left behind by a crash. Called before touching anything,
    /// so a previous bad run cannot compound with this one.
    /// </summary>
    /// <summary>
    /// Undo routes left by a previous run - unless they are still working.
    ///
    /// A journal on disk means one of two things: a crash left routes pointing
    /// at a tunnel that no longer exists, or a previous instance handed over
    /// deliberately. They look identical on disk, so the distinction is made by
    /// asking whether the routes still function.
    /// </summary>
    /// <returns>True when live routes were adopted rather than removed.</returns>
    public bool RecoverOrAdopt(Func<bool> tunnelReachable)
    {
        if (!File.Exists(_journalPath)) return false;

        if (tunnelReachable())
        {
            ActivityLog.WriteAndPrint("  adopting the routes already in place");
            try
            {
                var existing = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_journalPath));
                if (existing is { Count: > 0 })
                {
                    // Take ownership so this instance can undo them later.
                    _undoCommands.Clear();
                    _undoCommands.AddRange(existing);
                    _applied = true;
                    return true;
                }
            }
            catch (Exception ex)
            {
                ActivityLog.WriteAndPrint($"  could not read the journal ({ex.Message}); rebuilding");
            }
        }

        RecoverIfNeeded();
        return false;
    }

    public void RecoverIfNeeded()
    {
        if (!File.Exists(_journalPath)) return;

        try
        {
            var commands = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_journalPath));
            if (commands is { Count: > 0 })
            {
                ActivityLog.WriteAndPrint($"  recovering {commands.Count} route change(s) from a previous run...");
                // Reverse order: undo the last change first.
                for (int i = commands.Count - 1; i >= 0; i--)
                    RunQuiet(commands[i]);
            }
        }
        catch (Exception ex)
        {
            ActivityLog.WriteAndPrint($"  route recovery failed (continuing): {ex.Message}");
        }
        finally
        {
            TryDelete(_journalPath);
        }
    }

    /// <summary>
    /// Point the machine's default route at the tunnel.
    ///
    /// <paramref name="peerAddress"/> is the phone's address on the transport
    /// link. It gets a host route via the existing interface FIRST — without
    /// it the tunnel's own traffic would try to route through the tunnel, which
    /// is the classic split-tunnel bootstrap failure.
    /// </summary>
    public void ApplyTunnelRoutes(string interfaceName, string tunnelAddress, string peerAddress, string peerGateway)
    {
        // `route add ... if N` wants an index; look it up here rather than
        // making the caller find one, since on other platforms the index does
        // not exist until an address is bound.
        int interfaceIndex = IndexOf(interfaceName);
        var toApply = new List<(string Do, string Undo)>
        {
            // Keep reaching the phone over the real link.
            ($"route add {peerAddress} mask 255.255.255.255 {peerGateway} metric 1",
             $"route delete {peerAddress}"),

            // Default route through the tunnel. Split into two /1 routes rather
            // than replacing 0.0.0.0/0: they beat the existing default on
            // longest-prefix match without deleting it, so the original route
            // survives untouched for teardown.
            ($"route add 0.0.0.0 mask 128.0.0.0 {tunnelAddress} metric 1 if {interfaceIndex}",
             "route delete 0.0.0.0 mask 128.0.0.0"),
            ($"route add 128.0.0.0 mask 128.0.0.0 {tunnelAddress} metric 1 if {interfaceIndex}",
             "route delete 128.0.0.0 mask 128.0.0.0"),

            // The v6 default, split the same way and for the same reason: two
            // more specific routes win without deleting the original.
            ($"netsh interface ipv6 add route ::/1 \"{interfaceName}\" fd87::1 metric=1",
             $"netsh interface ipv6 delete route ::/1 \"{interfaceName}\""),
            ($"netsh interface ipv6 add route 8000::/1 \"{interfaceName}\" fd87::1 metric=1",
             $"netsh interface ipv6 delete route 8000::/1 \"{interfaceName}\""),
        };

        // Journal the undo steps BEFORE applying anything, so a crash midway
        // still leaves a complete recovery record.
        _undoCommands.Clear();
        _undoCommands.AddRange(toApply.Select(x => x.Undo));
        WriteJournal();

        foreach (var (doCmd, _) in toApply)
        {
            var (ok, output) = Run(doCmd);
            if (!ok)
                ActivityLog.WriteAndPrint($"  warning: '{doCmd}' -> {output.Trim()}");
        }

        _applied = true;
    }

    /// <summary>Configure the adapter's address, DNS and MTU.</summary>
    public void ConfigureInterface(string interfaceName, string address, string mask, string dns, int mtu = 1400)
    {
        Run($"netsh interface ip set address name=\"{interfaceName}\" static {address} {mask}");
        Run($"netsh interface ip set dns name=\"{interfaceName}\" static {dns}");

        // Without this Windows assumes 1500 and emits segments too large for
        // the tunnel, which then have to be split - more packets, more
        // round trips, and stalls when a split segment is lost (7.3).
        Run($"netsh interface ipv4 set subinterface \"{interfaceName}\" mtu={mtu} store=persistent");

        // IPv6, from a unique-local prefix. Without an address on this
        // interface Windows will not route v6 through it at all, and sites that
        // are v6-only simply fail while everything else works.
        Run($"netsh interface ipv6 set address \"{interfaceName}\" fd87::2/64");
        Run($"netsh interface ipv6 set subinterface \"{interfaceName}\" mtu={mtu} store=persistent");
        _undoCommands.Add($"netsh interface ipv6 delete address \"{interfaceName}\" fd87::2");

        _undoCommands.Add($"netsh interface ip set dns name=\"{interfaceName}\" dhcp");
        WriteJournal();
    }

    public void Revert()
    {
        if (HandingOver)
        {
            // The replacement owns these now. Removing them here is exactly the
            // gap in connectivity a handover exists to avoid.
            ActivityLog.WriteAndPrint("  leaving routes in place for the incoming instance");
            return;
        }

        if (!_applied && _undoCommands.Count == 0) return;

        ActivityLog.WriteAndPrint("  restoring routes...");
        for (int i = _undoCommands.Count - 1; i >= 0; i--)
            RunQuiet(_undoCommands[i]);

        _undoCommands.Clear();
        _applied = false;
        TryDelete(_journalPath);
    }

    /// <summary>
    /// Route these destinations via the machine's existing gateway, so they
    /// bypass the tunnel entirely.
    ///
    /// This is where an exclusion actually takes effect. Filtering in the
    /// packet pump would be too late - the packet has already been taken off
    /// the normal path by the time it arrives there.
    /// </summary>
    public void ExcludeRoutes(IEnumerable<string> destinations)
    {
        string? gateway = DefaultGateway();
        if (gateway is null)
        {
            ActivityLog.WriteAndPrint("  no default gateway found; split rules will not take effect");
            return;
        }

        foreach (var destination in destinations)
        {
            // Skip private ranges: they already route locally, and adding a
            // host route for them would fight the interface's own subnet route.
            if (destination.StartsWith("10.") || destination.StartsWith("192.168.") ||
                destination.StartsWith("172.") || destination.StartsWith("169.254."))
                continue;

            string spec = destination.Contains('/')
                ? destination.Replace("/", " mask ")
                : destination + " mask 255.255.255.255";

            var (ok, output) = Run($"route add {spec} {gateway} metric 1");
            if (!ok) ActivityLog.WriteAndPrint($"  could not exclude {destination}: {output.Trim()}");
            else _undoCommands.Add($"route delete {destination.Split('/')[0]}");
        }

        WriteJournal();
    }

    /// <summary>The gateway the machine used before the tunnel took over.</summary>
    private static string? DefaultGateway()
    {
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (nic.Name.Contains("PocketModem", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var gw in nic.GetIPProperties().GatewayAddresses)
            {
                if (gw.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                var text = gw.Address.ToString();
                if (text != "0.0.0.0") return text;
            }
        }
        return null;
    }

    private static int IndexOf(string name)
    {
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!nic.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
            try { return nic.GetIPProperties().GetIPv4Properties().Index; }
            catch { /* IPv4 not bound yet */ }
        }
        throw new InvalidOperationException($"Could not find the '{name}' interface.");
    }

    private void WriteJournal()
    {
        try
        {
            File.WriteAllText(_journalPath, JsonSerializer.Serialize(_undoCommands));
        }
        catch (Exception ex)
        {
            // A journal we cannot write is a real risk, not a detail: without it
            // a crash could leave the machine unroutable. Say so loudly.
            ActivityLog.WriteAndPrint($"  WARNING: could not write the route journal ({ex.Message}).");
            ActivityLog.WriteAndPrint("  If this process dies uncleanly you may need to remove routes by hand.");
        }
    }

    private static (bool Ok, string Output) Run(string command)
    {
        var parts = command.Split(' ', 2);
        var psi = new ProcessStartInfo
        {
            FileName = parts[0],
            Arguments = parts.Length > 1 ? parts[1] : string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return (false, "could not start process");
            string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);
            return (proc.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static void RunQuiet(string command) => Run(command);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    public void Dispose() => Revert();
}
