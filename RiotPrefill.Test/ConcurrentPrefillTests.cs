#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using FlatSharp;
using LancachePrefill.Common;
using LeagueToolkit.IO.ReleaseManifestFile;
using RiotPrefill.Api;

namespace RiotPrefill.Test;

[TestFixture]
[NonParallelizable]
public sealed class ConcurrentPrefillTests
{
    [Test]
    public async Task ThreePatchlines_ReadBodiesTogether_AndCancelOnlyOne()
    {
        await using var fixture = await Fixture.StartAsync(3);
        var ids = Fixture.Products.Select(_ => Guid.NewGuid().ToString()).ToArray();
        for (var i = 0; i < ids.Length; i++)
            Assert.That((await fixture.StartRunAsync(ids[i], Fixture.Products[i])).GetProperty("success").GetBoolean(), Is.True);
        await Task.WhenAll(fixture.Handler.Bodies.Values.Select(body => body.Entered.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(fixture.Handler.ActiveBodies, Is.EqualTo(3));

        var rejected = await fixture.StartRunAsync(Guid.NewGuid().ToString(), Fixture.Products[0]);
        Assert.That(rejected.GetProperty("errorCode").GetString(), Is.EqualTo("run-limit"));
        var replay = await fixture.StartRunAsync(ids[0], Fixture.Products[0].ToUpperInvariant());
        Assert.That(replay.GetProperty("success").GetBoolean(), Is.True);
        var conflict = await fixture.StartRunAsync(ids[0], Fixture.Products[1]);
        Assert.That(conflict.GetProperty("errorCode").GetString(), Is.EqualTo("operation-conflict"));

        var ambiguous = await fixture.CallAsync("cancel-prefill");
        Assert.That(ambiguous.GetProperty("errorCode").GetString(), Is.EqualTo("ambiguous-operation"));
        var wrong = await fixture.CallAsync("cancel-prefill", new()
        {
            ["operationId"] = ids[0],
            ["daemonInstanceId"] = Guid.NewGuid().ToString()
        });
        Assert.That(wrong.GetProperty("errorCode").GetString(), Is.EqualTo("instance-changed"));
        var cancel = await fixture.CallAsync("cancel-prefill", fixture.Identity(ids[0]));
        Assert.That(cancel.GetProperty("data").GetProperty("state").GetString(), Is.EqualTo("cancelling"));
        await fixture.Handler.Bodies[Fixture.Products[0]].Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancelled = await fixture.TerminalAsync(ids[0]);
        Assert.That(cancelled.GetProperty("operation").GetProperty("state").GetString(), Is.EqualTo("cancelled"));
        Assert.That(cancelled.GetProperty("operation").GetProperty("bytesTransferred").GetInt64(), Is.EqualTo(8));
        Assert.That(fixture.Handler.Bodies[Fixture.Products[1]].Cancelled.Task.IsCompleted, Is.False);
        Assert.That(fixture.Handler.Bodies[Fixture.Products[2]].Cancelled.Task.IsCompleted, Is.False);

        await fixture.ReconnectAsync();
        var active = await fixture.CallAsync("status");
        Assert.That(active.GetProperty("data").GetProperty("activeOperations").GetArrayLength(), Is.EqualTo(2));
        fixture.Handler.ReleaseAll();
        foreach (var id in ids.Skip(1))
        {
            var page = await fixture.TerminalAsync(id);
            Assert.That(page.GetProperty("operation").GetProperty("state").GetString(), Is.EqualTo("completed"));
            Assert.That(page.GetProperty("items")[0].GetProperty("result").GetString(), Is.EqualTo("success"));
        }
        Assert.That(File.Exists(fixture.Marker(Fixture.Products[0])), Is.False);
        Assert.That(File.Exists(fixture.Marker(Fixture.Products[1])), Is.True);
        Assert.That(File.Exists(fixture.Marker(Fixture.Products[2])), Is.True);
        Assert.That(fixture.Handler.ContentRequests, Is.EqualTo(3));
        Assert.That(fixture.Handler.PeakBodies, Is.EqualTo(3));
        Assert.That(fixture.Handler.ActiveBodies, Is.Zero);
        var repeated = await fixture.CallAsync("cancel-prefill", fixture.Identity(ids[0]));
        Assert.That(repeated.GetProperty("data").GetProperty("state").GetString(), Is.EqualTo("cancelled"));
    }

    [Test]
    public async Task DuplicatePatchline_SkipsOverlap_ThenContinuesNextItem()
    {
        await using var fixture = await Fixture.StartAsync(4);
        var first = Guid.NewGuid().ToString();
        await fixture.StartRunAsync(first, Fixture.Products[0]);
        await fixture.Handler.Bodies[Fixture.Products[0]].Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Guid.NewGuid().ToString();
        await fixture.StartRunAsync(second, Fixture.Products[0], Fixture.Products[1]);
        await fixture.Handler.Bodies[Fixture.Products[1]].Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var overlap = Guid.NewGuid().ToString();
        await fixture.StartRunAsync(overlap, Fixture.Products[0]);
        var skipped = await fixture.TerminalAsync(overlap);
        Assert.That(skipped.GetProperty("operation").GetProperty("reason").GetString(), Is.EqualTo("skippedOverlap"));
        Assert.That(skipped.GetProperty("items")[0].GetProperty("result").GetString(), Is.EqualTo("skipped"));
        Assert.That(File.Exists(fixture.Marker(Fixture.Products[0])), Is.False);
        fixture.Handler.ReleaseAll();
        var mixed = await fixture.TerminalAsync(second);
        Assert.That(mixed.GetProperty("operation").GetProperty("completedApps").GetInt32(), Is.EqualTo(1));
        Assert.That(mixed.GetProperty("operation").GetProperty("skippedApps").GetInt32(), Is.EqualTo(1));
        Assert.That(fixture.Handler.ContentRequests, Is.EqualTo(2));
    }

    [Test]
    public async Task SizePass_AndSelectionChanges_DoNotAlterAdmittedDownload()
    {
        var originalSkip = AppConfig.SkipDownloads;
        var originalWhole = AppConfig.DownloadWholeBundle;
        await using var fixture = await Fixture.StartAsync(4);
        try
        {
            var id = Guid.NewGuid().ToString();
            await fixture.StartRunAsync(id, Fixture.Products[0]);
            await fixture.Handler.Bodies[Fixture.Products[0]].Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AppConfig.SkipDownloads = true;
            AppConfig.DownloadWholeBundle = false;
            await fixture.CallAsync("set-selected-apps", new() { ["appIds"] = "[\"valorant\"]" });
            var size = await fixture.CallAsync("get-selected-apps-status");
            Assert.That(size.GetProperty("success").GetBoolean(), Is.True);
            Assert.That(size.GetProperty("data").GetProperty("totalDownloadSize").GetInt64(), Is.GreaterThan(0));
            Assert.That(File.Exists(fixture.Marker(Fixture.Products[1])), Is.False);
            fixture.Handler.ReleaseAll();
            var page = await fixture.TerminalAsync(id);
            Assert.That(page.GetProperty("items")[0].GetProperty("appId").GetString(), Is.EqualTo(Fixture.Products[0]));
            Assert.That(page.GetProperty("operation").GetProperty("bytesTransferred").GetInt64(), Is.EqualTo(8));
            Assert.That(fixture.Handler.RangedRequests, Is.Zero);
            Assert.That(AppConfig.SkipDownloads, Is.True);
            Assert.That(AppConfig.DownloadWholeBundle, Is.False);
        }
        finally { AppConfig.SkipDownloads = originalSkip; AppConfig.DownloadWholeBundle = originalWhole; }
    }

    [Test]
    public async Task ProcessBudget_HoldsThroughBodyRead_AndCancelledRunReleasesNextTurn()
    {
        await using var fixture = await Fixture.StartAsync(3, maxRequests: 2);
        var first = Guid.NewGuid().ToString();
        await fixture.StartRunAsync(first, "league_of_legends");
        await fixture.Handler.Bodies["league_of_legends"].Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.StartRunAsync(Guid.NewGuid().ToString(), "valorant");
        await fixture.Handler.Bodies["valorant"].Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var third = Guid.NewGuid().ToString();
        await fixture.StartRunAsync(third, "bacon");
        var status = await fixture.CallAsync("status");
        Assert.That(status.GetProperty("data").GetProperty("activeOperations").GetArrayLength(), Is.EqualTo(3));
        Assert.That(fixture.Handler.Bodies["bacon"].Entered.Task.IsCompleted, Is.False);
        await fixture.CallAsync("cancel-prefill", fixture.Identity(first));
        await fixture.Handler.Bodies["bacon"].Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(fixture.Handler.ActiveBodies, Is.EqualTo(2));
        Assert.That(fixture.Handler.PeakBodies, Is.EqualTo(2));
        fixture.Handler.ReleaseAll();
        Assert.That((await fixture.TerminalAsync(third)).GetProperty("operation").GetProperty("state").GetString(), Is.EqualTo("completed"));
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        internal static readonly string[] Products = { "league_of_legends", "valorant", "bacon" };
        internal readonly LocalRequests Handler = new();
        internal readonly string DirectoryPath = Directory.CreateTempSubdirectory("riot-runs-").FullName;
        private SocketCommandInterface _server = null!;
        private SocketAdapterTests.FramedClient _client = null!;
        private PrefillProtocol _protocol = null!;

        internal static async Task<Fixture> StartAsync(int maxRuns, Action<string, string>? replace = null, int maxRequests = 20)
        {
            var fixture = new Fixture();
            fixture._protocol = new PrefillProtocol(20, maxRuns.ToString(System.Globalization.CultureInfo.InvariantCulture),
                maxRequests.ToString(System.Globalization.CultureInfo.InvariantCulture));
            fixture._server = new SocketCommandInterface(0, fixture._protocol,
                () => new HttpClient(fixture.Handler, false), fixture.DirectoryPath, "127.0.0.1", replace);
            await fixture._server.StartAsync();
            fixture._client = await SocketAdapterTests.FramedClient.ConnectAsync(fixture._server.BoundTcpPort);
            return fixture;
        }

        internal string Marker(string product) => Path.Combine(DirectoryPath, $"prefilledVersion-{product}.txt");
        internal Dictionary<string, string> Identity(string id) => new()
        {
            ["operationId"] = id,
            ["daemonInstanceId"] = _protocol.DaemonInstanceId
        };

        internal Task<JsonElement> StartRunAsync(string id, params string[] products)
            => CallAsync("prefill", new()
            {
                ["protocolVersion"] = "2",
                ["daemonInstanceId"] = _protocol.DaemonInstanceId,
                ["appIds"] = JsonSerializer.Serialize(products),
                ["maxConcurrency"] = "2"
            }, id);

        internal async Task<JsonElement> CallAsync(string type, Dictionary<string, string>? parameters = null, string? id = null)
        {
            id ??= Guid.NewGuid().ToString();
            await _client.SendAsync(new CommandRequest { Id = id, Type = type, Parameters = parameters });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var message = await _client.ReadAsync(timeout.Token);
                if (message.TryGetProperty("id", out var key) && key.GetString() == id) return message;
            }
        }

        internal async Task<JsonElement> TerminalAsync(string id)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var response = await CallAsync("get-operation", Identity(id));
                Assert.That(response.GetProperty("success").GetBoolean(), Is.True, response.ToString());
                var page = response.GetProperty("data");
                if (page.GetProperty("operation").GetProperty("state").GetString() is "completed" or "failed" or "cancelled") return page;
                await Task.Yield();
            }
        }

