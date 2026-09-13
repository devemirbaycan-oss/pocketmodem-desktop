namespace PocketModem.Client.Tunnel;

/// <summary>
/// Puts a stream's frames back in order after they race across four links.
///
/// A single TCP connection over Wi-Fi Direct measured 31 Mbps on this
/// hardware, while four concurrent sockets reached 99: the radio has capacity
/// that one stream cannot use, because a single stream is bounded by ACK
/// round-trips rather than bandwidth. Spreading one stream's frames over every
/// link recovers most of that gap.
///
/// The cost is that frames stop arriving in the order they were sent, and TCP
/// payload bytes must reach the application in order or the stream is corrupt.
/// So each frame carries a sequence number and passes through here first.
///
/// Mirrors Reassembler.kt on the phone; the two must agree, since each side
/// reassembles what the other stripes.
///
/// Not thread-safe. One instance per stream, and callers hold that stream's
/// lock - which they need anyway, because delivery order is the entire point.
/// </summary>
internal sealed class Reassembler
{
    /// <summary>
    /// Frames held while waiting for a gap to fill.
    ///
    /// Bounded out of necessity rather than tidiness: a frame lost with a dead
    /// link would otherwise grow this until the process died. Sixty-four
    /// frames at the 60 KB the phone reads is about 4 MB per stream, far more
    /// slack than four links can plausibly need.
    /// </summary>
    private readonly int _maxHeld;

    private readonly Dictionary<int, byte[]> _held = new();
    private int _expected;

    public Reassembler(int maxHeld = 64) => _maxHeld = maxHeld;

    /// <summary>How many frames are waiting for a gap ahead of them.</summary>
    public int Pending => _held.Count;

    /// <summary>
    /// True once the buffer is full and the missing frame still has not come.
    ///
    /// The gap will not close at that point - something was lost with a link -
    /// and the caller should reset the stream. Quietly releasing what is held
    /// would deliver bytes out of order, which corrupts the download rather
    /// than failing it.
    /// </summary>
    public bool Overflowed => _held.Count >= _maxHeld;

    /// <summary>
    /// Offer a frame; take back everything that is now deliverable.
    ///
    /// Usually that is just this frame - the links stay roughly in step, so
    /// most frames arrive in order and pass straight through. When one link
    /// runs ahead its frames wait here, and several come back at once when the
    /// slower link catches up.
    /// </summary>
    public List<byte[]> Accept(int sequence, byte[] data)
    {
        // Already delivered. A duplicate can only arrive from a retransmit
        // above us, and delivering it twice corrupts the stream just as surely
        // as delivering it late.
        if (sequence < _expected) return [];

        if (sequence != _expected)
        {
            if (_held.Count < _maxHeld) _held[sequence] = data;
            return [];
        }

        var ready = new List<byte[]>(1 + _held.Count) { data };
        _expected++;

        while (_held.Remove(_expected, out var next))
        {
            ready.Add(next);
            _expected++;
        }

        return ready;
    }

    public void Reset()
    {
        _expected = 0;
        _held.Clear();
    }
}
