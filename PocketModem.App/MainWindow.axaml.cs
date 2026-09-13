using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PocketModem.Client;
using PocketModem.Client.Net;
using PocketModem.Client.Platform;

namespace PocketModem.App;

public partial class MainWindow : Window
{
    private TunnelSession? _session;
    private IWifiJoiner? _joiner;
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _timer;

    private long _lastUp, _lastDown;
    private DateTime _lastSample = DateTime.UtcNow;
    private DateTime _connectedAt;
    /// <summary>
    /// Throughput history for the sparkline.
    ///
    /// Written by the stats timer and read while drawing, and cleared on
    /// connect. A plain List mutated during enumeration throws, and that
    /// exception was fatal, so access is locked.
    /// </summary>
    private readonly List<double> _downHistory = new();
    private readonly object _historyLock = new();
    private DispatcherTimer? _autoConnectTimer;

    /// <summary>
    /// Loaded once at startup and reused, so the rules a session runs with are
    /// the ones shown in the panel rather than whatever the file said at the
    /// moment Connect was pressed.
    /// </summary>
    private SplitRules? _split;

    // Kept in step with Styles.axaml; Avalonia gives no typed access to those.
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#3DDC91"));
    private static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#4C9AFF"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#FFB020"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#FF5C5C"));
    private static readonly IBrush Grey = new SolidColorBrush(Color.Parse("#5F6B7F"));
    private static readonly IBrush Idle = new SolidColorBrush(Color.Parse("#1A1F28"));

    public MainWindow()
    {
        InitializeComponent();
        var (token, passphrase, dns) = Settings.Load();
        TokenBox.Text = token;
        PassphraseBox.Text = passphrase;
        DnsBox.Text = dns;

        LoadSplitRules();

        // A tunnel left running by a previous instance is adopted rather than
        // rebuilt, so updating the app does not interrupt connectivity. The
        // adapter and routes are OS state and outlive the process.
        if (TunnelSession.HasLiveTunnel() && !string.IsNullOrEmpty(token))
        {
            SetStatus("Tunnel already running",
                "Press Connect to take it over without dropping it.", Amber);
            ConnectButton.Content = "Take over";
        }
        else if (!string.IsNullOrEmpty(token))
        {
            StartAutoConnect();
        }

        // A previous run died. Saying so beats the app simply having vanished
        // with no explanation, which is how this was first reported.
        var crash = CrashLog.LastCrash();
        if (crash is not null)
        {
            var firstLine = crash.Split('\n').Skip(1).FirstOrDefault()?.Trim() ?? "unknown";
            SetStatus("Recovered from a crash",
                $"{firstLine}  -  details in {CrashLog.Location}", Amber);
            CrashLog.Clear();
        }
    }

    /// <summary>
    /// Connect on its own once the phone's network is reachable.
    ///
    /// Only when a pairing code is already remembered - a first-time setup
    /// should not connect to something the user has not confirmed. Polling
    /// rather than watching for network events because joining the phone's
    /// Wi-Fi is itself one of the steps, so there is nothing to subscribe to
    /// until it has happened.
    /// </summary>
    private void StartAutoConnect()
    {
        _autoConnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _autoConnectTimer.Tick += async (_, _) =>
        {
            if (_session?.IsRunning == true) { StopAutoConnect(); return; }
            if (!PlatformFactory.IsJoinedToPhone()) return;

            // On the phone's network with a code already saved: this is the
            // case the user would have connected by hand anyway.
            StopAutoConnect();
            SetStatus("Connecting", "The phone's network appeared.", Blue);
            await ConnectAsync();
        };
        _autoConnectTimer.Start();

        SetStatus("Waiting for the phone",
            "Will connect automatically when it starts sharing.", Grey);
    }

    private void StopAutoConnect()
    {
        _autoConnectTimer?.Stop();
        _autoConnectTimer = null;
    }

