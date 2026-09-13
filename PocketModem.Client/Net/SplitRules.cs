using System.Buffers.Binary;
using System.Net;
using System.Text.Json;

namespace PocketModem.Client.Net;

/// <summary>
/// Which traffic goes through the phone, and which does not.
///
/// The reason this matters more here than on a normal VPN: the connection is
/// metered. Sending a Steam download or a cloud backup over a mobile plan is
/// rarely what anyone wants, but excluding it should not mean disconnecting.
/// No modem can do this at all - it has one pipe and everything takes it.
///
/// Rules are evaluated per destination rather than per application, because a
/// TUN interface sees packets, not processes. Naming an app is possible on
/// Windows by mapping the connection table back to a PID, but that is a
/// different mechanism with different failure modes and is deliberately not
/// mixed in here.
/// </summary>
public sealed class SplitRules
{
    /// <summary>What happens to traffic that matches no rule.</summary>
    public enum Mode
    {
        /// <summary>Everything goes through the phone unless excluded.</summary>
        TunnelEverything,

        /// <summary>Nothing goes through the phone unless included.</summary>
        TunnelOnlyListed,
    }

    private readonly record struct Rule(uint Network, uint Mask, bool Tunnel, string Label);

    private readonly List<Rule> _rules = new();
    private readonly List<(string Suffix, bool Tunnel)> _hostRules = new();

    public Mode DefaultMode { get; private set; } = Mode.TunnelEverything;

    /// <summary>Destinations resolved from a hostname rule, filled in as DNS answers arrive.</summary>
    private readonly Dictionary<uint, bool> _resolved = new();

    // ------------------------------------------------------------- loading --

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PocketModem", "split-rules.json");

    private sealed record StoredRule(string Match, bool Tunnel);
    private sealed record Stored(string Mode, List<StoredRule> Rules);

    /// <summary>
    /// Load the user's rules, or a sensible default set.
    ///
    /// The defaults exclude the traffic almost nobody wants on a mobile plan -
    /// Windows Update and Steam - because a feature that requires research
    /// before it helps anyone mostly goes unused.
    /// </summary>
    public static SplitRules Load()
    {
        var rules = new SplitRules();

        try
        {
            if (File.Exists(ConfigPath))
            {
                var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(ConfigPath));
                if (stored is not null)
                {
                    rules.DefaultMode = stored.Mode == nameof(Mode.TunnelOnlyListed)
                        ? Mode.TunnelOnlyListed
                        : Mode.TunnelEverything;

                    foreach (var r in stored.Rules ?? new List<StoredRule>())
                        rules.Add(r.Match, r.Tunnel);

                    return rules;
                }
            }
        }
        catch (Exception ex)
        {
            ActivityLog.WriteAndPrint($"  split rules could not be read ({ex.Message}); using defaults");
        }

