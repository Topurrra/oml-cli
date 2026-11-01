using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// `oml memory bench <new folder> --records N [--runs R]`: a maintainer tool, not part of the user commands. It writes a
// synthetic store of N records (a fixed seed, so the same file every time) into a folder that has no store yet, then times
// recall in three ways: without the index cache, with a rebuild of it, and with it. tests/memory-check.ps1 runs it at
// 1k, 10k and 100k records. It never touches a real project: the folder must be new, and it is the caller's.
static class MemoryBench
{
    static readonly string[] Modules = ["Orders", "Billing", "Customers", "Inventory", "Shipping", "Catalog", "Payments", "Reports", "Users", "Audit", "Pricing", "Returns",
        "Suppliers", "Warehouse", "Invoices", "Notifications", "Search", "Tax", "Loyalty", "Support", "Analytics", "Contracts", "Fleet", "Planning", "Quality", "Sales", "Orders_CS", "Billing_CS", "Shared_CS", "Core_CS"];
    static readonly string[] Subjects = ["the order screen", "the invoice total", "the customer lookup", "the stock level", "the shipping label", "the price list", "the payment callback", "the nightly timer",
        "the export action", "the login flow", "the audit entity", "the tax rule", "the return form", "the supplier import", "the REST client", "the dashboard block", "the search aggregate", "the email template"];
    static readonly string[] Verbs = ["must call", "should never call", "waits for", "is cached by", "reads from", "writes to", "is private to", "is exposed through", "retries after", "validates before",
        "is slow without", "fails when", "needs a role for", "is limited by", "is generated from"];
    static readonly string[] Objects = ["a server action", "the producer module", "a static entity", "a service action", "an aggregate", "a site property", "the legacy API", "a client action",
        "a timer", "the shared block", "an index", "the external service", "a role check", "a bulk insert", "the OSUI popup"];
    static readonly string[] Tags = ["api", "perf", "security", "ui", "data", "timer", "legacy", "naming", "review", "release"];
    static readonly string[] Queries =
    [
        "why is the order screen slow", "which module owns the invoice total", "is the payment callback exposed", "what must the nightly timer wait for",
        "do not call the legacy API from a client action", "how is the stock level cached",
    ];

