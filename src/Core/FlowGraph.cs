using System.Collections;
using OutSystems.Model;
using OutSystems.Model.Logic.Nodes;

namespace OmlCli;

// `oml flow <module.oml> <Element>`: one flow (an action, screen action, data action or preparation) as a graph:
// its nodes with their Service Studio positions (model units, as FlowLayout reads them) and its connectors. The Dif
// page draws it, and diffs two of them (`flow diff`), so a person sees the logic the agent changed, from the model.
// A node's id is its path segment under the flow (its name, or Kind[index] when it has none): the same node in the
// module and its staged copy has the same id, which is what a diff matches on.
static class FlowGraph
{
    public static object Read(IESpace m, string path)
    {
        var owner = ElementPaths.Resolve(m, path);
        var nodes = FlowLayout.Nodes(owner).Where(n => !FlowLayout.IsComment(n)).ToList();
        if (nodes.Count == 0)
            throw new ModelError($"'{path}' has no flow. Name an action, a screen action, a data action or a preparation.");
        var ids = nodes.ToDictionary(n => n, n => ElementPaths.Segment((IObjectSignature)n, owner));
        return new
        {
            element = CheckRules.PathOf(owner),
            kind = ElementPaths.Kind(owner),
            nodes = nodes.OrderBy(n => n.VerticalPosition).ThenBy(n => n.HorizontalPosition).Select(n => new
            {
                id = ids[n],
                name = Model.NameOf(n),
                kind = ElementPaths.Kind(n),
                x = n.HorizontalPosition,
                y = n.VerticalPosition,
                detail = Detail(n),
            }),
            edges = nodes.SelectMany(n => FlowLayout.Edges(n).Where(e => ids.ContainsKey(e.target))
                .Select(e => new { from = ids[n], to = ids[e.target], kind = e.kind })).Distinct(),
        };
    }

    // One short line that says what the node does, for the diagram's tooltip and the changed-node check.
    static string? Detail(object node)
    {
        try
        {
            static string Expr(object? o, string key) => Reflect.Get(o!, key) is IExpression e ? (e.Text ?? "").Trim() : "";
            static string Clip(string s) => s.Length > 120 ? s[..117] + "..." : s;
            var kind = node.GetType().Name;
            if (node is IIfNode) return Clip("if " + Expr(node, "Condition"));
            if (kind.Contains("ForEach")) return Clip("for each " + Expr(node, "RecordList"));
            if (kind.Contains("Switch")) return "switch, " + (Reflect.Get(node, "Conditions") as IEnumerable)?.Cast<object>().Count() + " cases";
            if (Reflect.Get(node, "Assignments") is IEnumerable assignments)
            {
                var list = assignments.Cast<object>().Select(a => Expr(a, "Variable") + " = " + Expr(a, "Value")).ToList();
                return list.Count == 0 ? "assign" : Clip(list[0] + (list.Count > 1 ? $" (+{list.Count - 1} more)" : ""));
            }
            if (Reflect.Get(node, "Action") is { } action) return "call " + Model.NameOf(action);
            if (kind.Contains("SQL")) return "sql";
            if (kind.Contains("RaiseException")) return "raise " + Model.NameOf(Reflect.Get(node, "Exception")!);
            if (Reflect.Get(node, "Destination") is { } to) return "go to " + Model.NameOf(to);
            if (Reflect.Get(node, "DataSource") is { } source) return "refresh " + Model.NameOf(source);
            if (Reflect.Property(node, "AsDatabaseAggregate") is not null) return "aggregate";
            return null;
        }
        catch (Exception e) when (e is System.Reflection.TargetInvocationException or NullReferenceException or InvalidCastException or InvalidOperationException) { return null; }
    }
}
