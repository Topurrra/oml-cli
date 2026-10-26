namespace OmlCli;

// Publish order: producers before consumers. Pure functions on names and edges, so the self-test needs no model.
static class PublishOrder
{
    // edges: (consumer, producer). Edges to a name that is not in modules, and self edges, are ignored.
    // changed: null orders every module; otherwise only those, ordered by the full graph (a chain through an unchanged module still counts).
    // level: 0 has no producer in the input; the others are one more than their highest producer. Members of a cycle share one level.
    // cycles: groups of two or more modules that depend on each other (only changed members when changed is set).
    internal static (List<(string module, int level)> order, List<string[]> cycles) Compute(
        IEnumerable<string> modules, IEnumerable<(string from, string to)> edges, ISet<string>? changed = null)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        var names = modules.Distinct(cmp).Order(cmp).ToList();
        var known = names.ToHashSet(cmp);
        var uses = names.ToDictionary(n => n, _ => new SortedSet<string>(cmp), cmp);
        foreach (var (from, to) in edges)
            if (known.Contains(from) && known.Contains(to) && !from.Equals(to, StringComparison.OrdinalIgnoreCase)) uses[from].Add(to);

        // Tarjan: component of every module.
        var index = new Dictionary<string, int>(cmp);
        var low = new Dictionary<string, int>(cmp);
        var comp = new Dictionary<string, int>(cmp);
        var stack = new Stack<string>();
        var groups = new List<List<string>>();
        void Visit(string v)
        {
            index[v] = low[v] = index.Count; stack.Push(v);
            foreach (var w in uses[v])
                if (!index.ContainsKey(w)) { Visit(w); low[v] = Math.Min(low[v], low[w]); }
                else if (!comp.ContainsKey(w)) low[v] = Math.Min(low[v], index[w]);
            if (low[v] != index[v]) return;
            var group = new List<string>();
            string x;
            do { x = stack.Pop(); comp[x] = groups.Count; group.Add(x); } while (!x.Equals(v, StringComparison.OrdinalIgnoreCase));
            group.Sort(cmp);
            groups.Add(group);
        }
        foreach (var n in names) if (!index.ContainsKey(n)) Visit(n);

        // Components are numbered producers first, so one pass in that order finds every level.
        var level = new int[groups.Count];
        for (var c = 0; c < groups.Count; c++)
            foreach (var m in groups[c])
                foreach (var p in uses[m])
                    if (comp[p] != c) level[c] = Math.Max(level[c], level[comp[p]] + 1);

        var kept = names.Where(n => changed is null || changed.Contains(n)).ToList();
        var levels = kept.Select(n => level[comp[n]]).Distinct().Order().ToList();
        var order = kept.OrderBy(n => level[comp[n]]).ThenBy(n => n, cmp).Select(n => (n, levels.IndexOf(level[comp[n]]))).ToList();
        var cycles = groups.Select(g => g.Where(n => changed is null || changed.Contains(n)).ToArray())
            .Where(g => g.Length > 1).OrderBy(g => g[0], cmp).ToList();
        return (order, cycles);
    }
}