    private async void OnConnectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_session?.IsRunning == true) { Disconnect(); return; }
        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        string token = TokenBox.Text?.Trim() ?? "";
        string passphrase = PassphraseBox.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(token))
        {
            SetStatus("Pairing code needed", "It is shown on the phone once sharing is started.", Amber);
            return;
        }

        if (!PlatformFactory.IsPrivileged())
        {
            SetStatus("Elevated rights needed", PlatformFactory.PrivilegeHint(), Amber);
            // Windows can relaunch itself elevated; on Linux the user has to
            // start it with sudo, so saying so beats failing silently.
            if (OperatingSystem.IsWindows()) ElevationHelper.RestartElevated();
            return;
        }

        ConnectButton.IsEnabled = false;
        _cts = new CancellationTokenSource();

        try
        {
            if (!PlatformFactory.IsJoinedToPhone())
            {
                if (string.IsNullOrEmpty(passphrase))
                {
                    SetStatus("Wi-Fi passphrase needed",
                        "This PC has not joined the phone's network yet.", Amber);
                    ConnectButton.IsEnabled = true;
                    return;
                }

                SetStatus("Joining", "Connecting to the phone's network...", Blue);
                _joiner = PlatformFactory.CreateWifiJoiner();
                if (!await _joiner.JoinAsync(passphrase, _cts.Token))
                {
                    SetStatus("Could not join",
                        "Check the phone is sharing, and the passphrase is right.", Red);
                    ConnectButton.IsEnabled = true;
                    return;
                }
            }

            SetStatus("Connecting", "Setting up the tunnel...", Blue);
            _session = new TunnelSession(PlatformFactory.PhoneAddress, token, passphrase)
            {
                DnsServer = ResolveDns(),

                // Null disables the check entirely rather than passing an empty
                // rule set: an empty set still costs a lookup per packet.
                Split = SplitEnabled.IsChecked == true ? _split : null,
            };
            _session.Status += s => Dispatcher.UIThread.Post(() => DetailText.Text = s);

            if (await _session.StartAsync(_cts.Token))
            {
                Settings.Save(token, passphrase, ResolveDns());
                _connectedAt = DateTime.UtcNow;
                lock (_historyLock) _downHistory.Clear();

                SetStatus("Connected", "All traffic is going through the phone.", Green);
                ConnectButton.Content = "Disconnect";
                ConnectButton.Classes.Set("primary", false);
                ConnectButton.Classes.Set("secondary", true);

                // Credentials are only useful before a tunnel exists; once
                // traffic is flowing they are clutter.
                SetupPanel.IsVisible = false;
                TrafficPanel.IsVisible = true;
                DetailPanel.IsVisible = true;
                LinkBars.IsVisible = true;

                StartStatsTimer();
            }
            else
            {
                SetStatus("Could not connect",
                    "Check the pairing code, and that the phone is sharing.", Red);
                _session = null;
            }
        }
        catch (Exception ex)
        {
            SetStatus("Failed", ex.Message, Red);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void Disconnect()
    {
        // Stopping by hand means stopping: reconnecting automatically after an
        // explicit disconnect would be the opposite of what was asked.
        StopAutoConnect();
        _timer?.Stop();
        _cts?.Cancel();
        _session?.Stop();
        _session = null;
        _joiner?.Leave();
        _joiner = null;

        ConnectButton.Content = "Connect";
        ConnectButton.Classes.Set("secondary", false);
        ConnectButton.Classes.Set("primary", true);

        SetupPanel.IsVisible = true;
        TrafficPanel.IsVisible = false;
        DetailPanel.IsVisible = false;
        LinkBars.IsVisible = false;

        SetStatus("Not connected", "Normal routing restored.", Grey);
    }

    private void StartStatsTimer()
    {
        _lastUp = _lastDown = 0;
        _lastSample = DateTime.UtcNow;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            // Everything here runs on a timer callback. An exception escaping
            // one is unhandled, and an unhandled exception kills the process
            // with no message - which is how the silent crashes presented.
            try
            {
                UpdateStats();
            }
            catch (Exception ex)
            {
                CrashLog.Write("stats timer", ex);
            }
        };
        _timer.Start();
    }

    private void UpdateStats()
    {
        {
            var s = _session;
            if (s is null) return;

            var now = DateTime.UtcNow;
            double seconds = (now - _lastSample).TotalSeconds;
            if (seconds <= 0) return;

            long up = s.BytesUp, down = s.BytesDown;
            double upMbps = (up - _lastUp) * 8.0 / seconds / 1_000_000.0;
            double downMbps = (down - _lastDown) * 8.0 / seconds / 1_000_000.0;
            _lastUp = up; _lastDown = down; _lastSample = now;

            DownSpeed.Text = $"{downMbps:F1}";
            UpSpeed.Text = $"{upMbps:F1}";
            TotalDown.Text = $"{FormatBytes(down)} down";
            TotalUp.Text = $"{FormatBytes(up)} up";
            LinkCount.Text = $"{s.Links} of {s.MaxLinks}";
            StreamCount.Text = s.ActiveStreams.ToString();

            // The phone's count, when it reports one. Amber once it runs well
            // ahead of this PC's: that gap is the phone holding streams it has
            // already closed, and it ends with every new TCP connection
            // refused while UDP keeps working.
            int phoneStreams = s.PhoneStreams;
            if (phoneStreams < 0)
            {
                PhoneStreamCount.Text = "-";
                PhoneStreamCount.Foreground = Grey;
            }
            else
            {
                PhoneStreamCount.Text = phoneStreams.ToString();
                bool drifting = phoneStreams > s.ActiveStreams + 50;
                PhoneStreamCount.Foreground = drifting ? Amber : Brushes.White;
            }
            ReconnectCount.Text = s.Reconnects.ToString();
            Uptime.Text = FormatDuration(now - _connectedAt);

            long excluded = s.PacketsExcluded;
            if (excluded > 0)
            {
                ExcludedRow.IsVisible = true;
                ExcludedCount.Text = $"{excluded:N0} packets";
            }

            LinkCount.Foreground = s.Links < s.MaxLinks ? Amber : Brushes.White;
            UpdateLinkBars(s.Links);

            var drops = s.RecentCloses;
            if (drops.Count > 0)
            {
                DropsSection.IsVisible = true;
                DropList.Text = string.Join(Environment.NewLine, drops.Reverse().Take(6));
            }

            lock (_historyLock)
            {
                _downHistory.Add(downMbps);
                if (_downHistory.Count > 40) _downHistory.RemoveAt(0);
            }
            DrawSparkline();

            if (!s.Connected)
                SetStatus("Reconnecting", "The adapter stays up, so apps keep their connections.", Amber);
            // The link can be perfectly healthy while the phone itself has no
            // signal - a handover between LTE and 5G looks exactly like this.
            // Saying "connected" here was the single most misleading thing the
            // app did, because nothing was actually getting through.
            else if (!s.UpstreamUp)
                SetStatus("Phone has no signal", "The link is fine; the phone's mobile data is not.", Amber);
            else if (s.Links < s.MaxLinks)
                SetStatus("Connected", $"Running on {s.Links} of {s.MaxLinks} links.", Amber);
            else if (StatusText.Text != "Connected")
                SetStatus("Connected", "All traffic is going through the phone.", Green);
        }
    }

    private void UpdateLinkBars(int active)
    {
        var bars = new[] { Link0, Link1, Link2, Link3 };
        for (int i = 0; i < bars.Length; i++)
            bars[i].Background = i < active ? Green : Idle;
    }

    /// <summary>
    /// Throughput history as bars. The shape tells you whether the connection
    /// is steady or stalling, which the current number alone cannot.
    /// </summary>
    private void DrawSparkline()
    {
        Sparkline.Children.Clear();

        // Snapshot under the lock, then draw from the copy: holding it across
        // the drawing would block the timer for no benefit.
        double[] history;
        lock (_historyLock) history = _downHistory.ToArray();

        if (history.Length == 0) return;

        double max = Math.Max(history.Max(), 0.001);
        foreach (double v in history)
        {
            double fraction = Math.Clamp(v / max, 0.02, 1.0);
            Sparkline.Children.Add(new Border
            {
                Width = 6,
                Height = 30 * fraction,
                CornerRadius = new Avalonia.CornerRadius(1),
                Background = Green,
                Opacity = 0.35 + 0.55 * fraction,
                VerticalAlignment = VerticalAlignment.Bottom,
            });
        }
    }

    /// <summary>
    /// The DNS server to hand the adapter.
    ///
    /// Validated rather than trusted: a typo here does not fail loudly, it
    /// produces an adapter whose resolver does not answer, which presents as
    /// "the internet is broken" with a connection that is otherwise fine.
    /// </summary>
    private string ResolveDns()
    {
        string text = DnsBox.Text?.Trim() ?? "";
        if (System.Net.IPAddress.TryParse(text, out _)) return text;

        if (text.Length > 0)
            DnsBox.Text = Settings.DefaultDns;

        return Settings.DefaultDns;
    }

    /// <summary>
    /// Read the split rules and say what they will do.
    ///
    /// The panel does not edit them - a list is easier to manage on the command
    /// line than in a dialog - but it does report them, because a rule that
    /// silently fails to match is this feature's characteristic failure and
    /// nothing else would reveal it.
    /// </summary>
    private void LoadSplitRules()
    {
        try
        {
            _split = SplitRules.Load();
            var rules = _split.All();

            int excluded = rules.Count(r => !r.Tunnel);
            int included = rules.Count - excluded;

            SplitSummary.Text = rules.Count == 0
                ? "No rules: everything goes through the phone."
                : _split.DefaultMode == SplitRules.Mode.TunnelOnlyListed
                    ? $"Only {included} listed destination{(included == 1 ? "" : "s")} go through the phone."
                    : $"{excluded} destination{(excluded == 1 ? "" : "s")} kept off the phone.";
        }
        catch (Exception ex)
        {
            // A broken rules file must not stop the app connecting; the tunnel
            // works without split rules, it just carries more.
            _split = null;
            SplitSummary.Text = $"Rules could not be read ({ex.Message}); everything will go through the phone.";
        }
    }

    private void SetStatus(string status, string detail, IBrush colour)
    {
        // Every state the user is shown goes to the log too, so a report of
        // "it said could not connect" can be matched against what led to it.
        PocketModem.Client.ActivityLog.Write($"status: {status} - {detail}");

        StatusText.Text = status;
        DetailText.Text = detail;
        StatusDot.Fill = colour;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };

    private static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m"
        : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds}s"
        : $"{(int)d.TotalSeconds}s";

    protected override void OnClosed(EventArgs e)
    {
        Disconnect();
        base.OnClosed(e);
    }
}
