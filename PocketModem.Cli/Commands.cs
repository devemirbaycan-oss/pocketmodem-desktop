using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using PocketModem.Client;
using PocketModem.Client.Net;
using PocketModem.Client.Platform;
using PocketModem.Client.Tunnel;

namespace PocketModem.Cli;

internal static class Commands
{
    private const int TunnelPort = 47812;

    // ------------------------------------------------------------- connect --

    public static async Task<int> ConnectAsync(CommandLine cmd)
    {
        Output.Configure(!cmd.NoColour, cmd.Quiet, cmd.Json);

        if (!PlatformFactory.IsSupported)
        {
            Output.Error("PocketModem supports Windows and Linux.");
            return 1;
        }

        if (!PlatformFactory.IsPrivileged())
        {
            Output.Error(PlatformFactory.PrivilegeHint());
            return 1;
        }

        if (OperatingSystem.IsWindows() &&
            !File.Exists(Path.Combine(AppContext.BaseDirectory, "wintun.dll")))
        {
            Output.Error("wintun.dll is missing.");
            Output.Hint("Download the amd64 build from https://www.wintun.net");
            Output.Hint("and put it next to this executable.");
            return 1;
        }

        string? token = ResolveToken(cmd);
        if (token is null) return 1;

        Output.Title("PocketModem");

        // Join the phone's Wi-Fi unless told not to.
        IWifiJoiner? joiner = null;
        if (!PlatformFactory.IsJoinedToPhone())
        {
            if (!cmd.AutoJoin)
            {
                Output.Error($"This PC has no {PlatformFactory.P2pPrefix}x address.");
                Output.Hint("Join the phone's network, or drop --no-join to let this do it.");
                return 1;
            }

            string? passphrase = cmd.Passphrase;
            if (string.IsNullOrWhiteSpace(passphrase))
            {
                if (Console.IsInputRedirected)
                {
                    Output.Error("Not joined to the phone, and no passphrase given.");
                    Output.Hint("Pass --passphrase, or set POCKETMODEM_PASSPHRASE.");
                    return 1;
                }
                Console.Write("  Wi-Fi passphrase (shown on the phone): ");
                passphrase = (Console.ReadLine() ?? "").Trim();
            }

            Output.Step("Joining the phone's network...");
            joiner = PlatformFactory.CreateWifiJoiner();
            if (!await joiner.JoinAsync(passphrase))
            {
                Output.Fail("Could not join the phone's Wi-Fi Direct group.");
                Output.Hint("Check the phone is showing a group, and the passphrase is right.");
                Output.Hint("Run 'pocketmodem doctor' for a fuller check.");
                return 1;
            }
            Output.Ok("joined");
        }

        using var session = new TunnelSession(cmd.Phone, token, cmd.Passphrase)
        {
            Split = SplitRules.Load(),
        };
        if (!string.IsNullOrWhiteSpace(cmd.Dns)) session.DnsServer = cmd.Dns!;
        var stopped = new CancellationTokenSource();

        session.Status += s =>
        {
            if (cmd.Verbose) Output.Step(s);
        };

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Output.EndStatusLine();
            Output.Step("Stopping...");
            stopped.Cancel();
        };

        Output.Step($"Connecting to {cmd.Phone}...");
        if (!await session.StartAsync(stopped.Token))
        {
            Output.Fail("Could not establish the tunnel.");
            Output.Hint("Run 'pocketmodem doctor' to find out why.");
            joiner?.Leave();
            return 1;
        }

        Output.Ok("connected - all traffic is going through the phone");
        if (cmd.DurationSeconds is int secs)
            Output.Step($"Will disconnect after {FormatDuration(secs)}.");
        Output.Info("");
        Output.Info(cmd.Quiet ? "" : "  Press Ctrl+C to stop.");
        Output.Info("");

        if (cmd.DurationSeconds is int limit)
            stopped.CancelAfter(TimeSpan.FromSeconds(limit));

