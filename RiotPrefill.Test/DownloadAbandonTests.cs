#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RiotPrefill.Handlers;
using RiotPrefill.Models;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace RiotPrefill.Test;

[TestFixture]
[NonParallelizable]
public sealed class DownloadAbandonTests
{
    [Test]
    public async Task DownloadQueuedChunks_SourceFailsEverything_AbandonsWithinOneAttempt()
    {
        // Every request fails, which is what a cache or CDN that has stopped answering looks like.
        using var handler = new CountingHandler(_ => false);
        using var httpClient = new HttpClient(handler);
        using var downloader = new DownloadHandler(
            new LockingConsole(new TestConsole()), Patchline.LeagueOfLegends, null, null, null, httpClient, "127.0.0.1");

        var requests = BuildRequests(60);

        Assert.ThrowsAsync<TimeoutException>(async () =>
            await downloader.DownloadQueuedChunksAsync(requests));

        // Three attempts over 60 requests would be 180. Stopping inside the first attempt has to be
        // fewer than one full pass, and must not reach a second attempt.
        Assert.That(handler.RequestCount, Is.LessThan(requests.Count),
            "the queue should have been abandoned before every request was tried");
        await Task.CompletedTask;
    }

    [Test]
    public async Task DownloadQueuedChunks_FirstWaveFailsThenSourceRecovers_StillCompletes()
    {
        // The first wave fails and everything after it works. That is a working download, not a dead
        // source, so it must run to completion rather than being abandoned.
        var seen = 0;
        using var handler = new CountingHandler(_ => Interlocked.Increment(ref seen) > 20);
        using var httpClient = new HttpClient(handler);
        using var downloader = new DownloadHandler(
            new LockingConsole(new TestConsole()), Patchline.LeagueOfLegends, null, null, null, httpClient, "127.0.0.1");

        var allSucceeded = await downloader.DownloadQueuedChunksAsync(BuildRequests(60));

        Assert.That(allSucceeded, Is.True, "the retried requests succeed, so the download completes");
        await Task.CompletedTask;
    }

    [Test]
    public async Task DownloadQueuedChunks_ClientTimeoutLooksLikeCancel_IsCountedAsFailureAndAbandons()
    {
        // HttpClient reports its own request timeout as a TaskCanceledException, which derives from
        // OperationCanceledException. Nothing has asked to cancel here, so it has to be treated as a
        // failed request and feed the abandon rule rather than escaping as a user cancel.
        using var handler = new ThrowingHandler(() => new TaskCanceledException("simulated client timeout"));
        using var httpClient = new HttpClient(handler);
        using var downloader = new DownloadHandler(
            new LockingConsole(new TestConsole()), Patchline.LeagueOfLegends, null, null, null, httpClient, "127.0.0.1");

        Assert.ThrowsAsync<TimeoutException>(async () =>
            await downloader.DownloadQueuedChunksAsync(BuildRequests(60)));
        await Task.CompletedTask;
    }

    [Test]
    public void DownloadQueuedChunks_UserCancels_StillPropagatesPromptly()
    {
        // A real cancel must still win, and must not be turned into a failed request.
        using var handler = new ThrowingHandler(null);
        using var httpClient = new HttpClient(handler);
        using var downloader = new DownloadHandler(
            new LockingConsole(new TestConsole()), Patchline.LeagueOfLegends, null, null, null, httpClient, "127.0.0.1");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await downloader.DownloadQueuedChunksAsync(BuildRequests(60), cancellation.Token));
    }

    private static List<Request> BuildRequests(int count)
    {
        return Enumerable.Range(0, count)
            .Select(i => new Request($"bundle{i:D4}", new List<ByteRange> { new ByteRange(0, 4095) }))
            .ToList();
    }

    /// <summary>
    /// Spectre's TestConsole writes into a StringBuilder with no locking, so twenty parallel requests
    /// logging failures at once corrupt it. Serializing writes keeps the harness out of the results.
    /// </summary>
    private sealed class LockingConsole : IAnsiConsole
    {
        private readonly IAnsiConsole _inner;
        private readonly object _gate = new object();

        public LockingConsole(IAnsiConsole inner) => _inner = inner;

        public Profile Profile => _inner.Profile;
        public IAnsiConsoleCursor Cursor => _inner.Cursor;
        public IAnsiConsoleInput Input => _inner.Input;
        public IExclusivityMode ExclusivityMode => _inner.ExclusivityMode;
        public RenderPipeline Pipeline => _inner.Pipeline;

        public void Clear(bool home)
        {
            lock (_gate)
            {
                _inner.Clear(home);
            }
        }

        public void Write(IRenderable renderable)
        {
            lock (_gate)
            {
                _inner.Write(renderable);
            }
        }
    }

    /// <summary>
    /// Throws whatever the caller asks for. With a null factory it waits on the token instead, so the
    /// cancel path can be exercised without a real network.
    /// </summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Func<Exception>? _exception;

        public ThrowingHandler(Func<Exception>? exception)
        {
            _exception = exception;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_exception != null)
            {
                throw _exception();
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, bool> _shouldSucceed;
        private int _requestCount;

        public CountingHandler(Func<HttpRequestMessage, bool> shouldSucceed)
        {
            _shouldSucceed = shouldSucceed;
        }

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            if (!_shouldSucceed(request))
            {
                throw new HttpRequestException("simulated dead source");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[4096])
            });
        }
    }
}
