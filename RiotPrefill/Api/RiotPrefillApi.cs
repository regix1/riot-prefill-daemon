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

    private int _isPrefilling;
    private readonly SemaphoreSlim _sizePassLock = new(1, 1);
    private readonly ConcurrentDictionary<string, long> _downloadSizeCache = new();
    private readonly ItemClaims _claims = new();
    private readonly RequestBudget _budget;
    private readonly Func<HttpClient> _createClient;
    private readonly string? _lancacheAddress;
    private readonly DownloadSettings _settings;
    private readonly Action<string, string>? _replace;

    public bool IsPrefilling => Volatile.Read(ref _isPrefilling) != 0;

    public RiotPrefillApi(IPrefillProgress? progress = null)
        : this(progress, PrefillProtocol.FromEnvironment(20), () => new HttpClient(), AppConfig.CacheDir, null)
    {
    }

    internal RiotPrefillApi(IPrefillProgress? progress, PrefillProtocol protocol,
        Func<HttpClient> createClient, string cacheDirectory, string? lancacheAddress,
        Action<string, string>? replace = null)
    {
        _progress = progress ?? NullProgress.Instance;
        _console = new ApiConsoleAdapter(_progress);
        _budget = new RequestBudget(protocol.MaxConcurrentRequests);
        _createClient = createClient;
        _lancacheAddress = lancacheAddress;
        _settings = new DownloadSettings(false, true, AppConfig.NoLocalCache, cacheDirectory);
        _replace = replace;
        Directory.CreateDirectory(cacheDirectory);
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

        if (_selectedAppsCache != null)
        {
            _progress.OnLog(LogLevel.Info, $"GetSelectedApps: Returning {_selectedAppsCache.Count} cached apps");
            return _selectedAppsCache.ToList();
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
    /// Reports cache status by comparing the Manager's stored revision with the current live
    /// manifest revision for each requested product.
    /// </summary>
    public async Task<CacheStatusResult> CheckCacheStatusAsync(List<CachedAppInput> cachedApps, CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        if (cachedApps.Count == 0)
        {
            return new CacheStatusResult
            {
                Apps = new List<AppCacheStatus>(),
                Message = "No app IDs provided"
            };
        }

        var apps = new List<AppCacheStatus>();
        foreach (var cachedApp in cachedApps
                     .DistinctBy(app => app.AppId, StringComparer.OrdinalIgnoreCase))
        {
            var patchline = ResolvePatchline(cachedApp.AppId);
            if (patchline == null) continue;
            var currentRevision = await GetCurrentRevisionAsync(patchline, cancellationToken);
            var storedRevision = string.IsNullOrWhiteSpace(cachedApp.Revision)
                ? ReadPrefillMarker(cachedApp.AppId)
                : cachedApp.Revision;
            if (string.IsNullOrWhiteSpace(storedRevision)) continue;
            apps.Add(new AppCacheStatus
            {
                AppId = cachedApp.AppId,
                Name = DisplayNameFor(patchline),
                IsUpToDate = StringComparer.Ordinal.Equals(storedRevision, currentRevision)
            });
        }

        return new CacheStatusResult
        {
            Apps = apps,
            Message = $"Checked {apps.Count} apps"
        };
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

            // Only run the (network-touching) size pass for products that still need downloading.
            if (patchline != null && !isUpToDate)
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

    private async Task<long> GetCachedDownloadSizeAsync(Patchline patchline, CancellationToken cancellationToken)
    {
        if (_downloadSizeCache.TryGetValue(patchline.Value, out var size)) return size;
        await _sizePassLock.WaitAsync(cancellationToken);
        try
        {
            if (_downloadSizeCache.TryGetValue(patchline.Value, out size)) return size;
            size = await ComputeDownloadSizeAsync(patchline, cancellationToken);
            _downloadSizeCache[patchline.Value] = size;
            return size;
        }
        finally { _sizePassLock.Release(); }
    }

    private async Task<long> ComputeDownloadSizeAsync(Patchline patchline, CancellationToken cancellationToken)
    {
        using var manifestHandler = new ManifestHandler(_console, _createClient(),
            _budget, "size", 1, _settings with { SkipDownloads = true }, _replace);
        var manifestUrl = await manifestHandler.FindPatchlineReleaseAsync(patchline, cancellationToken);
        var manifestPathOnDisk = await manifestHandler.DownloadManifestAsync(manifestUrl, cancellationToken);
        var manifest = new ReleaseManifest(manifestPathOnDisk);
        return manifestHandler.BuildDownloadQueue(manifest).Sum(e => e.TotalBytes);
    }

    private async Task<string> GetCurrentRevisionAsync(Patchline patchline, CancellationToken cancellationToken)
    {
        using var manifestHandler = new ManifestHandler(_console, _createClient(),
            _budget, "cache-status", 1, _settings with { SkipDownloads = true }, _replace);
        var manifestUrl = await manifestHandler.FindPatchlineReleaseAsync(patchline, cancellationToken);
        return manifestUrl.Split('/').Last();
    }

    /// <summary>
    /// Runs the prefill operation, emitting structured progress events per product.
    /// </summary>
    public Task<PrefillResult> PrefillAsync(PrefillOptions? options = null, CancellationToken cancellationToken = default)
        => PrefillAsync(options ?? new PrefillOptions(), null, cancellationToken);

    internal async Task<PrefillResult> PrefillAsync(PrefillOptions options, PrefillRun? run,
        CancellationToken cancellationToken)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();
        var progress = (IPrefillProgress?)run ?? _progress;
        var force = run?.Options.Force ?? options.Force;
        var operationId = run?.Progress.Snapshot.OperationId ?? "legacy";
        var maxConcurrency = run?.Options.MaxConcurrency ?? _budget.MaxConcurrentRequests;
        var selected = run?.Options.AppIds ?? (options.Products is { Count: > 0 }
            ? options.Products.ToArray() : options.DownloadAllOwnedGames
                ? AllPatchlines.Select(p => p.Value).ToArray() : GetSelectedApps().ToArray());
        if (run?.Options.Selection == "all") selected = AllPatchlines.Select(p => p.Value).ToArray();
        var products = selected.Select(id => ResolvePatchline(id)
            ?? throw new ArgumentException($"Unknown Riot patchline: {id}")).Distinct().ToArray();
        if (run != null && !run.Progress.Snapshot.SelectionResolved)
            run.Progress.ResolveSelection(products.Select(p => p.Value));
        if (products.Length == 0)
            return new PrefillResult { Success = false, ErrorMessage = "No apps selected for prefill" };

        var timer = Stopwatch.StartNew();
        var updated = 0;
        var alreadyUpToDate = 0;
        var failed = 0;
        long totalBytesTransferred = 0;
        Interlocked.Increment(ref _isPrefilling);
        progress.OnOperationStarted("Prefill operation");
        try
        {
            foreach (var patchline in products)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var app = new AppDownloadInfo { AppId = patchline.Value, Name = DisplayNameFor(patchline) };
                var claim = _claims.TryClaim(operationId, new[] { patchline.Value });
                if (claim == null)
                {
                    progress.OnAppCompleted(app, AppDownloadResult.Skipped);
                    continue;
                }
                using var legacyClaim = run == null ? claim : null;
                run?.Hold(claim);
                progress.OnAppStarted(app);
                try
                {
                    var versionBefore = run == null
                        ? ReadPrefillMarker(patchline.Value)
                        : run.Options.CachedApps.FirstOrDefault(cached => string.Equals(
                            cached.AppId, patchline.Value, StringComparison.OrdinalIgnoreCase))?.Revision;
                    var outcome = await PrefillPatchlineAsync(patchline, app, versionBefore,
                        force, progress, operationId, maxConcurrency, cancellationToken);
                    totalBytesTransferred += outcome.Bytes;
                    cancellationToken.ThrowIfCancellationRequested();
                    var completedApp = new AppDownloadInfo
                    {
                        AppId = app.AppId,
                        Name = app.Name,
                        TotalBytes = app.TotalBytes,
                        ChunkCount = app.ChunkCount,
                        CacheRevision = outcome.LiveVersion
                    };
                    if (outcome.Skipped)
                    {
                        alreadyUpToDate++;
                        progress.OnAppCompleted(completedApp, AppDownloadResult.AlreadyUpToDate);
                    }
                    else if (outcome.Success)
                    {
                        if (!string.IsNullOrWhiteSpace(outcome.LiveVersion))
                        {
                            if (!await WritePrefillMarkerAsync(completedApp, outcome.LiveVersion, run, cancellationToken)) break;
                        }
                        else if (run != null && !run.OnAppCompleted(completedApp, AppDownloadResult.Success, null))
                        {
                            break;
                        }
                        updated++;
                        if (run == null) progress.OnAppCompleted(completedApp, AppDownloadResult.Success);
                    }
                    else
                    {
                        failed++;
                        progress.OnAppCompleted(app, AppDownloadResult.Failed);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    progress.OnLog(LogLevel.Warning, $"Prefill failed for {app.Name}: {ex.Message}");
                    progress.OnAppCompleted(app, AppDownloadResult.Failed);
                }
                finally
                {
                    _downloadSizeCache.TryRemove(patchline.Value, out _);
                }
            }
            progress.OnPrefillCompleted(new PrefillSummary
            {
                TotalApps = products.Length,
                UpdatedApps = updated,
                AlreadyUpToDate = alreadyUpToDate,
                FailedApps = failed,
                TotalBytesTransferred = totalBytesTransferred,
                TotalTime = timer.Elapsed
            });
            return new PrefillResult
            {
                Success = failed == 0,
                ErrorMessage = failed == 0 ? null : $"{failed} product(s) failed to prefill",
                TotalTime = timer.Elapsed
            };
        }
        finally
        {
            Interlocked.Decrement(ref _isPrefilling);
        }
    }

    private async Task<PrefillPatchlineOutcome> PrefillPatchlineAsync(Patchline patchline, AppDownloadInfo appInfo,
        string? versionBefore, bool force, IPrefillProgress progress, string operationId, int maxConcurrency,
        CancellationToken cancellationToken)
    {
        var console = new ApiConsoleAdapter(progress);
        using var manifestHandler = new ManifestHandler(console, _createClient(), _budget,
            operationId, maxConcurrency, _settings, _replace);
        var manifestUrl = await manifestHandler.FindPatchlineReleaseAsync(patchline, cancellationToken);
        var liveVersion = manifestUrl.Split('/').Last();
        if (!force && versionBefore != null && !string.IsNullOrWhiteSpace(liveVersion) && versionBefore == liveVersion)
            return new PrefillPatchlineOutcome(0, liveVersion, true, true);

        var manifestPathOnDisk = await manifestHandler.DownloadManifestAsync(manifestUrl, cancellationToken);
        var manifest = new ReleaseManifest(manifestPathOnDisk);
        var downloadQueue = manifestHandler.BuildDownloadQueue(manifest);
        var requests = downloadQueue.GroupBy(e => e.BundleKey).Select(bundle => new Request(bundle.Key,
            bundle.OrderBy(e => e.LowerByteRange).Select(e => new ByteRange(e.LowerByteRange, e.UpperByteRange)).ToList())).ToList();
        cancellationToken.ThrowIfCancellationRequested();
        using var downloader = new DownloadHandler(console, patchline, progress, appInfo.AppId, appInfo.Name,
            _createClient(), _lancacheAddress, _budget, operationId, maxConcurrency, _settings);
        var success = await downloader.DownloadQueuedChunksAsync(requests, cancellationToken);
        return new PrefillPatchlineOutcome(downloader.BytesTransferred, liveVersion, false, success);
    }

    internal static string Canonicalize(string appId)
        => ResolvePatchline(appId)?.Value ?? throw new ArgumentException($"Unknown Riot patchline: {appId}");

    private static IReadOnlyList<Patchline> AllPatchlines { get; } = new[]
    {
        Patchline.LeagueOfLegends,
        Patchline.Valorant,
        Patchline.LegendsOfRuneterra
    };

    private static string DisplayNameFor(Patchline patchline)
    {
        if (patchline == Patchline.LeagueOfLegends) return "League of Legends";
        if (patchline == Patchline.Valorant) return "Valorant";
        if (patchline == Patchline.LegendsOfRuneterra) return "Legends of Runeterra";
        return patchline.Value;
    }

    private static Patchline? ResolvePatchline(string appId)
    {
        return AllPatchlines
            .FirstOrDefault(p => string.Equals(p.Value, appId, StringComparison.OrdinalIgnoreCase));
    }

    private string PrefillMarkerPath(string slug)
        => Path.Combine(_settings.CacheDirectory, $"prefilledVersion-{slug}.txt");

    private bool HasPrefillMarker(string slug)
        => File.Exists(PrefillMarkerPath(slug));

    private string? ReadPrefillMarker(string slug)
    {
        var path = PrefillMarkerPath(slug);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private Task<bool> WritePrefillMarkerAsync(AppDownloadInfo app, string version, PrefillRun? run,
        CancellationToken cancellationToken)
        => ManifestHandler.CommitAsync(PrefillMarkerPath(app.AppId), System.Text.Encoding.UTF8.GetBytes(version),
            cancellationToken, _replace,
            run == null ? null : commit => run.OnAppCompleted(app, AppDownloadResult.Success, commit));

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
        _budget.Dispose();
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
