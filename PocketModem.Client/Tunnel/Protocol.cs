using System.Buffers.Binary;

namespace PocketModem.Client.Tunnel;

/// <summary>
/// Wire format mirror of the Kotlin Protocol object (DESIGN.md §5.1).
///
///   +--------+--------+----------+--------+---------+
///   | type   | flags  | streamId | length | payload |
///   | 1 byte | 1 byte | 4 bytes  | 2 bytes|   ...   |
///   +--------+--------+----------+--------+---------+
///
/// Big-endian, matching ByteBuffer's default on the Android side. Any change
/// here must be made in Protocol.kt at the same time.
/// </summary>
public static class Protocol
{
    public const int HeaderSize = 8;
    public const int MaxPayload = 65535;
    /// <summary>
    /// Version 2 adds a client id to HELLO so several PCs can share one phone.
    /// A v1 client sends none and is treated as client 0, so a single PC keeps
    /// working across the upgrade.
    /// </summary>
    public const byte ProtocolVersion = 2;

    /// <summary>HELLO: version(1) linkIndex(1) clientId(4) token(rest) from v2.</summary>
    public const int ClientIdSize = 4;

    public const byte Hello = 0x01;
    public const byte HelloAck = 0x02;

    public const byte TcpOpen = 0x10;
    public const byte TcpData = 0x11;
    public const byte TcpClose = 0x12;
    public const byte TcpWindow = 0x13;

    public const byte UdpDatagram = 0x20;

    public const byte DnsQuery = 0x30;
    public const byte DnsReply = 0x31;

    public const byte Ping = 0x40;
    public const byte Pong = 0x41;

    /// <summary>
    /// Upstream state, appended by the phone after a PONG's echoed payload.
    ///
    /// A pong is answered on the phone's link thread and never touches the
    /// cellular interface, so on its own it proves the phone is alive rather
    /// than that it has internet. This is what separates the two.
    ///
    /// A phone that predates it echoes the payload unchanged, which reads as
    /// Unknown - treated as up, exactly as before.
    /// </summary>
    public static class UpstreamState
    {
        public const byte Unknown = 0;
        public const byte Up = 1;
        public const byte Down = 2;
    }

    public const byte Stats = 0x50;

    /// <summary>Set in a frame's flags byte when its addresses are IPv6.</summary>
    public const byte FlagIpv6 = 0x01;

    /// <summary>
    /// Set on a HELLO that reopens one link of an existing session, rather
    /// than starting a new one.
    ///
    /// The phone discards a client's streams when link 0 says hello, because a
    /// reconnecting PC renumbers its streams from 1 and the old ids would
    /// collide. A repaired link is the opposite case: the other links are
    /// still carrying those very streams, and clearing them would turn the
    /// loss of one socket into the loss of the session.
    ///
    /// Carried in flags rather than a version bump, so a phone that predates
    /// it simply ignores the bit and behaves as it always did.
    /// </summary>
    public const byte FlagLinkRepair = 0x02;

    public static class CloseReason
    {
        public const byte Normal = 0;
        public const byte Refused = 1;
        public const byte Timeout = 2;
        public const byte Reset = 3;
        public const byte Unreachable = 4;

        public static string Describe(byte reason) => reason switch
        {
            Normal => "closed",
            Refused => "connection refused",
            Timeout => "timed out",
            Reset => "reset",
            Unreachable => "unreachable",
            _ => $"reason {reason}",
        };
    }

    public static void WriteHeader(Span<byte> buffer, byte type, byte flags, int streamId, int length)
    {
        if (buffer.Length < HeaderSize) throw new ArgumentException("buffer too small");
        if (length is < 0 or > MaxPayload) throw new ArgumentOutOfRangeException(nameof(length));

        buffer[0] = type;
        buffer[1] = flags;
        BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(2, 4), streamId);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(6, 2), (ushort)length);
    }

    public static (byte Type, byte Flags, int StreamId, int Length) ReadHeader(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < HeaderSize) throw new ArgumentException("buffer too small");
        return (
            buffer[0],
            buffer[1],
            BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(2, 4)),
            BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(6, 2))
        );
    }

    /// <summary>
    /// TCP_OPEN payload: address + port. The family is carried by length -
    /// 4 bytes is IPv4, 16 is IPv6 - so no new message type was needed.
    /// </summary>
    public static byte[] EncodeOpen(ReadOnlySpan<byte> dstIp, int dstPort)
    {
        if (dstIp.Length is not (4 or 16))
            throw new ArgumentException("address must be IPv4 or IPv6", nameof(dstIp));

        var payload = new byte[dstIp.Length + 2];
        dstIp.CopyTo(payload);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(dstIp.Length, 2), (ushort)dstPort);
        return payload;
    }

    /// <summary>UDP_DATAGRAM payload: srcPort(2) + address + dstPort(2) + data.</summary>
    public static byte[] EncodeDatagram(int srcPort, ReadOnlySpan<byte> dstIp, int dstPort, ReadOnlySpan<byte> data)
    {
        if (dstIp.Length is not (4 or 16))
            throw new ArgumentException("address must be IPv4 or IPv6", nameof(dstIp));

        var payload = new byte[4 + dstIp.Length + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), (ushort)srcPort);
        dstIp.CopyTo(payload.AsSpan(2, dstIp.Length));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2 + dstIp.Length, 2), (ushort)dstPort);
        data.CopyTo(payload.AsSpan(4 + dstIp.Length));
        return payload;
    }

    /// <summary>
    /// The family comes from the frame's flags rather than the total length,
    /// because the payload that follows is variable.
    /// </summary>
    public static (int SrcPort, byte[] DstIp, int DstPort, byte[] Data) DecodeDatagram(
        ReadOnlySpan<byte> payload, bool ipv6 = false)
    {
        int addressSize = ipv6 ? 16 : 4;
        if (payload.Length < 4 + addressSize) throw new ArgumentException("short UDP_DATAGRAM");

        int srcPort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(0, 2));
        var dstIp = payload.Slice(2, addressSize).ToArray();
        int dstPort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2 + addressSize, 2));
        var data = payload[(4 + addressSize)..].ToArray();
        return (srcPort, dstIp, dstPort, data);
    }
}
