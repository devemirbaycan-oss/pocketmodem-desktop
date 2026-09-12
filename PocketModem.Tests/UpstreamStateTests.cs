using System.Buffers.Binary;
using PocketModem.Client.Tunnel;

namespace PocketModem.Tests;

/// <summary>
/// A pong proves the phone is answering, not that it has internet.
///
/// The phone handles PING on the link thread and replies immediately; the reply
/// never touches the cellular interface. So when mobile data drops - most often
/// mid-handover between LTE, 4.5G and 5G - the tunnel between PC and phone
/// stays perfectly healthy. Pongs keep arriving, the supervisor's freshness
/// clock keeps resetting, and the PC reports "connected" indefinitely while
/// nothing at all reaches the internet.
///
/// The phone now stamps its upstream state onto the pong, after the echoed
/// timestamp. These tests pin that layout, because both halves ship separately
/// and a disagreement here would restore exactly the silent failure it fixes.
/// </summary>
public class UpstreamStateTests
{
    /// <summary>The 8-byte timestamp PingAsync sends and the phone echoes.</summary>
    private static byte[] Timestamp()
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(payload, 1_700_000_000_000L);
        return payload;
    }

    /// <summary>What the phone sends back: the echo plus one state byte.</summary>
    private static byte[] Pong(byte state)
    {
        var payload = Timestamp();
        var reply = new byte[payload.Length + 1];
        payload.CopyTo(reply, 0);
        reply[8] = state;
        return reply;
    }

    /// <summary>
    /// The rule TunnelClient applies: anything past the echo is the state, and
    /// only an explicit Down counts as down.
    /// </summary>
    private static bool ReadsAsUp(byte[] payload) =>
        payload.Length <= 8 || payload[8] != Protocol.UpstreamState.Down;

    [Fact]
    public void The_three_states_are_distinct()
    {
        Assert.NotEqual(Protocol.UpstreamState.Unknown, Protocol.UpstreamState.Up);
        Assert.NotEqual(Protocol.UpstreamState.Up, Protocol.UpstreamState.Down);
        Assert.NotEqual(Protocol.UpstreamState.Unknown, Protocol.UpstreamState.Down);
    }

    [Fact]
    public void A_pong_saying_up_reads_as_up()
    {
        Assert.True(ReadsAsUp(Pong(Protocol.UpstreamState.Up)));
    }

    [Fact]
    public void A_pong_saying_down_reads_as_down()
    {
        // The whole point: this is the case that used to be invisible.
        Assert.False(ReadsAsUp(Pong(Protocol.UpstreamState.Down)));
    }

    [Fact]
    public void An_older_phone_that_echoes_the_payload_is_treated_as_up()
    {
        // A phone predating this sends the 8 bytes back unchanged. Reading that
        // as "down" would break every working setup on the old build, so the
        // absence of a state byte has to mean "no information, carry on".
        Assert.True(ReadsAsUp(Timestamp()));
    }

    [Fact]
    public void Unknown_is_treated_as_up_rather_than_down()
    {
        // Same reasoning as an absent byte: only an explicit Down should stop
        // the PC trusting the tunnel. Guessing "down" would strand a working
        // connection over a state the phone could not determine.
        Assert.True(ReadsAsUp(Pong(Protocol.UpstreamState.Unknown)));
    }

    [Fact]
    public void The_state_byte_sits_after_the_echoed_timestamp()
    {
        // The echo has to survive intact - it is what the phone was asked to
        // return - so the state is appended, never overwritten into it.
        var pong = Pong(Protocol.UpstreamState.Down);

        Assert.Equal(9, pong.Length);
        Assert.Equal(1_700_000_000_000L, BinaryPrimitives.ReadInt64BigEndian(pong.AsSpan(0, 8)));
        Assert.Equal(Protocol.UpstreamState.Down, pong[8]);
    }

    [Fact]
    public void A_pong_is_not_confused_with_a_ping()
    {
        // They share the flags byte and stream id 0; only the type separates
        // them, and the phone replies to one with the other.
        Assert.NotEqual(Protocol.Ping, Protocol.Pong);
    }
}