        await RunStatsLoop(session, cmd, stopped.Token);

        Output.EndStatusLine();
        session.Stop();
        joiner?.Leave();
        Output.Ok("routing restored");
        return 0;
    }

    private static async Task RunStatsLoop(TunnelSession session, CommandLine cmd, CancellationToken ct)
    {
        long lastUp = 0, lastDown = 0;
        var lastSample = DateTime.UtcNow;
        var started = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { break; }

            var now = DateTime.UtcNow;
            double seconds = (now - lastSample).TotalSeconds;
            if (seconds <= 0) continue;

            long up = session.BytesUp, down = session.BytesDown;
            double upMbps = (up - lastUp) * 8.0 / seconds / 1_000_000.0;
            double downMbps = (down - lastDown) * 8.0 / seconds / 1_000_000.0;
            lastUp = up; lastDown = down; lastSample = now;

            if (cmd.Json)
            {
                Output.Json(new
                {
                    uptime = (int)(now - started).TotalSeconds,
                    connected = session.Connected,
                    links = session.Links,
                    maxLinks = session.MaxLinks,
                    streams = session.ActiveStreams,
                    reconnects = session.Reconnects,
                    downMbps = Math.Round(downMbps, 2),
                    upMbps = Math.Round(upMbps, 2),
                    bytesDown = down,
                    bytesUp = up,
                });
                continue;
            }

            string state = session.Connected
                ? $"{session.Links}/{session.MaxLinks} links"
                : "reconnecting";

            Output.StatusLine(
                $"  down {Output.Speed(downMbps)}   up {Output.Speed(upMbps)}   " +
                $"{session.ActiveStreams,3} conns   {state}" +
                (session.Reconnects > 0 ? $"   {session.Reconnects} reconnect(s)" : ""));
        }
    }

    // -------------------------------------------------------------- status --

    public static int Status(CommandLine cmd)
    {
        Output.Configure(!cmd.NoColour, cmd.Quiet, cmd.Json);

        bool joined = PlatformFactory.IsJoinedToPhone();
        bool adapterUp = FindAdapter() is not null;
        bool routed = HasTunnelRoutes();
        bool phoneReachable = joined && CanReach(cmd.Phone, TunnelPort, 1500);

        if (cmd.Json)
        {
            Output.Json(new
            {
                joinedToPhone = joined,
                adapterPresent = adapterUp,
                tunnelRoutes = routed,
                phoneReachable,
                running = adapterUp && routed,
            });
            return adapterUp && routed ? 0 : 1;
        }

        Output.Title("Status");
        Output.Field("Wi-Fi group", joined ? "joined" : "not joined");
        Output.Field("Adapter", adapterUp ? "present" : "absent");
        Output.Field("Routing", routed ? "through the phone" : "normal");
        Output.Field("Phone", phoneReachable ? "reachable" : joined ? "not answering" : "-");
        Output.Info("");

        if (adapterUp && routed) Output.Ok("A tunnel is running.");
        else if (routed && !adapterUp)
        {
            Output.Warn("Routes exist but the adapter is gone.");
            Output.Hint("This is the state a crash leaves. Run: pocketmodem recover");
        }
        else Output.Info("  No tunnel is running.");

        return adapterUp && routed ? 0 : 1;
    }

    // ---------------------------------------------------------------- test --

    /// <summary>
    /// Verify the tunnel path without touching the adapter or routing. Isolates
    /// the forwarding path from everything the virtual interface adds, which is
    /// what made the early failures diagnosable.
    /// </summary>
    public static async Task<int> TestAsync(CommandLine cmd)
    {
        Output.Configure(!cmd.NoColour, cmd.Quiet, cmd.Json);

        string? token = ResolveToken(cmd);
        if (token is null) return 1;

        Output.Title("Tunnel test");
        Output.Step($"Connecting to {cmd.Phone}:{TunnelPort}...");

        using var tunnel = new TunnelClient(cmd.Phone, TunnelPort);
        if (!await tunnel.ConnectAsync(token))
        {
            Output.Fail("Could not reach the tunnel.");
            Output.Hint("Is the phone app running with a transport started?");
            return 1;
        }

        await Task.Delay(500);
        if (tunnel.Rejected)
        {
            Output.Fail("The phone rejected this pairing code.");
            Output.Hint("Check the PAIRING CODE card in the phone app.");
            return 1;
        }
        Output.Ok($"connected ({tunnel.ConnectedLinks}/{TunnelClient.LinkCount} links)");

        var sw = Stopwatch.StartNew();
        var (ok, detail) = await FetchThroughTunnel(tunnel);
        sw.Stop();

        if (cmd.Json)
        {
            Output.Json(new { ok, detail, milliseconds = sw.ElapsedMilliseconds, links = tunnel.ConnectedLinks });
            return ok ? 0 : 1;
        }

        if (ok)
        {
            Output.Ok($"{detail} ({sw.ElapsedMilliseconds} ms)");
            Output.Info("");
            Output.Info("  The phone is fetching real pages on this PC's behalf.");
            return 0;
        }

        Output.Fail(detail);
        Output.Hint("On the phone: adb logcat -s TunnelServer:V StreamRelay:V");
        return 1;
    }

    private static async Task<(bool Ok, string Detail)> FetchThroughTunnel(TunnelClient tunnel)
    {
        // 1.1.1.1 is a fixed anycast address, so this needs no DNS - DNS itself
        // rides UDP and is worth isolating from a TCP failure.
        byte[] dstIp = { 1, 1, 1, 1 };
        var received = new MemoryStream();
        var finished = new TaskCompletionSource<byte>();
        int streamId = 0;

        tunnel.TcpDataReceived += (id, data) =>
        {
            if (id != streamId) return;
            received.Write(data);
            if (received.Length > 200) finished.TrySetResult(Protocol.CloseReason.Normal);
        };
        tunnel.TcpClosed += (id, reason) => { if (id == streamId) finished.TrySetResult(reason); };

        streamId = tunnel.OpenStream(dstIp, 80);
        await tunnel.SendTcpAsync(streamId, Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\nHost: one.one.one.one\r\nConnection: close\r\n\r\n"));

        if (await Task.WhenAny(finished.Task, Task.Delay(15000)) != finished.Task)
            return (false, "No reply after 15s - the tunnel connected but carried nothing.");

        byte reason = await finished.Task;
        if (received.Length == 0)
            return (false, $"Stream closed with no data: {Protocol.CloseReason.Describe(reason)}");

        string firstLine = Encoding.ASCII.GetString(received.ToArray()).Split('\r')[0];
        return (firstLine.Contains("HTTP/1."), firstLine);
    }

    // ------------------------------------------------------------ handover --

    /// <summary>
    /// Take over a tunnel a previous instance left running.
    ///
    /// The adapter and route table are OS state that outlives the process, so
    /// updating the binaries need not interrupt connectivity at all: the old
    /// process exits without reverting anything, and this one adopts what it
    /// finds. Without it, every update costs a gap where the machine has a
    /// default route pointing at a tunnel nobody is serving.
    /// </summary>
    public static async Task<int> HandoverAsync(CommandLine cmd)
    {
        Output.Configure(!cmd.NoColour, cmd.Quiet, cmd.Json);

        if (!PlatformFactory.IsPrivileged())
        {
            Output.Error(PlatformFactory.PrivilegeHint());
            return 1;
        }

        Output.Title("Handover");

        // Ask the running instance to stand down without reverting. Killing it
        // outright would work too, since a killed process reverts nothing - but
        // asking is cleaner and lets it flush its state.
        var running = System.Diagnostics.Process.GetProcessesByName("PocketModem-Desktop")
            .Concat(System.Diagnostics.Process.GetProcessesByName("pocketmodem"))
            .Where(p => p.Id != Environment.ProcessId)
            .ToArray();

        if (running.Length > 0)
        {
            Output.Step($"asking {running.Length} running instance(s) to stand down...");
            foreach (var p in running)
            {
                try
                {
                    // A killed process never reaches its cleanup, so the routes
                    // it installed simply stay - which is exactly what we want.
                    p.Kill();
                    await Task.Delay(300);
                }
                catch (Exception ex)
                {
                    Output.Warn($"could not stop pid {p.Id}: {ex.Message}");
                }
            }
            Output.Ok("previous instance stopped, routing left in place");
        }
        else
        {
            Output.Step("nothing was running; starting fresh");
        }

        string? token = ResolveToken(cmd);
        if (token is null) return 1;

        using var session = new TunnelSession(cmd.Phone, token, cmd.Passphrase)
        {
            Split = SplitRules.Load(),
        };
        if (!string.IsNullOrWhiteSpace(cmd.Dns)) session.DnsServer = cmd.Dns!;
        var stopped = new CancellationTokenSource();
        session.Status += s => Output.Step(s);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Output.EndStatusLine();
            stopped.Cancel();
        };

        if (!await session.StartAsync(stopped.Token))
        {
            Output.Fail("Could not take over.");
            Output.Hint("Run 'pocketmodem recover' if the machine has no internet.");
            return 1;
        }

        Output.Ok("took over - the connection was never dropped");
        Output.Info("");

        await RunStatsLoop(session, cmd, stopped.Token);

        Output.EndStatusLine();
        session.Stop();
        return 0;
    }

    // --------------------------------------------------------------- split --

    /// <summary>
    /// Show or change which traffic goes through the phone.
    ///
    /// The connection is metered, so excluding a game update or a backup is
    /// often more useful than any amount of extra throughput. No modem can do
    /// this - it has one pipe and everything takes it.
    /// </summary>
    public static int Split(CommandLine cmd)
    {
        Output.Configure(!cmd.NoColour, cmd.Quiet, cmd.Json);

        var rules = SplitRules.Load();
        var rest = cmd.Rest;

        // Bare "split" lists, and so does "split list": the second is what
        // people type first, and refusing it teaches nothing.
        if (rest.Count == 0 || rest[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var all = rules.All();
            if (cmd.Json)
            {
                Output.Json(new
                {
                    mode = rules.DefaultMode.ToString(),
                    rules = all.Select(r => new { match = r.Match, tunnel = r.Tunnel }),
                });
                return 0;
            }

            Output.Title("Split rules");
            Output.Field("Default", rules.DefaultMode == SplitRules.Mode.TunnelEverything
                ? "everything through the phone"
                : "only what is listed");
            Output.Info("");

            if (all.Count == 0) { Output.Info("  No rules."); return 0; }

            foreach (var (match, tunnel) in all)
                Output.Field(tunnel ? "via phone" : "bypass", match);

            Output.Info("");
            Output.Hint("pocketmodem split bypass <host or range>   keep it off the phone");
            Output.Hint("pocketmodem split via <host or range>      send it through the phone");
            Output.Hint("pocketmodem split remove <match>           drop a rule");
            return 0;
        }

        string verb = rest[0].ToLowerInvariant();
        string? target = rest.Count > 1 ? rest[1] : null;

        switch (verb)
        {
            case "bypass" when target is not null:
                rules.Add(target, tunnel: false);
                rules.Save();
                Output.Ok($"{target} will bypass the phone");
                break;

            case "via" when target is not null:
                rules.Add(target, tunnel: true);
                rules.Save();
                Output.Ok($"{target} will go through the phone");
                break;

            case "remove" when target is not null:
                rules.Remove(target);
                rules.Save();
                Output.Ok($"removed {target}");
                break;

            case "only-listed":
                rules.SetMode(SplitRules.Mode.TunnelOnlyListed);
                rules.Save();
                Output.Ok("only listed traffic will use the phone");
                break;

            case "everything":
                rules.SetMode(SplitRules.Mode.TunnelEverything);
                rules.Save();
                Output.Ok("everything will use the phone unless excluded");
                break;

            case "bypass" or "via" or "remove":
                Output.Error($"'{verb}' needs something to act on.");
                Output.Hint($"pocketmodem split {verb} steamcontent.com");
                Output.Hint($"pocketmodem split {verb} 203.0.113.0/24");
                return 2;

            default:
                Output.Error($"Don't know how to '{verb}'.");
                Output.Hint("Try: list, bypass, via, remove, only-listed, everything");
                return 2;
        }

        Output.Hint("Reconnect for this to take effect.");
        return 0;
    }

    // -------------------------------------------------------------- doctor --

    /// <summary>
    /// Works through the connection in order and stops at the first thing that
    /// is actually wrong, so the answer is "your phone is not showing a group"
    /// rather than "connection failed".
    /// </summary>
    public static async Task<int> DoctorAsync(CommandLine cmd)
    {
        Output.Configure(!cmd.NoColour, cmd.Quiet, cmd.Json);
        Output.Title("Diagnosis");

        var problems = new List<string>();

        // 1. Platform and privileges
        if (!PlatformFactory.IsSupported)
        {
            Output.Fail("This platform is not supported (Windows and Linux only).");
            return 1;
        }
        Output.Ok($"platform: {BuildInfo.Platform}");

        if (PlatformFactory.IsPrivileged()) Output.Ok("running with the rights needed");
        else
        {
            Output.Fail("not privileged");
            problems.Add(PlatformFactory.PrivilegeHint());
        }

        // 2. Driver
        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "wintun.dll")))
                Output.Ok("wintun.dll present");
            else
            {
                Output.Fail("wintun.dll missing");
                problems.Add("Download wintun.dll (amd64) from https://www.wintun.net into this folder.");
            }
        }
        else
        {
            if (File.Exists("/dev/net/tun")) Output.Ok("/dev/net/tun present");
            else
            {
                Output.Fail("/dev/net/tun missing");
                problems.Add("Load the tun module: sudo modprobe tun");
            }
        }

        // 3. Wi-Fi
        if (PlatformFactory.IsJoinedToPhone())
        {
            Output.Ok($"joined to the phone's network ({PlatformFactory.P2pPrefix}x)");
        }
        else
        {
            Output.Fail("not joined to the phone's Wi-Fi Direct group");
            var joiner = PlatformFactory.CreateWifiJoiner();
            if (joiner.IsVisible())
                problems.Add("The phone's group is visible. Connect to it, or run 'pocketmodem connect' with the passphrase.");
            else
                problems.Add("The phone's group is not visible. Open the app on the phone and tap Wi-Fi Direct. "
                + "If it is already on, check the phone's mobile hotspot is OFF - "
                + "a phone cannot run a hotspot and Wi-Fi Direct at the same time.");
        }

        // 4. Reachability
        if (PlatformFactory.IsJoinedToPhone())
        {
            if (CanReach(cmd.Phone, TunnelPort, 2000))
                Output.Ok($"phone answering on {cmd.Phone}:{TunnelPort}");
            else
            {
                Output.Fail($"no answer on {cmd.Phone}:{TunnelPort}");
                problems.Add("The Wi-Fi is joined but the app is not listening. Start the tunnel on the phone.");
            }
        }

        // 5. Leftovers from a crash
        if (HasTunnelRoutes() && FindAdapter() is null)
        {
            Output.Fail("routes point at an adapter that no longer exists");
            problems.Add("Run 'pocketmodem recover' to restore normal routing.");
        }

        // 6. End to end, if everything above allows it
        if (problems.Count == 0)
        {
            string? token = cmd.Token;
            if (!string.IsNullOrWhiteSpace(token))
            {
                Output.Step("testing the tunnel end to end...");
                using var tunnel = new TunnelClient(cmd.Phone, TunnelPort);
                if (await tunnel.ConnectAsync(token))
                {
                    await Task.Delay(500);
                    if (tunnel.Rejected)
                    {
                        Output.Fail("the phone rejected this pairing code");
                        problems.Add("Check the PAIRING CODE on the phone and pass it with --token.");
                    }
                    else
                    {
                        var (ok, detail) = await FetchThroughTunnel(tunnel);
                        if (ok) Output.Ok($"end to end: {detail}");
                        else
                        {
                            Output.Fail($"end to end: {detail}");
                            problems.Add("The tunnel connects but carries no traffic. Check mobile data is on.");
                        }
                    }
                }
            }
            else
            {
                Output.Warn("no pairing code given, so the end-to-end check was skipped");
                Output.Hint("Pass --token to test the whole path.");
            }
        }

        Output.Info("");
        if (problems.Count == 0)
        {
            Output.Ok("Nothing wrong found.");
            return 0;
        }

        Output.Info($"  {problems.Count} problem(s) to fix:");
        Output.Info("");
        for (int i = 0; i < problems.Count; i++)
            Output.Info($"  {i + 1}. {problems[i]}");
        Output.Info("");
        return 1;
    }

    // ------------------------------------------------------------- recover --

    public static int Recover(CommandLine cmd)
    {
        Output.Configure(!cmd.NoColour, cmd.Quiet, cmd.Json);

        if (!PlatformFactory.IsPrivileged())
        {
            Output.Error(PlatformFactory.PrivilegeHint());
            return 1;
        }

        Output.Title("Recovery");
        Output.Step("undoing any route changes left by a previous run...");

        var routes = PlatformFactory.CreateRouteManager();
        routes.RecoverIfNeeded();

        Output.Ok("done - normal routing should be restored");
        if (HasTunnelRoutes())
        {
            Output.Warn("some tunnel routes are still present");
            Output.Hint(OperatingSystem.IsWindows()
                ? "Try: route delete 0.0.0.0 mask 128.0.0.0"
                : "Try: sudo ip route del 0.0.0.0/1");
        }
        return 0;
    }

    // --------------------------------------------------------------- utils --

    private static string? ResolveToken(CommandLine cmd)
    {
        if (!string.IsNullOrWhiteSpace(cmd.Token)) return cmd.Token;

        if (Console.IsInputRedirected)
        {
            Output.Error("No pairing code given.");
            Output.Hint("Pass --token, or set POCKETMODEM_TOKEN.");
            return null;
        }

        Console.Write("  Pairing code (shown on the phone): ");
        string token = (Console.ReadLine() ?? "").Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            Output.Error("A pairing code is required.");
            return null;
        }
        return token;
    }

    private static NetworkInterface? FindAdapter() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.Name.Contains("pocketmodem", StringComparison.OrdinalIgnoreCase));

    private static bool HasTunnelRoutes()
    {
        // The tunnel installs two /1 routes; their presence is the signature.
        var (ok, output) = Run(
            OperatingSystem.IsWindows() ? "route" : "ip",
            OperatingSystem.IsWindows() ? "print -4" : "route show");
        if (!ok) return false;

        return OperatingSystem.IsWindows()
            ? output.Contains("128.0.0.0") && output.Contains("0.0.0.0")
              && output.Contains("10.87.0.")
            : output.Contains("0.0.0.0/1") || output.Contains("128.0.0.0/1");
    }

    private static bool CanReach(string host, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            return task.Wait(timeoutMs) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatDuration(int seconds) => seconds switch
    {
        < 60 => $"{seconds}s",
        < 3600 => $"{seconds / 60}m",
        _ => $"{seconds / 3600}h {(seconds % 3600) / 60}m",
    };

    private static (bool Ok, string Output) Run(string file, string args)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return (false, "");
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return (proc.ExitCode == 0, output);
        }
        catch
        {
            return (false, "");
        }
    }
}
