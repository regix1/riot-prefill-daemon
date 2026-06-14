// ReSharper disable MemberCanBePrivate.Global - Properties used as parameters can't be private with CliFx, otherwise they won't work.
namespace RiotPrefill.CliCommands
{
    [UsedImplicitly]
    [Command("prefill", Description = "Downloads the latest version of one or more specified app(s).")]
    public class PrefillCommand : ICommand
    {
        [CommandOption("force", 'f',
            Description = "Forces the prefill to always run, overrides the default behavior of only prefilling if a newer version is available.",
            Converter = typeof(NullableBoolConverter))]
        public bool? Force { get; init; }

        private IAnsiConsole _ansiConsole;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            _ansiConsole = console.CreateAnsiConsole();

            AppConfig.DownloadWholeBundle = true;

            await DownloadPatchlineAsync(Patchline.LeagueOfLegends);
            await DownloadPatchlineAsync(Patchline.Valorant);
            await DownloadPatchlineAsync(Patchline.LegendsOfRuneterra);

            // Standalone
            //var releaseInfo = await manifestHandler.FindLatestProductReleaseAsync(ArtifactType.LolStandaloneClientContent);

            //LolGameClient
            //var releaseInfo = await manifestHandler.FindLatestProductReleaseAsync(ArtifactType.LolGameClient);
            //var manifestPathOnDisk = await manifestHandler.DownloadManifestAsync(releaseInfo);

        }

        private async Task DownloadPatchlineAsync(Patchline currentPatchline)
        {
            _ansiConsole.LogMarkupLine($"Starting {Cyan(currentPatchline.Name)}");

            // Downloading manifest
            var manifestHandler = new ManifestHandler(_ansiConsole);
            var manifestUrl = await manifestHandler.FindPatchlineReleaseAsync(currentPatchline);
            var manifestPathOnDisk = await manifestHandler.DownloadManifestAsync(manifestUrl);

            // Parsing manifest
            var parseTimer = Stopwatch.StartNew();
            ReleaseManifest manifest = new ReleaseManifest(manifestPathOnDisk);
            _ansiConsole.LogMarkupLine("Finished parsing manifest", parseTimer);

            // Building out queue
            var downloadQueue = manifestHandler.BuildDownloadQueue(manifest);

            if (AppConfig.CompareAgainstRealRequests)
            {
                var comparisonUtil = new ComparisonUtil();
                await comparisonUtil.CompareAgainstRealRequestsAsync(downloadQueue);
                _ansiConsole.MarkupLine("");
                return;
            }
            if (AppConfig.SkipDownloads)
            {
                _ansiConsole.MarkupLine("");
                return;
            }

            //TODO this needs to be moved over to the manifest handler at some point when it won't break
            // Combining requests to the same bundle into a single request
            var combinedRequests = new List<Request>();
            foreach (var bundle in downloadQueue.GroupBy(e => e.BundleKey).ToList())
            {
                var ranges = bundle.OrderBy(e => e.LowerByteRange)
                                   .Select(e => new ByteRange(e.LowerByteRange, e.UpperByteRange))
                                   .ToList();
                combinedRequests.Add(new Request(bundle.Key, ranges));
            }

            // Do the download
            using var downloader = new DownloadHandler(_ansiConsole, currentPatchline);
            if (AppConfig.DownloadMultirangeOnly)
            {
                var filteredToRangedOnly = combinedRequests.Where(e => e.ByteRanges.Count > 1).ToList();
                await downloader.DownloadQueuedChunksAsync(filteredToRangedOnly);
            }
            else
            {
                await downloader.DownloadQueuedChunksAsync(combinedRequests);
            }

            _ansiConsole.MarkupLine("");
        }

    }
}
