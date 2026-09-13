using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using LancachePrefill.Common;
using RiotPrefill.Api;

namespace RiotPrefill.Test;

[TestFixture]
[NonParallelizable]
public sealed class SocketAdapterTests
{
    [Test]
    public async Task CancelPrefill_WaitsForCleanup_EmitsCancelled_AndAllowsNewStart()
    {
        var operationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;

        async Task<PrefillResult> RunPrefillAsync(
            PrefillOptions options,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref invocationCount) > 1)
            {
                return new PrefillResult { Success = true };
            }

            operationStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new PrefillResult { Success = true };
            }
            finally
            {
                cancellationObserved.TrySetResult();
                await releaseCleanup.Task;
            }
        }

        using var commandInterface = new SocketCommandInterface(0, RunPrefillAsync);
        await commandInterface.StartAsync();
        await using var client = await FramedClient.ConnectAsync(commandInterface.BoundTcpPort);

        await client.SendAsync(new CommandRequest { Id = "prefill-1", Type = "prefill" });
        Assert.That((await client.ReadAsync()).GetProperty("success").GetBoolean(), Is.True);
        await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await client.SendAsync(new CommandRequest { Id = "cancel-1", Type = "cancel-prefill" });
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var pendingMessage = client.ReadAsync();
        await Task.Delay(100);
        Assert.That(pendingMessage.IsCompleted, Is.False);

        releaseCleanup.TrySetResult();
        var messages = new[]
        {
            await pendingMessage.WaitAsync(TimeSpan.FromSeconds(2)),
            await client.ReadAsync().WaitAsync(TimeSpan.FromSeconds(2))
        };

        Assert.That(messages.Any(message =>
            message.TryGetProperty("id", out var id) &&
            id.GetString() == "cancel-1" &&
            message.GetProperty("success").GetBoolean()), Is.True);
        Assert.That(messages.Any(message =>
            message.TryGetProperty("type", out var type) &&
            type.GetString() == "progress" &&
            message.GetProperty("data").GetProperty("state").GetString() == "cancelled"), Is.True);
        Assert.That(messages.Any(message =>
            message.TryGetProperty("data", out var data) &&
            data.TryGetProperty("state", out var state) &&
            state.GetString() is "completed" or "error"), Is.False);

        await client.SendAsync(new CommandRequest { Id = "prefill-2", Type = "prefill" });
        var secondStart = await client.ReadAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(secondStart.GetProperty("id").GetString(), Is.EqualTo("prefill-2"));
        Assert.That(secondStart.GetProperty("success").GetBoolean(), Is.True);

        await commandInterface.StopAsync();
    }

    [Test]
    public async Task SocketReader_HandlesControlRequestWhileConcurrentHandlerIsBlocked()
    {
        var slowStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = new SocketServer(0, NullProgress.Instance);
        server.OnCommand = async (request, cancellationToken) =>
        {
            if (request.Type == "slow")
            {
                slowStarted.TrySetResult();
                await releaseSlow.Task.WaitAsync(cancellationToken);
            }

            return new CommandResponse { Id = request.Id, Success = true };
        };

        await server.StartAsync();
        await using var client = await FramedClient.ConnectAsync(server.BoundTcpPort);
        await client.SendAsync(new CommandRequest { Id = "slow-1", Type = "slow" });
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await client.SendAsync(new CommandRequest { Id = "status-1", Type = "status" });

        var status = await client.ReadAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(status.GetProperty("id").GetString(), Is.EqualTo("status-1"));

        releaseSlow.TrySetResult();
        var slow = await client.ReadAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(slow.GetProperty("id").GetString(), Is.EqualTo("slow-1"));

        await server.StopAsync();
    }

    [Test]
    public async Task ClientDisconnect_CancelsAndDrainsDispatchedHandler()
    {
        var handlerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = new SocketServer(0, NullProgress.Instance);
        server.OnCommand = async (request, cancellationToken) =>
        {
            handlerStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new CommandResponse { Id = request.Id, Success = true };
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult();
                throw;
            }
        };

        await server.StartAsync();
        var client = await FramedClient.ConnectAsync(server.BoundTcpPort);
        await client.SendAsync(new CommandRequest { Id = "slow-1", Type = "slow" });
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await client.DisposeAsync();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await server.StopAsync();
    }

    [Test]
    public void SocketProgress_UsesSharedDefaultAndDebugLogPolicy()
    {
        var normalLines = new List<string>();
        var normal = new SocketCommandInterface.SocketProgress(
            normalLines.Add,
            DaemonLogLevel.Info);

        normal.OnLog(LogLevel.Debug, "hidden");
        normal.OnLog(LogLevel.Info, "information");
        normal.OnLog(LogLevel.Warning, "warning");
        normal.OnLog(LogLevel.Error, "error");

        Assert.That(normalLines.Any(line => line.Contains("hidden", StringComparison.Ordinal)), Is.False);
        Assert.That(normalLines.Any(line => line.Contains("information", StringComparison.Ordinal)), Is.True);
        Assert.That(normalLines.Any(line => line.Contains("warning", StringComparison.Ordinal)), Is.True);
        Assert.That(normalLines.Any(line => line.Contains("error", StringComparison.Ordinal)), Is.True);

        var debugLines = new List<string>();
        var debug = new SocketCommandInterface.SocketProgress(
            debugLines.Add,
            DaemonLogLevel.Debug);
        debug.OnLog(LogLevel.Debug, "visible");

        Assert.That(debugLines.Any(line => line.Contains("visible", StringComparison.Ordinal)), Is.True);
    }

    internal sealed class FramedClient : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        private FramedClient(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        public static async Task<FramedClient> ConnectAsync(int port)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            return new FramedClient(client);
        }

        public async Task SendAsync(CommandRequest request)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                request,
                DaemonSerializationContext.Default.CommandRequest);
            var length = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
            await _stream.WriteAsync(length);
            await _stream.WriteAsync(payload);
            await _stream.FlushAsync();
        }

        public async Task<JsonElement> ReadAsync(CancellationToken cancellationToken = default)
        {
            var length = new byte[sizeof(int)];
            await _stream.ReadExactlyAsync(length, cancellationToken);
            var payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(length)];
            await _stream.ReadExactlyAsync(payload, cancellationToken);
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.Clone();
        }

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            _client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
