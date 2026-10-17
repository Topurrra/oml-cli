using System.Text.RegularExpressions;
using OutSystems.Model;
using OutSystems.Model.Logic.Nodes;
using OutSystems.Model.Processes.Nodes;

namespace OmlCli;

// Coordinates and sizes are in model units, not pixels. Measured through oml api/tree
// on Studio 11: NodePositions (3196,800), displacement 2660; NodeSizes 914 x 928.
// The typed API has no per-node rendered label bounds. Reserve an extra icon width
// for labels, and use the Studio displacement in both directions.
static class FlowLayout
{
    internal const int StartX = 3196, StartY = 800, Spacing = 2660;
    internal const int NodeWidth = 914, NodeHeight = 928;
    static string Key(IObjectSignature o) => o.GlobalKey.ToString()!;
    internal static bool IsComment(IFlowNode n) => n is OutSystems.Model.Logic.Nodes.ICommentNode
        or OutSystems.Model.Processes.Nodes.ICommentNode;
    internal static List<IFlowNode> Nodes(IObjectSignature owner) => owner.Children
        .OfType<IFlowNode>().Where(n => n is IActionNode or IProcessNode).Distinct().ToList();

    internal static IEnumerable<IObjectSignature> Flows(IObjectSignature root)
    {
        if (root is IReference) yield break;
        if (Nodes(root).Count > 0) yield return root;
        foreach (var child in root.Children.Distinct())
            foreach (var flow in Flows(child)) yield return flow;
    }

    internal static Dictionary<string, HashSet<string>> Capture(IESpace m) => Flows(m)
        .ToDictionary(Key, f => Nodes(f).Select(Key).ToHashSet());

    internal static void Apply(IESpace m, Dictionary<string, HashSet<string>> before,
        List<string>? requests, List<string> problems)
    {
        var flows = Flows(m).ToList();
        var selected = new HashSet<IObjectSignature>();
        foreach (var pattern in requests ?? new())
        {
            if (string.IsNullOrWhiteSpace(pattern)) { problems.Add("tidyFlows: empty path or pattern"); continue; }
            if (!pattern.Contains('*'))
            {
                try
                {
                    var owner = ElementPaths.Resolve(m, pattern);
                    if (!flows.Contains(owner)) throw new ModelError($"'{pattern}' is not an editable logic or process flow");
                    selected.Add(owner);
                }
                catch (ModelError e) { problems.Add("tidyFlows: " + e.Message); }
                continue;
            }
            var regex = new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$", RegexOptions.CultureInvariant);
            string PathOf(IObjectSignature o) => o.Parent is { } p && !ReferenceEquals(p, m)
                ? PathOf(p) + "/" + ElementPaths.Segment(o, p) : ElementPaths.Segment(o, m);
            var matches = flows.Where(f => regex.IsMatch(Model.NameOf(f) ?? "") || regex.IsMatch(PathOf(f))).ToList();
            if (matches.Count == 0) problems.Add($"tidyFlows: no flows match '{pattern}'");
            selected.UnionWith(matches);
        }
        foreach (var flow in flows)
            if (selected.Contains(flow) || !before.TryGetValue(Key(flow), out var old)
                || !old.SetEquals(Nodes(flow).Select(Key))) Layout(flow);
    }

    internal static IEnumerable<(IFlowNode target, string kind)> Edges(IFlowNode n)
    {
        // Node.Target getters can return null in this build; connectors retain the real endpoints.
        var links = n switch
        {
            IActionNode a => a.Connectors.Cast<IConnector>(),
            IProcessNode p => p.Connectors.Cast<IConnector>(),
            _ => Enumerable.Empty<IConnector>()
        };
        foreach (var link in links.OrderBy(l => l.GetType().Name == "Otherwise" ? 1 : 0)
                     // SwitchCondition is a wrapper; its Link child owns the case priority.
                     .ThenBy(l => Reflect.Get(Reflect.Get(l, "Link") ?? l, "Index") as int? ?? 0)
                     .ThenBy(l => Model.NameOf(l) ?? "", StringComparer.Ordinal)
                     .ThenBy(l => l.Target is { } t ? Model.NameOf(t) ?? "" : "", StringComparer.Ordinal)
                     .ThenBy(Key, StringComparer.Ordinal))
            if (link.Target is { } target && !IsComment(target))
                yield return (target, link.GetType().Name);
    }

