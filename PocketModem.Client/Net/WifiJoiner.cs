using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace PocketModem.Client.Net;

/// <summary>
/// Joins the phone's Wi-Fi Direct group without the user opening Wi-Fi settings.
///
/// Uses netsh wlan with a temporary profile rather than the native WLAN API:
/// the whole interaction is three one-shot control-plane commands, and a
/// profile that a user can inspect and delete by hand is easier to reason about
/// than an opaque API handle. It also keeps the Linux port honest, since that
/// will shell out to nmcli in the same shape.
///
/// The profile is deleted on disconnect so Windows does not silently rejoin the
/// phone's group later when the tunnel is not running.
/// </summary>
public sealed class WifiJoiner : Platform.IWifiJoiner
{
    /// <summary>Subnet the Android group owner uses; the phone is always .1.</summary>
    public const string P2pPrefix = "192.168.49.";
    public const string PhoneAddress = "192.168.49.1";

    private readonly string _ssid;
    private string? _profileName;

    public WifiJoiner(string ssid = "DIRECT-WD-PocketModem")
    {
        _ssid = ssid;
    }

    /// <summary>True when this PC already has an address on the P2P subnet.</summary>
    public static bool IsJoined()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (ua.Address.ToString().StartsWith(P2pPrefix)) return true;
            }
        }
        return false;
    }

    /// <summary>Is the phone's group visible right now?</summary>
    public bool IsVisible()
    {
        var (_, output) = Run("netsh", "wlan show networks mode=bssid");
        return output.Contains(_ssid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Join the group. Returns true once this PC holds a P2P address.
    /// </summary>
    public async Task<bool> JoinAsync(string passphrase, CancellationToken ct = default)
    {
        // Only short-circuit when the address is actually usable. Windows can
        // report a stale profile as connected after the phone's group has been
        // rebuilt, and returning success there means the caller reconnects to
        // nothing.
        if (IsJoined()) return true;

        _profileName = _ssid;
        var profilePath = Path.Combine(Path.GetTempPath(), $"pocketmodem-{Guid.NewGuid():N}.xml");

        try
        {
            await File.WriteAllTextAsync(profilePath, BuildProfileXml(_ssid, passphrase), ct);

            var (ok, output) = Run("netsh", $"wlan add profile filename=\"{profilePath}\" user=all");
            if (!ok)
            {
                // netsh explains itself; discarding that left "could not join"
                // as the only symptom of every possible cause.
                ActivityLog.Write($"join: adding the profile failed: {output.Trim()}");
                return false;
            }

            var (connected, connectOutput) = Run("netsh", $"wlan connect name=\"{_ssid}\"");
            if (!connected)
                ActivityLog.Write($"join: connect refused: {connectOutput.Trim()}");

            ActivityLog.Write($"join: asked Windows to connect to {_ssid}");

            // Association plus DHCP takes a few seconds; poll rather than
            // guessing a fixed delay.
            for (int i = 0; i < 30 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(500, ct);
                if (IsJoined())
                {
                    ActivityLog.Write($"join: joined after {(i + 1) * 500} ms");
                    return true;
                }
            }

            // Timing out is the common failure and said nothing at all. Whether
            // the SSID is even visible is the difference between "the phone is
            // not sharing" and "the passphrase is wrong", so record it.
            var (_, scan) = Run("netsh", "wlan show networks");
            bool visible = scan.Contains(_ssid, StringComparison.OrdinalIgnoreCase);
            ActivityLog.Write(
                $"join: gave up after 15 s. {_ssid} " +
                (visible
                    ? "IS visible - association or the passphrase is the problem."
                    : "is NOT visible - the phone is not broadcasting the group."));
            return false;
        }
        catch (Exception ex)
        {
            ActivityLog.Write($"join: failed with {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            try { if (File.Exists(profilePath)) File.Delete(profilePath); } catch { }
        }
    }

    /// <summary>
    /// Leave the group and remove the profile, so Windows will not rejoin the
    /// phone on its own later.
    /// </summary>
    public void Leave()
    {
        // Not conditional on having joined in this instance. A reconnect uses a
        // fresh joiner, which would have nothing to leave and would silently do
        // nothing - leaving Windows attached to a profile whose group is gone,
        // still reporting itself connected.
        Run("netsh", "wlan disconnect");
        Run("netsh", $"wlan delete profile name=\"{_ssid}\"");
        _profileName = null;
    }

    private static string BuildProfileXml(string ssid, string passphrase)
    {
        string hex = Convert.ToHexString(Encoding.UTF8.GetBytes(ssid));
        return $"""
        <?xml version="1.0"?>
        <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
          <name>{ssid}</name>
          <SSIDConfig>
            <SSID>
              <hex>{hex}</hex>
              <name>{ssid}</name>
            </SSID>
          </SSIDConfig>
          <connectionType>ESS</connectionType>
          <connectionMode>manual</connectionMode>
          <MSM>
            <security>
              <authEncryption>
                <authentication>WPA2PSK</authentication>
                <encryption>AES</encryption>
                <useOneX>false</useOneX>
              </authEncryption>
              <sharedKey>
                <keyType>passPhrase</keyType>
                <protected>false</protected>
                <keyMaterial>{passphrase}</keyMaterial>
              </sharedKey>
            </security>
          </MSM>
        </WLANProfile>
        """;
    }

    private static (bool Ok, string Output) Run(string file, string args)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return (false, "");
            string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit(15000);
            return (proc.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
