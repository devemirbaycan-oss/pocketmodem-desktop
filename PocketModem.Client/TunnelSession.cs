using System.Linq;
using PocketModem.Client.Net;
using PocketModem.Client.Platform;
using PocketModem.Client.Tunnel;

namespace PocketModem.Client;

/// <summary>
/// One end-to-end tunnel session: adapter, routes, packet pump and reconnect.
///
/// Extracted so the CLI and the desktop app drive exactly the same code. The
/// GUI should be a view over this, never a second implementation - a divergence
/// here would mean bugs fixed in one and not the other.
///
/// Platform-specific pieces (Wintun, netsh) sit behind this class, so a Linux
/// port replaces those rather than the session logic.
/// </summary>
public sealed class TunnelSession : IDisposable
{
    public const string AdapterName = "PocketModem";
    private const string TunnelAddress = "10.87.0.2";
    private const string TunnelMask = "255.255.255.0";
    private const string TunnelGateway = "10.87.0.1";
    /// <summary>
    /// Resolver handed to the adapter. A modem lets you choose this, so this
    /// does too - the default is Cloudflare's, and a household resolver like a
    /// Pi-hole is the usual reason to change it.
    /// </summary>
    public string DnsServer { get; set; } = "1.1.1.1";

    /// <summary>Which destinations belong in the tunnel. Null tunnels everything.</summary>
    public SplitRules? Split { get; set; }
    private const int DefaultPort = 47812;

    private readonly string _phone;
    private readonly string _token;
    private readonly string? _passphrase;

    private ITunAdapter? _adapter;
    private IRouteManager? _routes;
    private ConnectionManager? _connection;
    private PacketPump? _pump;
    private Thread? _pumpThread;
    private CancellationTokenSource? _cts;
    private bool _adoptedRoutes;

    public event Action<string>? Status;

    public bool IsRunning { get; private set; }
    public long BytesUp => _pump?.BytesOut ?? 0;
    public long BytesDown => _pump?.BytesIn ?? 0;
    public long PacketsUp => _pump?.PacketsOut ?? 0;
    public long PacketsDown => _pump?.PacketsIn ?? 0;
    public int ActiveStreams => _connection?.Current?.ActiveStreams ?? 0;

    /// <summary>
    /// Packets a split rule kept off the phone.
    ///
    /// Worth surfacing because the failure mode of split tunnelling is silence:
    /// a rule that never matches looks exactly like a rule that is working, and
    /// the difference only shows up on the bill.
    /// </summary>
    public long PacketsExcluded => _pump?.PacketsExcluded ?? 0;
    public int Reconnects => _connection?.ReconnectCount ?? 0;

    /// <summary>Why the links last closed, for the diagnostics panel.</summary>
    public IReadOnlyList<string> RecentCloses =>
        _connection?.RecentCloses ?? Array.Empty<string>();
    public bool Connected => _connection?.IsConnected == true;

    /// <summary>
    /// Whether the phone has mobile data, as distinct from whether this PC can
    /// reach the phone.
    ///
    /// Both must hold for traffic to flow, and they fail independently: during
    /// a network handover the link stays perfectly healthy while nothing
    /// reaches the internet. Reporting only the link is what made the app say
    /// "connected" through an outage.
    /// </summary>
    public bool UpstreamUp => _connection?.Current?.UpstreamUp != false;
    /// <summary>Live parallel links; fewer than the max is degraded, not broken.</summary>
    public int Links => _connection?.Current?.ConnectedLinks ?? 0;
    public int MaxLinks => Tunnel.TunnelClient.LinkCount;

    /// <param name="passphrase">
    /// Needed to rejoin the phone's Wi-Fi if its group restarts mid-session.
    /// Without it a dropped group leaves the PC retrying an address it can no
    /// longer reach.
    /// </param>
    public TunnelSession(string phone, string token, string? passphrase = null)
    {
        _phone = phone;
        _token = token;
        _passphrase = passphrase;
    }

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _routes = PlatformFactory.CreateRouteManager();

        // Routes left on disk mean either a crash or a deliberate handover from
        // a previous version. Adopt them when the tunnel still works, so an
        // update does not take the machine offline; undo them when it does not.
        _adoptedRoutes = _routes.RecoverOrAdopt(() => CanReachPhone(_phone));
        if (_adoptedRoutes) Status?.Invoke("Taking over the existing connection...");

        Status?.Invoke($"Connecting to {_phone}...");
        _connection = new ConnectionManager(_phone, DefaultPort, _token);
        _connection.StateChanged += s => Status?.Invoke(s);

        // If the phone's group restarts, this PC falls off its network and no
        // amount of socket retrying will help - it has to rejoin first.
        if (!string.IsNullOrEmpty(_passphrase))
        {
            _connection.RejoinNetwork = async token =>
            {
                var joiner = PlatformFactory.CreateWifiJoiner();

                // Leave before joining. Windows keeps reporting the old profile
                // as connected after the phone's group has gone, so a join that
                // short-circuits on "already joined" returns success without
                // reconnecting to anything - which is why only a manual
                // disconnect and reconnect recovered the session.
                joiner.Leave();
                await Task.Delay(500, token);

                return await joiner.JoinAsync(_passphrase!, token);
            };
        }

        // Rebind the pump on reconnect: a new TunnelClient means new event
        // sources, and the adapter deliberately stays up throughout. Only the
        // event wiring is replaced - the pump thread keeps running, because a
        // second thread on the same adapter would consume packets from the same
        // ring and duplicate traffic.
        _connection.Connected += client =>
        {
            if (_adapter is not null) StartOrRebindPump(client);
        };

