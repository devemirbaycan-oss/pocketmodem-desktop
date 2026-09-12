using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;

namespace PocketModem.Client.Tunnel;

/// <summary>
/// Client half of the tunnel (DESIGN.md §5).
///
/// Opens several parallel connections rather than one, and pins each stream to
/// a link by id (§5.2). With a single connection one lost Wi-Fi frame stalls
/// every multiplexed stream behind it — felt as an occasional total freeze
/// rather than steady slowness, which is the symptom PdaNet is disliked for.
/// Spreading streams across N links cuts a stall's blast radius to 1/N.
///
/// Pinning is not optional: moving a stream between links would reorder its
/// bytes, and a TCP payload must arrive in order. The mapping here must match
/// TunnelServer.linkFor on the phone.
///
/// This is the cheap 80% of what QUIC would give, with no new transport. QUIC
/// remains the proper fix, and the framing is deliberately transport-agnostic
/// so that change stays a transport swap rather than a rewrite.
/// </summary>
public sealed class TunnelClient : IDisposable
{
    /// <summary>Must match TunnelServer.LINK_COUNT on the phone.</summary>
    public const int LinkCount = 4;

    private readonly string _host;
    private readonly int _port;

    /// <summary>
    /// Identifies this PC to the phone, so several can share it without their
    /// links and streams colliding.
    ///
    /// Derived from the machine name rather than random, so reconnecting takes
    /// over this PC's own slot instead of consuming a new one each time and
    /// exhausting the limit after a few restarts.
    /// </summary>
    private readonly int _clientId;

    private sealed class Link : IDisposable
    {
        public required int Index;
        public required TcpClient Client;
        public required NetworkStream Stream;
        public readonly SemaphoreSlim WriteLock = new(1, 1);
        public volatile bool Closed;

        public void Dispose()
        {
            Closed = true;
            try { Stream.Dispose(); } catch { }
            try { Client.Dispose(); } catch { }
            try { WriteLock.Dispose(); } catch { }
        }
    }

    private readonly ConcurrentDictionary<int, Link> _links = new();

    /// <summary>
    /// Frame encryption keyed from the pairing token (§5.3). Created once the
    /// token is known, which is before the first link is opened.
    /// </summary>
    private Crypto? _crypto;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Kept so a link can be reopened later. Every link authenticates
    /// independently, so repairing one needs the same token the first four
    /// were opened with.
    /// </summary>
    private string _token = "";

    /// <summary>
    /// Spreads datagrams over the links.
    ///
    /// UDP has no stream id to hash - every datagram goes out as stream 0 - so
    /// without this the whole of UDP rode link 0. Round-robin is the right
    /// choice here rather than hashing by port: datagrams are independent, so
    /// there is no ordering to preserve, and a hash would still pin one busy
    /// flow to one link.
    /// </summary>
    private int _udpCursor;

    /// <summary>
    /// Shared across reconnects, because a new client restarting at 1 would
    /// reuse ids the phone may still be holding from the previous session.
    /// </summary>
    private static int _nextStreamId;
    private readonly ConcurrentDictionary<int, StreamHandle> _streams = new();

    public event Action<int, byte[]>? TcpDataReceived;
    public event Action<int, byte>? TcpClosed;
    public event Action<int, byte[], int, byte[]>? UdpReceived;
    public event Action<string>? Disconnected;
    public event Action? PongReceived;

    /// <summary>
    /// What the phone last said about its own internet connection.
    ///
    /// Separate from IsConnected, which is only about the link to the phone.
    /// Both must hold for traffic to flow, and they fail independently: during
    /// a handover between LTE and 5G the link stays perfectly healthy while
    /// nothing reaches the internet. Until the phone began stamping this onto
    /// pongs, the PC could see only the first and reported "connected"
    /// throughout.
    /// </summary>
    public bool UpstreamUp { get; private set; } = true;

    /// <summary>Set when the phone refused our pairing token.</summary>
    public bool Rejected { get; private set; }

    /// <summary>
    /// Connected while at least one link is alive: losing one of four is a
    /// degraded session, not a dead one.
    /// </summary>
    public bool IsConnected => _links.Values.Any(l => !l.Closed && l.Client.Connected);

    public int ConnectedLinks => _links.Values.Count(l => !l.Closed);

    /// <summary>
    /// Why each link last closed, newest first.
    ///
    /// "tunnel closed" on its own tells nobody anything. Whether the socket was
    /// reset, timed out, or the host became unreachable is the difference
    /// between a phone-side drop and the Wi-Fi going away, and it is the first
    /// question worth asking when a session fails.
    /// </summary>
    public IReadOnlyList<string> RecentCloses => _closeReasons.ToArray();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _closeReasons = new();

