#nullable enable

using RiotPrefill.Handlers;
using RiotPrefill.Models;
using RiotPrefill.ReleaseManifestFile;
using RiotPrefill.Settings;
using ByteSizeLib;
using Spectre.Console;
using System.Diagnostics;

namespace RiotPrefill.Api;

/// <summary>
/// High-level programmatic API for Riot Prefill operations.
///
/// Riot content (League of Legends + VALORANT) is served anonymously from Riot's public CDN —
/// there is NO account login, no credentials, and no concept of an "owned" library. "Owned games"
/// is therefore the fixed set of Riot patchlines the upstream tool knows how to prefill.
///
/// This wraps the upstream <see cref="ManifestHandler"/> / <see cref="DownloadHandler"/> in-process
/// and routes their Spectre console output to <see cref="IPrefillProgress"/> via
/// <see cref="ApiConsoleAdapter"/>.
/// </summary>
public sealed class RiotPrefillApi : IDisposable
{
    private readonly IPrefillProgress _progress;
    private readonly IAnsiConsole _console;

    private List<string>? _selectedAppsCache;
    private bool _isInitialized;
    private bool _isDisposed;

    /// <summary>
    /// True while <see cref="PrefillAsync"/> is actively running. The download-size estimate in
    /// <see cref="GetSelectedAppsStatusAsync"/> toggles the process-global <see cref="AppConfig.SkipDownloads"/>
    /// flag (via the size pass). We must NEVER run that metadata-only pass while a prefill is active, or the
    /// running prefill would silently skip all transfers. Set/cleared via <see cref="Interlocked"/>.
    /// </summary>
    private int _isPrefilling;

    /// <summary>
    /// Serializes the <see cref="AppConfig.SkipDownloads"/>-mutating size pass so two concurrent
    /// <c>get-selected-apps-status</c> polls cannot clobber each other's save/restore of the global flag.
    /// </summary>
    private readonly SemaphoreSlim _sizePassLock = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Per-product cached download-size estimate, keyed by patchline slug. A status poll computes the size
    /// once (build the manifest + download queue) and reuses it on subsequent polls.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _downloadSizeCache = new ConcurrentDictionary<string, long>();

    /// <summary>
    /// True while a prefill operation is running. Used to suppress the SkipDownloads-mutating size pass.
    /// </summary>
    public bool IsPrefilling => Volatile.Read(ref _isPrefilling) != 0;

    public RiotPrefillApi(IPrefillProgress? progress = null)
    {
        _progress = progress ?? NullProgress.Instance;
        _console = new ApiConsoleAdapter(_progress);
    }

    public bool IsInitialized => _isInitialized;

    public string? DisplayName => "Riot";

