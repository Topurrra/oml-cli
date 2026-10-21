using System.Text.Json.Nodes;

namespace OmlCli;

// Layered layout of the module view: the same algorithm as Architecture/template.html, so the page and the JSON
// agree on `layout.crossings`. Keep both in step: tests/architecture-page-check.ps1 compares the two numbers.
// Bands (End-user, Core, Foundation, Platform) are fixed. Inside a band, intra-band edges give levels (at most MaxLevels;
// deeper modules join the last level), and a level wider than Cols wraps into rows. An edge between two modules of the
// same level is drawn as an arc by the page and is not part of the layout. Rows are ranks; an edge that spans several ranks gets one dummy item per skipped rank.
// Ordering is deterministic: barycenter sweeps down and up, a transpose pass, and the best pass wins.
static class ArchitectureLayout
{
    // The module view: the page uses Cols and MaxLevels for it, and 4 columns and 1 level when applications are collapsed.
    internal const int Cols = 6, MaxLevels = 3, Passes = 12;
    static readonly string[] Layers = ["EndUser", "Core", "Foundation"];

    internal sealed class Structure
    {
        public int N;
        public List<int> ItemRank = new(), Pos = new();
        public List<List<int>> Up = new(), Down = new(), Ranks = new();
        public List<(int key, int top, int bottom, List<int> chain)> Links = new();
        public List<double> Key = new();
        public int Crossings;
    }

