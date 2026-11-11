#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LancachePrefill.Common;
using NUnit.Framework;
using RiotPrefill.Api;

namespace RiotPrefill.Test;

[TestFixture]
public sealed class CacheStatusTests
{
    private static readonly DateTimeOffset StartTime = new(2026, 9, 21, 4, 0, 0, TimeSpan.Zero);

    [Test]
    public void WireShapesRemainCompatible()
    {
        var legacy = new CacheStatusResult
        {
            Apps =
            [
                new AppCacheStatus
                {
                    AppId = "league_of_legends",
                    Name = "League of Legends",
                    IsUpToDate = true
                }
            ],
            Message = "Checked 1 apps"
        };
        var versionTwo = new CacheStatusResult
        {
            Version = 2,
            Apps =
            [
                new AppCacheStatus
                {
                    AppId = "valorant",
                    Name = "Valorant",
                    IsUpToDate = false,
                    Outcome = CacheOutcome.Unknown,
                    Reason = CacheReason.DeadlineReached
                }
            ]
        };

        const string legacyJson = """
            {
              "apps": [
                {
                  "appId": "league_of_legends",
                  "name": "League of Legends",
                  "isUpToDate": true
                }
              ],
              "message": "Checked 1 apps"
            }
            """;
        const string versionTwoJson = """
            {
              "version": 2,
              "apps": [
                {
                  "appId": "valorant",
                  "name": "Valorant",
                  "isUpToDate": false,
                  "outcome": "Unknown",
                  "reason": "DeadlineReached"
                }
              ]
            }
            """;

        Assert.That(JsonSerializer.Serialize(legacy, DaemonSerializationContext.Default.CacheStatusResult),
            Is.EqualTo(legacyJson).Using<string>((left, right) =>
                string.CompareOrdinal(left.Replace("\r\n", "\n"), right.Replace("\r\n", "\n"))));
        Assert.That(JsonSerializer.Serialize(versionTwo, DaemonSerializationContext.Default.CacheStatusResult),
            Is.EqualTo(versionTwoJson).Using<string>((left, right) =>
                string.CompareOrdinal(left.Replace("\r\n", "\n"), right.Replace("\r\n", "\n"))));
    }

    [Test]
    public async Task VersionTwoAtReserveReturnsAllRowsWithoutRequests()
    {
        using var cache = new TestDirectory();
        var clock = new CacheStatusClock(StartTime);
        var handler = new QueueHandler();
        using var api = CreateApi(cache.Path, handler, clock);
        await api.InitializeAsync();

        var result = await api.CheckCacheStatusAsync(
            [Input("league_of_legends", "one"), Input("unknown", "two")],
            expiresAtUtc: clock.GetUtcNow().AddSeconds(2),
            cacheStatusVersion: 2);

        Assert.Multiple(() =>
        {
            Assert.That(handler.RequestCount, Is.Zero);
            Assert.That(result.Version, Is.EqualTo(2));
            Assert.That(result.Apps.Select(app => app.AppId),
                Is.EqualTo(new[] { "league_of_legends", "unknown" }));
            Assert.That(result.Apps.All(app => app.Outcome == CacheOutcome.Unknown), Is.True);
            Assert.That(result.Apps.All(app => app.Reason == CacheReason.DeadlineReached), Is.True);
            Assert.That(result.Apps.All(app => !app.IsUpToDate), Is.True);
            Assert.That(result.Message, Is.Null);
        });
    }

