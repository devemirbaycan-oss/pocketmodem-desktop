namespace PocketModem.Cli;

/// <summary>
/// Command-line front end.
///
/// Drives the same TunnelSession the desktop app uses, so the two cannot drift
/// apart; this project is argument parsing and terminal output, not a second
/// implementation of the tunnel.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        PocketModem.Client.ActivityLog.Start();

        CommandLine cmd;
        try
        {
            cmd = CommandLine.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Output.Error(ex.Message);
            Console.Error.WriteLine("Run 'pocketmodem --help' for usage.");
            return 2;
        }

        if (cmd.ShowHelp)
        {
            // `--help` alone is the overview; `connect --help` is that
            // command's page.
            CommandLine.PrintHelp(cmd.VerbExplicit ? cmd.Verb : null);
            return 0;
        }

        if (cmd.ShowVersion)
        {
            Console.WriteLine($"pocketmodem {BuildInfo.Version} ({BuildInfo.Platform})");
            return 0;
        }

        try
        {
            return cmd.Verb switch
            {
                "connect" => await Commands.ConnectAsync(cmd),
                "status" => Commands.Status(cmd),
                "test" => await Commands.TestAsync(cmd),
                "recover" => Commands.Recover(cmd),
                "doctor" => await Commands.DoctorAsync(cmd),
                "handover" => await Commands.HandoverAsync(cmd),
                "split" => Commands.Split(cmd),
                "install-service" => ServiceMode.Install(cmd),
                "uninstall-service" => ServiceMode.Uninstall(cmd),
                "run-service" => await ServiceMode.Run(cmd),
                _ => Unknown(cmd.Verb),
            };
        }
        catch (ArgumentException ex)
        {
            // Usage errors get their own code so scripts can tell a typo from
            // a connection that genuinely failed.
            Output.Error(ex.Message);
            Console.Error.WriteLine("Run 'pocketmodem --help' for usage.");
            return 2;
        }
        catch (OperationCanceledException)
        {
            return 130;   // conventional exit code for interruption
        }
        catch (Exception ex)
        {
            Output.Error(ex.Message);
            if (cmd.Verbose) Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static int Unknown(string verb)
    {
        Output.Error($"Unknown command '{verb}'.");
        Console.Error.WriteLine("Run 'pocketmodem --help' to see the available commands.");
        return 2;
    }
}

internal static class BuildInfo
{
    public const string Version = "1.0.0";

    public static string Platform =>
        OperatingSystem.IsWindows() ? "windows-x64" :
        OperatingSystem.IsLinux() ? "linux-x64" :
        "unsupported";
}
