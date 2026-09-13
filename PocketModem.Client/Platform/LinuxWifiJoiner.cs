using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PocketModem.Client.Platform;

/// <summary>
/// Joins the phone's Wi-Fi Direct group on Linux via NetworkManager.
///
/// Mirrors the Windows implementation's shape deliberately: nmcli plays the
/// role netsh does there, and the connection profile is deleted on leaving so
/// NetworkManager will not silently rejoin the phone's group later when the
/// tunnel is not running.
///
/// NetworkManager is assumed because it is the default on Ubuntu, Fedora,
/// Debian desktop and most derivatives. On a system using iwd or wpa_supplicant
/// directly, joining by hand still works - the tunnel only needs an address on
/// the phone's subnet, however it was obtained.
/// </summary>
public sealed class LinuxWifiJoiner : IWifiJoiner
{
    public const string P2pPrefix = "192.168.49.";
    public const string PhoneAddress = "192.168.49.1";

    private readonly string _ssid;
    private bool _joined;

    public LinuxWifiJoiner(string ssid = "DIRECT-WD-PocketModem")
    {
        _ssid = ssid;
    }

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

    public bool IsVisible()
    {
        var (_, output) = Run("nmcli", "-t -f SSID device wifi list");
        return output.Contains(_ssid, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> JoinAsync(string passphrase, CancellationToken ct = default)
    {
        if (IsJoined()) return true;

        if (!HasCommand("nmcli"))
        {
            ActivityLog.WriteAndPrint(
                "  nmcli not found. Join the phone's network manually, then run " +
                "this again - the tunnel only needs an address on its subnet.");
            return false;
        }

        // A rescan first: the group may have appeared since the last one, and
        // connecting to an SSID NetworkManager has not seen fails.
        Run("nmcli", "device wifi rescan");
        await Task.Delay(2000, ct);

        var (ok, output) = Run("nmcli", $"device wifi connect \"{_ssid}\" password \"{passphrase}\"");
        if (!ok)
        {
            ActivityLog.WriteAndPrint($"  nmcli connect failed: {output.Trim()}");
            return false;
        }
        _joined = true;

        // Association plus DHCP takes a few seconds; poll rather than guess.
        for (int i = 0; i < 30 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(500, ct);
            if (IsJoined()) return true;
        }
        return false;
    }

    public void Leave()
    {
        if (!_joined) return;
        Run("nmcli", $"connection down \"{_ssid}\"");
        // Remove the saved profile too, so NetworkManager does not rejoin the
        // phone's group on its own later.
        Run("nmcli", $"connection delete \"{_ssid}\"");
        _joined = false;
    }

    private static bool HasCommand(string name) => Run("which", name).Ok;

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
            proc.WaitForExit(20000);
            return (proc.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
