#nullable enable

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using LancachePrefill.Common;
using RiotPrefill.Api;
using RiotPrefill.Handlers;

namespace RiotPrefill.Test;

[TestFixture]
[NonParallelizable]
public sealed class CacheCommitTests
{
    [Test]
    public async Task TerminalBeforeMarker_DoesNotReplaceOrPublishSuccess_WhenTokenSignalIsDelayed()
    {
        var directory = Directory.CreateTempSubdirectory("riot-terminal-").FullName;
        var marker = Path.Combine(directory, "prefilledVersion-valorant.txt");
        await File.WriteAllTextAsync(marker, "previous.manifest");
        using var handler = new ConcurrentPrefillTests.LocalRequests();
        var protocol = new PrefillProtocol(20);
        var replacements = 0;
        using var api = new RiotPrefillApi(NullProgress.Instance, protocol,
            () => new HttpClient(handler, false), directory, "127.0.0.1", (source, target) =>
            {
                if (target == marker) Interlocked.Increment(ref replacements);
                File.Move(source, target, true);
            });
        await api.InitializeAsync();
        var run = new PrefillRun(Guid.NewGuid().ToString(), protocol,
            new RunOptions { AppIds = new[] { "valorant" }, MaxConcurrency = 2 }, NullProgress.Instance);
        var operation = api.PrefillAsync(new PrefillOptions(), run, CancellationToken.None);
        try
        {
            await handler.Bodies["valorant"].Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(run.Progress.TryChooseTerminal("cancelled"), Is.True);
            handler.ReleaseAll();
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            await run.CompleteAsync();
            Assert.That(replacements, Is.Zero);
            Assert.That(await File.ReadAllTextAsync(marker), Is.EqualTo("previous.manifest"));
            Assert.That(run.Progress.Snapshot.State, Is.EqualTo("cancelled"));
            Assert.That(run.Progress.Snapshot.CompletedApps, Is.Zero);
            Assert.That(run.Progress.GetPage().Items[0].Result, Is.EqualTo("cancelled"));
            Assert.That(run.Progress.Snapshot.BytesTransferred, Is.EqualTo(8));
            Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
        }
        finally
        {
            handler.ReleaseAll();
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            await run.CompleteAsync();
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task CommittedItem_RemainsSuccessful_WhenCancellationWinsBeforeNextMarker()
    {
        var directory = Directory.CreateTempSubdirectory("riot-commit-").FullName;
        using var handler = new ConcurrentPrefillTests.LocalRequests();
        var protocol = new PrefillProtocol(20);
        using var api = new RiotPrefillApi(NullProgress.Instance, protocol,
            () => new HttpClient(handler, false), directory, "127.0.0.1");
        await api.InitializeAsync();
        var run = new PrefillRun(Guid.NewGuid().ToString(), protocol,
            new RunOptions { AppIds = new[] { "league_of_legends", "valorant" }, MaxConcurrency = 2 }, NullProgress.Instance);
        var operation = api.PrefillAsync(new PrefillOptions(), run, CancellationToken.None);
        try
        {
            await handler.Bodies["league_of_legends"].Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            handler.Bodies["league_of_legends"].Release.TrySetResult();
            await handler.Bodies["valorant"].Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var committed = run.Progress.GetPage().Items[0];
            Assert.That(committed.Result, Is.EqualTo("success"));
            Assert.That(await File.ReadAllTextAsync(Path.Combine(directory, "prefilledVersion-league_of_legends.txt")),
                Is.EqualTo("league_of_legends.manifest"));
            Assert.That(run.Progress.TryChooseTerminal("cancelled"), Is.True);
            handler.ReleaseAll();
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            await run.CompleteAsync();
            var page = run.Progress.GetPage();
            Assert.That(page.Items[0].Result, Is.EqualTo("success"));
            Assert.That(page.Items[1].Result, Is.EqualTo("cancelled"));
            Assert.That(page.Operation.CompletedApps, Is.EqualTo(1));
            Assert.That(page.Operation.CancelledApps, Is.EqualTo(1));
            Assert.That(page.Operation.BytesTransferred, Is.EqualTo(16));
            Assert.That(File.Exists(Path.Combine(directory, "prefilledVersion-valorant.txt")), Is.False);
            Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
        }
        finally
        {
            handler.ReleaseAll();
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            await run.CompleteAsync();
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task MarkerReplaceFailure_PreservesPreviousVersion_AndFailsRun()
    {
        await using var fixture = await ConcurrentPrefillTests.Fixture.StartAsync(4, (source, target) =>
        {
            if (Path.GetFileName(target).StartsWith("prefilledVersion-", StringComparison.Ordinal))
                throw new IOException("Injected marker replacement failure");
            File.Move(source, target, true);
        });
        await File.WriteAllTextAsync(fixture.Marker("valorant"), "previous.manifest");
        var id = Guid.NewGuid().ToString();
        await fixture.StartRunAsync(id, "valorant");
        await fixture.Handler.Bodies["valorant"].Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(await File.ReadAllTextAsync(fixture.Marker("valorant")), Is.EqualTo("previous.manifest"));
        fixture.Handler.ReleaseAll();
        var page = await fixture.TerminalAsync(id);
        Assert.That(page.GetProperty("operation").GetProperty("state").GetString(), Is.EqualTo("failed"));
        Assert.That(page.GetProperty("operation").GetProperty("completedApps").GetInt32(), Is.Zero);
        Assert.That(page.GetProperty("operation").GetProperty("failedApps").GetInt32(), Is.EqualTo(1));
        Assert.That(page.GetProperty("items")[0].GetProperty("result").GetString(), Is.EqualTo("failed"));
        Assert.That(page.GetProperty("operation").GetProperty("bytesTransferred").GetInt64(), Is.EqualTo(8));
        Assert.That(await File.ReadAllTextAsync(fixture.Marker("valorant")), Is.EqualTo("previous.manifest"));
        Assert.That(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"), Is.Empty);
    }

    [Test]
    public async Task FailedContent_DoesNotReportQueuedBytesOrWriteMarker()
    {
        await using var fixture = await ConcurrentPrefillTests.Fixture.StartAsync(4);
        fixture.Handler.FailContent = true;
        var id = Guid.NewGuid().ToString();
        await fixture.StartRunAsync(id, "valorant");
        var page = await fixture.TerminalAsync(id);
        Assert.That(page.GetProperty("operation").GetProperty("state").GetString(), Is.EqualTo("failed"));
        Assert.That(page.GetProperty("operation").GetProperty("bytesTransferred").GetInt64(), Is.Zero);
        Assert.That(File.Exists(fixture.Marker("valorant")), Is.False);
    }

    [Test]
    public async Task SameCacheName_SerializesFetch_AndFailedReplaceKeepsCompleteFile()
    {
        var directory = Directory.CreateTempSubdirectory("riot-cache-").FullName;
        var cached = Path.Combine(directory, "shared.manifest");
        var expected = ConcurrentPrefillTests.LocalRequests.Manifest(1);
        using var budget = new RequestBudget(3);
        using var handler = new HeldManifest(expected);
        var settings = new DownloadSettings(true, false, false, directory);
        using var first = new ManifestHandler(new ApiConsoleAdapter(NullProgress.Instance), new HttpClient(handler, false), budget, "first", 1, settings);
        using var second = new ManifestHandler(new ApiConsoleAdapter(NullProgress.Instance), new HttpClient(handler, false), budget, "second", 1, settings);
        try
        {
            var one = first.DownloadManifestAsync("https://one.invalid/shared.manifest");
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var two = second.DownloadManifestAsync("https://two.invalid/shared.manifest");
            Assert.That(handler.Calls, Is.EqualTo(1));
            Assert.That(File.Exists(cached), Is.False);
            Assert.That(budget.ActiveRequests, Is.EqualTo(1));
            handler.Release.TrySetResult();
            await Task.WhenAll(one, two).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(handler.Calls, Is.EqualTo(1));
            Assert.That(await File.ReadAllBytesAsync(cached), Is.EqualTo(expected));
            using var failing = new ManifestHandler(new ApiConsoleAdapter(NullProgress.Instance),
                new HttpClient(handler, false), budget, "third", 1, settings with { NoLocalCache = true },
                (_, _) => throw new IOException("Injected replacement failure"));
            Assert.ThrowsAsync<IOException>(async () => await failing.DownloadManifestAsync("https://three.invalid/shared.manifest"));
            Assert.That(await File.ReadAllBytesAsync(cached), Is.EqualTo(expected));
            Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
            Assert.That(budget.ActiveRequests, Is.Zero);
        }
        finally { handler.Release.TrySetResult(); Directory.Delete(directory, true); }
    }

    private sealed class HeldManifest : HttpMessageHandler
    {
        private readonly byte[] _bytes;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal HeldManifest(byte[] bytes) => _bytes = bytes;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_bytes) };
        }
    }
}
