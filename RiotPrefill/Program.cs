#nullable enable

using RiotPrefill.Api;

namespace RiotPrefill
{
    public static class Program
    {
        public static async Task<int> Main()
        {
            // The first CLI argument selects the mode:
            //   * "prefill" (and any other CliFx command) -> run the interactive CLI (unchanged behavior).
            //   * no command                              -> run as the socket daemon (what the Docker image does).
            var rawArgs = Environment.GetCommandLineArgs().Skip(1).ToList();
            var isCliCommand = rawArgs.Any(a => a.Equals("prefill", StringComparison.OrdinalIgnoreCase));

            if (isCliCommand)
            {
                return await RunCliAsync();
            }

            return await RunDaemonAsync();
        }

        /// <summary>
        /// Runs the interactive CLI (the upstream tpill90 prefill command). Unchanged from the original.
        /// </summary>
        private static async Task<int> RunCliAsync()
        {
            try
            {
                OperatingSystemUtils.DetectDoubleClickOnWindows("RiotPrefill");

                var cliArgs = ParseHiddenFlags();
                var description = "Automatically fills a Lancache with games from Riot, so that subsequent downloads will be \n" +
                                  "  served from the Lancache, improving speeds and reducing load on your internet connection. \n";

                return await new CliApplicationBuilder()
                             .AddCommandsFromThisAssembly()
                             .SetTitle("RiotPrefill")
                             .SetExecutableNamePlatformAware("RiotPrefill")
                             .SetDescription(description)
                             .SetVersion($"v{ThisAssembly.Info.InformationalVersion}")
                             .Build()
                             .RunAsync(cliArgs);
            }
            catch (Exception e)
            {
                AnsiConsole.Console.LogException(e);
            }

            return 1;
        }

        /// <summary>
        /// Runs RiotPrefill in daemon mode over a Unix Domain Socket (or TCP via PREFILL_TCP_PORT),
        /// speaking the length-prefixed JSON protocol that lancache-manager's SocketDaemonClient expects.
        /// Riot content is anonymous - no account login required.
        /// </summary>
        private static async Task<int> RunDaemonAsync()
        {
            try
            {
                ParseHiddenFlags();

                Console.WriteLine($"""
                    ╔═══════════════════════════════════════════════════════════╗
                    ║                  RiotPrefill Daemon                       ║
                    ║                  v{ThisAssembly.Info.InformationalVersion,-20}             ║
                    ╚═══════════════════════════════════════════════════════════╝

                    Riot content is anonymous - no account login required.

                    """);

                var tcpPortEnv = Environment.GetEnvironmentVariable("PREFILL_TCP_PORT");
                var useTcp = int.TryParse(tcpPortEnv, out var tcpPort) && tcpPort > 0;

                if (!useTcp)
                {
                    Console.WriteLine("Using Unix Domain Socket for reliable, low-latency IPC.");
                    Console.WriteLine();
                }

                var responsesDir = Environment.GetEnvironmentVariable("PREFILL_RESPONSES_DIR") ?? "/responses";
                var socketPath = Environment.GetEnvironmentVariable("PREFILL_SOCKET_PATH") ??
                                Path.Combine(responsesDir, "daemon.sock");

                using var cts = new CancellationTokenSource();

                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("\nShutdown signal received...");
                    cts.Cancel();
                };

                // Optional max-lifetime self-shutdown. If PREFILL_MAX_LIFETIME_SECONDS is a positive
                // integer, schedule a one-shot timer that cancels the host CTS so the daemon exits cleanly.
                using System.Threading.Timer? maxLifetimeTimer = CreateMaxLifetimeTimer(cts);

                if (useTcp)
                {
                    await DaemonMode.RunTcpAsync(tcpPort, cts.Token);
                }
                else
                {
                    await DaemonMode.RunAsync(socketPath, cts.Token);
                }

                return 0;
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Fatal error: {e.Message}");
                if (AppConfig.VerboseLogs)
                {
                    Console.WriteLine(e.StackTrace);
                }
                return 1;
            }
        }

