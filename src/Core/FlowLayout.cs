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
    internal const int GridStep = 228; // the grid positions snap to, also used by PositionOnLink and MoveAhead
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
                    var owner = ElementPaths.Resolve(m, pattern, write: true);
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
        {
            // A flow whose node set changed gets laid out again, unless this session placed its new nodes on their links
            // (PositionOnLink) without also deleting a node, and the user did not ask for a tidy: then the surrounding
            // layout is preserved, as it is when a step is inserted into an existing link.
            if (selected.Contains(flow)) { Layout(flow); continue; }
            if (!before.TryGetValue(Key(flow), out var old) || !old.SetEquals(Nodes(flow).Select(Key)))
                if (!positioned.Contains(Key(flow)) || deleted.Contains(Key(flow))) Layout(flow);
        }
    }

    // Flows whose new nodes were placed in place by PositionOnLink within this run (so Apply leaves them alone).
    static readonly HashSet<string> positioned = new();
    // Flows a node was deleted from in this run: a deletion always gets a full lay-out.
    static readonly HashSet<string> deleted = new();

    // The marks are per apply: a long-lived process (oml run, dashboard) runs many applies. Reset them when an apply
    // starts, so a flow marked in an earlier apply never skips a later re-layout.
    internal static void BeginApply()
    {
        positioned.Clear();
        deleted.Clear();
    }

    internal static void MarkPositioned(IObjectSignature owner) => positioned.Add(Key(owner));

    internal static void MarkChanged(IObjectSignature owner) => deleted.Add(Key(owner));

    // Places a node on the link between source and target: midpoint by default, snapped to the 228 grid, and successor
    // nodes nudged ahead when the link is short relative to the node size. Only positions change; connectors are the
    // caller's.
    internal static void PositionOnLink(IObjectSignature owner, IFlowNode node, IFlowNode source, IFlowNode target, bool keepCurrent = false)
    {
        if (!keepCurrent)
        {
            node.HorizontalPosition = target.HorizontalPosition - (target.HorizontalPosition - source.HorizontalPosition) / 2;
            node.VerticalPosition = target.VerticalPosition - (target.VerticalPosition - source.VerticalPosition) / 2;
        }
        var dx = (double)target.HorizontalPosition - source.HorizontalPosition;
        var dy = (double)target.VerticalPosition - source.VerticalPosition;
        var relX = Relative(node, source, dx, keepCurrent, GridStep, NodeWidth, NodeWidth * 2, isHorizontal: true,
            basePos: () => source.HorizontalPosition, nodePos: () => node.HorizontalPosition,
            nudge: v => MoveAhead(owner, v, horizontal: true, from: node.HorizontalPosition));
        var relY = Relative(node, source, dy, keepCurrent, GridStep, NodeHeight, NodeHeight * 2, isHorizontal: false,
            basePos: () => source.VerticalPosition, nodePos: () => node.VerticalPosition,
            nudge: v => MoveAhead(owner, v, horizontal: false, from: node.VerticalPosition));
        node.HorizontalPosition = Snap(source.HorizontalPosition + relX * dx, GridStep);
        node.VerticalPosition = Snap(source.VerticalPosition + relY * dy, GridStep);
        MarkPositioned(owner);
    }

    internal static int Snap(double coordinate, int gridInterval) => (int)Math.Round(coordinate / gridInterval) * gridInterval;

    // How far along the source->target axis the node goes: a fraction when the link is long enough, otherwise 1 with a
    // nudge of the nodes ahead, or 0 on a too-short link.
    static double Relative(IFlowNode node, IFlowNode source, double delta, bool keepCurrent, int gridInterval, int size, int maxDelta,
        bool isHorizontal, Func<int> basePos, Func<int> nodePos, Action<double> nudge)
    {
        var abs = Math.Abs(delta);
        if (abs >= size * 3)
            return keepCurrent ? (nodePos() - basePos()) / delta : 0.5;
        if (abs >= gridInterval)
        {
            var coordinate = abs;
            var rel = 1.0;
            if (abs > maxDelta)
            {
                coordinate = maxDelta * 2 - abs;
                rel = maxDelta / abs;
            }
            nudge(coordinate);
            return rel;
        }
        return 0;
    }

    // Moves nodes that lie at or after the placed node along the axis ahead by the snapped offset, so the new node has
    // room. Connectors are untouched; only positions move.
    static void MoveAhead(IObjectSignature owner, double by, bool horizontal, int from)
    {
        foreach (var n in Nodes(owner))
        {
            if (IsComment(n)) continue;
            var coord = horizontal ? n.HorizontalPosition : n.VerticalPosition;
            if (coord < from) continue;
            if (horizontal) n.HorizontalPosition = Snap(coord + by, GridStep);
            else n.VerticalPosition = Snap(coord + by, GridStep);
        }
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
