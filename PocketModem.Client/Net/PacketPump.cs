using System.Buffers.Binary;
using System.Collections.Concurrent;
using PocketModem.Client.Tunnel;
using PocketModem.Client.Platform;

namespace PocketModem.Client.Net;

/// <summary>
/// Bridges the Wintun adapter and the tunnel (DESIGN.md §7).
///
/// Windows hands us raw IP packets; the phone speaks streams. This class does
/// the translation in both directions:
///
///   outbound  IP packet -> classify -> TCP_OPEN/TCP_DATA or UDP_DATAGRAM
///   inbound   tunnel frame -> synthesise an IP packet -> inject into Wintun
///
/// The synthesis matters: Windows' TCP stack must see a plausible, correctly
/// checksummed packet stream or it will silently discard everything. So we
/// track per-flow sequence numbers and answer the handshake ourselves, acting
/// as the remote end from Windows' point of view while the phone does the real
/// connecting.
/// </summary>
public sealed class PacketPump
{
    private readonly ITunAdapter _adapter;
    private TunnelClient _tunnel;
    private readonly string _localAddress;

    /// <summary>This PC's tunnel address in IPv6 form, for synthesised replies.</summary>
    private byte[]? _localAddress6;

    /// <summary>
    /// Which destinations belong in the tunnel.
    ///
    /// Excluded traffic is dropped here rather than forwarded, because the
    /// route table is what actually keeps it off the tunnel - a host route via
    /// the real gateway means those packets never reach this adapter at all.
    /// Anything that does arrive despite a rule saying otherwise is a routing
    /// mistake, and dropping it is safer than sending it somewhere unintended.
    /// </summary>
    public SplitRules? Split { get; set; }

    public long PacketsExcluded;

    /// <summary>Adapter MTU. Keep in step with RouteManager.ConfigureInterface.</summary>
    public const int Mtu = 1400;

    /// <summary>
    /// Window scale shift we advertise. 7 means the 16-bit window field is
    /// multiplied by 128, so 65535 becomes an 8 MB receive window - enough to
    /// keep a high-latency cellular path saturated instead of stalling every
    /// 64 KB waiting for an ACK.
    /// </summary>
    private const byte WindowScaleShift = 7;

    private readonly ConcurrentDictionary<string, TcpFlow> _flowsByKey = new();
    private readonly ConcurrentDictionary<int, TcpFlow> _flowsByStream = new();

    /// <summary>Source port to address family, so UDP replies match the request.</summary>
    private readonly ConcurrentDictionary<int, bool> _udpFamilies = new();

    public long PacketsOut;
    public long PacketsIn;
    public long BytesOut;
    public long BytesIn;

    public PacketPump(ITunAdapter adapter, TunnelClient tunnel, string localAddress)
    {
        _adapter = adapter;
        _tunnel = tunnel;
        _localAddress = localAddress;
        // Must match the address RouteManager puts on the interface, or
        // synthesised v6 replies are addressed to a machine that is not us.
        _localAddress6 = System.Net.IPAddress.Parse("fd87::2").GetAddressBytes();

        Subscribe(tunnel);
    }

    private void Subscribe(TunnelClient tunnel)
    {
        tunnel.TcpDataReceived += OnTunnelData;
        tunnel.TcpClosed += OnTunnelClosed;
        tunnel.UdpReceived += OnUdpReceived;
    }

    private void Unsubscribe(TunnelClient tunnel)
    {
        tunnel.TcpDataReceived -= OnTunnelData;
        tunnel.TcpClosed -= OnTunnelClosed;
        tunnel.UdpReceived -= OnUdpReceived;
    }