        /// <summary>
        /// Creates a one-shot self-shutdown timer driven by the PREFILL_MAX_LIFETIME_SECONDS env var.
        /// When it elapses, the host <see cref="CancellationTokenSource"/> is cancelled so the long-lived
        /// daemon loop unblocks and the process exits 0. Returns null when the env var is unset/0/non-integer.
        /// </summary>
        private static System.Threading.Timer? CreateMaxLifetimeTimer(CancellationTokenSource cts)
        {
            var raw = Environment.GetEnvironmentVariable("PREFILL_MAX_LIFETIME_SECONDS");
            if (!int.TryParse(raw, out var seconds) || seconds <= 0)
            {
                return null;
            }

            Console.WriteLine($"Max lifetime enabled: daemon will self-shutdown after {seconds} second(s).");

            return new System.Threading.Timer(
                _ =>
                {
                    Console.WriteLine($"Max lifetime of {seconds} second(s) reached. Initiating clean shutdown...");
                    try
                    {
                        cts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // CTS already disposed (process is shutting down anyway) - nothing to do.
                    }
                },
                null,
                TimeSpan.FromSeconds(seconds),
                Timeout.InfiniteTimeSpan);
        }

        /// <summary>
        /// Adds hidden flags that may be useful for debugging/development, but shouldn't be displayed to users in the help text
        /// </summary>
        private static List<string> ParseHiddenFlags()
        {
            // Have to skip the first argument, since its the path to the executable
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();

            if (args.Any(e => e.Contains("--debug")) || args.Any(e => e.Contains("--verbose")))
            {
                AnsiConsole.Console.LogMarkupLine($"Using verbose logging flag.  Displaying verbose logging...");
                AppConfig.VerboseLogs = true;
                args.Remove("--debug");
                args.Remove("--verbose");
            }

            if (args.Any(e => e.Contains("--compare-requests")))
            {
                AnsiConsole.Console.LogMarkupLine($"Using {LightYellow("--compare-requests")} flag.  Running comparison logic...");
                AppConfig.CompareAgainstRealRequests = true;
                args.Remove("--compare-requests");
            }

            // Will skip over downloading logic.  Will only download manifests
            if (args.Any(e => e.Contains("--no-download")))
            {
                AnsiConsole.Console.LogMarkupLine($"Using {LightYellow("--no-download")} flag.  Will skip downloading chunks...");
                AppConfig.SkipDownloads = true;
                args.Remove("--no-download");
            }

            if (args.Any(e => e.Contains("--multirange-only")))
            {
                AnsiConsole.Console.LogMarkupLine($"Using {LightYellow("--multirange-only")} flag.  Will only download requests with multiple ranges specified...");
                AppConfig.DownloadMultirangeOnly = true;
                args.Remove("--multirange-only");
            }

            if (args.Any(e => e.Contains("--whole-bundle")))
            {
                AnsiConsole.Console.LogMarkupLine($"Using {LightYellow("--whole-bundle")} flag.  Will download entire bundle instead of only ranges");
                AppConfig.DownloadMultirangeOnly = true;
                args.Remove("--whole-bundle");
            }

            // Skips using locally cached manifests. Saves disk space, at the expense of slower subsequent runs.
            if (args.Any(e => e.Contains("--nocache")) || args.Any(e => e.Contains("--no-cache")))
            {
                AnsiConsole.Console.LogMarkupLine($"Using {LightYellow("--nocache")} flag.  Will always re-download manifests...");
                AppConfig.NoLocalCache = true;
                args.Remove("--nocache");
                args.Remove("--no-cache");
            }

            // Adding some formatting to logging to make it more readable + clear that these flags are enabled
            if (AppConfig.CompareAgainstRealRequests || AppConfig.SkipDownloads || AppConfig.NoLocalCache || AppConfig.DownloadMultirangeOnly)
            {
                AnsiConsole.Console.WriteLine();
                AnsiConsole.Console.Write(new Rule());
            }

            return args;
        }
    }
}