        rules.AddDefaults();
        return rules;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);

            var stored = new Stored(
                DefaultMode.ToString(),
                _rules.Select(r => new StoredRule(r.Label, r.Tunnel))
                      .Concat(_hostRules.Select(h => new StoredRule(h.Suffix, h.Tunnel)))
                      .ToList());

            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(stored,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            ActivityLog.WriteAndPrint($"  split rules could not be saved: {ex.Message}");
        }
    }

    private void AddDefaults()
    {
        // Private ranges never belong in the tunnel: sending them there breaks
        // printers, NAS boxes and anything else on the local network, and the
        // phone could not reach them anyway.
        Add("10.0.0.0/8", tunnel: false);
        Add("172.16.0.0/12", tunnel: false);
        Add("192.168.0.0/16", tunnel: false);
        Add("169.254.0.0/16", tunnel: false);

        // The heavy downloads nobody wants on a mobile plan.
        Add("windowsupdate.com", tunnel: false);
        Add("update.microsoft.com", tunnel: false);
        Add("delivery.mp.microsoft.com", tunnel: false);
        Add("steamcontent.com", tunnel: false);
        Add("steamcdn-a.akamaihd.net", tunnel: false);
    }

    // --------------------------------------------------------------- rules --

    /// <summary>
    /// Add a rule. Accepts a CIDR block, a single address, or a hostname
    /// suffix - the three shapes people actually reach for.
    /// </summary>
    public void Add(string match, bool tunnel)
    {
        match = match.Trim();
        if (match.Length == 0) return;

        if (TryParseCidr(match, out uint network, out uint mask))
        {
            _rules.Add(new Rule(network, mask, tunnel, match));
            return;
        }

        // Anything else is treated as a hostname suffix, matched against DNS
        // answers as they pass through.
        _hostRules.Add((match.TrimStart('.').ToLowerInvariant(), tunnel));
    }

    public void Remove(string match)
    {
        _rules.RemoveAll(r => r.Label.Equals(match, StringComparison.OrdinalIgnoreCase));
        _hostRules.RemoveAll(h => h.Suffix.Equals(match.TrimStart('.'), StringComparison.OrdinalIgnoreCase));
    }

    public void SetMode(Mode mode) => DefaultMode = mode;

    public IReadOnlyList<(string Match, bool Tunnel)> All() =>
        _rules.Select(r => (r.Label, r.Tunnel))
              .Concat(_hostRules.Select(h => (h.Suffix, h.Tunnel)))
              .ToList();

    // ------------------------------------------------------------ decision --

    /// <summary>
    /// Should this destination go through the phone?
    ///
    /// The most specific matching rule wins, so 10.0.0.0/8 excluded and
    /// 10.1.2.3/32 included behaves the way anyone would expect.
    /// </summary>
    public bool ShouldTunnel(ReadOnlySpan<byte> destination)
    {
        // IPv6 is not split yet: it needs its own prefix matching, and
        // pretending otherwise would silently send v6 somewhere unintended.
        if (destination.Length != 4)
            return DefaultMode == Mode.TunnelEverything;

        uint address = BinaryPrimitives.ReadUInt32BigEndian(destination);

        if (_resolved.TryGetValue(address, out bool fromHostname))
            return fromHostname;

        bool? decision = null;
        uint bestMask = 0;

        foreach (var rule in _rules)
        {
            if ((address & rule.Mask) != rule.Network) continue;
            if (decision is not null && rule.Mask <= bestMask) continue;
            decision = rule.Tunnel;
            bestMask = rule.Mask;
        }

        return decision ?? DefaultMode == Mode.TunnelEverything;
    }

    /// <summary>
    /// Tie a hostname rule to the addresses it resolves to.
    ///
    /// Hostname rules cannot be applied to a packet directly - by then only an
    /// address remains - so DNS answers are watched and the addresses recorded
    /// as they appear. A name excluded before its first lookup takes effect on
    /// that lookup, not before.
    /// </summary>
    public void NoteResolution(string hostname, IEnumerable<byte[]> addresses)
    {
        if (_hostRules.Count == 0) return;

        string host = hostname.TrimEnd('.').ToLowerInvariant();

        foreach (var (suffix, tunnel) in _hostRules)
        {
            if (host != suffix && !host.EndsWith("." + suffix, StringComparison.Ordinal)) continue;

            foreach (var address in addresses)
            {
                if (address.Length != 4) continue;
                _resolved[BinaryPrimitives.ReadUInt32BigEndian(address)] = tunnel;
            }
            return;
        }
    }

    private static bool TryParseCidr(string text, out uint network, out uint mask)
    {
        network = 0;
        mask = 0;

        var parts = text.Split('/');
        if (!IPAddress.TryParse(parts[0], out var ip)) return false;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;

        int prefix = 32;
        if (parts.Length > 1 && (!int.TryParse(parts[1], out prefix) || prefix is < 0 or > 32))
            return false;

        network = BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
        mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        network &= mask;
        return true;
    }
}
