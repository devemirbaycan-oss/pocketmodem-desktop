using PocketModem.Client.Tunnel;

namespace PocketModem.Tests;

/// <summary>
/// Ordering, which is the entire risk in striping a stream across four links.
///
/// Every failure here is silent in production: the bytes arrive, the download
/// finishes, and the file is wrong. So the cases worth pinning are the ones
/// that produce plausible-looking output - a duplicate delivered twice, a gap
/// skipped, frames released in the wrong order - not the ones that throw.
///
/// Mirrors ReassemblerTest.kt. Both sides reassemble what the other stripes,
/// so the two implementations have to behave identically.
/// </summary>
public class ReassemblerTests
{
    private static byte[] Frame(int n) => [(byte)n];
    private static int[] Seqs(List<byte[]> frames) => frames.Select(f => (int)f[0]).ToArray();

    [Fact]
    public void In_order_frames_pass_straight_through()
    {
        // The common case: four links roughly in step, nothing held back.
        var r = new Reassembler();

        Assert.Equal([0], Seqs(r.Accept(0, Frame(0))));
        Assert.Equal([1], Seqs(r.Accept(1, Frame(1))));
        Assert.Equal([2], Seqs(r.Accept(2, Frame(2))));
        Assert.Equal(0, r.Pending);
    }

    [Fact]
    public void A_frame_arriving_early_waits_for_the_gap()
    {
        var r = new Reassembler();

        Assert.Empty(r.Accept(1, Frame(1)));
        Assert.Equal(1, r.Pending);

        Assert.Equal([0, 1], Seqs(r.Accept(0, Frame(0))));
        Assert.Equal(0, r.Pending);
    }

    [Fact]
    public void A_run_released_at_once_comes_back_in_order()
    {
        var r = new Reassembler();
        foreach (int n in new[] { 4, 2, 1, 3 }) r.Accept(n, Frame(n));

        Assert.Equal([0, 1, 2, 3, 4], Seqs(r.Accept(0, Frame(0))));
        Assert.Equal(0, r.Pending);
    }

    [Fact]
    public void Fully_reversed_arrival_still_delivers_in_order()
    {
        // The worst plausible interleaving of four links at different speeds.
        var r = new Reassembler();
        for (int n = 7; n >= 1; n--) Assert.Empty(r.Accept(n, Frame(n)));

        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], Seqs(r.Accept(0, Frame(0))));
    }

    [Fact]
    public void A_duplicate_is_dropped_rather_than_delivered_twice()
    {
        // Delivering the same bytes twice corrupts a stream exactly as much as
        // delivering them out of order.
        var r = new Reassembler();

        Assert.Equal([0], Seqs(r.Accept(0, Frame(0))));
        Assert.Empty(r.Accept(0, Frame(0)));
        Assert.Equal([1], Seqs(r.Accept(1, Frame(1))));
    }

    [Fact]
    public void The_buffer_is_bounded()
    {
        // Without a cap a frame lost with a dead link grows this until the
        // process dies.
        var r = new Reassembler(maxHeld: 8);
        for (int n = 1; n <= 20; n++) r.Accept(n, Frame(n));

        Assert.Equal(8, r.Pending);
    }

    [Fact]
    public void Overflow_is_reported_rather_than_flushed_out_of_order()
    {
        var r = new Reassembler(maxHeld: 4);
        Assert.False(r.Overflowed);

        for (int n = 1; n <= 4; n++) r.Accept(n, Frame(n));
        Assert.True(r.Overflowed);
    }

    [Fact]
    public void Reset_clears_held_frames_and_the_counter()
    {
        var r = new Reassembler();
        r.Accept(3, Frame(3));
        r.Reset();

        Assert.Equal(0, r.Pending);
        Assert.Equal([0], Seqs(r.Accept(0, Frame(0))));
    }

    [Fact]
    public void Payload_bytes_survive_reassembly_unchanged()
    {
        // Ordering alone is not enough; the bytes must be the ones sent.
        var r = new Reassembler();
        byte[] a = [1, 2, 3];
        byte[] b = [4, 5, 6];

        r.Accept(1, b);
        var outFrames = r.Accept(0, a);

        Assert.Equal(2, outFrames.Count);
        Assert.Equal(a, outFrames[0]);
        Assert.Equal(b, outFrames[1]);
    }

    [Fact]
    public void The_striping_flag_does_not_collide_with_the_others()
    {
        // All three share the flags byte on HELLO_ACK.
        Assert.Equal(0, Protocol.FlagStriping & Protocol.FlagIpv6);
        Assert.Equal(0, Protocol.FlagStriping & Protocol.FlagLinkRepair);
    }
}