    internal static void Layout(IObjectSignature owner)
    {
        var all = Nodes(owner);
        var nodes = all.Where(n => !IsComment(n)).ToList();
        var allowed = nodes.ToHashSet();
        // A stable spanning forest gives each branch a separate band of columns.
        // Remove only DFS back edges for ranking, retaining them in the model (loop returns).
        var state = new Dictionary<IFlowNode, int>();
        var lanes = new Dictionary<IFlowNode, int>();
        var forward = nodes.ToDictionary(n => n, _ => new List<IFlowNode>());
        var returns = new List<(IFlowNode from, IFlowNode to)>();
        var order = new List<IFlowNode>();
        int Visit(IFlowNode n, int lane)
        {
            state[n] = 1;
            lanes[n] = lane;
            order.Add(n);
            var right = lane;
            var edges = Edges(n).Where(e => allowed.Contains(e.target))
                // Studio's sample uses False down, True right. ForEach: next down, cycle right.
                .OrderBy(e => e.kind is "False" or "Sequence" ? 0 : e.kind is "True" or "Cycle" ? 2 : 1).ToList();
            var first = true;
            foreach (var (target, _) in edges)
            {
                if (state.GetValueOrDefault(target) == 1) { returns.Add((n, target)); continue; }
                forward[n].Add(target);
                if (!state.ContainsKey(target))
                {
                    right = Math.Max(right, Visit(target, first ? lane : right + 1));
                    first = false;
                }
            }
            state[n] = 2;
            return right;
        }
        var nextLane = 0;
        foreach (var root in nodes.OrderBy(n => n is OutSystems.Model.Logic.Nodes.IStartNode
                     or OutSystems.Model.Processes.Nodes.IStartNode ? 0 : n is IExceptionHandlerNode ? 1 : 2)
                     .ThenBy(n => Model.NameOf(n) ?? n.GetType().Name, StringComparer.Ordinal)
                     .ThenBy(Key, StringComparer.Ordinal))
            if (!state.ContainsKey(root)) nextLane = Visit(root, nextLane) + 1;

        bool Reaches(IFlowNode from, IFlowNode target)
        {
            var seen = new HashSet<IFlowNode>();
            var pending = new Stack<IFlowNode>();
            pending.Push(from);
            while (pending.TryPop(out var n))
            {
                if (ReferenceEquals(n, target)) return true;
                if (seen.Add(n)) foreach (var child in forward[n]) pending.Push(child);
            }
            return false;
        }
        // Reserve vertical room for a loop body before continuing down the main column.
        // These are layout constraints only; no connectors are created or changed.
        foreach (var (from, loop) in returns.Where(e => e.to is IForEachNode))
        foreach (var next in Edges(loop).Where(e => e.kind == "Sequence").Select(e => e.target).Where(allowed.Contains))
            if (!ReferenceEquals(from, next) && !Reaches(next, from)) forward[from].Add(next);

        // Longest path in the acyclic graph puts shared joins/Ends below every incoming branch.
        var indegree = nodes.ToDictionary(n => n, _ => 0);
        foreach (var targets in forward.Values) foreach (var n in targets) indegree[n]++;
        var rows = nodes.ToDictionary(n => n, _ => 0);
        var queue = new Queue<IFlowNode>(order.Where(n => indegree[n] == 0));
        while (queue.TryDequeue(out var n))
            foreach (var target in forward[n])
            {
                rows[target] = Math.Max(rows[target], rows[n] + 1);
                if (--indegree[target] == 0) queue.Enqueue(target);
            }

        // Leave comments fixed. Push nodes and their successors down around fixed comments.
        var placed = all.Where(IsComment).Select(n => (x: n.HorizontalPosition, y: n.VerticalPosition)).ToList();
        foreach (var n in order.OrderBy(n => rows[n]).ThenBy(n => lanes[n]))
        {
            var x = StartX + lanes[n] * Spacing;
            var y = StartY + rows[n] * Spacing;
            // Include already placed predecessors if a comment forced them down.
            foreach (var from in order.Where(p => forward[p].Contains(n)))
                y = Math.Max(y, StartY + rows[from] * Spacing + Spacing);
            while (placed.Any(p => Math.Abs(p.x - x) < NodeWidth * 2 && Math.Abs(p.y - y) < NodeHeight * 2))
                y += Spacing;
            rows[n] = (y - StartY) / Spacing;
            n.HorizontalPosition = x;
            n.VerticalPosition = y;
            placed.Add((x, y));
        }
    }
}
