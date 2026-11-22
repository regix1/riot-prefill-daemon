#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LancachePrefill.Common;
using RiotPrefill.Handlers;
using RiotPrefill.Models;
using Spectre.Console;

namespace RiotPrefill.Test;

[TestFixture]
[NonParallelizable]
public sealed class DeadSourceWaitingRequestsTests
{
    private const int ProcessCeiling = 128;
    private const int QueueLength = 400;
    // CdnRequestManager.cs judges the source down after 40 failed requests (twice its 20 requests).
    private const int FailuresThatJudgeTheSourceDown = 40;

    /// <summary>
    /// A run whose request limit (1) is far below the process ceiling (128) meets a cache that refuses every
    /// request. Once the run has judged the source down, the requests already waiting for the run's permit must
    /// not go out one after another, each costing its own stall timeout on a real dead cache.
    /// </summary>
    [Test]
    public async Task ARunBelowTheCeilingSendsNoWaitingRequestsOnceTheSourceIsJudgedDown()
    {
        using var cache = new RefusingCache();
        using var httpClient = new HttpClient(cache);
        using var budget = new RequestBudget(ProcessCeiling);
        using var downloader = new DownloadHandler(
            AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Interactive = InteractionSupport.No,
                Out = new AnsiConsoleOutput(TextWriter.Synchronized(TextWriter.Null))
            }),
            Patchline.LeagueOfLegends, null, null, null, httpClient, "127.0.0.1", budget, "dead-source", 1,
            new RiotPrefill.Api.DownloadSettings(false, false, true, Path.GetTempPath()));
        var requests = Enumerable.Range(0, QueueLength)
            .Select(i => new Request($"bundle{i:D4}", new List<ByteRange> { new ByteRange(0, 4095) }))
            .ToList();

        Assert.ThrowsAsync<TimeoutException>(async () => await downloader.DownloadQueuedChunksAsync(requests));

        var sentAfterTheVerdict = cache.Refused - FailuresThatJudgeTheSourceDown;
        Assert.That(sentAfterTheVerdict, Is.LessThanOrEqualTo(8),
            $"{sentAfterTheVerdict} requests went out one at a time after the run judged the source down " +
            $"({cache.Refused} in all, the verdict came after {FailuresThatJudgeTheSourceDown})");
        await Task.CompletedTask;
    }

    /// <summary>A cache that refuses every request, and counts them.</summary>
    private sealed class RefusingCache : HttpMessageHandler
    {
        private int _refused;

        public int Refused => Volatile.Read(ref _refused);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _refused);
            throw new HttpRequestException("Connection refused", null, HttpStatusCode.ServiceUnavailable);
        }
    }
}