        if (!await _connection.StartAsync(_cts.Token))
        {
            Status?.Invoke("Could not reach the phone.");
            Cleanup();
            return false;
        }

        try
        {
            // Wintun reopens an existing adapter by name, so a handover
            // reuses the interface Windows already has - no new "unidentified
            // network", and no gap while one is created.
            Status?.Invoke(_adoptedRoutes ? "Reusing the network adapter..." : "Creating the network adapter...");
            _adapter = PlatformFactory.CreateAdapter(AdapterName);

            // Address first, then routes: on Linux the interface has no
            // address (and so no usable index) until it is configured.
            // Re-applying identical routes is harmless but noisy, and on a
            // handover the addressing is already exactly as we want it.
            if (!_adoptedRoutes)
            {
                _routes.ConfigureInterface(AdapterName, TunnelAddress, TunnelMask, DnsServer, PacketPump.Mtu);
                _routes.ApplyTunnelRoutes(AdapterName, TunnelGateway, _phone, _phone);

                // Excluded destinations need a route via the real gateway
                // BEFORE the default route moves, or their first packets are
                // lost while the rule is still being installed.
                if (Split is not null)
                {
                    var excluded = Split.All()
                        .Where(r => !r.Tunnel && r.Match.Contains('.') && char.IsDigit(r.Match[0]))
                        .Select(r => r.Match);
                    _routes.ExcludeRoutes(excluded);
                }
            }

            // The adapter did not exist when the first connection landed,
            // so bind the pump to it now.
            var client = _connection.Current;
            if (client is not null) StartOrRebindPump(client);

            IsRunning = true;
            Status?.Invoke("Connected - all traffic is going through the phone.");
            return true;
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Failed: {ex.Message}");
            Cleanup();
            return false;
        }
    }

    /// <summary>
    /// Point the pump at a (possibly new) tunnel client. The pump thread is
    /// started once and only once; reconnects swap the client under it.
    /// </summary>
    private void StartOrRebindPump(Tunnel.TunnelClient client)
    {
        if (_adapter is null) return;

        // One pump for the life of the session, pointed at whichever tunnel is
        // current. PumpOutbound does not return until cancellation, so a thread
        // started on one pump can never pick up a replacement - it would keep
        // feeding packets to a dead client while everything looked healthy.
        if (_pump is null)
        {
            _pump = new PacketPump(_adapter, client, TunnelAddress);
            _pumpThread = new Thread(() => _pump!.PumpOutbound(_cts!.Token))
            {
                IsBackground = true,
                Name = "packet-pump",
            };
            _pumpThread.Start();
        }
        else
        {
            _pump.SwapTunnel(client);
        }
    }

    public void Stop()
    {
        Status?.Invoke("Disconnecting...");
        Cleanup();
        Status?.Invoke("Disconnected - normal routing restored.");
    }

    /// <summary>
    /// Exit without reverting the routes.
    ///
    /// NOTE ON WHAT THIS CANNOT DO. A Wintun adapter is owned by the process
    /// that created it: when that process exits the driver removes the adapter,
    /// and Windows drops every route through it. So a replacement cannot adopt
    /// a live interface, and there is no way to swap binaries with zero
    /// interruption - that was measured, not assumed.
    ///
    /// What this does achieve is a faster, cleaner restart: routes are left
    /// alone rather than being explicitly reverted and re-applied, so the
    /// incoming instance rebuilds in one step instead of undoing the previous
    /// one first. The gap is a second or two rather than none.
    /// </summary>
    public void HandOver()
    {
        Status?.Invoke("Handing over to the new version...");
        if (_routes is not null) _routes.HandingOver = true;

        IsRunning = false;
        _cts?.Cancel();
        _connection?.Dispose();
        _connection = null;
        _routes = null;      // deliberately not reverted
        _adapter?.Dispose(); // closing our handle leaves the adapter itself up
        _adapter = null;
        _pump = null;
        _pumpThread = null;
    }

    /// <summary>
    /// Is another instance currently serving a tunnel?
    ///
    /// The adapter only exists while its owning process does, so finding one
    /// means a live instance rather than leftovers from a crash - the opposite
    /// of what one might expect from a route journal on disk.
    /// </summary>
    public static bool HasLiveTunnel()
    {
        try
        {
            bool adapterUp = System.Net.NetworkInformation.NetworkInterface
                .GetAllNetworkInterfaces()
                .Any(n => n.Name.Contains(AdapterName, StringComparison.OrdinalIgnoreCase)
                          && n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up);

            return adapterUp && CanReachPhone(PlatformFactory.PhoneAddress);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Is the phone answering on the tunnel port?</summary>
    private static bool CanReachPhone(string host)
    {
        try
        {
            using var probe = new System.Net.Sockets.TcpClient();
            return probe.ConnectAsync(host, DefaultPort).Wait(1500) && probe.Connected;
        }
        catch
        {
            return false;
        }
    }

    private void Cleanup()
    {
        IsRunning = false;
        _cts?.Cancel();
        _connection?.Dispose();
        _connection = null;
        // Routes first, then the adapter: removing the interface underneath a
        // live default route is what leaves a machine unroutable.
        _routes?.Revert();
        _routes = null;
        _adapter?.Dispose();
        _adapter = null;
        _pump = null;
        _pumpThread = null;
    }


    public void Dispose() => Cleanup();
}
