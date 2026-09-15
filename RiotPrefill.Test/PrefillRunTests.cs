#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

    [Test]
    public async Task CacheRevisionSurvivesOperationRecovery()
    {
        var protocol = new PrefillProtocol(20);
        var run = new PrefillRun(Guid.NewGuid().ToString(), protocol,
            new RunOptions
            {
                AppIds = ["valorant"],
                CachedApps = [new CachedAppInput { AppId = "valorant", Revision = "revision-1" }],
                MaxConcurrency = 1
            }, NullProgress.Instance);
        run.OnAppStarted(new AppDownloadInfo { AppId = "valorant", Name = "Valorant" });
        run.OnAppCompleted(new AppDownloadInfo
        {
            AppId = "valorant",
            Name = "Valorant",
            CacheRevision = "revision-1"
        }, AppDownloadResult.Success);
        await run.CompleteAsync();

        Assert.That(run.Progress.GetPage(0, 10).Items.Single().CacheRevision, Is.EqualTo("revision-1"));
    }

    [Test]
    public async Task MissingRevisionUsesStoredMarkerForCacheStatus()
    {
        await using var fixture = await ConcurrentPrefillTests.Fixture.StartAsync(4);
        const string current = "valorant";
        const string missing = "league_of_legends";
        await File.WriteAllTextAsync(fixture.Marker(current), "valorant.manifest");
        var response = await fixture.CallAsync("check-cache-status", new Dictionary<string, string>
        {
            ["cachedApps"] = JsonSerializer.Serialize(new[]
            {
                new { appId = current, revision = (string?)null },
                new { appId = missing, revision = (string?)null }
            })
        });

        Assert.That(response.GetProperty("success").GetBoolean(), Is.True);
        var app = response.GetProperty("data").GetProperty("apps").EnumerateArray().Single();
        Assert.That(app.GetProperty("appId").GetString(), Is.EqualTo(current));
        Assert.That(app.GetProperty("isUpToDate").GetBoolean(), Is.True);
    }

    [Test]
    public async Task MissingManagerCacheRecordForcesDownloadDespiteLocalMarker()
    {
        await using var fixture = await ConcurrentPrefillTests.Fixture.StartAsync(4);
        const string product = "valorant";
        await File.WriteAllTextAsync(fixture.Marker(product), "valorant.manifest");
        fixture.Handler.Bodies[product].Release.TrySetResult();
        var firstId = Guid.NewGuid().ToString();
        var first = fixture.Identity(firstId);
        first["protocolVersion"] = "2";
        first["appIds"] = JsonSerializer.Serialize(new[] { product });
        first["cachedApps"] = "[]";
        Assert.That((await fixture.CallAsync("prefill", first, firstId)).GetProperty("success").GetBoolean(), Is.True);
        Assert.That((await fixture.TerminalAsync(firstId)).GetProperty("items")[0].GetProperty("result").GetString(), Is.EqualTo("success"));
        Assert.That(fixture.Handler.ContentRequests, Is.EqualTo(1));

        var secondId = Guid.NewGuid().ToString();
        var second = fixture.Identity(secondId);
        second["protocolVersion"] = "2";
        second["appIds"] = JsonSerializer.Serialize(new[] { product });
        second["cachedApps"] = JsonSerializer.Serialize(new[]
        {
            new { appId = product, revision = "valorant.manifest" }
        });
        Assert.That((await fixture.CallAsync("prefill", second, secondId)).GetProperty("success").GetBoolean(), Is.True);
        Assert.That((await fixture.TerminalAsync(secondId)).GetProperty("items")[0].GetProperty("result").GetString(), Is.EqualTo("already_cached"));
        Assert.That(fixture.Handler.ContentRequests, Is.EqualTo(1));
    }
}