    /// <summary>
    /// Point this pump at a new tunnel after a reconnect.
    ///
    /// The reading thread stays where it is. Replacing the pump instead meant
    /// the running thread carried on inside the old one - still reading packets
    /// from the adapter and handing them to a dead client - so traffic stopped
    /// permanently the first time the tunnel reconnected, while the UI happily
    /// reported four healthy links.
    ///
    /// Existing flows are reset because the new tunnel issues new stream ids,
    /// so the old ones map to nothing on the phone.
    /// </summary>
    public void SwapTunnel(TunnelClient replacement)
    {
        var previous = _tunnel;
        ResetFlows();

        Unsubscribe(previous);
        _tunnel = replacement;
        Subscribe(replacement);
    }

    /// <summary>One TCP connection as Windows sees it.</summary>
    private sealed class TcpFlow
    {
        public required byte[] LocalIp;
        public required byte[] RemoteIp;
        public required int LocalPort;
        public required int RemotePort;
        public int StreamId;

        /// <summary>Next sequence number we will use when sending to Windows.</summary>
        public uint OurSeq;

        /// <summary>Next sequence number we expect from Windows.</summary>
        public uint TheirSeq;

        public bool Established;
        public bool Closing;

        /// <summary>
        /// Which IP version this flow speaks. Replies must be synthesised with
        /// a matching header, and the checksums differ - IPv6 has none in the
        /// IP header at all, but its pseudo-header is a different shape.
        /// </summary>
        public bool IsIpv6;

        /// <summary>Window scale shift Windows advertised on its SYN.</summary>
        public int TheirWindowScale;

        public string Key => $"{LocalPort}:{RemoteIp[0]}.{RemoteIp[1]}.{RemoteIp[2]}.{RemoteIp[3]}:{RemotePort}";
    }