        internal async Task ReconnectAsync()
        {
            await _client.DisposeAsync();
            _client = await SocketAdapterTests.FramedClient.ConnectAsync(_server.BoundTcpPort);
        }

        public async ValueTask DisposeAsync()
        {
            Handler.ReleaseAll();
            await _server.StopAsync();
            await _client.DisposeAsync();
            _server.Dispose();
            Handler.Dispose();
            Directory.Delete(DirectoryPath, true);
        }
    }

    internal sealed class LocalRequests : HttpMessageHandler
    {
        internal readonly ConcurrentDictionary<string, Body> Bodies = new(
            Fixture.Products.Select(product => new KeyValuePair<string, Body>(product, new Body())));
        private int _requests;
        private int _ranged;
        internal int ContentRequests => Volatile.Read(ref _requests);
        internal int RangedRequests => Volatile.Read(ref _ranged);
        internal int ActiveBodies => Bodies.Values.Count(body => body.Entered.Task.IsCompleted && !body.Drained.Task.IsCompleted);
        internal int PeakBodies { get; private set; }
        internal bool FailContent { get; set; }
        internal void ReleaseAll() { foreach (var body in Bodies.Values) body.Release.TrySetResult(); }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "clientconfig.rpg.riotgames.com")
            {
                var product = Fixture.Products.Single(product => uri.Query.Contains(product, StringComparison.Ordinal));
                var json = "{\"keystone.products." + product + ".patchlines.live\":{\"platforms\":{\"win\":{\"configurations\":[{\"id\":\"NA\",\"patch_url\":\"https://manifest.invalid/" + product + ".manifest\"}]}}}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
            if (uri.Host == "manifest.invalid")
            {
                var index = Array.FindIndex(Fixture.Products, product => uri.AbsolutePath.Contains(product, StringComparison.Ordinal));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Manifest((ulong)index + 1)) });
            }
            Interlocked.Increment(ref _requests);
            if (request.Headers.Range != null) Interlocked.Increment(ref _ranged);
            var productId = request.Headers.Host switch
            {
                "lol.dyn.riotcdn.net" => Fixture.Products[0],
                "valorant.dyn.riotcdn.net" => Fixture.Products[1],
                "bacon.dyn.riotcdn.net" => Fixture.Products[2],
                _ => throw new InvalidOperationException("Unexpected content host")
            };
            if (FailContent) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var body = Bodies[productId];
            body.OnEntered = () => PeakBodies = Math.Max(PeakBodies, ActiveBodies);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        }

        internal static byte[] Manifest(ulong id)
        {
            var body = new ReleaseManifestBody
            {
                Bundles = new[] { new ReleaseManifestBundle
                {
                    ID = id, Chunks = new[] { new ReleaseManifestBundleChunk { ID = id, CompressedSize = 8, UncompressedSize = 8 } }
                } },
                Files = new[] { new LeagueToolkit.IO.ReleaseManifestFile.ReleaseManifestFile
                {
                    ID = id, Name = "content.bin", Size = 8, ChunkIDs = new[] { id }
                } }
            };
            var buffer = new byte[ReleaseManifestBody.Serializer.GetMaxSize(body)];
            var length = ReleaseManifestBody.Serializer.Write(buffer, body);
            using var compressor = new ZstdSharp.Compressor();
            var compressed = compressor.Wrap(buffer.AsSpan(0, length)).ToArray();
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write("RMAN"u8); writer.Write((byte)2); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0);
                writer.Write(28); writer.Write(compressed.Length); writer.Write(id); writer.Write(length); writer.Write(compressed);
            }
            return stream.ToArray();
        }
    }

    internal sealed class Body : MemoryStream
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action? OnEntered;
        private bool _read;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_read) { _read = true; buffer.Span[..8].Clear(); return 8; }
            Entered.TrySetResult();
            OnEntered?.Invoke();
            try { await Release.Task.WaitAsync(cancellationToken); return 0; }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
        }
        protected override void Dispose(bool disposing) { Drained.TrySetResult(); base.Dispose(disposing); }
    }
}
