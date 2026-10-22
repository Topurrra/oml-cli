using System.Text.RegularExpressions;
using OutSystems.Model;
using OutSystems.Model.Data;
using OutSystems.Model.Types;

namespace OmlCli;

// Names the element that already holds a name, for every "name taken" message: its kind, owner and path.
static class NameClash
{
    // The nearest scope first: the siblings of `created`, then each ancestor's children and the children of the
    // containers among them (references, an entity's CRUD actions, a role's actions, UI flows), and the widgets of a
    // screen or block. Flow nodes and parameters (and locals) hold a name only for another node, parameter or local.
    // OutSystems names ignore case.
    public static string Taken(object created, string name) =>
        "name taken by " + (Holder(created, name) ?? "another element (not found by oml: it may be generated, or in a referenced module)");

    public static string? Holder(object created, string name) => HolderElement(created, name) is { } hit ? Describe(hit) : null;

    public static IObjectSignature? HolderElement(object created, string name)
    {
        if (created is not IObjectSignature o) return null;
        var local = Is(o, Locals);
        for (var scope = o.Parent; scope is not null; scope = scope.Parent)
        {
            bool Holds(IObjectSignature c) => !ReferenceEquals(c, o) && !c.WasDeleted && (local || !Is(c, Locals))
                && string.Equals(Model.NameOf(c), name, StringComparison.OrdinalIgnoreCase);
            var children = ElementPaths.Children(scope).ToList();
            // Attributes last: an entity attribute does not share the module's names.
            var hit = children.FirstOrDefault(Holds) ?? children.Where(c => Is(c, Containers)).SelectMany(ElementPaths.Children).Where(Holds)
                .OrderBy(c => c is IAttributeSignature).FirstOrDefault()
                // A screen or block's widgets share its names too, at any depth.
                ?? (scope is OutSystems.Model.UI.IScreenSignature || Inspect.IsBlock(scope)
                    ? ProductionRules.Walk(scope).OfType<OutSystems.Model.UI.IWidgetSignature>().Cast<IObjectSignature>().FirstOrDefault(Holds) : null);
            if (hit is not null) return hit;
        }
        return null;
    }

    static readonly string[] Locals = ["IFlowNodeSignature", "IParameterSignature", "ILocalVariable"];
    static readonly string[] Containers = ["IReference", "IEntitySignature", "IRoleSignature", "IUIFlowSignature"];
    static bool Is(IObjectSignature o, string[] interfaces) => o.GetType().GetInterfaces().Any(i => interfaces.Contains(i.Name));

    // "input parameter Status of block LatticeLoader (path MainFlow/LatticeLoader/Status)".
    public static string Describe(IObjectSignature o) =>
        $"{Kind(o)} {Model.NameOf(o)} of {(Owner(o) is { } p and not IESpace ? $"{Kind(p)} {Model.NameOf(p)}" : $"module {Module(o)}")} (path {Path(o)})";

    // A widget's owner is its screen or block, not the content area it sits in.
    static IObjectSignature? Owner(IObjectSignature o)
    {
        if (o is not OutSystems.Model.UI.IWidgetSignature) return o.Parent;
        var p = o.Parent;
        while (p is not null && p is not OutSystems.Model.UI.IScreenSignature && !Inspect.IsBlock(p)) p = p.Parent;
        return p ?? o.Parent;
    }

    static string Module(IObjectSignature o)
    {
        var top = o;
        while (top.Parent is { } p) top = p;
        return Model.NameOf(top) ?? "?";
    }

    internal static string Kind(IObjectSignature o) =>
        Inspect.IsBlock(o) ? "block" : o is OutSystems.Model.UI.IScreenSignature ? "screen"
        : Reflect.Get(o, "EntityKind")?.ToString() == "Static" ? "static entity"
        : o is IEntitySignature ? "entity"
        : Regex.Replace(o.GetInterface().Name[1..].Replace("Signature", ""), "(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ").ToLowerInvariant();

    // Every segment from the module down, when that path means this element alone under the write rules of
    // ElementPaths; otherwise the shortest path that does. So the path works in `describe`, `tree` and `edits`.
    static string Path(IObjectSignature o)
    {
        var parts = new List<string>();
        var top = o;
        for (var x = o; x.Parent is { } parent; x = parent, top = parent)
            if (ElementPaths.Children(parent).Contains(x)) parts.Insert(0, ElementPaths.Segment(x, parent));
        return ElementPaths.Best(top, o, string.Join("/", parts));
    }
}
