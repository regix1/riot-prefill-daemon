namespace RiotPrefill
{
    public static class Program
    {
        public static async Task<int> Main()
        {
            try
            {
                // Checking to see if the user double clicked the exe in Windows, and display a message on how to use the app
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

            // Return failed status code, since you can only get to this line if an exception was handled
            return 1;
        }

        /// <summary>
        /// Adds hidden flags that may be useful for debugging/development, but shouldn't be displayed to users in the help text
        /// </summary>
        private static List<string> ParseHiddenFlags()
        {
            // Have to skip the first argument, since its the path to the executable
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();

            if (args.Any(e => e.Contains("--compare-requests")))
            {
                AnsiConsole.Console.LogMarkupLine($"Using {LightYellow("--compare-requests")} flag.  Running comparison logic...");
                // Need to enable SkipDownloads as well in order to get this to work well
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
            // Useful for debugging since the manifests will always be re-downloaded.
            if (args.Any(e => e.Contains("--nocache")) || args.Any(e => e.Contains("--no-cache")))
            {
                AnsiConsole.Console.LogMarkupLine($"Using {LightYellow("--nocache")} flag.  Will always re-download manifests...");
                AppConfig.NoLocalCache = true;
                args.Remove("--nocache");
                args.Remove("--no-cache");
            }

            // Adding some formatting to logging to make it more readable + clear that these flags are enabled
            if (AppConfig.CompareAgainstRealRequests || AppConfig.SkipDownloads || AppConfig.NoLocalCache || AppConfig.DownloadMultirangeOnly || AppConfig.DownloadMultirangeOnly)
            {
                AnsiConsole.Console.WriteLine();
                AnsiConsole.Console.Write(new Rule());
            }

            return args;
        }


    }
}