    // Adds the layout object for the module view: every module is a node, and external modules sit in the Platform band.
    internal static JsonObject Compute(JsonObject document)
    {
        var nodes = (document["modules"] as JsonArray ?? []).Select(m => m as JsonObject).Where(m => m is not null)
            .Select(m => (name: Str(m!, "name"), app: Str(m!, "application"), band: Band(m!)))
            .OrderBy(m => m.band).ThenBy(m => m.app, StringComparer.Ordinal).ThenBy(m => m.name, StringComparer.Ordinal).ToList();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++) index.TryAdd(nodes[i].name, i);
        var seen = new HashSet<(int, int)>();
        var pairs = new List<(int, int)>();
        foreach (var e in (document["edges"] as JsonArray ?? []).OfType<JsonObject>())
            if (index.TryGetValue(Str(e, "from"), out var a) && index.TryGetValue(Str(e, "to"), out var b) && a != b && seen.Add((a, b))) pairs.Add((a, b));
        var s = Run(nodes.Select(n => n.band).ToArray(), pairs, Cols, MaxLevels);
        return new JsonObject { ["crossings"] = s.Crossings, ["ranks"] = s.Ranks.Count };
    }

    static string Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    static int Band(JsonObject m)
    {
        if (m["external"] is JsonValue v && v.TryGetValue<bool>(out var external) && external) return 3;
        var i = Array.IndexOf(Layers, Str(m, "layer"));
        return i < 0 ? 2 : i;
    }

    internal static Structure Run(int[] bands, List<(int a, int b)> pairs, int cols = Cols, int maxLevels = MaxLevels)
    {
        var n = bands.Length;
        var outs = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
        foreach (var (a, b) in pairs) if (bands[a] == bands[b]) outs[a].Add(b);
        foreach (var l in outs) l.Sort();
        var color = new int[n];
        var back = new HashSet<long>();
        void Dfs(int v)
        {
            color[v] = 1;
            foreach (var w in outs[v])
                if (color[w] == 1) back.Add((long)v * n + w);
                else if (color[w] == 0) Dfs(w);
            color[v] = 2;
        }
        for (var v = 0; v < n; v++) if (color[v] == 0) Dfs(v);
        var indegree = new int[n];
        for (var v = 0; v < n; v++) foreach (var w in outs[v]) if (!back.Contains((long)v * n + w)) indegree[w]++;
        var queue = new List<int>();
        for (var v = 0; v < n; v++) if (indegree[v] == 0) queue.Add(v);
        var level = new int[n];
        for (var h = 0; h < queue.Count; h++)
        {
            var u = queue[h];
            foreach (var w in outs[u])
            {
                if (back.Contains((long)u * n + w)) continue;
                level[w] = Math.Max(level[w], level[u] + 1);
                if (--indegree[w] == 0) queue.Add(w);
            }
        }
        var wide = Enumerable.Range(0, n).GroupBy(v => (long)bands[v] * (n + 1) + Math.Min(level[v], maxLevels - 1)).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
        var rank1 = new int[n];
        for (var r = 0; r < wide.Count; r++) foreach (var v in wide[r]) rank1[v] = r;
        var linkPairs = pairs.Where(p => rank1[p.a] != rank1[p.b]).ToList();
        var s1 = Build(n, rank1, wide.Count, linkPairs, v => v, (key, _) => 1e9 + key);
        s1.Crossings = Sweep(s1);
        if (wide.All(g => g.Count <= cols)) return s1;
        // Wrap wide levels into rows of at most cols nodes, in the order the sweeps found.
        var rank2 = new int[n];
        var wideOf = new List<int>();
        var rows = 0;
        for (var w = 0; w < wide.Count; w++)
        {
            var ordered = s1.Ranks[w].Where(i => i < n).ToList();
            var count = ordered.Count;
            var k = (count + cols - 1) / cols;
            var p = 0;
            for (var r = 0; r < k; r++)
            {
                var size = count / k + (r < count % k ? 1 : 0);
                for (var c = 0; c < size; c++) rank2[ordered[p++]] = rows + r;
                wideOf.Add(w);
            }
            rows += k;
        }
        var byKey = s1.Links.ToDictionary(l => l.key);
        double DummyKey(int key, int r)
        {
            if (!byKey.TryGetValue(key, out var l)) return 0;
            var item = l.chain.First(i => s1.ItemRank[i] == wideOf[r]);
            return s1.Pos[item];
        }
        var s2 = Build(n, rank2, rows, linkPairs, v => s1.Pos[v], DummyKey);
        s2.Crossings = Sweep(s2);
        return s2;
    }

    static Structure Build(int n, int[] nodeRank, int rankCount, List<(int a, int b)> pairs, Func<int, double> nodeKey, Func<int, int, double> dummyKey)
    {
        var s = new Structure { N = n };
        var keys = pairs.Select(p => Math.Min(p.a, p.b) * n + Math.Max(p.a, p.b)).Distinct().Order().ToList();
        for (var v = 0; v < n; v++)
        {
            s.ItemRank.Add(nodeRank[v]); s.Up.Add(new()); s.Down.Add(new()); s.Key.Add(nodeKey(v));
        }
        foreach (var key in keys)
        {
            int lo = key / n, hi = key % n, top = lo, bottom = hi;
            if (nodeRank[top] > nodeRank[bottom]) (top, bottom) = (bottom, top);
            if (nodeRank[top] == nodeRank[bottom]) continue;
            var chain = new List<int> { top };
            for (var r = nodeRank[top] + 1; r < nodeRank[bottom]; r++)
            {
                chain.Add(s.ItemRank.Count);
                s.ItemRank.Add(r); s.Up.Add(new()); s.Down.Add(new()); s.Key.Add(dummyKey(key, r));
            }
            chain.Add(bottom);
            for (var i = 0; i + 1 < chain.Count; i++) { s.Down[chain[i]].Add(chain[i + 1]); s.Up[chain[i + 1]].Add(chain[i]); }
            s.Links.Add((key, top, bottom, chain));
        }
        for (var r = 0; r < rankCount; r++) s.Ranks.Add(new());
        for (var i = 0; i < s.ItemRank.Count; i++) s.Ranks[s.ItemRank[i]].Add(i);
        for (var r = 0; r < rankCount; r++) s.Ranks[r] = s.Ranks[r].OrderBy(i => s.Key[i]).ThenBy(i => i).ToList();
        s.Pos = Enumerable.Repeat(0, s.ItemRank.Count).ToList();
        Reindex(s);
        return s;
    }

    static void Reindex(Structure s)
    {
        foreach (var rank in s.Ranks) for (var p = 0; p < rank.Count; p++) s.Pos[rank[p]] = p;
    }

    static int Sweep(Structure s)
    {
        var best = s.Ranks.Select(r => r.ToList()).ToList();
        var bestCount = Count(s);
        for (var pass = 0; pass < Passes && bestCount > 0; pass++)
        {
            if (pass % 2 == 0) for (var r = 1; r < s.Ranks.Count; r++) SortRank(s, r, s.Up);
            else for (var r = s.Ranks.Count - 2; r >= 0; r--) SortRank(s, r, s.Down);
            Transpose(s);
            var c = Count(s);
            if (c < bestCount) { bestCount = c; best = s.Ranks.Select(r => r.ToList()).ToList(); }
        }
        s.Ranks = best;
        Reindex(s);
        return bestCount;
    }

    static void SortRank(Structure s, int r, List<List<int>> reference)
    {
        var score = new Dictionary<int, double>();
        foreach (var i in s.Ranks[r])
        {
            var ns = reference[i];
            score[i] = ns.Count > 0 ? ns.Sum(k => (double)s.Pos[k]) / ns.Count : s.Pos[i];
        }
        s.Ranks[r] = s.Ranks[r].OrderBy(i => score[i]).ToList();
        for (var p = 0; p < s.Ranks[r].Count; p++) s.Pos[s.Ranks[r][p]] = p;
    }

    static int PairCross(Structure s, int u, int v)
    {
        var c = 0;
        foreach (var a in s.Up[u]) foreach (var b in s.Up[v]) if (s.Pos[a] > s.Pos[b]) c++;
        foreach (var a in s.Down[u]) foreach (var b in s.Down[v]) if (s.Pos[a] > s.Pos[b]) c++;
        return c;
    }

    static void Transpose(Structure s)
    {
        var improved = true;
        for (var guard = 0; improved && guard < 10; guard++)
        {
            improved = false;
            foreach (var rank in s.Ranks)
                for (var p = 0; p + 1 < rank.Count; p++)
                {
                    int u = rank[p], v = rank[p + 1];
                    if (PairCross(s, v, u) < PairCross(s, u, v))
                    {
                        rank[p] = v; rank[p + 1] = u; s.Pos[v] = p; s.Pos[u] = p + 1; improved = true;
                    }
                }
        }
    }

    // Crossings of adjacent-rank segments: sorted by upper then lower position, count strict inversions of the lower position.
    internal static int Count(Structure s)
    {
        var total = 0;
        for (var r = 0; r + 1 < s.Ranks.Count; r++)
        {
            var tree = new int[s.Ranks[r + 1].Count + 1];
            var seen = 0;
            foreach (var u in s.Ranks[r])
                foreach (var l in s.Down[u].Select(w => s.Pos[w]).Order())
                {
                    var below = 0;
                    for (var i = l + 1; i > 0; i -= i & -i) below += tree[i];
                    total += seen - below;
                    for (var i = l + 1; i < tree.Length; i += i & -i) tree[i]++;
                    seen++;
                }
        }
        return total;
    }
}
