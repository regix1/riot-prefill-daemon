#nullable enable

namespace RiotPrefill.Api;

internal sealed class PrefillRun : IPrefillProgress
{
    private readonly IPrefillProgress _log;
    private readonly object _sync = new();
    private readonly Dictionary<string, long> _bytes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _totals = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _claims = new();

    internal PrefillRun(string id, PrefillProtocol protocol, RunOptions options, IPrefillProgress log,
        Func<RunSnapshot, CancellationToken, Task>? publish = null)
    {
        Options = protocol.Capture(options);
        _log = log;
        Progress = new RunProgress(id, protocol.DaemonInstanceId, Options, publish);
    }

    internal RunOptions Options { get; }
    internal RunProgress Progress { get; }

    internal void Hold(IDisposable claim) => _claims.Add(claim);

    internal async Task CompleteAsync()
    {
        Task publication;
        lock (_sync) { publication = Progress.CompleteAsync(itemBytesTransferred: _bytes); }
        try { await publication; }
        finally
        {
            foreach (var claim in _claims) claim.Dispose();
            _claims.Clear();
        }
    }

    public void OnLog(LogLevel level, string message) => _log.OnLog(level, message);
    public void OnOperationStarted(string operationName) => OnLog(LogLevel.Info, operationName);
    public void OnOperationCompleted(string operationName, TimeSpan elapsed) => OnLog(LogLevel.Info, operationName);
    public void OnError(string message, Exception? exception = null)
    {
        OnLog(LogLevel.Error, message);
        Progress.TryChooseTerminal("failed", "prefill-failed");
    }

    public void OnAppStarted(AppDownloadInfo app)
        => Progress.UpdateItem(new RunItemSnapshot { AppId = app.AppId, Name = app.Name, State = "preparing" });

    public void OnDownloadProgress(DownloadProgressInfo progress)
    {
        lock (_sync)
        {
            var bytes = Math.Max(_bytes.GetValueOrDefault(progress.AppId), progress.BytesDownloaded);
            _bytes[progress.AppId] = bytes;
            _totals[progress.AppId] = progress.TotalBytes;
            Progress.UpdateItem(new RunItemSnapshot
            {
                AppId = progress.AppId,
                Name = progress.AppName,
                State = progress.State,
                BytesTransferred = bytes,
                TotalBytes = progress.TotalBytes
            });
        }
    }

    public void OnAppCompleted(AppDownloadInfo app, AppDownloadResult result)
        => OnAppCompleted(app, result, null);

    internal bool OnAppCompleted(AppDownloadInfo app, AppDownloadResult result, Action? commit)
    {
        var outcome = result switch
        {
            AppDownloadResult.Success => "success",
            AppDownloadResult.AlreadyUpToDate => "already_cached",
            AppDownloadResult.Skipped => "skipped",
            _ => "failed"
        };
        lock (_sync)
        {
            var item = new RunItemSnapshot
            {
                AppId = app.AppId,
                Name = app.Name,
                State = "app_completed",
                Result = outcome,
                Reason = outcome == "skipped" ? "skippedOverlap" : null,
                BytesTransferred = _bytes.GetValueOrDefault(app.AppId),
                TotalBytes = _totals.GetValueOrDefault(app.AppId),
                CacheRevision = app.CacheRevision
            };
            return commit == null ? Progress.UpdateItem(item) : Progress.TryCommitItem(item, commit);
        }
    }

    public void OnPrefillCompleted(PrefillSummary summary)
        => Progress.TryChooseTerminal(summary.FailedApps > 0 ? "failed" : "completed");

    internal static PrefillProgressUpdate ToUpdate(RunSnapshot snapshot)
    {
        var item = snapshot.CurrentItem;
        var terminal = snapshot.State is "completed" or "failed" or "cancelled" or "cancelling";
        return new PrefillProgressUpdate
        {
            OperationId = snapshot.OperationId,
            DaemonInstanceId = snapshot.DaemonInstanceId,
            Sequence = snapshot.Sequence,
            StartedAt = snapshot.StartedAt,
            UpdatedAt = snapshot.UpdatedAt.UtcDateTime,
            State = terminal ? snapshot.State : item?.State ?? snapshot.State,
            CurrentAppId = item?.AppId,
            CurrentAppName = item?.Name,
            Result = item?.Result,
            Reason = terminal ? snapshot.Reason : item?.Reason,
            BytesDownloaded = item?.BytesTransferred ?? 0,
            TotalBytes = item?.TotalBytes ?? 0,
            CacheRevision = item?.CacheRevision,
            PercentComplete = item?.TotalBytes > 0 ? 100d * item.BytesTransferred / item.TotalBytes.Value : 0,
            BytesPerSecond = (snapshot.UpdatedAt - snapshot.StartedAt).TotalSeconds > 0
                ? snapshot.BytesTransferred / (snapshot.UpdatedAt - snapshot.StartedAt).TotalSeconds : 0,
            Elapsed = snapshot.UpdatedAt - snapshot.StartedAt,
            TotalBytesTransferred = snapshot.BytesTransferred,
            TotalApps = snapshot.TotalApps,
            UpdatedApps = snapshot.CompletedApps,
            AlreadyUpToDate = snapshot.CachedApps,
            FailedApps = snapshot.FailedApps,
            SkippedApps = snapshot.SkippedApps,
            CancelledApps = snapshot.CancelledApps,
            TotalTime = snapshot.UpdatedAt - snapshot.StartedAt
        };
    }
}
