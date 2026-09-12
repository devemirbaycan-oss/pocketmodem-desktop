namespace PocketModem.Client.Tunnel;

/// <summary>
/// Keeps the tunnel alive across drops (DESIGN.md §8.1).
///
/// The original complaint about PdaNet was "slow, not working reliably", and
/// speed is only half of that. This is the other half: a link that wobbles must
/// recover on its own rather than requiring the user to restart everything.
///
/// The ladder:
///
///   PING every 2 s
///   no PONG for 6 s      -> reopen the tunnel socket        (~1 s)
///   3 failed reopens     -> the link is presumed gone; wait for it to return
///   link back            -> reopen and carry on
///
/// The critical rule is that the Wintun adapter and the route table are NEVER
/// torn down during a reconnect. Windows aggressively marks an interface
/// unusable when it disappears, and applications give up on it; keeping the
/// adapter up means a reconnect looks like a brief stall rather than the
/// network vanishing.
/// </summary>
public sealed class ConnectionManager : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _token;

    private TunnelClient? _client;
    private CancellationTokenSource? _cts;
    private DateTime _lastPong = DateTime.UtcNow;
    private long _lastBytesSeen;
    private int _consecutiveFailures;

    public event Action<TunnelClient>? Connected;
    public event Action<string>? StateChanged;

    public TunnelClient? Current => _client;
    public bool IsConnected => _client?.IsConnected == true;
    public int ReconnectCount { get; private set; }

    /// <summary>Why the links closed, newest first. Empty before any drop.</summary>
    public IReadOnlyList<string> RecentCloses =>
        _lastCloses.Count > 0 ? _lastCloses : (_client?.RecentCloses ?? Array.Empty<string>());
    private List<string> _lastCloses = new();

    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Called when reconnection keeps failing, so the caller can rejoin the
    /// phone's Wi-Fi. A dropped P2P group takes the whole network with it, and
    /// retrying a socket against an address this PC can no longer reach will
    /// never succeed however long it runs.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? RejoinNetwork { get; set; }

    public ConnectionManager(string host, int port, string token)
    {
        _host = host;
        _port = port;
        _token = token;
    }

    public async Task<bool> StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (!await ConnectOnceAsync(_cts.Token))
            return false;

        _ = Task.Run(() => SuperviseAsync(_cts.Token), _cts.Token);
        return true;
    }

    private void OnPong() => _lastPong = DateTime.UtcNow;

    private async Task<bool> ConnectOnceAsync(CancellationToken ct)
    {
        var client = new TunnelClient(_host, _port);
        // A named handler so it can be detached again. A lambda per reconnect
        // left every previous client still updating this clock, and a disposed
        // client could keep a dead session looking alive.
        client.PongReceived += OnPong;

        if (!await client.ConnectAsync(_token, ct))
        {
            client.Dispose();
            return false;
        }

        // Give the phone a moment to accept or reject the token before
        // declaring success, so a rejected pairing is reported as such.
        await Task.Delay(400, ct);
        if (client.Rejected)
        {
            StateChanged?.Invoke("rejected: the phone did not accept this pairing code");
            client.Dispose();
            return false;
        }

        _client = client;
        _lastPong = DateTime.UtcNow;
        _consecutiveFailures = 0;
        Connected?.Invoke(client);
        return true;
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PingInterval, ct);

                var client = _client;
                if (client is null) continue;

                if (client.IsConnected)
                {
                    await client.PingAsync();

                    // Bytes arriving are proof the tunnel works, whatever the
                    // pongs are doing. Without this a heavily loaded link can
                    // delay a pong past the timeout and get a working session
                    // torn down for it.
                    long received = client.BytesReceived;
                    if (received != _lastBytesSeen)
                    {
                        _lastBytesSeen = received;
                        _lastPong = DateTime.UtcNow;
                    }

                    // Silence for longer than the timeout means the socket is
                    // up as far as the OS knows but nothing is getting through.
                    if (DateTime.UtcNow - _lastPong > PongTimeout)
                    {
                        StateChanged?.Invoke("no response from phone - reconnecting");
                        await ReconnectAsync(ct);
                    }
                    else if (!client.UpstreamUp)
                    {
                        // The phone is answering but has no internet of its own,
                        // which is what a network handover looks like from here.
                        // Reconnecting would not help - the link is fine - so say
                        // so and wait for the phone to come back, rather than
                        // reporting a healthy connection that carries nothing.
                        StateChanged?.Invoke("phone has no mobile data right now");
                    }
                }
                else
                {
                    StateChanged?.Invoke("tunnel closed - reconnecting");
                    await ReconnectAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                StateChanged?.Invoke($"supervisor error: {ex.Message}");
            }
        }
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        var old = _client;
        _client = null;
        if (old is not null)
        {
            // Capture before disposing: otherwise the reasons disappear at the
            // exact moment someone wants to read them.
            _lastCloses = old.RecentCloses.ToList();
            old.PongReceived -= OnPong;
            old.Dispose();
        }

        while (!ct.IsCancellationRequested)
        {
            _consecutiveFailures++;

            // Distinguish the two failures that look identical from the UI:
            // the PC has fallen off the phone's Wi-Fi, or it is still on the
            // network but the phone is not answering.
            // Rejoin FIRST when we already know the PC is off the phone's
            // network. Trying the socket first burned attempts against an
            // address that cannot be reached, and waiting for three failures
            // before rejoining meant several seconds of guaranteed-futile
            // retries every time.
            bool onNetwork = Platform.PlatformFactory.IsJoinedToPhone();

            if (!onNetwork && RejoinNetwork is not null)
            {
                StateChanged?.Invoke($"rejoining the phone's Wi-Fi (attempt {_consecutiveFailures})");
                try
                {
                    if (!await RejoinNetwork(ct))
                    {
                        // The group is probably still being rebuilt on the
                        // phone; wait for it rather than hammering.
                        StateChanged?.Invoke("waiting for the phone's network");
                        await Task.Delay(TimeSpan.FromSeconds(3), ct);
                        continue;
                    }
                    StateChanged?.Invoke("rejoined");
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    StateChanged?.Invoke($"rejoin failed: {ex.Message}");
                }
            }
            else if (!onNetwork)
            {
                StateChanged?.Invoke($"not on the phone's Wi-Fi (attempt {_consecutiveFailures})");
            }
            else
            {
                StateChanged?.Invoke($"phone not answering (attempt {_consecutiveFailures})");
            }

            if (await ConnectOnceAsync(ct))
            {
                ReconnectCount++;
                StateChanged?.Invoke($"reconnected (attempt {_consecutiveFailures})");
                return;
            }

            // Still failing while apparently on the network: the profile may
            // say connected while the group underneath is gone, so force a
            // genuine rejoin rather than trusting it.
            if (_consecutiveFailures % 3 == 0 && RejoinNetwork is not null)
            {
                StateChanged?.Invoke("forcing a fresh join...");
                try { await RejoinNetwork(ct); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { StateChanged?.Invoke($"rejoin failed: {ex.Message}"); }
            }

            // Back off, but stay responsive: the phone may reappear at any
            // moment and the adapter is still up, so the user sees a stall
            // rather than a dead network.
            var delay = TimeSpan.FromSeconds(Math.Min(_consecutiveFailures * 2, 15));
            if (_consecutiveFailures == 3)
                StateChanged?.Invoke("phone unreachable - waiting for it to come back");

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _client?.Dispose();
        _cts?.Dispose();
    }
}
