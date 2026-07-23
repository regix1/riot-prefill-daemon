#nullable enable

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using RiotPrefill.Handlers;
using RiotPrefill.Models;
using Spectre.Console.Testing;

namespace RiotPrefill.Test;

[TestFixture]
[NonParallelizable]
public sealed class ManifestCancellationTests
{
    [Test]
    public async Task FindPatchlineRelease_PropagatesCallerCancellationToSendAsync()
    {
        using var handler = new BlockingSendHandler();
        using var httpClient = new HttpClient(handler);
        var manifestHandler = new ManifestHandler(new TestConsole(), httpClient);
        using var cancellation = new CancellationTokenSource();

        var request = manifestHandler.FindPatchlineReleaseAsync(
            Patchline.LeagueOfLegends,
            cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await request.WaitAsync(TimeSpan.FromSeconds(2)));
        await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task DownloadManifest_PropagatesCallerCancellationToBodyRead()
    {
        using var content = new BlockingContent();
        using var handler = new StaticResponseHandler(content);
        using var httpClient = new HttpClient(handler);
        var manifestHandler = new ManifestHandler(new TestConsole(), httpClient);
        using var cancellation = new CancellationTokenSource();
        var manifestUrl = $"https://example.invalid/{Guid.NewGuid():N}.manifest";

        var request = manifestHandler.DownloadManifestAsync(manifestUrl, cancellation.Token);
        await content.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await request.WaitAsync(TimeSpan.FromSeconds(2)));
        await content.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class BlockingSendHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly HttpContent _content;

        public StaticResponseHandler(HttpContent content)
        {
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = _content
            });
    }

    private sealed class BlockingContent : HttpContent
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