    private void RecordClose(int index, Exception? ex)
    {
        string reason = ex switch
        {
            null => "closed cleanly",
            SocketException se => $"socket {se.SocketErrorCode}",
            IOException io when io.InnerException is SocketException inner
                => $"socket {inner.SocketErrorCode}",
            IOException => "stream ended",
            _ => ex.GetType().Name,
        };
        _closeReasons.Enqueue($"{DateTime.Now:HH:mm:ss} link {index}: {reason}");
        while (_closeReasons.Count > 12) _closeReasons.TryDequeue(out _);
    }
    public long BytesSent;
    public long BytesReceived;
    public int ActiveStreams => _streams.Count;

    public sealed record StreamHandle(int Id, string Destination, int Port);

    public TunnelClient(string host, int port)
    {
        _host = host;
        _port = port;
        _clientId = StableClientId();
    }

    /// <summary>
    /// A stable id for this machine. Hashing the machine name keeps it the
    /// same across restarts, which is what makes a reconnect reclaim the same
    /// slot on the phone.
    /// </summary>
    private static int StableClientId()
    {
        try
        {
            var name = Environment.MachineName + "|" + Environment.UserName;
            var hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(name));
            return BitConverter.ToInt32(hash, 0) & 0x7FFFFFFF;
        }
        catch
        {
            return 0;
        }
    }

    public async Task<bool> ConnectAsync(string token = "", CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Both sides derive the same key from the token, so nothing extra has
        // to be exchanged. HELLO itself stays in the clear because it carries
        // the token that proves we are allowed to talk at all.
        if (!string.IsNullOrEmpty(token)) _crypto = new Crypto(token);
        _token = token;

        // Link 0 first: if the pairing token is wrong, learn it once rather
        // than opening four connections only to have all four refused.
        if (!await OpenLinkAsync(0, token, _cts.Token))
            return false;

        await Task.Delay(300, ct);
        if (Rejected) return false;

        for (int i = 1; i < LinkCount; i++)
        {
            if (!await OpenLinkAsync(i, token, _cts.Token))
                Console.WriteLine($"  tunnel: link {i} could not be opened; continuing");
        }

        // Links die individually - a phone that briefly stops answering, a
        // radio glitch, one socket reset - and until now none of them ever
        // came back. Nothing else notices: IsConnected is true while any link
        // lives, so a session could sit at one link of four indefinitely,
        // carrying four times the traffic it was designed for on a quarter of
        // the sockets.
        _ = Task.Run(() => RepairLinksAsync(_cts.Token), _cts.Token);

        return true;
    }

    /// <summary>
    /// Reopen links that have died, while the tunnel is otherwise healthy.
    ///
    /// Deliberately separate from the reconnect path in ConnectionManager: that
    /// one rebuilds the whole tunnel and resets every TCP flow, which is the
    /// right response to losing the last link and far too much for losing one
    /// of four. Repairing in place keeps every open connection alive.
    /// </summary>
    private async Task RepairLinksAsync(CancellationToken ct)
    {
        // Long enough that a link dropping and instantly returning does not
        // cause a reconnect storm, short enough that a degraded session
        // recovers before anyone notices it.
        var interval = TimeSpan.FromSeconds(5);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);

                // Nothing to repair onto: the whole tunnel is gone, and that
                // is ConnectionManager's job, not this loop's.
                if (!IsConnected) continue;

                for (int i = 0; i < LinkCount; i++)
                {
                    if (ct.IsCancellationRequested) return;
                    if (_links.TryGetValue(i, out var existing) && !existing.Closed) continue;

                    if (await OpenLinkAsync(i, _token, ct, repair: true))
                        Console.WriteLine($"  tunnel: link {i} restored ({ConnectedLinks}/{LinkCount})");
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed repair is not fatal - the surviving links keep
                // carrying traffic, and the next pass tries again.
                Console.WriteLine($"  tunnel: link repair failed: {ex.Message}");
            }
        }
    }

    private async Task<bool> OpenLinkAsync(int index, string token, CancellationToken ct,
        bool repair = false)
    {
        try
        {
            var client = new TcpClient
            {
                NoDelay = true,
                ReceiveBufferSize = 512 * 1024,
                SendBufferSize = 512 * 1024,
            };
            await client.ConnectAsync(_host, _port, ct);

            var link = new Link
            {
                Index = index,
                Client = client,
                Stream = client.GetStream(),
            };
            _links[index] = link;

            _ = Task.Run(() => ReadLoopAsync(link, ct), ct);

            // HELLO: version(1) linkIndex(1) clientId(4) token(rest)
            var tokenBytes = System.Text.Encoding.ASCII.GetBytes(token);
            var hello = new byte[2 + Protocol.ClientIdSize + tokenBytes.Length];
            hello[0] = Protocol.ProtocolVersion;
            hello[1] = (byte)index;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
                hello.AsSpan(2, Protocol.ClientIdSize), _clientId);
            tokenBytes.CopyTo(hello, 2 + Protocol.ClientIdSize);

            // The repair bit keeps the phone from treating a reopened link 0
            // as a fresh session and discarding streams the other links are
            // still carrying.
            byte flags = repair ? Protocol.FlagLinkRepair : (byte)0;
            await SendOnAsync(link, Protocol.Hello, flags, 0, hello);
            return true;
        }
        catch (Exception ex)
        {
            if (index == 0) Disconnected?.Invoke(ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Which link carries a stream. Must match TunnelServer.linkFor on the
    /// phone. Stream 0 (control, UDP) rides link 0.
    /// </summary>
    private static int LinkFor(int streamId) =>
        streamId == 0 ? 0 : ((streamId % LinkCount) + LinkCount) % LinkCount;

    private Link? PickLink(int streamId)
    {
        if (_links.TryGetValue(LinkFor(streamId), out var preferred) && !preferred.Closed)
            return preferred;
        // Fall back to any live link so a frame is not lost while one link is
        // reconnecting.
        return _links.Values.FirstOrDefault(l => !l.Closed);
    }

    /// <summary>
    /// The next link for a datagram, skipping dead ones.
    ///
    /// Unlike a TCP stream, a datagram has no ordering to protect, so it can
    /// take whichever link is free rather than being pinned to one.
    /// </summary>
    private Link? PickLinkForDatagram()
    {
        for (int attempt = 0; attempt < LinkCount; attempt++)
        {
            int index = (int)((uint)Interlocked.Increment(ref _udpCursor) % LinkCount);
            if (_links.TryGetValue(index, out var link) && !link.Closed)
                return link;
        }

        // Every preferred slot was dead; take anything still open.
        return _links.Values.FirstOrDefault(l => !l.Closed);
    }

    public int OpenStream(ReadOnlySpan<byte> dstIp, int dstPort)
    {
        // Keep ids positive and never reuse one that is still live.
        //
        // Masking the counter is not enough on its own: two different counter
        // values can mask to the same id, and the phone then sees a duplicate
        // TCP_OPEN and drops the connection. Checking the live set closes that,
        // and 0 is reserved for control and UDP.
        int id;
        do
        {
            id = Interlocked.Increment(ref _nextStreamId) & 0x7FFFFFFF;
        }
        while (id == 0 || _streams.ContainsKey(id));
        string dst = $"{dstIp[0]}.{dstIp[1]}.{dstIp[2]}.{dstIp[3]}";
        _streams[id] = new StreamHandle(id, dst, dstPort);

        var payload = Protocol.EncodeOpen(dstIp, dstPort);
        _ = SendFrameAsync(Protocol.TcpOpen, 0, id, payload);
        return id;
    }

    public Task SendTcpAsync(int streamId, byte[] data) =>
        SendFrameAsync(Protocol.TcpData, 0, streamId, data);

    public Task CloseStreamAsync(int streamId)
    {
        _streams.TryRemove(streamId, out _);
        return SendFrameAsync(Protocol.TcpClose, Protocol.CloseReason.Normal, streamId, Array.Empty<byte>());
    }

    public Task SendUdpAsync(int srcPort, ReadOnlySpan<byte> dstIp, int dstPort, ReadOnlySpan<byte> data)
    {
        // Flag the family: the payload after the address is variable, so the
        // receiver cannot infer the width from the frame length.
        byte flags = dstIp.Length == 16 ? Protocol.FlagIpv6 : (byte)0;
        var payload = Protocol.EncodeDatagram(srcPort, dstIp, dstPort, data);

        // Round-robin rather than link 0. The frame still carries stream id 0 -
        // that is its protocol identity and the phone reads it the same way -
        // but which socket carries it is ours to choose. The phone's read loop
        // uses the arriving link only to read from, so nothing there depends
        // on UDP arriving on link 0.
        var link = PickLinkForDatagram();
        if (link is null) return Task.CompletedTask;
        return SendOnAsync(link, Protocol.UdpDatagram, flags, 0, payload);
    }

    /// <summary>
    /// Ping every link, not just the one stream 0 maps to.
    ///
    /// Pinging a single link means a brief stall on that one link looks like a
    /// dead tunnel, and the supervisor tears down a session whose other three
    /// links are carrying traffic perfectly well. A pong from any link proves
    /// the tunnel is alive, which is the question actually being asked.
    /// </summary>
    public async Task PingAsync()
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(payload, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        foreach (var link in _links.Values.Where(l => !l.Closed).ToArray())
            await SendOnAsync(link, Protocol.Ping, 0, 0, payload);
    }

    private Task SendFrameAsync(byte type, byte flags, int streamId, byte[] payload)
    {
        var link = PickLink(streamId);
        if (link is null) return Task.CompletedTask;
        return SendOnAsync(link, type, flags, streamId, payload);
    }

    private async Task SendOnAsync(Link link, byte type, byte flags, int streamId, byte[] payload)
    {
        // HELLO is the one frame sent in the clear: it carries the pairing
        // token the key is derived from.
        var body = (_crypto is not null && type != Protocol.Hello && payload.Length > 0)
            ? _crypto.Seal(payload)
            : payload;

        // Header and payload go out as ONE write. Two writes plus a flush per
        // frame meant three syscalls and, with TCP_NODELAY, several small
        // segments on the wire for every 1360 bytes of data.
        var frame = new byte[Protocol.HeaderSize + body.Length];
        Protocol.WriteHeader(frame, type, flags, streamId, body.Length);
        if (body.Length > 0) body.CopyTo(frame, Protocol.HeaderSize);

        try
        {
            await link.WriteLock.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            await link.Stream.WriteAsync(frame);
            Interlocked.Add(ref BytesSent, body.Length);
        }
        catch (Exception ex)
        {
            link.Closed = true;
            _links.TryRemove(link.Index, out _);
            RecordClose(link.Index, ex);
            // Only report a full disconnect when the last link goes.
            if (!IsConnected) Disconnected?.Invoke(ex.Message);
        }
        finally
        {
            try { link.WriteLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task ReadLoopAsync(Link link, CancellationToken ct)
    {
        var header = new byte[Protocol.HeaderSize];
        try
        {
            while (!ct.IsCancellationRequested && !link.Closed)
            {
                await ReadExactlyAsync(link.Stream, header, Protocol.HeaderSize, ct);
                var (type, flags, streamId, length) = Protocol.ReadHeader(header);

                var payload = length > 0 ? new byte[length] : Array.Empty<byte>();
                if (length > 0) await ReadExactlyAsync(link.Stream, payload, length, ct);

                Interlocked.Add(ref BytesReceived, length);

                if (_crypto is not null && type != Protocol.HelloAck && payload.Length > 0)
                {
                    var plain = _crypto.Open(payload);
                    if (plain is null)
                    {
                        // Tampered, corrupt, or sealed under a different key.
                        Console.WriteLine("  tunnel: dropped a frame that failed authentication");
                        continue;
                    }
                    payload = plain;
                }

                Dispatch(type, flags, streamId, payload);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            link.Closed = true;
            _links.TryRemove(link.Index, out _);
            RecordClose(link.Index, ex);
            if (!IsConnected) Disconnected?.Invoke(ex.Message);
        }
    }

    private void Dispatch(byte type, byte flags, int streamId, byte[] payload)
    {
        switch (type)
        {
            case Protocol.HelloAck:
                // A non-zero flag means the phone refused the pairing token.
                if (flags != 0)
                {
                    Rejected = true;
                    Console.WriteLine("  tunnel: phone REJECTED this pairing code");
                }
                break;

            case Protocol.TcpData:
                TcpDataReceived?.Invoke(streamId, payload);
                break;

            case Protocol.TcpClose:
                _streams.TryRemove(streamId, out _);
                TcpClosed?.Invoke(streamId, flags);
                break;

            case Protocol.UdpDatagram:
                var (srcPort, dstIp, dstPort, data) =
                    Protocol.DecodeDatagram(payload, (flags & Protocol.FlagIpv6) != 0);
                UdpReceived?.Invoke(srcPort, dstIp, dstPort, data);
                break;

            case Protocol.Pong:
                // Anything past the echoed 8-byte timestamp is the phone's
                // upstream state. A phone predating this sends nothing extra,
                // which leaves the flag true - the old behaviour, so an older
                // phone keeps working rather than looking permanently offline.
                if (payload.Length > 8)
                    UpstreamUp = payload[8] != Protocol.UpstreamState.Down;

                PongReceived?.Invoke();
                break;

            default:
                Console.WriteLine($"  tunnel: unhandled frame 0x{type:x2}");
                break;
        }
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n <= 0) throw new IOException("tunnel closed by peer");
            read += n;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        foreach (var link in _links.Values) link.Dispose();
        _links.Clear();
        _cts?.Dispose();
    }
}
