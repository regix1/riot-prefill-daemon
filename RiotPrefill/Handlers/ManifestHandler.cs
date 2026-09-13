namespace RiotPrefill.Handlers
{
    using RiotPrefill.Api;

    //TODO comment everything in here
    public sealed class ManifestHandler : IDisposable
    {
        private readonly IAnsiConsole _ansiConsole;
        private readonly HttpClient _httpClient;
        private readonly RequestBudget _budget;
        private readonly string _operationId;
        private readonly int _maxConcurrency;
        private readonly DownloadSettings _settings;
        private readonly Action<string, string> _replace;
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> CacheLocks =
            new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        // HttpClient.Timeout stops applying once ResponseHeadersRead has returned the headers, so each body
        // read below needs its own bound.  Two values because the two kinds of response differ by orders of
        // magnitude: the config endpoints return a few kilobytes of JSON, a manifest is a few megabytes.
        private static readonly TimeSpan ApiBodyTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan ManifestBodyTimeout = TimeSpan.FromSeconds(90);

        // Bounds the request half, up to response headers arriving.  This does NOT overlap the body bounds
        // above, it runs before them, so a call's worst case is this plus its body bound.  The framework
        // default of 100s is far longer than these endpoints take to answer and dominated time to failure.
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

        public ManifestHandler(IAnsiConsole ansiConsole)
            : this(ansiConsole, new HttpClient { Timeout = RequestTimeout })
        {
        }

        internal ManifestHandler(IAnsiConsole ansiConsole, HttpClient httpClient)
            : this(ansiConsole, httpClient, null, "legacy", 20,
                new DownloadSettings(AppConfig.SkipDownloads, AppConfig.DownloadWholeBundle, AppConfig.NoLocalCache, AppConfig.CacheDir))
        {
        }

        internal ManifestHandler(IAnsiConsole ansiConsole, HttpClient httpClient, RequestBudget budget,
            string operationId, int maxConcurrency, DownloadSettings settings, Action<string, string> replace = null)
        {
            _budget = budget;
            _operationId = operationId;
            _maxConcurrency = maxConcurrency;
            _settings = settings;
            _replace = replace;
            _ansiConsole = ansiConsole ?? throw new ArgumentNullException(nameof(ansiConsole));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            if (_httpClient.Timeout == TimeSpan.FromSeconds(100)) _httpClient.Timeout = RequestTimeout;
            if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "RiotNetwork/1.0.0");
            }
        }

        public async Task<ReleaseInfo> FindLatestProductReleaseAsync(
            ArtifactType artifactType,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            //TODO parameterize
            var apiUrl = $"https://sieve.services.riotcdn.net/api/v1/products/lol/version-sets/NA1?q[platform]=windows";
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);

            using var permit = _budget == null ? null : await _budget.AcquireAsync(_operationId, _maxConcurrency, cancellationToken);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var releaseApiResponse = await ReadWithinTimeoutAsync(
                token => JsonSerializer.DeserializeAsync(
                    responseStream,
                    SerializationContext.Default.ReleaseApiResponse,
                    token).AsTask(),
                ApiBodyTimeout,
                cancellationToken);
            var releases = releaseApiResponse.releases;

            var latestVersion = releases.Where(e => e.Platform.Contains("windows"))
                                                    .Where(e => e.ArtifactTypeId == artifactType.Value)
                                                    .OrderByDescending(e => e.Version)
                                                    .First();

            _ansiConsole.LogMarkupLine($"Found latest version {LightYellow(latestVersion.Version)} for artifact {Cyan(latestVersion.ArtifactTypeId)}");
            return latestVersion;
        }

        public async Task<string> DownloadManifestAsync(
            ReleaseInfo release,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cachedFileName = Path.Combine(_settings.CacheDirectory, $"{release._Release.product}-{release.ArtifactTypeId}-{release.Version}.manifest");
            return await DownloadManifestAsync(release.DownloadUrl, cachedFileName, cancellationToken);
        }

        /// <summary>
        /// Bounds a response body read.  These requests use ResponseHeadersRead, which returns as soon as the
        /// headers arrive and takes the body outside HttpClient.Timeout, so a server that sends headers and
        /// then goes quiet would otherwise stall the read with nothing to end it.
        /// </summary>
        private static async Task<T> ReadWithinTimeoutAsync<T>(Func<CancellationToken, Task<T>> read, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try
            {
                return await read(deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Riot sent response headers and then stopped sending data for {timeout.TotalSeconds} seconds.  " +
                    "Check that Riot's servers and the LANCache between them are reachable.");
            }
        }

        private bool ManifestIsCached(string manifestFileName)
        {
            return !_settings.NoLocalCache && File.Exists(manifestFileName);
        }

        public async Task<string> FindPatchlineReleaseAsync(
            Patchline product,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var apiUrl = $"https://clientconfig.rpg.riotgames.com/api/v1/config/public?namespace=keystone.products.{product.Value}.patchlines";
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);

            using var permit = _budget == null ? null : await _budget.AcquireAsync(_operationId, _maxConcurrency, cancellationToken);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var releaseApiResponse = await ReadWithinTimeoutAsync(
                token => JsonSerializer.DeserializeAsync(
                    responseStream,
                    SerializationContext.Default.PatchlinesResponse,
                    token).AsTask(),
                ApiBodyTimeout,
                cancellationToken);

            // Win config selection is region-keyed for some products (LoL/Valorant use "NA"), but
            // others (e.g. Legends of Runeterra / "bacon") expose a single region-agnostic config
            // with id "default". Prefer NA, then default, then whatever Win config exists so we
            // never throw "Sequence contains no matching element" for a valid Win patchline.
            var configurations = releaseApiResponse.KeystoneProduct.platforms.Win.configurations;
            var configuration = configurations.FirstOrDefault(e => e.id.ToUpper() == "NA")
                                ?? configurations.FirstOrDefault(e => e.id.ToLower() == "default")
                                ?? configurations.FirstOrDefault();
            if (configuration == null)
            {
                throw new InvalidOperationException(
                    $"No Win configuration found for patchline '{product.Value}'; cannot resolve a manifest URL.");
            }
            var manifestUrl = configuration.patch_url;

            return manifestUrl;
        }

        public async Task<string> DownloadManifestAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = new Uri(url).AbsolutePath.Split('/').Last();
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains('\\'))
                throw new ArgumentException("The manifest URL must name a file.", nameof(url));
            return await DownloadManifestAsync(url, Path.Combine(_settings.CacheDirectory, name), cancellationToken);
        }

        private async Task<string> DownloadManifestAsync(string url, string cachedFileName, CancellationToken cancellationToken)
        {
            var gate = CacheLocks.GetOrAdd(Path.GetFullPath(cachedFileName), _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (ManifestIsCached(cachedFileName)) return cachedFileName;
                using var permit = _budget == null ? null : await _budget.AcquireAsync(_operationId, _maxConcurrency, cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                var bytes = await ReadWithinTimeoutAsync(token => response.Content.ReadAsByteArrayAsync(token),
                    ManifestBodyTimeout, cancellationToken);
                await CommitAsync(cachedFileName, bytes, cancellationToken, _replace);
                return cachedFileName;
            }
            finally { gate.Release(); }
        }

        internal static async Task<bool> CommitAsync(string path, byte[] bytes, CancellationToken cancellationToken,
            Action<string, string> replace = null, Func<Action, bool> commit = null)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                void Replace()
                {
                    if (replace == null) File.Move(temporary, path, true);
                    else replace(temporary, path);
                }
                if (commit != null) return commit(Replace);
                Replace();
                return true;
            }
            finally { File.Delete(temporary); }
        }

        public void Dispose() => _httpClient.Dispose();

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