    [Test]
    public void VersionTwoRejectsMissingExpiryAndDuplicateIds()
    {
        using var cache = new TestDirectory();
        var clock = new CacheStatusClock(StartTime);
        using var api = CreateApi(cache.Path, new QueueHandler(), clock);
        api.InitializeAsync().GetAwaiter().GetResult();

        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<ArgumentException>(() => api.CheckCacheStatusAsync(
                [Input("league_of_legends", "one")],
                cacheStatusVersion: 2));
            Assert.ThrowsAsync<ArgumentException>(() => api.CheckCacheStatusAsync(
                [Input("valorant", "one"), Input("VALORANT", "two")],
                expiresAtUtc: clock.GetUtcNow().AddMinutes(1),
                cacheStatusVersion: 2));
        });
    }

    [Test]
    public async Task LegacyRequestWithoutExpiryKeepsOriginalProjection()
    {
        using var cache = new TestDirectory();
        var clock = new CacheStatusClock(StartTime);
        var handler = new QueueHandler();
        handler.Enqueue((_, _) => Task.FromResult(RevisionResponse("league_of_legends", "current")));
        using var api = CreateApi(cache.Path, handler, clock);
        await api.InitializeAsync();

        var result = await api.CheckCacheStatusAsync(
            [Input("league_of_legends", "current"), Input("unknown", "stored")]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Version, Is.Null);
            Assert.That(result.Apps, Has.Count.EqualTo(1));
            Assert.That(result.Apps[0].IsUpToDate, Is.True);
            Assert.That(result.Apps[0].Outcome, Is.Null);
            Assert.That(result.Apps[0].Reason, Is.Null);
            Assert.That(result.Message, Is.EqualTo("Checked 1 apps"));
        });
    }

    [Test]
    public async Task VersionTwoCleanResultHasNoMessage()
    {
        using var cache = new TestDirectory();
        var clock = new CacheStatusClock(StartTime);
        var handler = new QueueHandler();
        handler.Enqueue((_, _) => Task.FromResult(RevisionResponse("league_of_legends", "current")));
        using var api = CreateApi(cache.Path, handler, clock);
        await api.InitializeAsync();

        var result = await api.CheckCacheStatusAsync(
            [Input("league_of_legends", "current")],
            expiresAtUtc: clock.GetUtcNow().AddMinutes(1),
            cacheStatusVersion: 2);

        Assert.Multiple(() =>
        {
            Assert.That(result.Apps, Has.Count.EqualTo(1));
            Assert.That(result.Apps[0].Outcome, Is.EqualTo(CacheOutcome.Current));
            Assert.That(result.Message, Is.Null);
        });
    }

    [Test]
    public async Task HeldPatchlineBodyReturnsCompletedAndDeadlineRows()
    {
        using var cache = new TestDirectory();
        var clock = new CacheStatusClock(StartTime);
        var heldBody = new HeldStream();
        var handler = new QueueHandler();
        handler.Enqueue((_, _) => Task.FromResult(RevisionResponse("league_of_legends", "current")));
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(heldBody)
        }));
        using var api = CreateApi(cache.Path, handler, clock);
        await api.InitializeAsync();

        var check = api.CheckCacheStatusAsync(
            [
                Input("league_of_legends", "current"),
                Input("valorant", "stored"),
                Input("bacon", "stored")
            ],
            expiresAtUtc: clock.GetUtcNow().AddSeconds(10),
            cacheStatusVersion: 2);

        await heldBody.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(8));
        var result = await check.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Multiple(() =>
        {
            Assert.That(result.Apps, Has.Count.EqualTo(3));
            Assert.That(result.Apps[0].Outcome, Is.EqualTo(CacheOutcome.Current));
            Assert.That(result.Apps[0].Reason, Is.Null);
            Assert.That(result.Apps[1].Reason, Is.EqualTo(CacheReason.DeadlineReached));
            Assert.That(result.Apps[2].Reason, Is.EqualTo(CacheReason.DeadlineReached));
            Assert.That(heldBody.ReadCanceled.Task.IsCompleted, Is.True);
            Assert.That(heldBody.Disposed.Task.IsCompleted, Is.True);
        });
    }

    [Test]
    public async Task LegacyExpiryReturnsOnlyCompletedRows()
    {
        using var cache = new TestDirectory();
        var clock = new CacheStatusClock(StartTime);
        var heldBody = new HeldStream();
        var handler = new QueueHandler();
        handler.Enqueue((_, _) => Task.FromResult(RevisionResponse("league_of_legends", "current")));
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(heldBody)
        }));
        using var api = CreateApi(cache.Path, handler, clock);
        await api.InitializeAsync();

        var check = api.CheckCacheStatusAsync(
            [Input("league_of_legends", "current"), Input("valorant", "stored")],
            expiresAtUtc: clock.GetUtcNow().AddSeconds(10));

        await heldBody.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(8));
        var result = await check.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Multiple(() =>
        {
            Assert.That(result.Version, Is.Null);
            Assert.That(result.Apps, Has.Count.EqualTo(1));
            Assert.That(result.Apps[0].AppId, Is.EqualTo("league_of_legends"));
            Assert.That(result.Apps[0].Outcome, Is.Null);
            Assert.That(result.Apps[0].Reason, Is.Null);
            Assert.That(heldBody.ReadCanceled.Task.IsCompleted, Is.True);
        });
    }

    [Test]
    public async Task CallerCancellationWinsDeadlineRace()
    {
        using var cache = new TestDirectory();
        var clock = new CacheStatusClock(StartTime);
        var heldBody = new HeldStream();
        var handler = new QueueHandler();
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(heldBody)
        }));
        using var api = CreateApi(cache.Path, handler, clock);
        await api.InitializeAsync();
        using var caller = new CancellationTokenSource();

        var check = api.CheckCacheStatusAsync(
            [Input("valorant", "stored")],
            caller.Token,
            clock.GetUtcNow().AddSeconds(10),
            2);

        await heldBody.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        caller.Cancel();
        clock.Advance(TimeSpan.FromSeconds(8));

        Assert.ThrowsAsync<OperationCanceledException>(async () => await check);
        Assert.That(heldBody.ReadCanceled.Task.IsCompleted, Is.True);
    }

    [Test]
    public async Task ItemFailureDoesNotStopLaterRows()
    {
        using var cache = new TestDirectory();
        var clock = new CacheStatusClock(StartTime);
        var handler = new QueueHandler();
        handler.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("unavailable")));
        handler.Enqueue((_, _) => Task.FromResult(RevisionResponse("valorant", "current")));
        using var api = CreateApi(cache.Path, handler, clock);
        await api.InitializeAsync();

        var result = await api.CheckCacheStatusAsync(
            [Input("league_of_legends", "stored"), Input("valorant", "current")],
            expiresAtUtc: clock.GetUtcNow().AddMinutes(1),
            cacheStatusVersion: 2);

        Assert.Multiple(() =>
        {
            Assert.That(result.Apps, Has.Count.EqualTo(2));
            Assert.That(result.Apps[0].Outcome, Is.EqualTo(CacheOutcome.Unknown));
            Assert.That(result.Apps[0].Reason, Is.EqualTo(CacheReason.InspectionFailed));
            Assert.That(result.Apps[1].Outcome, Is.EqualTo(CacheOutcome.Current));
            Assert.That(result.Apps[1].Reason, Is.Null);
        });
    }

    [Test]
    public async Task IndeterminateRowsUseSpecificReasonsInRequestOrder()
    {
        using var cache = new TestDirectory();
        var clock = new CacheStatusClock(StartTime);
        var handler = new QueueHandler();
        handler.Enqueue((_, _) => Task.FromResult(RevisionResponse("valorant", string.Empty)));
        using var api = CreateApi(cache.Path, handler, clock);
        await api.InitializeAsync();

        var result = await api.CheckCacheStatusAsync(
            [
                Input("unknown", "stored"),
                Input("league_of_legends", null),
                Input("valorant", "stored")
            ],
            expiresAtUtc: clock.GetUtcNow().AddMinutes(1),
            cacheStatusVersion: 2);

        Assert.Multiple(() =>
        {
            Assert.That(result.Apps.Select(app => app.AppId),
                Is.EqualTo(new[] { "unknown", "league_of_legends", "valorant" }));
            Assert.That(result.Apps.Select(app => app.Reason),
                Is.EqualTo(new[]
                {
                    CacheReason.MissingApp,
                    CacheReason.NoCacheEvidence,
                    CacheReason.ManifestUnavailable
                }));
            Assert.That(result.Apps.All(app => app.Outcome == CacheOutcome.Unknown), Is.True);
            Assert.That(handler.RequestCount, Is.EqualTo(1));
            Assert.That(result.Message, Is.Null);
        });
    }

    [Test]
    [NonParallelizable]
    public async Task SocketWritesDeadlineResponseAfterInspectionCancellation()
    {
        using var cache = new TestDirectory();
        var clock = new CacheStatusClock(StartTime);
        var heldBody = new HeldStream();
        var handler = new QueueHandler();
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(heldBody)
        }));
        using var socket = new SocketCommandInterface(
            0,
            PrefillProtocol.FromEnvironment(20),
            () => new HttpClient(handler, disposeHandler: false),
            cache.Path,
            null,
            clock: clock);
        await socket.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, socket.BoundTcpPort);
        await using var stream = client.GetStream();

        var secret = Environment.GetEnvironmentVariable("PREFILL_SOCKET_SECRET");
        if (!string.IsNullOrEmpty(secret))
        {
            await SendRequestAsync(stream, new CommandRequest
            {
                Id = "auth",
                Type = "auth",
                Parameters = new Dictionary<string, string> { ["secret"] = secret }
            });
            var auth = await ReadResponseAsync(stream);
            Assert.That(auth.Success, Is.True);
        }

        var cachedApps = JsonSerializer.Serialize(
            new List<CachedAppInput> { Input("valorant", "stored") },
            DaemonSerializationContext.Default.ListCachedAppInput);
        await SendRequestAsync(stream, new CommandRequest
        {
            Id = "cache-status",
            Type = "check-cache-status",
            Parameters = new Dictionary<string, string>
            {
                ["cachedApps"] = cachedApps,
                ["cacheStatusVersion"] = "2",
                ["expiresAtUtc"] = clock.GetUtcNow().AddSeconds(10).ToString("O")
            }
        });

        await heldBody.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(8));
        var response = await ReadResponseAsync(stream);
        var result = (JsonElement)response.Data!;

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(response.Message, Is.Null);
            Assert.That(result.GetProperty("version").GetInt32(), Is.EqualTo(2));
            Assert.That(result.GetProperty("apps")[0].GetProperty("reason").GetString(),
                Is.EqualTo("DeadlineReached"));
            Assert.That(result.TryGetProperty("message", out _), Is.False);
            Assert.That(heldBody.ReadCanceled.Task.IsCompleted, Is.True);
        });

        await SendRequestAsync(stream, new CommandRequest { Id = "status", Type = "status" });
        var status = await ReadResponseAsync(stream);
        var statusBody = (JsonElement)status.Data!;
        Assert.That(statusBody.GetProperty("features").EnumerateArray().Select(item => item.GetString()),
            Does.Contain("cacheStatusV2"));

        await SendRequestAsync(stream, new CommandRequest
        {
            Id = "malformed",
            Type = "check-cache-status",
            Parameters = new Dictionary<string, string>
            {
                ["cachedApps"] = cachedApps,
                ["cacheStatusVersion"] = "2"
            }
        });
        var malformed = await ReadResponseAsync(stream);
        Assert.Multiple(() =>
        {
            Assert.That(malformed.Success, Is.False);
            Assert.That(malformed.ErrorCode, Is.EqualTo("invalid-request"));
        });

        await SendRequestAsync(stream, new CommandRequest
        {
            Id = "legacy-expiry",
            Type = "check-cache-status",
            Parameters = new Dictionary<string, string>
            {
                ["cachedApps"] = cachedApps,
                ["expiresAtUtc"] = "not-a-timestamp"
            }
        });
        var malformedLegacyExpiry = await ReadResponseAsync(stream);
        Assert.Multiple(() =>
        {
            Assert.That(malformedLegacyExpiry.Success, Is.False);
            Assert.That(malformedLegacyExpiry.ErrorCode, Is.EqualTo("invalid-request"));
        });

        await SendRequestAsync(stream, new CommandRequest
        {
            Id = "legacy-no-expiry",
            Type = "check-cache-status",
            Parameters = new Dictionary<string, string> { ["cachedApps"] = "[]" }
        });
        var legacyWithoutExpiry = await ReadResponseAsync(stream);
        var legacyResult = (JsonElement)legacyWithoutExpiry.Data!;
        Assert.Multiple(() =>
        {
            Assert.That(legacyWithoutExpiry.Success, Is.True);
            Assert.That(legacyWithoutExpiry.Message, Is.EqualTo("No app IDs provided"));
            Assert.That(legacyResult.GetProperty("message").GetString(), Is.EqualTo("No app IDs provided"));
        });

        await socket.StopAsync();
    }

    private static RiotPrefillApi CreateApi(string cacheDirectory, QueueHandler handler, TimeProvider clock)
        => new(null, PrefillProtocol.FromEnvironment(20),
            () => new HttpClient(handler, disposeHandler: false), cacheDirectory, null, clock: clock);

    private static CachedAppInput Input(string appId, string? revision)
        => new() { AppId = appId, Revision = revision };

    private static HttpResponseMessage RevisionResponse(string appId, string revision)
    {
        var json = $$"""
            {
              "keystone.products.{{appId}}.patchlines.live": {
                "platforms": {
                  "win": {
                    "configurations": [
                      {
                        "id": "NA",
                        "patch_url": "https://example.test/releases/{{revision}}"
                      }
                    ]
                  }
                }
              }
            }
            """;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static async Task SendRequestAsync(NetworkStream stream, CommandRequest request)
    {
        var json = JsonSerializer.Serialize(request, DaemonSerializationContext.Default.CommandRequest);
        var bytes = Encoding.UTF8.GetBytes(json);
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length));
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static async Task<CommandResponse> ReadResponseAsync(NetworkStream stream)
    {
        var lengthBytes = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, lengthBytes);
        var bytes = new byte[BitConverter.ToInt32(lengthBytes)];
        await ReadExactlyAsync(stream, bytes);
        return JsonSerializer.Deserialize(bytes, DaemonSerializationContext.Default.CommandResponse)
               ?? throw new InvalidOperationException("Socket returned an empty response.");
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset));
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();

        internal int RequestCount { get; private set; }

        internal void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
            => _responses.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (_responses.Count == 0) throw new InvalidOperationException("No response was queued.");
            return _responses.Dequeue()(request, cancellationToken);
        }
    }

    private sealed class HeldStream : Stream
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReadCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => WaitForCancellationAsync(cancellationToken);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => new(WaitForCancellationAsync(cancellationToken));

        private async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                ReadCanceled.TrySetResult();
                throw;
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed.TrySetResult();
            base.Dispose(disposing);
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        internal TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"riot-cache-status-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