    public void PumpOutbound(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // The whole body is protected, not just the handler: reading from
            // the adapter can itself throw while it is being torn down, and an
            // exception escaping a thread kills the process with no message at
            // all - which is exactly how this presented.
            try
            {
                var packet = _adapter.ReceivePacket();
                if (packet is null) continue;

                Interlocked.Increment(ref PacketsOut);
                Interlocked.Add(ref BytesOut, packet.Length);

                HandleOutbound(packet);
            }
            catch (ObjectDisposedException)
            {
                return;   // adapter closed under us; a normal shutdown
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                ActivityLog.WriteAndPrint($"  outbound packet error: {ex.Message}");
            }
        }
    }

    private void HandleOutbound(byte[] raw)
    {
        var pkt = new IpPacket(raw);
        if (!pkt.IsValid) return;

        if (Split is not null && !Split.ShouldTunnel(pkt.DestinationIp))
        {
            // The route table should have kept this off the adapter entirely.
            // Counting it makes a misrouted exclusion visible rather than
            // silently sending metered traffic through the phone.
            Interlocked.Increment(ref PacketsExcluded);
            return;
        }

        switch (pkt.ProtocolNumber)
        {
            case IpPacket.ProtocolTcp:
                HandleOutboundTcp(pkt, raw);
                break;

            case IpPacket.ProtocolUdp:
                // Remember the family per source port so the reply can be
                // rebuilt correctly; a datagram carries no flow state of its own.
                _udpFamilies[pkt.SourcePort] = pkt.IsIpv6;
                _tunnel.SendUdpAsync(pkt.SourcePort, pkt.DestinationIp, pkt.DestinationPort, pkt.UdpPayload);
                break;

            // ICMP is not forwarded in v1. Ping through the tunnel would need
            // its own relay on the phone, and it is not on the path to a
            // working browser.
        }
    }

    private void HandleOutboundTcp(in IpPacket pkt, byte[] raw)
    {
        string key = $"{pkt.SourcePort}:{pkt.DestinationAddress}:{pkt.DestinationPort}";

        if (pkt.TcpSyn && !pkt.TcpAckFlag)
        {
            // New connection. Open a stream on the phone, and complete the
            // handshake locally so Windows can start sending immediately
            // rather than waiting for a round trip to the real server.
            var flow = new TcpFlow
            {
                LocalIp = pkt.SourceIp.ToArray(),
                RemoteIp = pkt.DestinationIp.ToArray(),
                LocalPort = pkt.SourcePort,
                RemotePort = pkt.DestinationPort,
                TheirSeq = pkt.TcpSequence + 1,
                OurSeq = (uint)Random.Shared.Next(),
                IsIpv6 = pkt.IsIpv6,
            };

            flow.TheirWindowScale = ReadWindowScale(pkt);
            flow.StreamId = _tunnel.OpenStream(pkt.DestinationIp, pkt.DestinationPort);
            _flowsByKey[key] = flow;
            _flowsByStream[flow.StreamId] = flow;

            SendSynAck(flow);
            flow.OurSeq++;          // SYN consumes one sequence number
            flow.Established = true;
            return;
        }

        if (!_flowsByKey.TryGetValue(key, out var existing)) return;

        if (pkt.TcpRst)
        {
            CloseFlow(existing, notifyPhone: true);
            return;
        }

        var payload = pkt.TcpPayload;
        if (payload.Length > 0)
        {
            existing.TheirSeq = pkt.TcpSequence + (uint)payload.Length;
            _tunnel.SendTcpAsync(existing.StreamId, payload.ToArray());
            // Acknowledge immediately. Delaying would halve throughput on a
            // link where the real ACK has to cross the tunnel and back.
            SendToWindows(existing, syn: false, ack: true, fin: false, rst: false, ReadOnlySpan<byte>.Empty);
        }

        if (pkt.TcpFin)
        {
            existing.TheirSeq++;
            existing.Closing = true;
            SendToWindows(existing, syn: false, ack: true, fin: false, rst: false, ReadOnlySpan<byte>.Empty);
            _tunnel.CloseStreamAsync(existing.StreamId);
        }
    }

    private void OnTunnelData(int streamId, byte[] data)
    {
        // Runs on the tunnel's read thread. Anything escaping here takes the
        // process down silently rather than dropping one packet.
        try
        {
            SendStreamData(streamId, data);
        }
        catch (Exception ex)
        {
            ActivityLog.WriteAndPrint($"  inbound data error on stream {streamId}: {ex.Message}");
        }
    }

    private void SendStreamData(int streamId, byte[] data)
    {
        if (!_flowsByStream.TryGetValue(streamId, out var flow)) return;

        // Segment to fit the adapter MTU, minus IP (20) and TCP (20) headers.
        // Must match the MTU set in RouteManager.ConfigureInterface or Windows
        // will emit segments that need splitting here.
        const int maxSegment = Mtu - 40;
        for (int offset = 0; offset < data.Length; offset += maxSegment)
        {
            int len = Math.Min(maxSegment, data.Length - offset);
            SendToWindows(flow, syn: false, ack: true, fin: false, rst: false,
                data.AsSpan(offset, len));
            flow.OurSeq += (uint)len;
        }
    }

    private void OnTunnelClosed(int streamId, byte reason)
    {
        try
        {
            CloseStream(streamId, reason);
        }
        catch (Exception ex)
        {
            ActivityLog.WriteAndPrint($"  close error on stream {streamId}: {ex.Message}");
        }
    }

    private void CloseStream(int streamId, byte reason)
    {
        if (!_flowsByStream.TryGetValue(streamId, out var flow)) return;

        // A refused or unreachable connection must reach Windows as RST, not
        // FIN: a browser retries on RST but hangs waiting on a half-closed
        // connection.
        bool graceful = reason == Protocol.CloseReason.Normal;
        SendToWindows(flow, syn: false, ack: true, fin: graceful, rst: !graceful, ReadOnlySpan<byte>.Empty);

        CloseFlow(flow, notifyPhone: false);
    }

    /// <summary>
    /// Reset every flow after a reconnect.
    ///
    /// A new tunnel means new stream ids on the phone, so the flows this pump
    /// was tracking no longer correspond to anything. Left alone, Windows keeps
    /// sending on connections that will never be answered and each one hangs
    /// until its own timeout - minutes of a browser tab doing nothing. RST tells
    /// it immediately, and applications reconnect on their own.
    /// </summary>
    public void ResetFlows()
    {
        foreach (var flow in _flowsByKey.Values.ToArray())
        {
            SendToWindows(flow, syn: false, ack: true, fin: false, rst: true, ReadOnlySpan<byte>.Empty);
        }
        _flowsByKey.Clear();
        _flowsByStream.Clear();
    }

    private void OnUdpReceived(int srcPort, byte[] fromIp, int fromPort, byte[] data)
    {
        try
        {
            SendDatagram(srcPort, fromIp, fromPort, data);
        }
        catch (Exception ex)
        {
            ActivityLog.WriteAndPrint($"  inbound datagram error: {ex.Message}");
        }
    }

    private void SendDatagram(int srcPort, byte[] fromIp, int fromPort, byte[] data)
    {
        var packet = BuildUdpPacket(fromIp, fromPort, srcPort, data);
        _adapter.SendPacket(packet);
        Interlocked.Increment(ref PacketsIn);
        Interlocked.Add(ref BytesIn, packet.Length);
    }

    private void CloseFlow(TcpFlow flow, bool notifyPhone)
    {
        _flowsByKey.TryRemove(flow.Key, out _);
        _flowsByStream.TryRemove(flow.StreamId, out _);
        if (notifyPhone) _tunnel.CloseStreamAsync(flow.StreamId);
    }

    /// <summary>Synthesise a TCP segment from the remote end and inject it.</summary>
    private void SendToWindows(TcpFlow flow, bool syn, bool ack, bool fin, bool rst, ReadOnlySpan<byte> payload)
    {
        var packet = BuildTcpPacket(flow, syn, ack, fin, rst, payload);
        if (_adapter.SendPacket(packet))
        {
            Interlocked.Increment(ref PacketsIn);
            Interlocked.Add(ref BytesIn, packet.Length);
        }
    }

    /// <summary>
    /// SYN-ACK carrying MSS and window-scale options.
    ///
    /// Without the window-scale option the receive window is capped at 65535
    /// bytes for the life of the connection. Over cellular latency that alone
    /// limits a single connection to roughly bandwidth = 64KB / RTT - about
    /// 10 Mbps at 50 ms - no matter how fast the link underneath is. This is
    /// the single biggest throughput constraint in a userspace TCP bridge.
    /// </summary>
    private void SendSynAck(TcpFlow flow)
    {
        // MSS (4 bytes) + NOP + window scale (3 bytes) = 8, one 32-bit word.
        var options = new byte[8];
        options[0] = 2;                    // kind: MSS
        options[1] = 4;                    // length
        BinaryPrimitives.WriteUInt16BigEndian(options.AsSpan(2), Mtu - 40);
        options[4] = 1;                    // NOP, to align
        options[5] = 3;                    // kind: window scale
        options[6] = 3;                    // length
        options[7] = WindowScaleShift;

        var packet = BuildTcpPacket(flow, syn: true, ack: true, fin: false, rst: false,
            ReadOnlySpan<byte>.Empty, options);
        if (_adapter.SendPacket(packet))
        {
            Interlocked.Increment(ref PacketsIn);
            Interlocked.Add(ref BytesIn, packet.Length);
        }
    }

    /// <summary>Read the window-scale shift from a SYN's options, if present.</summary>
    private static int ReadWindowScale(in IpPacket pkt)
    {
        var seg = pkt.Payload;
        int headerLen = pkt.TcpHeaderLength;
        if (headerLen <= 20 || headerLen > seg.Length) return 0;

        int i = 20;
        while (i < headerLen)
        {
            byte kind = seg[i];
            if (kind == 0) break;                      // end of options
            if (kind == 1) { i++; continue; }          // NOP
            if (i + 1 >= headerLen) break;
            byte len = seg[i + 1];
            if (len < 2 || i + len > headerLen) break;
            if (kind == 3 && len == 3) return seg[i + 2];
            i += len;
        }
        return 0;
    }

    private static byte[] BuildTcpPacket(TcpFlow flow, bool syn, bool ack, bool fin, bool rst, ReadOnlySpan<byte> payload)
        => BuildTcpPacket(flow, syn, ack, fin, rst, payload, ReadOnlySpan<byte>.Empty);

    private static byte[] BuildTcpPacket(TcpFlow flow, bool syn, bool ack, bool fin, bool rst, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> options)
    {
        if (flow.IsIpv6)
            return BuildTcpPacket6(flow, syn, ack, fin, rst, payload, options);

        int tcpHeaderLen = 20 + options.Length;
        int total = 20 + tcpHeaderLen + payload.Length;
        var buf = new byte[total];

        // --- IPv4 header ---
        buf[0] = 0x45;                                   // version 4, IHL 5
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), (ushort)total);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), (ushort)Random.Shared.Next(65536));
        buf[6] = 0x40;                                   // don't fragment
        buf[8] = 64;                                     // TTL
        buf[9] = IpPacket.ProtocolTcp;
        flow.RemoteIp.CopyTo(buf.AsSpan(12));            // from the remote host
        flow.LocalIp.CopyTo(buf.AsSpan(16));             // to this PC
        WriteChecksum(buf.AsSpan(0, 20), 10);

        // --- TCP header ---
        var tcp = buf.AsSpan(20);
        BinaryPrimitives.WriteUInt16BigEndian(tcp, (ushort)flow.RemotePort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], (ushort)flow.LocalPort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], flow.OurSeq);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[8..], flow.TheirSeq);
        tcp[12] = (byte)((tcpHeaderLen / 4) << 4);       // data offset in 32-bit words

        byte flags = 0;
        if (fin) flags |= 0x01;
        if (syn) flags |= 0x02;
        if (rst) flags |= 0x04;
        if (payload.Length > 0) flags |= 0x08;           // PSH
        if (ack) flags |= 0x10;
        tcp[13] = flags;

        // The window field is scaled by the shift we advertised, EXCEPT on the
        // SYN itself - the option is being negotiated there, so that one value
        // is unscaled by definition.
        BinaryPrimitives.WriteUInt16BigEndian(tcp[14..], 65535);

        if (!options.IsEmpty) options.CopyTo(tcp[20..]);
        payload.CopyTo(tcp[tcpHeaderLen..]);

        WriteTcpChecksum(buf, flow.RemoteIp, flow.LocalIp, tcpHeaderLen + payload.Length);
        return buf;
    }

    /// <summary>
    /// The IPv6 form. Simpler than v4 in one way - the header carries no
    /// checksum of its own - and stricter in another: the TCP checksum is
    /// mandatory, and its pseudo-header uses 16-byte addresses and a 32-bit
    /// length.
    /// </summary>
    private static byte[] BuildTcpPacket6(TcpFlow flow, bool syn, bool ack, bool fin, bool rst, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> options)
    {
        int tcpHeaderLen = 20 + options.Length;
        int tcpLength = tcpHeaderLen + payload.Length;
        var buf = new byte[40 + tcpLength];

        // --- IPv6 header ---
        buf[0] = 0x60;                                   // version 6, no traffic class
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), (ushort)tcpLength);
        buf[6] = IpPacket.ProtocolTcp;                   // next header
        buf[7] = 64;                                     // hop limit
        flow.RemoteIp.CopyTo(buf.AsSpan(8));             // from the remote host
        flow.LocalIp.CopyTo(buf.AsSpan(24));             // to this PC

        // --- TCP header ---
        var tcp = buf.AsSpan(40);
        BinaryPrimitives.WriteUInt16BigEndian(tcp, (ushort)flow.RemotePort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], (ushort)flow.LocalPort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], flow.OurSeq);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[8..], flow.TheirSeq);
        tcp[12] = (byte)((tcpHeaderLen / 4) << 4);

        byte flags = 0;
        if (fin) flags |= 0x01;
        if (syn) flags |= 0x02;
        if (rst) flags |= 0x04;
        if (payload.Length > 0) flags |= 0x08;
        if (ack) flags |= 0x10;
        tcp[13] = flags;

        BinaryPrimitives.WriteUInt16BigEndian(tcp[14..], 65535);
        if (!options.IsEmpty) options.CopyTo(tcp[20..]);
        payload.CopyTo(tcp[tcpHeaderLen..]);

        WriteTcpChecksum6(buf, flow.RemoteIp, flow.LocalIp, tcpLength);
        return buf;
    }

    /// <summary>TCP checksum over the IPv6 pseudo-header.</summary>
    private static void WriteTcpChecksum6(byte[] packet, byte[] srcIp, byte[] dstIp, int tcpLength)
    {
        var tcp = packet.AsSpan(40, tcpLength);
        tcp[16] = 0;
        tcp[17] = 0;

        uint sum = 0;
        for (int i = 0; i < 16; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(srcIp.AsSpan(i));
        for (int i = 0; i < 16; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(dstIp.AsSpan(i));

        // Upper-layer length is 32 bits in the v6 pseudo-header, unlike v4.
        sum += (uint)(tcpLength >> 16) & 0xFFFF;
        sum += (uint)tcpLength & 0xFFFF;
        sum += IpPacket.ProtocolTcp;

        for (int i = 0; i + 1 < tcpLength; i += 2)
            sum += BinaryPrimitives.ReadUInt16BigEndian(tcp[i..]);
        if ((tcpLength & 1) != 0) sum += (uint)(tcp[tcpLength - 1] << 8);

        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[16..], (ushort)~sum);
    }

    private byte[] BuildUdpPacket(byte[] fromIp, int fromPort, int toPort, byte[] data)
    {
        if (fromIp.Length == 16) return BuildUdpPacket6(fromIp, fromPort, toPort, data);

        int total = 20 + 8 + data.Length;
        var buf = new byte[total];

        buf[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), (ushort)total);
        buf[6] = 0x40;
        buf[8] = 64;
        buf[9] = IpPacket.ProtocolUdp;
        fromIp.CopyTo(buf.AsSpan(12));
        ParseIp(_localAddress).CopyTo(buf.AsSpan(16));
        WriteChecksum(buf.AsSpan(0, 20), 10);

        var udp = buf.AsSpan(20);
        BinaryPrimitives.WriteUInt16BigEndian(udp, (ushort)fromPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[2..], (ushort)toPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[4..], (ushort)(8 + data.Length));
        data.CopyTo(udp[8..]);

        // Compute it rather than leaving zero. A zero checksum is legal over
        // IPv4 and most stacks accept it, but QUIC implementations commonly
        // reject datagrams without one - which breaks HTTP/3 sites while
        // ordinary TCP sites work, an oddly selective failure to diagnose.
        WriteUdpChecksum(buf, fromIp, ParseIp(_localAddress), 8 + data.Length);

        return buf;
    }

    /// <summary>
    /// UDP checksum over the IPv4 pseudo-header.
    ///
    /// Optional in the standard, but not in practice: QUIC stacks tend to
    /// require it, so omitting it silently breaks HTTP/3.
    /// </summary>
    private static void WriteUdpChecksum(byte[] packet, byte[] srcIp, byte[] dstIp, int udpLength)
    {
        var udp = packet.AsSpan(20, udpLength);
        udp[6] = 0;
        udp[7] = 0;

        uint sum = 0;
        for (int i = 0; i < 4; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(srcIp.AsSpan(i));
        for (int i = 0; i < 4; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(dstIp.AsSpan(i));
        sum += IpPacket.ProtocolUdp;
        sum += (uint)udpLength;

        for (int i = 0; i + 1 < udpLength; i += 2)
            sum += BinaryPrimitives.ReadUInt16BigEndian(udp[i..]);
        if ((udpLength & 1) != 0) sum += (uint)(udp[udpLength - 1] << 8);

        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);

        // A computed zero is transmitted as 0xFFFF, since zero is reserved to
        // mean "no checksum".
        ushort value = (ushort)~sum;
        BinaryPrimitives.WriteUInt16BigEndian(udp[6..], value == 0 ? (ushort)0xFFFF : value);
    }

    /// <summary>
    /// IPv6 UDP. Unlike v4, the checksum here is mandatory rather than
    /// optional, so it is always computed.
    /// </summary>
    private byte[] BuildUdpPacket6(byte[] fromIp, int fromPort, int toPort, byte[] data)
    {
        int udpLength = 8 + data.Length;
        var buf = new byte[40 + udpLength];

        buf[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), (ushort)udpLength);
        buf[6] = IpPacket.ProtocolUdp;
        buf[7] = 64;
        fromIp.CopyTo(buf.AsSpan(8));
        (_localAddress6 ?? new byte[16]).CopyTo(buf.AsSpan(24));

        var udp = buf.AsSpan(40);
        BinaryPrimitives.WriteUInt16BigEndian(udp, (ushort)fromPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[2..], (ushort)toPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[4..], (ushort)udpLength);
        data.CopyTo(udp[8..]);

        WriteUdpChecksum6(buf, fromIp, _localAddress6 ?? new byte[16], udpLength);
        return buf;
    }

    private static void WriteUdpChecksum6(byte[] packet, byte[] srcIp, byte[] dstIp, int udpLength)
    {
        var udp = packet.AsSpan(40, udpLength);
        udp[6] = 0;
        udp[7] = 0;

        uint sum = 0;
        for (int i = 0; i < 16; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(srcIp.AsSpan(i));
        for (int i = 0; i < 16; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(dstIp.AsSpan(i));
        sum += (uint)(udpLength >> 16) & 0xFFFF;
        sum += (uint)udpLength & 0xFFFF;
        sum += IpPacket.ProtocolUdp;

        for (int i = 0; i + 1 < udpLength; i += 2)
            sum += BinaryPrimitives.ReadUInt16BigEndian(udp[i..]);
        if ((udpLength & 1) != 0) sum += (uint)(udp[udpLength - 1] << 8);

        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        // A computed zero is sent as 0xFFFF: zero means "no checksum", which
        // IPv6 does not permit.
        ushort value = (ushort)~sum;
        BinaryPrimitives.WriteUInt16BigEndian(udp[6..], value == 0 ? (ushort)0xFFFF : value);
    }

    private static byte[] ParseIp(string address) =>
        address.Split('.').Select(byte.Parse).ToArray();

    private static void WriteChecksum(Span<byte> header, int checksumOffset)
    {
        header[checksumOffset] = 0;
        header[checksumOffset + 1] = 0;

        uint sum = 0;
        for (int i = 0; i + 1 < header.Length; i += 2)
            sum += BinaryPrimitives.ReadUInt16BigEndian(header[i..]);
        if ((header.Length & 1) != 0) sum += (uint)(header[^1] << 8);

        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        BinaryPrimitives.WriteUInt16BigEndian(header[checksumOffset..], (ushort)~sum);
    }

    /// <summary>TCP checksum, which covers a pseudo-header of the IP addresses.</summary>
    private static void WriteTcpChecksum(byte[] packet, byte[] srcIp, byte[] dstIp, int tcpLength)
    {
        var tcp = packet.AsSpan(20, tcpLength);
        tcp[16] = 0;
        tcp[17] = 0;

        uint sum = 0;
        for (int i = 0; i < 4; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(srcIp.AsSpan(i));
        for (int i = 0; i < 4; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(dstIp.AsSpan(i));
        sum += IpPacket.ProtocolTcp;
        sum += (uint)tcpLength;

        for (int i = 0; i + 1 < tcpLength; i += 2)
            sum += BinaryPrimitives.ReadUInt16BigEndian(tcp[i..]);
        if ((tcpLength & 1) != 0) sum += (uint)(tcp[tcpLength - 1] << 8);

        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[16..], (ushort)~sum);
    }
}
