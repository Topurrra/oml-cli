using System.Collections;
using System.Reflection;
using OutSystems.Model;
using OutSystems.Model.Types;

namespace OmlCli;

// `oml review`: one module as a flat list of its own elements, keyed by the model's GlobalKey, so a renamed element is
// still the same element. Each element keeps its readable properties as text. Parts that are not changes of their own
// are folded into their owner: expressions into the property that holds them, a node's connectors into its "next"
// steps, arguments, assignments, widget attributes and event handlers into properties of the node or widget.
static class ReviewModel
{
    internal sealed class Item
    {
        public required string Key, Kind;
        public string? Name;
        public string? Parent;       // key of the element that holds it; null at the top
        public required string Unit; // key of the top element it belongs to: an entity, an action, a screen, a reference...
        public readonly SortedDictionary<string, string> Props = new(StringComparer.Ordinal);
        public readonly List<(string label, string target)> Next = new(); // a flow node's connectors: label -> target node key
        public bool IsNode, IsWidget;
        public string Label => Name ?? Main ?? Kind;
        // What an unnamed element is about: a filter's condition, a sort's attribute, a widget's value.
        public string? Main => new[] { "Identifier", "Condition", "Value", "Attribute", "Source", "Variable", "Text", "SourceBlock", "Action", "Destination" }
            .Select(p => Props.GetValueOrDefault(p)).FirstOrDefault(v => !string.IsNullOrEmpty(v));
    }

    internal sealed class Snapshot
    {
        public required string Module, Key;
        public readonly Dictionary<string, Item> Items = new(StringComparer.Ordinal);
        public readonly List<string> Order = new(); // walk order, for a stable report
        public int Unreadable;                      // parts whose getters threw; they are left out of the comparison
    }

