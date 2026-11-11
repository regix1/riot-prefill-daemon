#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using LancachePrefill.Common;
using RiotPrefill.Api;
using RiotPrefill.ReleaseManifestFile;

namespace RiotPrefill.Test;

/// <summary>
/// Teamfight Tactics has an empty patch_url in Riot's client config and names its release through the
/// sieve service instead.  These tests replay responses recorded from Riot's public services.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class TeamfightTacticsTests
{
    private const string Product = "teamfighttactics";
    private const string NewestManifest = "C00FB038C03890EE.manifest";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private static readonly string Recordings =
        Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "TeamfightTactics");

    [Test]
    public void RecordedManifest_Parses()
    {
        var manifest = new ReleaseManifest(Path.Combine(Recordings, NewestManifest));

        Assert.Multiple(() =>
        {
            Assert.That(manifest.ID, Is.EqualTo(0xC00FB038C03890EEUL));
            Assert.That(manifest.Bundles, Has.Count.EqualTo(564));
            Assert.That(manifest.Files, Has.Count.EqualTo(258));
        });
    }

    [Test]
    public async Task TeamfightTacticsIsListedAndPrefillsTheHighestSieveVersion()
    {
        var directory = Directory.CreateTempSubdirectory("riot-tft-").FullName;
        using var handler = new RecordedRiot();
        // One request slot, so a lookup that asked for a second permit while holding the first would never finish.
        using var api = new RiotPrefillApi(NullProgress.Instance, new PrefillProtocol(20, "1", "1"),
            () => new HttpClient(handler, false), directory, "127.0.0.1");
        try
        {
            await api.InitializeAsync();

            var owned = await api.GetOwnedGamesAsync().WaitAsync(Bound);
            Assert.That(owned.Select(game => (game.AppId, game.Name)), Does.Contain((Product, "Teamfight Tactics")));

            api.SetSelectedApps(new[] { Product });
            var selected = await api.GetSelectedAppsStatusAsync().WaitAsync(Bound);
            var row = selected.Apps.Single();
            Assert.Multiple(() =>
            {
                Assert.That(row.AppId, Is.EqualTo(Product));
                Assert.That(row.Name, Is.EqualTo("Teamfight Tactics"));
                Assert.That(row.DownloadSize, Is.GreaterThan(0));
            });

            var status = await api.CheckCacheStatusAsync(
                new List<CachedAppInput> { new() { AppId = Product, Revision = "older.manifest" } }).WaitAsync(Bound);
            Assert.That(status.Apps.Single().IsUpToDate, Is.False);

            var result = await api.PrefillAsync(null, CancellationToken.None).WaitAsync(Bound);

            Assert.Multiple(() =>
            {
                Assert.That(result.Success, Is.True, result.ErrorMessage);
                Assert.That(handler.SieveUrls, Is.Not.Empty);
                Assert.That(handler.SieveUrls.All(url => url.Contains("/products/teamfighttactics/version-sets/live-NA1?", StringComparison.Ordinal)
                                                          && url.Contains("q[artifact_type_id]=client", StringComparison.Ordinal)
                                                          && url.Contains("q[platform]=windows", StringComparison.Ordinal)), Is.True,
                    string.Join(", ", handler.SieveUrls));
                Assert.That(handler.ManifestNames.Distinct(), Is.EqualTo(new[] { NewestManifest }));
                Assert.That(handler.ContentHosts, Is.Not.Empty);
                Assert.That(handler.ContentHosts.Distinct(), Is.EqualTo(new[] { "teamfighttactics.dyn.riotcdn.net" }));
                Assert.That(File.ReadAllText(Path.Combine(directory, $"prefilledVersion-{Product}.txt")), Is.EqualTo(NewestManifest));
            });
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Serves the recorded responses.  One older release (18.1.0) is given the newest creation time, so a
    /// choice by creation time would fetch its manifest instead of the highest version's.
    /// </summary>
    private sealed class RecordedRiot : HttpMessageHandler
    {
        internal readonly ConcurrentQueue<string> SieveUrls = new();
        internal readonly ConcurrentQueue<string> ManifestNames = new();
        internal readonly ConcurrentQueue<string> ContentHosts = new();
        private readonly string _sieve;

        internal RecordedRiot()
        {
            var sieve = JsonNode.Parse(File.ReadAllText(Path.Combine(Recordings, "sieve-live-NA1.json")))!;
            var older = sieve["releases"]!.AsArray().Single(release => (string?)release!["release"]!["id"] == "A4250A4912E4118B")!;
            older["release"]!["created_at"] = "2026-11-09T00:00:00.000Z";
            _sieve = sieve.ToJsonString();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "clientconfig.rpg.riotgames.com")
                return Respond(new StringContent(File.ReadAllText(Path.Combine(Recordings, "patchlines.json"))));
            if (uri.Host == "sieve.services.riotcdn.net")
            {
                SieveUrls.Enqueue(Uri.UnescapeDataString(uri.ToString()));
                return Respond(new StringContent(_sieve));
            }
            if (uri.Host == "teamfighttactics.secure.dyn.riotcdn.net")
            {
                var name = uri.AbsolutePath.Split('/').Last();
                ManifestNames.Enqueue(name);
                var path = Path.Combine(Recordings, name);
                return File.Exists(path)
                    ? Respond(new ByteArrayContent(File.ReadAllBytes(path)))
                    : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            ContentHosts.Enqueue(request.Headers.Host ?? uri.Host);
            return Respond(new ByteArrayContent(Array.Empty<byte>()));
        }

        private static Task<HttpResponseMessage> Respond(HttpContent content)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}
