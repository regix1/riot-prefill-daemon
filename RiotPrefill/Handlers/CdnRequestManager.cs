namespace RiotPrefill.Handlers
{
    public sealed class DownloadHandler : IDisposable
    {
        private readonly IAnsiConsole _ansiConsole;
        private readonly HttpClient _client;

        private readonly string _currentCdn;

        // Optional structured-progress sink used when the handler is driven by the daemon API layer
        // (RiotPrefillApi). When null the handler behaves exactly like the interactive CLI path and
        // only renders Spectre progress. When set, it emits IPrefillProgress.OnDownloadProgress byte
        // updates so the socket can stream "downloading" events to lancache-manager.
        private readonly RiotPrefill.Api.IPrefillProgress _progress;
        private readonly string _progressAppId;
        private readonly string _progressAppName;

        // Cumulative bytes transferred across the whole queue (live byte progress for the API sink).
        private long _bytesTransferred;
        private long _queueTotalBytes;
        private readonly Stopwatch _progressTimer = new Stopwatch();

        /// <summary>
        /// The URL/IP Address where the Lancache has been detected.
        /// </summary>
        private string _lancacheAddress;

        public DownloadHandler(IAnsiConsole ansiConsole, Patchline product)
            : this(ansiConsole, product, null, null, null)
        {
        }

        /// <summary>
        /// Progress-aware constructor used by the daemon API. <paramref name="progress"/>,
        /// <paramref name="appId"/> and <paramref name="appName"/> let the handler emit structured
        /// byte-progress events for the current product. All three are optional; when omitted the
        /// handler runs exactly as the interactive CLI does.
        /// </summary>
        public DownloadHandler(IAnsiConsole ansiConsole, Patchline product, RiotPrefill.Api.IPrefillProgress progress, string appId, string appName)
        {
            _ansiConsole = ansiConsole;
            _progress = progress ?? RiotPrefill.Api.NullProgress.Instance;
            _progressAppId = appId ?? product.Value;
            _progressAppName = appName ?? product.Name;

            _client = new HttpClient();
            _client.DefaultRequestHeaders.Add("User-Agent", "RiotNetwork/1.0.0");

            //TODO this is ugly and I don't like having to determine which cdn like this.  Should probably be passed in with the download list.
            if (product == Patchline.LeagueOfLegends)
            {
                _currentCdn = "lol.dyn.riotcdn.net";
            }
            if (product == Patchline.Valorant)
            {
                _currentCdn = "valorant.dyn.riotcdn.net";
            }
            if (product == Patchline.LegendsOfRuneterra)
            {
                _currentCdn = "bacon.dyn.riotcdn.net";
            }
        }

        public async Task InitializeAsync()
        {
            if (_lancacheAddress == null)
            {
                _lancacheAddress = await LancacheIpResolver.ResolveLancacheIpAsync(_ansiConsole, _currentCdn);
            }
        }

        /// <summary>
        /// Attempts to download all queued requests.  If all downloads are successful, will return true.
        /// In the case of any failed downloads, the failed downloads will be retried up to 3 times.  If the downloads fail 3 times, then
        /// false will be returned
        /// </summary>
        /// <returns>True if all downloads succeeded.  False if any downloads failed 3 times in a row.</returns>
        public async Task<bool> DownloadQueuedChunksAsync(List<Request> queuedRequests, CancellationToken cancellationToken = default)
        {
            await InitializeAsync();

            // Emit the up-front "preparing" total so the daemon UI can show "0 B / <total>" before
            // the first byte flows. No-op when running interactively (NullProgress).
            _queueTotalBytes = queuedRequests.Sum(e => e.TotalBytes2);
            _bytesTransferred = 0;
            _progressTimer.Restart();
            _progress.OnDownloadProgress(new RiotPrefill.Api.DownloadProgressInfo
            {
                AppId = _progressAppId,
                AppName = _progressAppName,
                BytesDownloaded = 0,
                TotalBytes = _queueTotalBytes,
                Elapsed = _progressTimer.Elapsed,
                State = "preparing"
            });

            int retryCount = 0;
            var failedRequests = new ConcurrentBag<Request>();
            await _ansiConsole.CreateSpectreProgress(TransferSpeedUnit.Bits).StartAsync(async ctx =>
            {
                // Run the initial download
                failedRequests = await AttemptDownloadAsync(ctx, "Downloading..", queuedRequests, cancellationToken: cancellationToken);

                // Handle any failed requests
                while (failedRequests.Any() && retryCount < 2)
                {
                    retryCount++;
                    failedRequests = await AttemptDownloadAsync(ctx, $"Retrying  {retryCount}..", failedRequests.ToList(), forceRecache: true, cancellationToken: cancellationToken);
                }
            });

            // Handling final failed requests
            if (failedRequests.IsEmpty)
            {
                return true;
            }

            _ansiConsole.LogMarkupError($"Download failed! {LightYellow(failedRequests.Count)} requests failed unexpectedly, see {LightYellow("app.log")} for more details.");
            _ansiConsole.WriteLine();

            return false;
        }

        //TODO I don't like the number of parameters here, should maybe rethink the way this is written.
        /// <summary>
        /// Attempts to download the specified requests.  Returns a list of any requests that have failed for any reason.
        /// </summary>
        /// <param name="forceRecache">When specified, will cause the cache to delete the existing cached data for a request, and re-download it again.</param>
        /// <returns>A list of failed requests</returns>
        public async Task<ConcurrentBag<Request>> AttemptDownloadAsync(ProgressContext ctx, string taskTitle, List<Request> requestsToDownload, bool forceRecache = false, CancellationToken cancellationToken = default)
        {
            // Route every request through the resolved lancache server (URL-rewrite + Host-header spoof).
            // Without this the request connects straight to the public CDN IP and the cache is bypassed.
            if (string.IsNullOrEmpty(_lancacheAddress))
            {
                throw new InvalidOperationException(
                    $"Lancache address has not been resolved for CDN '{_currentCdn}'. Cannot route downloads through the cache.");
            }

            double requestTotalSize = requestsToDownload.Sum(e => e.TotalBytes2);
            var progressTask = ctx.AddTask(taskTitle, new ProgressTaskSettings { MaxValue = requestTotalSize });

            var failedRequests = new ConcurrentBag<Request>();

            await Parallel.ForEachAsync(requestsToDownload, new ParallelOptions { MaxDegreeOfParallelism = 20, CancellationToken = cancellationToken }, body: async (request, ct) =>
            {
                try
                {
                    // Connect to the lancache server (so it can cache), but keep the real CDN host name as the
                    // Host header so lancache keys/serves the cached object correctly.
                    var url = $"http://{_lancacheAddress}/channels/public/bundles/{request.BundleKey.ToUpper()}.bundle";
                    if (forceRecache)
                    {
                        url += "?nocache=1";
                    }
                    using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
                    requestMessage.Headers.Host = _currentCdn;

                    BuildRangeHeader(request, requestMessage);

                    using var response = await _client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, ct);
                    using Stream responseStream = await response.Content.ReadAsStreamAsync(ct);
                    response.EnsureSuccessStatusCode();

                    // Don't save the data anywhere, so we don't have to waste time writing it to disk.
                    var buffer = new byte[4096];
                    while (await responseStream.ReadAsync(buffer, ct) != 0)
                    {
                    }
                }
                catch (OperationCanceledException)
                {
                    // User-initiated cancel: propagate so Parallel.ForEachAsync stops and the caller treats
                    // it as a cancellation rather than a per-request failure.
                    throw;
                }
                catch (Exception)
                {
                    _ansiConsole.LogMarkupError($"Request failed {request.ToString()}");
                    failedRequests.Add(request);
                }
                progressTask.Increment(request.TotalBytes2);

                // Structured byte-progress for the daemon API sink. Internally throttled by the
                // SocketProgress (250ms) so emitting per-request here is fine; the sink coalesces.
                var transferred = Interlocked.Add(ref _bytesTransferred, request.TotalBytes2);
                var elapsed = _progressTimer.Elapsed;
                var bytesPerSecond = elapsed.TotalSeconds > 0 ? transferred / elapsed.TotalSeconds : 0;
                _progress.OnDownloadProgress(new RiotPrefill.Api.DownloadProgressInfo
                {
                    AppId = _progressAppId,
                    AppName = _progressAppName,
                    BytesDownloaded = transferred,
                    TotalBytes = _queueTotalBytes,
                    BytesPerSecond = bytesPerSecond,
                    Elapsed = elapsed,
                    State = "downloading"
                });
            });


            // Making sure the progress bar is always set to its max value, in-case some unexpected error leaves the progress bar showing as unfinished
            progressTask.Increment(progressTask.MaxValue);
            return failedRequests;
        }

        private static void BuildRangeHeader(Request request, HttpRequestMessage requestMessage)
        {
            if (AppConfig.DownloadWholeBundle)
            {
                return;
            }

            if (request.ByteRanges == null || request.ByteRanges.Count == 0)
            {
                // Single range
                requestMessage.Headers.Range = new RangeHeaderValue(request.LowerByteRange, request.UpperByteRange);
            }
            else
            {
                // Multiple combined
                var joined = String.Join(",", request.ByteRanges.Select(e => e.ToString()));
                requestMessage.Headers.Add("Range", $"bytes={joined}");
            }
        }

        public void Dispose()
        {
            _client?.Dispose();
        }
    }
}