using System.Diagnostics;
using System.Text.Json;

namespace OmlCli.Qa;

// Every request of the page, from the Network events. OutSystems calls are named: a Reactive screen fetches its
// aggregates and data actions and runs client-called server actions through
// /<Module>/screenservices/<Module>/<Flow>/<Screen>/<Name> (and module-level ones through moduleservices), so a step
// can wait for, and check, "the FetchUsers aggregate" by its model name.
sealed class QaNetwork
{
    public sealed class Request
    {
        public required string Id;
        public required string Url;
        public required string Method;
        public required long StartedMs;
        public required int Seq;
        public int Status;
        public bool Done;
        public string? Failed;
        public string? Call;      // the OutSystems call name, for screenservices/moduleservices requests
    }

    readonly object gate = new();
    readonly Dictionary<string, Request> requests = [];
    readonly List<Request> calls = [];
    readonly Stopwatch clock = Stopwatch.StartNew();
    long lastActivityMs;
    int seq;

    public int Seq { get { lock (gate) return seq; } }

    public void OnEvent(string method, JsonElement p)
    {
        lock (gate)
        {
            switch (method)
            {
                case "Network.requestWillBeSent":
                {
                    var url = p.GetProperty("request").GetProperty("url").GetString()!;
                    if (url.StartsWith("data:") || url.StartsWith("blob:")) return;
                    var id = p.GetProperty("requestId").GetString()!;
                    var r = new Request { Id = id, Url = url, Method = p.GetProperty("request").GetProperty("method").GetString()!, StartedMs = clock.ElapsedMilliseconds, Seq = ++seq, Call = CallName(url) };
                    requests[id] = r; // a redirect reuses the id: the newest request replaces it
                    if (r.Call is not null) calls.Add(r);
                    lastActivityMs = clock.ElapsedMilliseconds;
                    break;
                }
                case "Network.responseReceived":
                    if (requests.TryGetValue(p.GetProperty("requestId").GetString()!, out var got))
                        got.Status = p.GetProperty("response").GetProperty("status").GetInt32();
                    break;
                case "Network.loadingFinished":
                case "Network.loadingFailed":
                    if (requests.TryGetValue(p.GetProperty("requestId").GetString()!, out var ended))
                    {
                        ended.Done = true;
                        if (method == "Network.loadingFailed" && !(p.TryGetProperty("canceled", out var c) && c.GetBoolean()))
                            ended.Failed = p.GetProperty("errorText").GetString();
                        lastActivityMs = clock.ElapsedMilliseconds;
                    }
                    break;
            }
        }
    }

    internal static string? CallName(string url)
    {
        var path = url.Split('?', '#')[0];
        var at = path.IndexOf("/screenservices/", StringComparison.OrdinalIgnoreCase);
        if (at < 0) at = path.IndexOf("/moduleservices/", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var name = path[(path.LastIndexOf('/') + 1)..];
        return name.Length == 0 ? null : name;
    }

    // Requests still open (ignoring ones open longer than stuckMs) and how long the network has been quiet.
    public (int InFlight, long QuietFor) State(int stuckMs)
    {
        lock (gate)
        {
            var now = clock.ElapsedMilliseconds;
            var open = requests.Values.Count(r => !r.Done && now - r.StartedMs < stuckMs);
            return (open, now - lastActivityMs);
        }
    }

    public string Pending(int stuckMs)
    {
        lock (gate)
        {
            var now = clock.ElapsedMilliseconds;
            var open = requests.Values.Where(r => !r.Done && now - r.StartedMs < stuckMs).Select(r => r.Call ?? Short(r.Url)).Take(5).ToList();
            return open.Count == 0 ? "the document did not finish loading" : "still waiting for " + string.Join(", ", open);
        }
    }

    // The OutSystems calls (and failed requests) that started after the given sequence number.
    public List<Request> CallsSince(int sinceSeq) { lock (gate) return calls.Where(r => r.Seq > sinceSeq).ToList(); }
    public List<Request> FailedSince(int sinceSeq)
    {
        lock (gate) return requests.Values.Where(r => r.Seq > sinceSeq && (r.Failed is not null || r.Status >= 400)).OrderBy(r => r.Seq).ToList();
    }

    internal static string Short(string url)
    {
        var path = url.Split('?', '#')[0];
        return path.Length > 90 ? "..." + path[^87..] : path;
    }

    // What an OutSystems call answered: the server exception it reported, and the row count of the first list in its
    // data (an aggregate answers {"data":{"List":{"List":[...]},"Count":N}}, a data action its own outputs).
    public static async Task<CallResult> Read(Cdp cdp, Request r)
    {
        string body;
        try
        {
            var got = await cdp.Send("Network.getResponseBody", new { requestId = r.Id });
            body = got.GetProperty("body").GetString() ?? "";
            if (got.TryGetProperty("base64Encoded", out var b64) && b64.GetBoolean()) body = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(body));
        }
        catch (CdpException) { return new CallResult(null, null, "no response body"); }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? exception = null;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("exception", out var ex) && ex.ValueKind == JsonValueKind.Object)
                exception = (ex.TryGetProperty("name", out var n) ? n.GetString() + ": " : "") + (ex.TryGetProperty("message", out var m) ? m.GetString() : ex.ToString());
            var data = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) ? d : default;
            return new CallResult(data.ValueKind == JsonValueKind.Undefined ? null : Rows(data, 0), exception, null);
        }
        catch (JsonException) { return new CallResult(null, null, "the answer is not JSON"); }
    }

    static int? Rows(JsonElement e, int depth)
    {
        if (depth > 6) return null;
        if (e.ValueKind == JsonValueKind.Array) return e.GetArrayLength();
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty("List", out var list)) return Rows(list, depth + 1);
        foreach (var p in e.EnumerateObject())
            if (Rows(p.Value, depth + 1) is { } n) return n;
        return null;
    }

    public sealed record CallResult(int? Rows, string? Exception, string? Note);
}
