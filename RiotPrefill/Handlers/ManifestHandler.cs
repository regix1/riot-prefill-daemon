namespace RiotPrefill.Handlers
{
    //TODO comment everything in here
    public sealed class ManifestHandler
    {
        private readonly IAnsiConsole _ansiConsole;
        private readonly HttpClient _httpClient;

        public ManifestHandler(IAnsiConsole ansiConsole)
        {
            _ansiConsole = ansiConsole;

            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "RiotNetwork/1.0.0");
        }

        public async Task<ReleaseInfo> FindLatestProductReleaseAsync(ArtifactType artifactType)
        {
            //TODO parameterize
            var apiUrl = $"https://sieve.services.riotcdn.net/api/v1/products/lol/version-sets/NA1?q[platform]=windows";
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);

            // Send request
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var responseStream = await response.Content.ReadAsStreamAsync();
            var releaseApiResponse = await JsonSerializer.DeserializeAsync(responseStream, SerializationContext.Default.ReleaseApiResponse);
            var releases = releaseApiResponse.releases;

            var latestVersion = releases.Where(e => e.Platform.Contains("windows"))
                                                    .Where(e => e.ArtifactTypeId == artifactType.Value)
                                                    .OrderByDescending(e => e.Version)
                                                    .First();

            _ansiConsole.LogMarkupLine($"Found latest version {LightYellow(latestVersion.Version)} for artifact {Cyan(latestVersion.ArtifactTypeId)}");
            return latestVersion;
        }

        public async Task<string> DownloadManifestAsync(ReleaseInfo release)
        {
            // Load from disk if manifest already exists
            var cachedFileName = Path.Combine(AppConfig.CacheDir, $"{release._Release.product}-{release.ArtifactTypeId}-{release.Version}.manifest");
            if (ManifestIsCached(cachedFileName))
            {
                return cachedFileName;
            }

            byte[] responseAsBytes = null;
            await _ansiConsole.StatusSpinner().StartAsync("Downloading manifest", async ctx =>
            {
                var timer = Stopwatch.StartNew();
                using var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl);

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                responseAsBytes = await response.Content.ReadAsByteArrayAsync();
                // Cache to disk
                await File.WriteAllBytesAsync(cachedFileName, responseAsBytes);

                _ansiConsole.LogMarkupLine("Downloaded manifest", timer);
            });
            return cachedFileName;
        }

        private bool ManifestIsCached(string manifestFileName)
        {
            return !AppConfig.NoLocalCache && File.Exists(manifestFileName);
        }

        public async Task<string> FindPatchlineReleaseAsync(Patchline product)
        {
            var apiUrl = $"https://clientconfig.rpg.riotgames.com/api/v1/config/public?namespace=keystone.products.{product.Value}.patchlines";
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);

            // Send request
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var responseStream = await response.Content.ReadAsStreamAsync();
            var releaseApiResponse = await JsonSerializer.DeserializeAsync(responseStream, SerializationContext.Default.PatchlinesResponse);
            var manifestUrl = releaseApiResponse.KeystoneProduct.platforms.Win.configurations.First(e => e.id.ToUpper() == "NA").patch_url;


            return manifestUrl;
        }

        public async Task<string> DownloadManifestAsync(string url)
        {
            // Load from disk if manifest already exists
            var cachedFileName = Path.Combine(AppConfig.CacheDir, url.Split("/").Last());
            if (ManifestIsCached(cachedFileName))
            {
                return cachedFileName;
            }

            byte[] responseAsBytes = null;
            await _ansiConsole.StatusSpinner().StartAsync("Downloading manifest", async ctx =>
            {
                var timer = Stopwatch.StartNew();
                using var request = new HttpRequestMessage(HttpMethod.Get, url);

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                responseAsBytes = await response.Content.ReadAsByteArrayAsync();
                // Cache to disk
                await File.WriteAllBytesAsync(cachedFileName, responseAsBytes);

                _ansiConsole.LogMarkupLine("Downloaded manifest", timer);
            });
            return cachedFileName;
        }

        //TODO improve performance
        public List<Request> BuildDownloadQueue(ReleaseManifest manifest)
        {
            var timer = Stopwatch.StartNew();

            Dictionary<BundleId, Bundle> bundleLookup = manifest.Bundles.Select(originalBundle => new Bundle(originalBundle))
                                                           .ToDictionary(bundle => bundle.Id);

            var allChunksLookup = new Dictionary<string, BundleChunk>();
            var dupes = new List<BundleChunk>();
            foreach (var chunk in bundleLookup.Values.SelectMany(e => e.Chunks).ToList())
            {
                if (!allChunksLookup.ContainsKey(chunk.Id))
                {
                    allChunksLookup.Add(chunk.Id, chunk);
                }
                else
                {
                    //TODO this isn't correct in the way that I'm doing it.  There can be duplicate chunkids between bundles, because the chunk id is only unique for that bundle
                    var existing = allChunksLookup[chunk.Id];
                    dupes.Add(chunk);
                }
            }

            //TODO explain this
            ulong bitMask = 1 | (2 << 9);
            var filteredFiles = manifest.Files.Where(e => e.LanguageFlags == 0 || ((e.LanguageFlags & bitMask) != 0)).ToList();

            //TODO figure out language flags
            var fileChunkIds = filteredFiles.SelectMany(e => e.ChunkIDs)
                                          .Select(e => BitConverter.GetBytes(e).ToHexString())
                                          .ToList();
            _ansiConsole.LogMarkupLine($"Filtered down to {LightYellow(filteredFiles.Count)} files and {Cyan(fileChunkIds.Count)} chunks");

            var chunksToDownload = new List<BundleChunk>();
            foreach (var chunkId in fileChunkIds)
            {
                if (allChunksLookup.ContainsKey(chunkId))
                {
                    chunksToDownload.Add(allChunksLookup[chunkId]);
                }
            }
            var chunksToDownloadDeduped = chunksToDownload.DistinctBy(e => e.Id).ToList();
            _ansiConsole.LogMarkupLine($"Deduped {LightYellow(chunksToDownload.Count)} chunks down to {Cyan(chunksToDownloadDeduped.Count)}");

            var requests = chunksToDownloadDeduped
                                     .Select(e => new Request(e.BundleId.Value, e.OffsetFromStart, e.UpperBound))
                                     .ToList();

            //TODO these need to be combined into multiple ranges in the same request for a single bundle
            var coalesced = RequestUtils.CoalesceRequests(requests);


            var totalSize = ByteSize.FromBytes(coalesced.Sum(e => e.TotalBytes));
            _ansiConsole.LogMarkupLine($"Total download size : {Magenta(totalSize.ToDecimalString())}, with {LightYellow(coalesced.Count)} requests");
            _ansiConsole.LogMarkupLine("Download queue built", timer);
            return coalesced;
        }
    }
}