    /// <summary>
    /// Initializes the API. Riot is anonymous, so there is no login step — this only
    /// marks the API as ready. Kept async to mirror the daemon contract used by the manager.
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_isInitialized)
            return Task.CompletedTask;

        _progress.OnOperationStarted("Initializing Riot prefill");
        _isInitialized = true;
        _progress.OnOperationCompleted("Initializing Riot prefill", TimeSpan.Zero);
        _progress.OnLog(LogLevel.Info, "Riot prefill ready (anonymous - no login required)");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the fixed Riot patchline catalog. Riot has no per-account library, so this is every
    /// product the prefill tool supports: { AppId = patchline slug, Name = display name }.
    /// </summary>
    public Task<List<OwnedGame>> GetOwnedGamesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        var result = AllPatchlines
            .Select(p => new OwnedGame { AppId = p.Value, Name = DisplayNameFor(p) })
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _progress.OnLog(LogLevel.Info, $"Returning {result.Count} Riot products");
        return Task.FromResult(result);
    }

    public List<string> GetSelectedApps()
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        if (_selectedAppsCache != null && _selectedAppsCache.Count > 0)
        {
            _progress.OnLog(LogLevel.Info, $"GetSelectedApps: Returning {_selectedAppsCache.Count} cached apps");
            return _selectedAppsCache;
        }

        // Riot has no persisted selection file wired up in the upstream tool; default to the full
        // catalog so a fresh daemon still has something to prefill / size.
        var all = AllPatchlines.Select(p => p.Value).ToList();
        _progress.OnLog(LogLevel.Info, $"GetSelectedApps: No selection set, defaulting to all {all.Count} products");
        return all;
    }

    public void SetSelectedApps(IEnumerable<string> appIds)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        var appIdList = appIds
            .Where(id => ResolvePatchline(id) != null)
            .ToList();
        _selectedAppsCache = appIdList;
        _progress.OnLog(LogLevel.Info, $"Set {appIdList.Count} apps for prefill");
    }

    /// <summary>
    /// Reports cache status by checking the per-product prefill marker files written after a successful
    /// prefill. A product is considered "up to date" when a prefill marker exists for it. A live CDN
    /// version comparison is performed by the actual prefill run; this status is network-free.
    /// </summary>
    public Task<CacheStatusResult> CheckCacheStatusAsync(List<string> appIds, CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        if (appIds.Count == 0)
        {
            return Task.FromResult(new CacheStatusResult
            {
                Apps = new List<AppCacheStatus>(),
                Message = "No app IDs provided"
            });
        }

        var apps = new List<AppCacheStatus>();
        foreach (var appId in appIds.Distinct())
        {
            var patchline = ResolvePatchline(appId);
            apps.Add(new AppCacheStatus
            {
                AppId = appId,
                Name = patchline != null ? DisplayNameFor(patchline) : appId,
                IsUpToDate = HasPrefillMarker(appId)
            });
        }

        return Task.FromResult(new CacheStatusResult
        {
            Apps = apps,
            Message = $"Checked {apps.Count} apps"
        });
    }

    /// <summary>
    /// Status of the currently selected apps, including the per-product download size. The size is
    /// computed by building the download queue (manifest discovery + parse + coalesce) WITHOUT
    /// transferring any bytes — this is exactly what the CLI prints as "Total download size".
    /// </summary>
    public async Task<SelectedAppsStatus> GetSelectedAppsStatusAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        var selectedAppIds = GetSelectedApps();
        if (selectedAppIds.Count == 0)
        {
            return new SelectedAppsStatus
            {
                Apps = new List<AppStatus>(),
                TotalDownloadSize = 0,
                Message = "No apps selected"
            };
        }

        var apps = new List<AppStatus>();
        long totalDownloadSize = 0;

        foreach (var appId in selectedAppIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var patchline = ResolvePatchline(appId);
            var isUpToDate = HasPrefillMarker(appId);
            long downloadSize = 0;

            if (patchline != null)
            {
                downloadSize = await GetCachedDownloadSizeAsync(patchline, cancellationToken);
            }

            totalDownloadSize += downloadSize;
            apps.Add(new AppStatus
            {
                AppId = appId,
                Name = patchline != null ? DisplayNameFor(patchline) : appId,
                DownloadSize = downloadSize,
                IsUpToDate = isUpToDate
            });
        }

        return new SelectedAppsStatus
        {
            Apps = apps,
            TotalDownloadSize = totalDownloadSize
        };
    }

    /// <summary>
    /// Returns the per-product download-size estimate, using a slug-keyed cache to avoid repeated
    /// manifest round-trips. SAFETY: the size pass sets the process-global <see cref="AppConfig.SkipDownloads"/>
    /// so the queue is built but no bytes transfer. A concurrent live prefill reads that same flag, so we MUST
    /// NOT run the pass while <see cref="IsPrefilling"/> is true. The pass is serialized behind
    /// <see cref="_sizePassLock"/> so two concurrent polls cannot clobber the flag's save/restore.
    /// </summary>
    private async Task<long> GetCachedDownloadSizeAsync(Patchline patchline, CancellationToken cancellationToken)
    {
        if (_downloadSizeCache.TryGetValue(patchline.Value, out var cachedSize))
        {
            return cachedSize;
        }

        if (IsPrefilling)
        {
            _progress.OnLog(LogLevel.Info, $"Prefill in progress - skipping size estimate for {DisplayNameFor(patchline)}");
            return 0;
        }

        await _sizePassLock.WaitAsync(cancellationToken);
        try
        {
            if (_downloadSizeCache.TryGetValue(patchline.Value, out cachedSize))
            {
                return cachedSize;
            }
            if (IsPrefilling)
            {
                _progress.OnLog(LogLevel.Info, $"Prefill started - skipping size estimate for {DisplayNameFor(patchline)}");
                return 0;
            }

            var previousSkip = AppConfig.SkipDownloads;
            AppConfig.SkipDownloads = true;
            try
            {
                var size = await ComputeDownloadSizeAsync(patchline, cancellationToken);
                _downloadSizeCache[patchline.Value] = size;
                return size;
            }
            catch (Exception ex)
            {
                var lancacheIp = Environment.GetEnvironmentVariable("LANCACHE_IP");
                var lancacheInfo = string.IsNullOrWhiteSpace(lancacheIp)
                    ? "LANCACHE_IP not set (using DNS auto-detect)"
                    : $"LANCACHE_IP={lancacheIp}";
                var inner = ex.InnerException != null ? $" | Inner: {ex.InnerException.Message}" : string.Empty;
                _progress.OnLog(LogLevel.Warning,
                    $"Failed to get size for {DisplayNameFor(patchline)} [{lancacheInfo}]: {ex.Message}{inner}");
                return 0;
            }
            finally
            {
                AppConfig.SkipDownloads = previousSkip;
            }
        }
        finally
        {
            _sizePassLock.Release();
        }
    }

    /// <summary>
    /// Builds the download queue for a patchline and returns its total byte size, WITHOUT transferring
    /// any bytes. Mirrors the CLI's discovery -> manifest download -> parse -> BuildDownloadQueue path.
    /// </summary>
    private async Task<long> ComputeDownloadSizeAsync(Patchline patchline, CancellationToken cancellationToken)
    {
        var manifestHandler = new ManifestHandler(_console);
        var manifestUrl = await manifestHandler.FindPatchlineReleaseAsync(patchline);
        var manifestPathOnDisk = await manifestHandler.DownloadManifestAsync(manifestUrl);

        var manifest = new ReleaseManifest(manifestPathOnDisk);
        var downloadQueue = manifestHandler.BuildDownloadQueue(manifest);

        return downloadQueue.Sum(e => e.TotalBytes);
    }

    /// <summary>
    /// Runs the prefill operation, emitting structured progress events per product.
    /// </summary>
    public async Task<PrefillResult> PrefillAsync(
        PrefillOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        options ??= new PrefillOptions();

        Interlocked.Exchange(ref _isPrefilling, 1);
        try
        {

        _progress.OnOperationStarted("Prefill operation");
        var timer = Stopwatch.StartNew();

        // The whole-bundle download path is what the CLI uses for a real prefill.
        AppConfig.DownloadWholeBundle = true;
        // Ensure a live prefill is never in skip-downloads mode (a prior size pass restores the flag,
        // but be explicit so a stray flag can't silently no-op the transfer).
        AppConfig.SkipDownloads = false;

        // Resolve the set of patchlines to prefill.
        List<Patchline> products;
        if (options.Products is { Count: > 0 })
        {
            products = options.Products
                .Select(ResolvePatchline)
                .Where(p => p != null)
                .Select(p => p!)
                .ToList();
        }
        else if (options.DownloadAllOwnedGames)
        {
            products = AllPatchlines.ToList();
        }
        else
        {
            products = GetSelectedApps()
                .Select(ResolvePatchline)
                .Where(p => p != null)
                .Select(p => p!)
                .ToList();
        }

        products = products.Distinct().ToList();

        if (products.Count == 0)
        {
            _progress.OnError("No apps selected for prefill. Select apps first or pass 'all'.");
            return new PrefillResult
            {
                Success = false,
                ErrorMessage = "No apps selected for prefill",
                TotalTime = timer.Elapsed
            };
        }

        var updated = 0;
        var failed = 0;
        long totalBytesTransferred = 0;

        try
        {
            foreach (var patchline in products)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var cachedTotal = _downloadSizeCache.TryGetValue(patchline.Value, out var estTotal) ? estTotal : 0;
                var appInfo = new AppDownloadInfo { AppId = patchline.Value, Name = DisplayNameFor(patchline), TotalBytes = cachedTotal };
                _progress.OnAppStarted(appInfo);

                try
                {
                    var bytes = await PrefillPatchlineAsync(patchline, appInfo, cancellationToken);
                    totalBytesTransferred += bytes;
                    WritePrefillMarker(patchline.Value);
                    updated++;
                    _progress.OnAppCompleted(appInfo, AppDownloadResult.Success);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    _progress.OnLog(LogLevel.Warning, $"Prefill failed for {DisplayNameFor(patchline)}: {ex.Message}");
                    _progress.OnAppCompleted(appInfo, AppDownloadResult.Failed);
                }
            }

            timer.Stop();

            _progress.OnPrefillCompleted(new PrefillSummary
            {
                TotalApps = products.Count,
                UpdatedApps = updated,
                AlreadyUpToDate = 0,
                FailedApps = failed,
                TotalBytesTransferred = totalBytesTransferred,
                TotalTime = timer.Elapsed
            });

            _progress.OnOperationCompleted("Prefill operation", timer.Elapsed);

            return new PrefillResult
            {
                Success = failed == 0,
                ErrorMessage = failed == 0 ? null : $"{failed} product(s) failed to prefill",
                TotalTime = timer.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            _progress.OnLog(LogLevel.Info, "Prefill operation cancelled");
            return new PrefillResult
            {
                Success = false,
                ErrorMessage = "Prefill cancelled",
                TotalTime = timer.Elapsed
            };
        }
        catch (Exception ex)
        {
            _progress.OnError("Prefill operation failed", ex);
            return new PrefillResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                TotalTime = timer.Elapsed
            };
        }

        }
        finally
        {
            _downloadSizeCache.Clear();
            Interlocked.Exchange(ref _isPrefilling, 0);
        }
    }

    /// <summary>
    /// Prefills a single patchline: discover release -> download+parse manifest -> build queue ->
    /// coalesce per-bundle ranges -> download. Returns the total bytes in the queue. Mirrors the CLI's
    /// DownloadPatchlineAsync but headless and with structured byte progress threaded into the handler.
    /// </summary>
    private async Task<long> PrefillPatchlineAsync(Patchline patchline, AppDownloadInfo appInfo, CancellationToken cancellationToken)
    {
        var manifestHandler = new ManifestHandler(_console);
        var manifestUrl = await manifestHandler.FindPatchlineReleaseAsync(patchline);
        var manifestPathOnDisk = await manifestHandler.DownloadManifestAsync(manifestUrl);

        var manifest = new ReleaseManifest(manifestPathOnDisk);
        var downloadQueue = manifestHandler.BuildDownloadQueue(manifest);

        var totalBytes = downloadQueue.Sum(e => e.TotalBytes);

        // Combine requests to the same bundle into a single multi-range request (same as the CLI).
        var combinedRequests = new List<Request>();
        foreach (var bundle in downloadQueue.GroupBy(e => e.BundleKey).ToList())
        {
            var ranges = bundle.OrderBy(e => e.LowerByteRange)
                               .Select(e => new ByteRange(e.LowerByteRange, e.UpperByteRange))
                               .ToList();
            combinedRequests.Add(new Request(bundle.Key, ranges));
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var downloader = new DownloadHandler(_console, patchline, _progress, patchline.Value, DisplayNameFor(patchline));
        await downloader.DownloadQueuedChunksAsync(combinedRequests);

        return totalBytes;
    }

    private static IReadOnlyList<Patchline> AllPatchlines { get; } = new[]
    {
        Patchline.LeagueOfLegends,
        Patchline.Valorant
    };

    private static string DisplayNameFor(Patchline patchline)
    {
        if (patchline == Patchline.LeagueOfLegends) return "League of Legends";
        if (patchline == Patchline.Valorant) return "Valorant";
        return patchline.Value;
    }

    private static Patchline? ResolvePatchline(string appId)
    {
        return AllPatchlines
            .FirstOrDefault(p => string.Equals(p.Value, appId, StringComparison.OrdinalIgnoreCase));
    }

    private static string PrefillMarkerPath(string slug)
        => Path.Combine(AppConfig.CacheDir, $"prefilledVersion-{slug}.txt");

    private static bool HasPrefillMarker(string slug)
        => File.Exists(PrefillMarkerPath(slug));

    private static void WritePrefillMarker(string slug)
    {
        try
        {
            File.WriteAllText(PrefillMarkerPath(slug), DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            // Best-effort marker; failure to write it only means the next status poll reports
            // "not up to date", which is harmless.
        }
    }

    private static (int FileCount, long TotalBytes)? GetCacheStats()
    {
        var cacheDir = new DirectoryInfo(AppConfig.CacheDir);
        if (!cacheDir.Exists)
            return null;

        var files = cacheDir.EnumerateFiles("*.*", SearchOption.AllDirectories).ToList();
        return (files.Count, files.Sum(e => e.Length));
    }

    public static ClearCacheResult ClearCache()
    {
        var stats = GetCacheStats();
        if (stats is not { FileCount: > 0 })
        {
            return new ClearCacheResult { Success = true, FileCount = 0, BytesCleared = 0, Message = "Cache directory is already empty" };
        }

        var (fileCount, totalBytes) = stats.Value;

        try
        {
            Directory.Delete(AppConfig.CacheDir, true);
            Directory.CreateDirectory(AppConfig.CacheDir);
            var clearedSize = ByteSize.FromBytes(totalBytes);
            return new ClearCacheResult
            {
                Success = true,
                FileCount = fileCount,
                BytesCleared = totalBytes,
                Message = $"Cleared {fileCount} files ({clearedSize.ToString()})"
            };
        }
        catch (Exception ex)
        {
            return new ClearCacheResult { Success = false, FileCount = 0, BytesCleared = 0, Message = $"Failed to clear cache: {ex.Message}" };
        }
    }

    public static ClearCacheResult GetCacheInfo()
    {
        var stats = GetCacheStats();
        if (stats == null)
        {
            return new ClearCacheResult { Success = true, FileCount = 0, BytesCleared = 0, Message = "Cache directory is empty" };
        }

        var (fileCount, totalBytes) = stats.Value;
        var cacheSize = ByteSize.FromBytes(totalBytes);

        return new ClearCacheResult
        {
            Success = true,
            FileCount = fileCount,
            BytesCleared = totalBytes,
            Message = $"Cache contains {fileCount} files ({cacheSize.ToString()})"
        };
    }

    public void Shutdown()
    {
        _isInitialized = false;
        _progress.OnLog(LogLevel.Info, "Riot prefill shut down");
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        Shutdown();
        _sizePassLock.Dispose();
        _isDisposed = true;
    }

    private void ThrowIfNotInitialized()
    {
        if (!_isInitialized)
            throw new InvalidOperationException("RiotPrefillApi not initialized. Call InitializeAsync first.");
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(RiotPrefillApi));
    }
}

public class PrefillOptions
{
    public bool DownloadAllOwnedGames { get; set; }
    public bool Force { get; set; }

    /// <summary>
    /// Optional explicit list of patchline slugs to prefill. When empty, falls back to the selected
    /// apps (or the full catalog when <see cref="DownloadAllOwnedGames"/> is set).
    /// </summary>
    public List<string>? Products { get; set; }
}

public class PrefillResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan TotalTime { get; init; }
}

public class ClearCacheResult
{
    public bool Success { get; init; }
    public int FileCount { get; init; }
    public long BytesCleared { get; init; }
    public string? Message { get; init; }
}

public class AppStatus
{
    public string AppId { get; init; } = "";
    public string Name { get; init; } = "";
    public long DownloadSize { get; init; }
    public bool IsUpToDate { get; init; }
}

public class SelectedAppsStatus
{
    public List<AppStatus> Apps { get; init; } = new();
    public long TotalDownloadSize { get; init; }
    public string? Message { get; init; }
}

public class OwnedGame
{
    public string AppId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

public class CacheStatusResult
{
    public List<AppCacheStatus> Apps { get; init; } = new();
    public string? Message { get; init; }
}

public class AppCacheStatus
{
    public string AppId { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsUpToDate { get; init; }
}
