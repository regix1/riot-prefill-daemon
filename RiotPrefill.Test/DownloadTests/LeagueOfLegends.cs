using RiotPrefill.Handlers;
using System.Diagnostics;
using LeagueToolkit.IO.ReleaseManifestFile;
using RiotPrefill.Models;
using RiotPrefill.ReleaseManifestFile;
using Spectre.Console;

namespace RiotPrefill.Test.DownloadTests
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    [ExcludeFromCodeCoverage, Category("SkipCI")]
    public class LeagueOfLegends
    {
        private ComparisonResult _results;

        [OneTimeSetUp]
        public async Task Setup()
        {
            AppConfig.CompareAgainstRealRequests = true;

            // Downloading manifest
            var manifestHandler = new ManifestHandler(AnsiConsole.Console);
            var manifestUrl = await manifestHandler.FindPatchlineReleaseAsync(Patchline.LeagueOfLegends);
            var manifestFilePath = await manifestHandler.DownloadManifestAsync(manifestUrl);

            // Parsing manifest
            ReleaseManifest manifest = new ReleaseManifest(manifestFilePath);

            // Building out queue
            var downloadQueue = manifestHandler.BuildDownloadQueue(manifest);

            var comparisonUtil = new ComparisonUtil();
            _results = await comparisonUtil.CompareAgainstRealRequestsAsync(downloadQueue);
        }

        [Test]
        public void Misses()
        {
            var targetMissCount = 127;
            Assert.LessOrEqual(_results.MissCount, targetMissCount);
        }

        [Test]
        public void MissedBandwidth()
        {
            var targetBandwidth = ByteSize.FromMegaBytes(43);
            Assert.Less(_results.MissedBandwidth.Bytes, targetBandwidth.Bytes);
        }

        [Test]
        public void WastedBandwidth()
        {
            // Anything less than 1MiB is fine
            var targetBandwidth = ByteSize.FromMegaBytes(35);
            Assert.Less(_results.WastedBandwidth.Bytes, targetBandwidth.Bytes);
        }
    }
}