    // Properties that are not something a person changes: identity, canvas position, Service Studio's authorship record,
    // validation state, values derived from other elements, and child lists (the children are compared themselves).
    static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "Name", "GlobalKey", "Key", "Parent", "Children", "Referers", "WasDeleted", "ObjectKey", "ESpace", "Module", "Digest", "IsValid",
        "HorizontalPosition", "VerticalPosition",
        "CreatedBy", "LastModifiedBy", "LastModifiedDate", "LastMergedBy", "LastMergedDate",
        "TargetNode", "SourceNode", "Target", "Targets", "TrueTarget", "FalseTarget", "CycleTarget", "OtherwiseTarget", "IncomingConnectors",
        "Nodes", "Arguments", "Conditions", "Link", "Widgets", "Entries", "Sources", "Filters", "Sorts",
        "Owner", "OwnerESpace", "IsInitialized", "HasValidationProperties", "IsValidationAggregator", "HasGridColsWidth", "HasFillParentWidth",
        "Icon", "ParentStructure", "TypeKind", "ListElementType", "VisibleWasExpression", "WidgetTypesToProcess", "IsAnonymous", "IsStaticEntity",
        "EffectiveMarginLeft", "EffectiveWidth", "ThemeBeingUsed", "HasScreenDestination", "ElementType", "ParentFolder", "RuntimePath",
        "ExpandedInWebEditor", "DesignMode", "AsFullAggregate", "AsDatabaseAggregate", "RootOperation", "SourceOperation",
        "ContextForAfterReplaceData", "IsFunction", "IdentifierType", "IsDisabled",
        "CheckRoleAction", "GrantRoleAction", "RevokeRoleAction", // generated; the system roles throw for them
    };
    // The only lists that are compared as a property: they hold references to other elements, not children.
    static readonly HashSet<string> Lists = new(StringComparer.Ordinal) { "Roles", "RequiredScripts" };

    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo[]> readable = new();
    static PropertyInfo[] Readable(object o) => readable.GetOrAdd(o.GetType(), _ =>
    {
        var all = Reflect.Interfaces(o)
            .SelectMany(i => i.GetProperties()).Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !Skip.Contains(p.Name))
            .Where(p => !typeof(IEnumerable).IsAssignableFrom(p.PropertyType) || p.PropertyType == typeof(string) || p.PropertyType == typeof(byte[]) || Lists.Contains(p.Name))
            .GroupBy(p => p.Name).Select(g => g.First()).OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
        // On parameters and attributes, Type repeats DataType. Elsewhere (a Traditional Web input) it is a setting of its own.
        return all.Any(p => p.Name == "DataType") ? all.Where(p => p.Name != "Type").ToArray() : all;
    });

    static bool IsConnector(object o) => Reflect.Interfaces(o).Any(i => i.Name.EndsWith("Connector", StringComparison.Ordinal))
        && Reflect.Property(o, "TargetNode") is not null;
    static bool IsArgument(object o) => Reflect.Interfaces(o).Any(i => i.Name is "IArgumentSignature" or "IArgument");

    // Folded into the owner's properties or "next" steps instead of being elements of their own.
    static bool Folded(IObjectSignature o) => o is IExpression || o is OutSystems.Model.Logic.Nodes.ISwitchCondition || IsConnector(o) || IsArgument(o)
        || Kind(o) is "Assignment" or "UILifeCycleEvent" or "ExtendedProperty";
    // Made by the platform from their owner (entity actions, a role's or timer's actions, a process's launch action,
    // derived types such as identifier, list and aggregate output types): never changed by a person, so not compared.
    // Entities, structures and the structures of a REST API are types too, and are compared.
    internal static bool Generated(IObjectSignature o) => Kind(o) is "EntityAction" or "Action" or "RoleException"
        || o is ITypeSignature && o is not OutSystems.Model.Data.IEntitySignature && o is not OutSystems.Model.Data.IStructureSignature && Kind(o) != "RestStructure";
    // Containers that only group elements: a UI flow, a folder, an aggregate's internal operations.
    static bool Transparent(IObjectSignature o) => Kind(o) is var kind && (kind.EndsWith("Flow", StringComparison.Ordinal) && o is not IFlowNodeSignature
        || kind.EndsWith("Folder", StringComparison.Ordinal) || kind.EndsWith("Operation", StringComparison.Ordinal));

    public static Snapshot Read(IESpace m)
    {
        var snap = new Snapshot { Module = ((OutSystems.Model.Applications.IModule)m).Name, Key = ((IObjectSignature)m).GlobalKey.ToString()! };
        var seen = new HashSet<IObjectSignature>();
        void Visit(IObjectSignature o, string? parent, string? unit, string? flow)
        {
            foreach (var child in Children(o, snap))
            {
                if (!seen.Add(child) || Folded(child) || Generated(child)) continue;
                // A UI flow passes its name to its screens and blocks: moving one to another flow changes its URL.
                if (Transparent(child)) { Visit(child, parent, unit, unit is null && Kind(child).EndsWith("Flow", StringComparison.Ordinal) ? Model.NameOf(child) : flow); continue; }
                // A static record is known by its identifier.
                var name = Model.NameOf(child) ?? (child is OutSystems.Model.Data.IRecord ? Get(child, "Identifier", snap)?.ToString() : null);
                if (unit is null && name is null) continue; // platform parts of the module (system exceptions, built-in destinations)
                var key = child.GlobalKey.ToString()!;
                var item = new Item { Key = key, Kind = Kind(child), Name = name, Parent = parent, Unit = unit ?? key,
                    IsNode = child is IFlowNodeSignature && !(o is OutSystems.Model.UI.IScreenSignature || Inspect.IsBlock(o)), IsWidget = child is OutSystems.Model.UI.IWidgetSignature };
                if (!snap.Items.TryAdd(key, item)) continue;
                snap.Order.Add(key);
                if (child is IReference) { References(child, key, snap); continue; }
                Fill(m, child, item, snap);
                if (unit is null && flow is not null && item.Kind is "Screen" or "Block") item.Props["ui flow"] = flow;
                // A role's and a timer's children are generated actions; a static record's are its values (in Fill).
                if (item.Kind is not ("Role" or "Timer" or "Record")) Visit(child, key, unit ?? key, flow);
            }
        }
        Visit(m, null, null, null);
        return snap;
    }

    // A reference lists the elements this module uses from another module, by name: their insides belong to the producer.
    // Screens and blocks sit inside the reference's UI flows (and folders), so those are walked through, not listed.
    static void References(IObjectSignature reference, string key, Snapshot snap)
    {
        foreach (var element in Children(reference, snap))
        {
            if (Transparent(element)) { References(element, key, snap); continue; }
            if (Model.NameOf(element) is not { } name) continue;
            var item = new Item { Key = element.GlobalKey.ToString()!, Kind = Kind(element), Name = name, Parent = key, Unit = key };
            if (snap.Items.TryAdd(item.Key, item)) snap.Order.Add(item.Key);
        }
    }

    static IObjectSignature[] Children(IObjectSignature o, Snapshot snap)
    {
        try { return ElementPaths.Children(o).ToArray(); }
        catch (TargetInvocationException) { snap.Unreadable++; return []; }
    }

    // Describe's kind names, or the concrete type for platform objects that name no model interface.
    internal static string Kind(IObjectSignature o)
    {
        try { return Describe.Kind(o); }
        catch (NullReferenceException) { return ElementPaths.Kind(o); }
    }

    static void Fill(IESpace m, IObjectSignature o, Item item, Snapshot snap)
    {
        foreach (var p in Readable(o))
            // A flow node's Label and a static record's Identifier are its name, already compared; a Traditional Web button's Label is its text.
            if (Value(m, o, p, snap) is { Length: > 0 } text && !(p.Name is "Label" or "Identifier" && text == item.Name)) item.Props[p.Name] = text;
        // A static record's values share their keys with the entity's attributes: they are properties of the record.
        if (o is OutSystems.Model.Data.IRecord)
            foreach (var value in Get(o, "AttributeValues", snap) as IEnumerable ?? Array.Empty<object>())
                item.Props["value " + (Get(value, "Attribute", snap) is { } attribute ? Model.NameOf(attribute) : "?")] = Text(Get(value, "Value", snap));
        if (o is OutSystems.Model.Data.IEntitySignature)
            foreach (var index in EntityOptions.Indexes(o))
                item.Props["index " + EntityOptions.IndexName(index)] = string.Join(", ", EntityOptions.Columns(index)) + (Reflect.Get(index, "Unique") is true ? " (unique)" : "");
        var cases = 0;
        foreach (var c in Children(o, snap))
        {
            if (IsConnector(c)) item.Next.Add((ElementPaths.Kind(c), Target(c, snap)));
            else if (IsArgument(c) && Get(c, "Value", snap) is IExpression { Text.Length: > 0 } value)
                item.Props["argument " + (Get(c, "Parameter", snap) is { } parameter ? Model.NameOf(parameter) : "?")] = value.Text;
            else if (c is OutSystems.Model.Logic.Nodes.ISwitchCondition)
                item.Next.Add(("case " + (Get(c, "Value", snap) is IExpression { Text.Length: > 0 } e ? e.Text : (++cases).ToString()), Target(Get(c, "Link", snap) ?? c, snap)));
            else if (c is IExpression) continue;
            else if (Kind(c) == "Assignment")
                item.Props["set " + Text(Get(c, "Variable", snap))] = Text(Get(c, "Value", snap));
            else if (Kind(c) == "ExtendedProperty")
                item.Props["attribute " + (Model.NameOf(c) ?? Text(Get(c, "Property", snap)))] = Text(Get(c, "Value", snap));
            else if (Kind(c) == "UILifeCycleEvent" && Get(c, "Destination", snap) is IObjectSignature handler)
            {
                var args = Children(c, snap).Where(IsArgument).Select(a => (name: Get(a, "Parameter", snap) is { } p ? Model.NameOf(p) : "?", value: Text(Get(a, "Value", snap))))
                    .Where(a => a.value.Length > 0).OrderBy(a => a.name, StringComparer.Ordinal).Select(a => a.name + " = " + a.value).ToList();
                var key = c.GlobalKey.ToString()!;
                item.Props[Model.NameOf(c) ?? key[(key.LastIndexOf('#') + 1)..]] = (Model.NameOf(handler) ?? Kind(handler)) + (args.Count > 0 ? "(" + string.Join(", ", args) + ")" : "");
            }
        }
        item.Next.Sort((a, b) => string.CompareOrdinal(a.label, b.label) is var byLabel and not 0 ? byLabel : string.CompareOrdinal(a.target, b.target));
    }

    static string Target(object connector, Snapshot snap) => Get(connector, "TargetNode", snap) is IObjectSignature t ? t.GlobalKey.ToString()! : "";
    static string Text(object? o) => o is IExpression e ? Expressions.Canonical(e.Text) ?? e.Text : o as string ?? (o is not null ? Model.NameOf(o) ?? "" : "");

    static object? Get(object o, string name, Snapshot snap)
    {
        try { return Reflect.Property(o, name)?.GetValue(o); }
        catch (TargetInvocationException) { snap.Unreadable++; return null; }
    }

    // Some platform types (the None type) throw for Parent: they belong to no element.
    static bool Owns(object o, IObjectSignature child)
    {
        try { return ReferenceEquals(child.Parent, o); }
        catch (InvalidOperationException) { return false; }
    }

    static string? Value(IESpace m, IObjectSignature o, PropertyInfo p, Snapshot snap)
    {
        object? v;
        try { v = p.GetValue(o); }
        catch (TargetInvocationException) { snap.Unreadable++; return null; }
        switch (v)
        {
            case null: return null;
            case string s: return s;
            case IExpression e: return Expressions.Canonical(e.Text) ?? e.Text;
            case ITypeSignature t: return Model.Describe(m, t);
            // Platform elements have no name but a display name ("All Exceptions").
            case IObjectSignature r: return Owns(o, r) ? null : Model.NameOf(r) ?? Get(r, "DisplayName", snap) as string ?? Kind(r);
            case byte[] bytes: return bytes.Length == 0 ? null : $"{bytes.Length} bytes, SHA-256 {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..12]}";
            case bool or int or long or decimal or double or Enum: return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            case IEnumerable list:
                var names = list.Cast<object>().Select(x => Model.NameOf(x) ?? "?").Order(StringComparer.Ordinal);
                return string.Join(", ", names);
            default: return null;
        }
    }
}
