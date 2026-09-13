#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LancachePrefill.Common;
using RiotPrefill.Api;

namespace RiotPrefill.Test;

[TestFixture]
[NonParallelizable]
public sealed class PrefillRunTests
{
    [Test]
    public async Task CapturedInput_AndTerminal_RejectLaterMutation()
    {
        var ids = new List<string> { "valorant", "valorant", "bacon" };
        var protocol = new PrefillProtocol(20);
        var run = new PrefillRun(Guid.NewGuid().ToString(), protocol,
            new RunOptions { AppIds = ids, Force = true, MaxConcurrency = 99 }, NullProgress.Instance);
        ids.Clear();
        Assert.That(run.Options.AppIds, Is.EqualTo(new[] { "valorant", "bacon" }));
        Assert.That(run.Options.MaxConcurrency, Is.EqualTo(20));
        run.OnAppStarted(new AppDownloadInfo { AppId = "valorant", Name = "Valorant" });
        run.OnDownloadProgress(new DownloadProgressInfo { AppId = "valorant", BytesDownloaded = 3, TotalBytes = 8 });
        run.OnAppCompleted(new AppDownloadInfo { AppId = "valorant" }, AppDownloadResult.Failed);
        run.OnPrefillCompleted(new PrefillSummary { FailedApps = 1 });
        run.OnPrefillCompleted(new PrefillSummary());
        await run.CompleteAsync();
        var snapshot = run.Progress.Snapshot;
        run.OnAppCompleted(new AppDownloadInfo { AppId = "valorant" }, AppDownloadResult.Success);
        Assert.That(run.Progress.Snapshot, Is.EqualTo(snapshot));
        Assert.That(snapshot.State, Is.EqualTo("failed"));
        Assert.That(snapshot.BytesTransferred, Is.EqualTo(3));
        Assert.That(snapshot.TotalApps, Is.EqualTo(2));
        Assert.That(run.Progress.GetPage(0, 1).NextOffset, Is.EqualTo(1));
    }

    [Test]
    public async Task EmptySelection_UnknownInstance_AndMissingRun_AreRejected()
    {
        await using var fixture = await ConcurrentPrefillTests.Fixture.StartAsync(4);
        var status = (await fixture.CallAsync("status")).GetProperty("data");
        Assert.That(status.GetProperty("protocolVersion").GetInt32(), Is.EqualTo(2));
        Assert.That(status.GetProperty("maxConcurrentRuns").GetInt32(), Is.EqualTo(4));
        Assert.That(status.GetProperty("maxConcurrentRequests").GetInt32(), Is.EqualTo(20));
        Assert.That(status.GetProperty("features").GetArrayLength(), Is.EqualTo(5));
        var empty = await fixture.StartRunAsync(Guid.NewGuid().ToString());
        Assert.That(empty.GetProperty("success").GetBoolean(), Is.False);
        var missing = await fixture.CallAsync("get-operation", fixture.Identity(Guid.NewGuid().ToString()));
        Assert.That(missing.GetProperty("errorCode").GetString(), Is.EqualTo("operation-not-found"));
        var preset = fixture.Identity(Guid.NewGuid().ToString());
        preset["protocolVersion"] = "2";
        preset["all"] = "true";
        preset["appIds"] = "[]";
        var conflict = await fixture.CallAsync("prefill", preset);
        Assert.That(conflict.GetProperty("success").GetBoolean(), Is.False);
        Assert.That(fixture.Handler.ContentRequests, Is.Zero);
    }

    [Test]
    public async Task LegacyStart_IsExclusive_AndBlocksThreeBodyBarrier()
    {
        await using var fixture = await ConcurrentPrefillTests.Fixture.StartAsync(4);
        var legacy = await fixture.CallAsync("prefill", new() { ["products"] = "[\"league_of_legends\"]" });
        Assert.That(legacy.GetProperty("success").GetBoolean(), Is.True);
        await fixture.Handler.Bodies["league_of_legends"].Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var next = await fixture.StartRunAsync(Guid.NewGuid().ToString(), "valorant");
        Assert.That(next.GetProperty("success").GetBoolean(), Is.False);
        Assert.That(fixture.Handler.ActiveBodies, Is.EqualTo(1));
        Assert.That(fixture.Handler.Bodies["valorant"].Entered.Task.IsCompleted, Is.False);
    }

    [Test]
    public async Task Publication_DrainsBeforeItemClaimIsReleased()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var protocol = new PrefillProtocol(20);
        var claims = new ItemClaims();
        var run = new PrefillRun(Guid.NewGuid().ToString(), protocol,
            new RunOptions { AppIds = new[] { "valorant" }, MaxConcurrency = 2 }, NullProgress.Instance,
            async (snapshot, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); });
        run.Hold(claims.TryClaim("first", new[] { "valorant" })!);
        run.OnAppCompleted(new AppDownloadInfo { AppId = "valorant" }, AppDownloadResult.Success);
        var completion = run.CompleteAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(claims.TryClaim("second", new[] { "valorant" }), Is.Null);
        release.TrySetResult();
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        using var after = claims.TryClaim("second", new[] { "valorant" });
        Assert.That(after, Is.Not.Null);
    }
}
