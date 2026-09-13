using System.Diagnostics;
using System.Text.Json;

namespace PocketModem.Client.Platform;

/// <summary>
/// Route and interface configuration on Linux, via iproute2.
///
/// Same contract and same safety rule as the Windows implementation: every undo
/// step is journalled to disk BEFORE the change is applied, and the journal is
/// replayed on the next start if we did not exit cleanly. A leftover default
/// route pointing at a dead tunnel leaves the machine with no internet, which
/// is the worst outcome this design can produce.
///
/// Uses `ip` rather than netlink for the same reason the Windows side uses
/// netsh: these are one-shot control-plane operations, not a hot path, and a
/// journal of shell commands is something a person can read and undo by hand
/// when everything else has failed.
/// </summary>
public sealed class LinuxRouteManager : IRouteManager
{
    private readonly string _journalPath;
    private readonly List<string> _undoCommands = new();
    private bool _applied;

    /// <summary>
    /// Leave the routes in place when this instance goes away, so handing over
    /// to a replacement does not interrupt connectivity.
    /// </summary>
    public bool HandingOver { get; set; }

    public LinuxRouteManager()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "pocketmodem");
        Directory.CreateDirectory(dir);
        _journalPath = Path.Combine(dir, "route-journal.json");
    }

    /// <summary>
    /// Adopt routes that still work, or undo ones a crash left behind. The two
    /// look identical on disk, so the caller decides by whether the tunnel is
    /// still reachable.
    /// </summary>
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
                for (int i = commands.Count - 1; i >= 0; i--) Run(commands[i]);
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

    public void ConfigureInterface(string interfaceName, string address, string mask, string dns, int mtu)
    {
        int prefix = MaskToPrefix(mask);

        Run($"ip addr add {address}/{prefix} dev {interfaceName}");
        Run($"ip link set dev {interfaceName} mtu {mtu}");
        Run($"ip link set dev {interfaceName} up");

        _undoCommands.Add($"ip link set dev {interfaceName} down");
        _undoCommands.Add($"ip addr del {address}/{prefix} dev {interfaceName}");

        // DNS: prefer resolvectl where systemd-resolved is in charge, since
        // editing /etc/resolv.conf under it is either ignored or destructive.
        if (HasCommand("resolvectl"))
        {
            Run($"resolvectl dns {interfaceName} {dns}");
            Run($"resolvectl domain {interfaceName} ~.");
            _undoCommands.Add($"resolvectl revert {interfaceName}");
        }
        else
        {
            ActivityLog.WriteAndPrint(
                "  note: systemd-resolved not found; set DNS manually if name " +
                $"resolution fails (nameserver {dns})");
        }

        WriteJournal();
    }

    public void ApplyTunnelRoutes(string interfaceName, string tunnelAddress, string peerAddress, string peerGateway)
    {
        string ifName = interfaceName;

        var toApply = new List<(string Do, string Undo)>
        {
            // Keep reaching the phone over the real link, or the tunnel would
            // try to route through itself.
            ($"ip route add {peerAddress}/32 dev {WifiInterfaceFor(peerAddress)}",
             $"ip route del {peerAddress}/32"),

            // Two /1 routes rather than replacing the default: they win on
            // longest-prefix match without deleting the original, so teardown
            // cannot lose it.
            ($"ip route add 0.0.0.0/1 dev {ifName}", "ip route del 0.0.0.0/1"),
            ($"ip route add 128.0.0.0/1 dev {ifName}", "ip route del 128.0.0.0/1"),
        };

        _undoCommands.AddRange(toApply.Select(x => x.Undo));
        WriteJournal();

        foreach (var (doCmd, _) in toApply)
        {
            var (ok, output) = Run(doCmd);
            if (!ok) ActivityLog.WriteAndPrint($"  warning: '{doCmd}' -> {output.Trim()}");
        }

        _applied = true;
    }

    /// <summary>
    /// Route these destinations via the existing gateway so they bypass the
    /// tunnel. Same reasoning as the Windows implementation: an exclusion has
    /// to happen in the route table, not after the packet has already been
    /// taken off the normal path.
    /// </summary>
    public void ExcludeRoutes(IEnumerable<string> destinations)
    {
        var (ok, route) = Run("ip route show default");
        if (!ok || route.Length == 0)
        {
            ActivityLog.WriteAndPrint("  no default route found; split rules will not take effect");
            return;
        }

        // "default via 192.168.1.1 dev wlan0 ..."
        var parts = route.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int viaAt = Array.IndexOf(parts, "via");
        if (viaAt < 0 || viaAt + 1 >= parts.Length)
        {
            ActivityLog.WriteAndPrint("  could not read the default gateway; split rules will not take effect");
            return;
        }
        string gateway = parts[viaAt + 1];

        foreach (var destination in destinations)
        {
            if (destination.StartsWith("10.") || destination.StartsWith("192.168.") ||
                destination.StartsWith("172.") || destination.StartsWith("169.254."))
                continue;

            string spec = destination.Contains('/') ? destination : destination + "/32";
            var (added, output) = Run($"ip route add {spec} via {gateway}");
            if (!added) ActivityLog.WriteAndPrint($"  could not exclude {destination}: {output.Trim()}");
            else _undoCommands.Add($"ip route del {spec}");
        }

        WriteJournal();
    }

    public void Revert()
    {
        if (HandingOver)
        {
            ActivityLog.WriteAndPrint("  leaving routes in place for the incoming instance");
            return;
        }

        if (!_applied && _undoCommands.Count == 0) return;

        ActivityLog.WriteAndPrint("  restoring routes...");
        for (int i = _undoCommands.Count - 1; i >= 0; i--) Run(_undoCommands[i]);

        _undoCommands.Clear();
        _applied = false;
        TryDelete(_journalPath);
    }

    private void WriteJournal()
    {
        try
        {
            File.WriteAllText(_journalPath, JsonSerializer.Serialize(_undoCommands));
        }
        catch (Exception ex)
        {
            ActivityLog.WriteAndPrint($"  WARNING: could not write the route journal ({ex.Message}).");
            ActivityLog.WriteAndPrint("  If this process dies uncleanly you may need to remove routes by hand.");
        }
    }


    /// <summary>The interface already on the phone's subnet.</summary>
    private static string WifiInterfaceFor(string peerAddress)
    {
        string prefix = peerAddress[..(peerAddress.LastIndexOf('.') + 1)];
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                if (ua.Address.ToString().StartsWith(prefix)) return nic.Name;
            }
        }
        return "wlan0";
    }

    private static int MaskToPrefix(string mask)
    {
        try
        {
            return mask.Split('.')
                .Select(byte.Parse)
                .Sum(b => System.Numerics.BitOperations.PopCount(b));
        }
        catch
        {
            return 24;
        }
    }

    private static bool HasCommand(string name) => Run($"which {name}").Ok;

    private static (bool Ok, string Output) Run(string command)
    {
        var parts = command.Split(' ', 2);
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = parts[0],
                Arguments = parts.Length > 1 ? parts[1] : string.Empty,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void Dispose() => Revert();
}
