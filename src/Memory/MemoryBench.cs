using System.Diagnostics;
using System.Globalization;

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
}
