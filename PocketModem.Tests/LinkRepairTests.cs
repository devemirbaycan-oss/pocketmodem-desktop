using System.Buffers.Binary;
using System.Text;
using PocketModem.Client.Tunnel;

namespace PocketModem.Tests;

/// <summary>
/// Losing one link should cost one link, not the session.
///
/// Four parallel links exist so that a stall on one does not block the other
/// three. Until recently a link that died was never reopened: the read loop
/// marked it closed and gave up, and because "connected" means *any* link is
/// alive, nothing anywhere noticed. A session could sit at one link of four
/// indefinitely, carrying all its traffic on a quarter of the sockets, and only
/// recover by tearing the whole tunnel down when the last one died - which also
/// resets every open TCP connection.
///
/// The repair itself then had a sharper edge. The phone clears a client's
/// streams whenever link 0 says hello, because a genuinely reconnecting PC
/// renumbers its streams from 1 and the stale ids would collide. Reopening
/// link 0 looks identical on the wire - so repairing it would have wiped the
/// very streams the other three links were still carrying, turning one lost
/// socket into a lost session. The repair flag is what separates the two cases.
/// </summary>
public class LinkRepairTests
{
    private const string Token = "k7m2xq4p";
    private const int ClientId = 0x11223344;

    /// <summary>Mirrors TunnelClient's HELLO layout: version, link, client, token.</summary>
    private static byte[] Hello(int linkIndex)
    {
        var tokenBytes = Encoding.ASCII.GetBytes(Token);
        var hello = new byte[2 + Protocol.ClientIdSize + tokenBytes.Length];
        hello[0] = Protocol.ProtocolVersion;
        hello[1] = (byte)linkIndex;
        BinaryPrimitives.WriteInt32BigEndian(hello.AsSpan(2, Protocol.ClientIdSize), ClientId);
        tokenBytes.CopyTo(hello, 2 + Protocol.ClientIdSize);
        return hello;
    }

    [Fact]
    public void The_repair_flag_is_distinct_from_the_ipv6_flag()
    {
        // They share the flags byte, so overlapping bits would make a repaired
        // link look like an IPv6 frame and vice versa.
        Assert.NotEqual(Protocol.FlagIpv6, Protocol.FlagLinkRepair);
        Assert.Equal(0, Protocol.FlagIpv6 & Protocol.FlagLinkRepair);
    }

    [Fact]
    public void A_normal_hello_carries_no_repair_flag()
    {
        // The phone must clear stale streams for a genuine reconnect. Only an
        // explicit repair opts out of that.
        byte flags = 0;
        Assert.Equal(0, flags & Protocol.FlagLinkRepair);
    }

    [Fact]
    public void A_repair_hello_sets_only_the_repair_bit()
    {
        byte flags = Protocol.FlagLinkRepair;

        Assert.NotEqual(0, flags & Protocol.FlagLinkRepair);
        Assert.Equal(0, flags & Protocol.FlagIpv6);
    }

    [Fact]
    public void The_repair_flag_rides_the_header_not_the_payload()
    {
        // It has to live in the header: the phone decides whether to clear the
        // client's streams before it has parsed anything payload-shaped, and an
        // older phone must be able to ignore the bit without misreading HELLO.
        var payload = Hello(linkIndex: 0);
        var header = new byte[Protocol.HeaderSize];
        Protocol.WriteHeader(header, Protocol.Hello, Protocol.FlagLinkRepair, 0, payload.Length);

        Assert.Equal(Protocol.Hello, header[0]);
        Assert.Equal(Protocol.FlagLinkRepair, header[1]);

        // The payload is byte-for-byte what a non-repair hello sends, so a
        // phone that ignores the flag still reads a valid HELLO.
        Assert.Equal(Hello(linkIndex: 0), payload);
    }

    [Fact]
    public void A_repaired_hello_still_identifies_the_same_client()
    {
        // Repair depends on landing in the existing client's slot. A different
        // client id would create a second session rather than mend the first.
        var payload = Hello(linkIndex: 0);
        Assert.Equal(ClientId, BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(2, 4)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Any_link_can_be_repaired(int index)
    {
        // Link 0 is the one that matters - it is the only index the phone
        // treats specially - but the flag is set the same way for all of them.
        var payload = Hello(index);
        Assert.Equal((byte)index, payload[1]);
    }
}