    public static int Run(string[] args)
    {
        if (args.Length > 0 && args[0] == "--quality") return Quality(args[1..]);
        string? folder = null;
        var records = 1000;
        var runs = 3;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--records" && i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out records) && records > 0) continue;
            if (args[i] == "--runs" && i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out runs) && runs > 0) continue;
            if (!args[i].StartsWith("--") && folder is null) { folder = Path.GetFullPath(args[i]); continue; }
            throw new MemoryException("Usage: oml memory bench <new folder> --records N [--runs R]", 2);
        }
        if (folder is null) throw new MemoryException("Usage: oml memory bench <new folder> --records N [--runs R]", 2);
        if (MemStore.Open(folder) is not null) throw new MemoryException("That folder has a memory store already. The benchmark needs a new folder.", 2);
        Directory.CreateDirectory(folder);
        var store = MemStore.Create(folder);
        Generate(store, records);

        // "all" asks the whole store (the worst case); "module" asks for one module of 30 (the usual case: agents pass --module).
        var modes = new Dictionary<string, object>();
        foreach (var scope in new[] { "all", "module" })
            foreach (var mode in new[] { "noIndex", "rebuild", "cached" })
            {
                if (mode == "cached") store.Load(useIndex: true); // make sure an index exists to hit
                var samples = new List<double[]>();
                for (var run = 0; run < runs; run++)
                {
                    if (mode == "rebuild") File.Delete(store.IndexPath);
                    samples.Add(Time(store, useIndex: mode != "noIndex", scope == "all" ? null : Modules[run % 3]));
                }
                double Median(int k) { var v = samples.Select(s => s[k]).Order().ToArray(); return Math.Round(v[v.Length / 2], 1); }
                modes[scope + "." + mode] = new { loadMs = Median(0), indexMs = Median(1), searchMs = Median(2), totalMs = Median(3) };
            }
        var state = store.Load(useIndex: true);
        TerminalOutput.PrintObject(new
        {
            records, live = state.LiveNow().Count, logBytes = store.Length, indexBytes = File.Exists(store.IndexPath) ? new FileInfo(store.IndexPath).Length : 0L,
            runs, queries = Queries.Length, modes,
        });
        return 0;
    }

    // One recall of every query: load the live set, build the search index, search. Times in ms: load, index, search, total.
    static double[] Time(MemStore store, bool useIndex, string? module)
    {
        var watch = Stopwatch.StartNew();
        var state = store.Load(useIndex);
        var load = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var pool = MemoryCommand.Filtered(state.LiveNow(), module, null);
        var index = new SearchIndex(MemoryCommand.Docs(pool));
        var build = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        foreach (var q in Queries) index.Search(q, 3);
        var search = watch.Elapsed.TotalMilliseconds;
        return [load, build, search, load + build + search];
    }

    static void Generate(MemStore store, int count)
    {
        var rnd = new Random(42);
        var replaced = new HashSet<int>();
        var previous = MemoryFormat.NoHash;
        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var f = new FileStream(store.LogPath, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 20);
        for (var i = 0; i < count; i++)
        {
            var module = Modules[rnd.Next(Modules.Length)];
            var r = new MemRecord
            {
                Seq = i, Ts = MemoryFormat.Iso(start.AddMinutes(i)), Conf = 60 + rnd.Next(40), Src = "bench", Method = "add", Module = module,
                Kind = MemoryFormat.Kinds[rnd.Next(MemoryFormat.Kinds.Length)],
                Tags = [Tags[rnd.Next(Tags.Length)]], Elements = [module + "_" + Subjects[rnd.Next(Subjects.Length)].Split(' ')[^1]],
            };
            r.Text = $"{Cap(Subjects[rnd.Next(Subjects.Length)])} {Verbs[rnd.Next(Verbs.Length)]} {Objects[rnd.Next(Objects.Length)]} in {module} (note {i}).";
            if (rnd.Next(3) == 0) r.Why = $"{Cap(Objects[rnd.Next(Objects.Length)])} {Verbs[rnd.Next(Verbs.Length)]} {Subjects[rnd.Next(Subjects.Length)]}.";
            if (i >= 40 && i % 40 == 0 && replaced.Add(i - 20)) { r.Sup = i - 20; r.Method = "supersede"; }
            else if (i >= 70 && i % 70 == 0 && replaced.Add(i - 30)) { r.Sup = i - 30; r.Kind = MemoryFormat.Retract; r.Method = "retract"; r.Why = ""; }
            var bytes = MemoryFormat.Pack(r, previous);
            previous = MemoryFormat.RawHash(bytes, 0)!;
            f.Write(bytes);
        }
    }

    static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    // ---- quality -------------------------------------------------------------------------------------------------

    // `oml memory bench --quality <fixture.json> [--details]`: recall quality on a fixed set of records and questions, in
    // memory (no store is written). The fixture holds "records" (module, kind, text, elements, why, tags) and "queries"
    // (q, expect = the 1-based record number that answers it, a list of them, or null when nothing does, and set = tune
    // or blind). It prints top 1, top 3, MRR and the weak flag per set; --details adds the rank, score and coverage of
    // every question, to calibrate the weak flag on the tune set. A record that holds a secret is refused, as by `add`.
    static int Quality(string[] args)
    {
        var usage = "Usage: oml memory bench --quality <fixture.json> [--details]";
        var details = args.Contains("--details");
        var files = args.Where(a => a != "--details").ToArray();
        if (files.Length != 1 || files[0].StartsWith("--", StringComparison.Ordinal)) throw new MemoryException(usage, 2);
        var path = Path.GetFullPath(files[0]);
        if (!File.Exists(path)) throw new MemoryException($"Fixture not found: {path}", 2);
        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(path)); }
        catch (JsonException e) { throw new MemoryException($"The fixture is not valid JSON: {e.Message}", 2); }
        if (root?["records"] is not JsonArray recs || root["queries"] is not JsonArray queries) throw new MemoryException("The fixture needs \"records\" and \"queries\" arrays.", 2);

        string S(JsonNode? n, string key) => n?[key]?.GetValue<string>() ?? "";
        string[] L(JsonNode? n, string key) => S(n, key).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var records = new List<MemRecord>();
        foreach (var n in recs)
        {
            var r = new MemRecord
            {
                Seq = records.Count, Ts = MemoryFormat.Iso(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(records.Count)),
                Kind = S(n, "kind").Length > 0 ? S(n, "kind") : "fact", Module = S(n, "module"), Text = MemoryFormat.Sanitize(S(n, "text")), Why = MemoryFormat.Sanitize(S(n, "why")),
                Elements = L(n, "elements"), Tags = L(n, "tags").Select(t => t.ToLowerInvariant()).ToArray(), Src = "bench", Method = "add",
            };
            if (!MemoryFormat.Kinds.Contains(r.Kind)) throw new MemoryException($"Fixture record {records.Count + 1} has the unknown kind '{r.Kind}'.", 2);
            if (r.Text.Length == 0 || MemoryFormat.Bytes(r.Text) > MemoryFormat.TextMax || MemoryFormat.Bytes(r.Why) > MemoryFormat.WhyMax)
                throw new MemoryException($"Fixture record {records.Count + 1} has no text, or a text or why over the limits of `add` ({MemoryFormat.TextMax} and {MemoryFormat.WhyMax} bytes).", 2);
            foreach (var value in new[] { r.Text, r.Why, r.Module, string.Join(',', r.Elements), string.Join(',', r.Tags) })
                if (MemorySecrets.Find(value) is { } secret) throw new MemoryException($"Fixture record {records.Count + 1} holds what looks like a secret ({secret}). A fixture must hold none.", 2);
            records.Add(r);
        }
        var state = new MemState { Candidates = records, Total = records.Count };

        var sets = new SortedDictionary<string, Score>(StringComparer.Ordinal);
        var rows = new List<object>();
        foreach (var q in queries)
        {
            var text = S(q, "q");
            var set = S(q, "set").Length > 0 ? S(q, "set") : "tune";
            var expect = q?["expect"] switch
            {
                JsonArray a => a.Select(x => x!.GetValue<int>() - 1).ToArray(),
                JsonValue v => [v.GetValue<int>() - 1],
                _ => Array.Empty<int>(),
            };
            var (hits, weak, _, coverage) = MemoryCommand.RecallScored(state, null, null, text, 10);
            var rank = hits.FindIndex(h => expect.Contains(h.Record.Seq)) + 1; // 0 = not in the top 10
            if (!sets.TryGetValue(set, out var s)) sets[set] = s = new Score();
            if (expect.Length == 0) { s.Unanswerable++; if (weak || hits.Count == 0) s.WeakUnanswerable++; }
            else
            {
                s.Answerable++;
                if (rank == 1) s.Top1++;
                if (rank is >= 1 and <= 3) s.Top3++; else s.Missed.Add(text);
                if (rank >= 1) s.Rr += 1.0 / rank;
                if (weak) s.WeakAnswerable++;
            }
            if (details)
                rows.Add(new { set, q = text, answerable = expect.Length > 0, rank = expect.Length > 0 ? rank : (int?)null, weak, score = hits.Count > 0 ? Math.Round(hits[0].Score, 2) : (double?)null, coverage = hits.Count > 0 ? Math.Round(coverage, 2) : (double?)null });
        }
        static double R(double x) => Math.Round(x, 3);
        TerminalOutput.PrintObject(new
        {
            records = records.Count, queries = queries.Count,
            sets = sets.ToDictionary(kv => kv.Key, kv => (object)new
            {
                answerable = kv.Value.Answerable, unanswerable = kv.Value.Unanswerable,
                top1 = R(kv.Value.Top1 / (double)Math.Max(1, kv.Value.Answerable)), top3 = R(kv.Value.Top3 / (double)Math.Max(1, kv.Value.Answerable)), mrr = R(kv.Value.Rr / Math.Max(1, kv.Value.Answerable)),
                weakAnswerable = kv.Value.WeakAnswerable, weakUnanswerable = kv.Value.WeakUnanswerable, missed = kv.Value.Missed,
            }),
            details = details ? rows : null,
        });
        return 0;
    }

    sealed class Score
    {
        public int Answerable, Unanswerable, Top1, Top3, WeakAnswerable, WeakUnanswerable;
        public double Rr;
        public List<string> Missed = [];
    }
}
