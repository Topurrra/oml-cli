using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli.Qa;

// A Chrome DevTools Protocol client for one page target: one WebSocket, commands matched to replies by id, events
// handed to the listeners on the reader thread. No library: the protocol is JSON over a WebSocket.
sealed class Cdp : IDisposable
{
    readonly ClientWebSocket socket = new();
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim sendLock = new(1, 1);
    int nextId;
    Task? reader;

    // Called on the reader thread with the event's method and params. Listeners must be quick and thread-safe.
    public event Action<string, JsonElement>? Event;

    // Set when the socket closed or failed: every later Send fails with it.
    public string? Closed { get; private set; }

    public static async Task<Cdp> Connect(Uri url, int timeoutMs)
    {
        var cdp = new Cdp();
        cdp.socket.Options.KeepAliveInterval = TimeSpan.Zero;
        using var limit = new CancellationTokenSource(timeoutMs);
        await cdp.socket.ConnectAsync(url, limit.Token);
        cdp.reader = Task.Run(cdp.ReadLoop);
        return cdp;
    }

    public async Task<JsonElement> Send(string method, object? parameters = null, int timeoutMs = 15_000)
    {
        if (Closed is { } why) throw new CdpException($"{method}: the browser connection is closed ({why}).");
        var id = Interlocked.Increment(ref nextId);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = reply;
        var message = new JsonObject { ["id"] = id, ["method"] = method };
        if (parameters is not null) message["params"] = JsonSerializer.SerializeToNode(parameters);
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await sendLock.WaitAsync();
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, stop.Token); }
        finally { sendLock.Release(); }
        var done = await Task.WhenAny(reply.Task, Task.Delay(timeoutMs));
        pending.TryRemove(id, out _);
        if (done != reply.Task) throw new CdpException($"{method}: no answer from the browser in {timeoutMs} ms.");
        return await reply.Task;
    }

    async Task ReadLoop()
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var part = await socket.ReceiveAsync(buffer, stop.Token);
                if (part.MessageType == WebSocketMessageType.Close) { Fail("the browser closed it"); return; }
                message.Write(buffer, 0, part.Count);
                if (!part.EndOfMessage) continue;
                Dispatch(message.GetBuffer().AsSpan(0, (int)message.Length));
                message.SetLength(0);
            }
        }
        catch (OperationCanceledException) { Fail("stopped"); }
        catch (Exception e) when (e is WebSocketException or IOException) { Fail(e.Message); }
    }

    void Dispatch(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        var root = doc.RootElement;
        if (root.TryGetProperty("id", out var id))
        {
            if (!pending.TryGetValue(id.GetInt32(), out var reply)) return;
            if (root.TryGetProperty("error", out var error))
                reply.TrySetException(new CdpException(error.TryGetProperty("message", out var m) ? m.GetString() ?? "error" : error.ToString()));
            else
                reply.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            return;
        }
        if (root.TryGetProperty("method", out var method))
            Event?.Invoke(method.GetString()!, root.TryGetProperty("params", out var p) ? p.Clone() : default);
    }

    void Fail(string why)
    {
        Closed ??= why;
        foreach (var reply in pending.Values) reply.TrySetException(new CdpException("The browser connection closed: " + why));
    }

    public void Dispose()
    {
        stop.Cancel();
        try { reader?.Wait(1_000); } catch (AggregateException) { }
        socket.Dispose();
        stop.Dispose();
    }
}

sealed class CdpException(string message) : Exception(message);
