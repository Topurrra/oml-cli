using System.Collections;
using System.Text;
using OutSystems.Model;
using OutSystems.Model.Types;
using OutSystems.Model.UI;

namespace OmlCli;

// Read only summaries. Connector children are the persisted source of flow topology.
static class Describe
{
    internal static IEnumerable<object> List(object o, string key)
    {
        // Reactive screen actions implement IAction but this build throws for its output contract.
        if (key == "OutputParameters" && o is OutSystems.Model.UI.Mobile.IScreenAction) return [];
        return Reflect.Get(o, key) is IEnumerable xs ? Unreadable.Try(() => key + " of " + Unreadable.Where(o), () => xs.Cast<object>().ToArray(), []) : [];
    }
    // The edits path of a refresh node when it differs from "refresh <data source>" (which works when it is unique).
    static string RefreshHandle(object node)
    {
        if (node is not IObjectSignature n || n.Parent is not { } owner) return "";
        if (Model.NameOf(n) is { Length: > 0 } named) return "  [path " + named + "]";
        var alias = "refresh " + Name(Reflect.Get(n, "DataSource"));
        var same = ElementPaths.Children(owner).Count(c => c is OutSystems.Model.Logic.Nodes.IRefreshDataNode && Model.NameOf(c) is null
                                                            && "refresh " + Name(Reflect.Get(c, "DataSource")) == alias);
        return same == 1 ? "" : "  [path " + ElementPaths.Segment(n, owner) + "]";
    }

    static string Name(object? o) => o is null ? "?" : Model.NameOf(o) ?? Reflect.Get(o, "DisplayName") as string ?? ElementPaths.Kind(o);
    static string Expr(object o, string key) => Text(Reflect.Get(o, key));
    static string Text(object? o) => o is IExpression e ? e.Text : o as string ?? "";
    internal static string Kind(object o)
    {
        if (FieldMappings.Is(o)) return FieldMappings.Kind;
        if (Reflect.Property(o, "HTTPMethod") is not null) return "RESTMethod";
        if (Reflect.Get(o, "EntityKind")?.ToString() == "Static") return "StaticEntity";
        if (Inspect.IsBlock(o)) return "Block";
        if (o is IScreenSignature) return "Screen";
        if (o is IObjectSignature s) return s.GetInterface().Name[1..].Replace("Signature", "");
        return ElementPaths.Kind(o);
    }
    static string Qualified(IESpace m, object o)
    {
        var hit = Refs.All(m).FirstOrDefault(x => ReferenceEquals(x.element, o));
        var name = Name(o);
        if (hit.element is null && o is IObjectSignature { Parent: { } scope } && (scope is IScreenSignature || Inspect.IsBlock(scope)))
            name = Name(scope) + "/" + name;
        if (o is IObjectSignature { Parent: { } parent } && Reflect.Property(o, "HTTPMethod") is not null) name = Name(parent) + "." + name;
        return (hit.owner ?? ((OutSystems.Model.Applications.IModule)m).Name) + "." + name;
    }
    static bool EntityAction(object o) => o.GetType().GetInterfaces().Any(i => i.Name.Contains("EntityAction"))
        || o.GetType().Name.Contains("EntityAction");

    internal sealed record DescribeError(string element, string error);

    // A record, not an anonymous type, so that `history` is left out (also with --pretty) unless it was asked for.
    sealed record Result(string element, string kind, bool? @public, object signature, string text,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] History.Info? history,
        string[] calls, string[] raises, object uses,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Unreadable.Item[]? unreadable);

    // `describe --css`: the whole style sheet of a screen, block, email or theme, to edit before a restyle replaces it.
    public static object StyleSheet(IESpace m, string path)
    {
        var o = ElementPaths.Resolve(m, path);
        if (Reflect.Property(o, "StyleSheet") is null) throw new ModelError($"{Kind(o)} {Name(o)} has no style sheet: --css reads screens, blocks, emails and themes.");
        return new { element = ElementPaths.PathTo(m, o) ?? Name(o), kind = Kind(o), styleSheet = Reflect.Get(o, "StyleSheet") as string ?? "" };
    }

    // `describe --css --all`: every theme, screen and block with a style sheet, as { element, kind, styleSheet }.
    public static List<object> AllStyleSheets(IESpace m)
    {
        var owners = new List<object>();
        foreach (var key in new[] { "MobileThemes", "WebThemes" })
            if (Reflect.Get(m, key) is System.Collections.IEnumerable themes) owners.AddRange(themes.Cast<object>());
        owners.AddRange(Inspect.Screens(m).Select(s => (object)s.screen));
        return owners.Distinct()
            .Select(o => (o, css: Reflect.Get(o, "StyleSheet") as string ?? ""))
            .Where(x => x.css.Trim().Length > 0)
            .Select(x => (object)new { element = (x.o is IObjectSignature s ? ElementPaths.PathTo(m, s) : null) ?? Name(x.o), kind = Kind(x.o), styleSheet = x.css })
            .ToList();
    }

    public static object Run(IESpace m, string path, int depth, List<string> searchIn, bool history = false) => Many(m, [path], depth, searchIn, collectErrors: false, history)[0];

    // One load of the module and of each producer file, however many elements are described.
    // collectErrors: a name that does not resolve becomes a DescribeError entry instead of failing the call.
    public static List<object> Many(IESpace m, IReadOnlyList<string> paths, int depth, List<string> searchIn, bool collectErrors, bool history = false)
    {
        var producers = new List<IESpace>();
        try
        {
            foreach (var input in searchIn)
            {
                var full = Path.GetFullPath(input);
                IEnumerable<string> files = Directory.Exists(full) ? Directory.EnumerateFiles(full, "*.oml", SearchOption.AllDirectories).Order(StringComparer.Ordinal) : new[] { full };
                foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase)) producers.Add(Model.LoadShared(file));
            }
            var results = new List<object>();
            foreach (var path in paths)
                try { results.Add(One(m, path, depth, producers, history)); }
                catch (ModelError e) when (collectErrors) { results.Add(new DescribeError(path, e.Message)); }
            return results;
        }
        finally { foreach (var p in producers) Model.Release(p); }
    }

    static object One(IESpace m, string path, int depth, List<IESpace> producers, bool history)
    {
        try
        {
            IObjectSignature root;
            using var unreadable = new Unreadable();
            try { root = ElementPaths.Resolve(m, path); }
            catch (ModelError e) when (e is not AmbiguousPathError && path.Contains('.'))
            {
                var own = ((OutSystems.Model.Applications.IModule)m).Name;
                if (path.StartsWith(own + ".", StringComparison.Ordinal)) path = path[(own.Length + 1)..];
                var methods = RestModel.Integrations(m, false).Concat(RestModel.Integrations(m, true))
                    .SelectMany(i => RestModel.Actions(i).Where(a => Name(i) + "." + Name(a) == path)).Cast<IObjectSignature>().ToArray();
                root = methods.Length == 1 ? methods[0] : (IObjectSignature)Refs.Find(m, path, typeof(IObjectSignature));
            }
            var writer = new Writer(m, producers);
            writer.Part("element " + Name(root), () => writer.Element(root, 0));
            // The line follows the element's own text, before any callee body that --depth adds.
            var changes = history ? History.Of(root) : null;
            if (History.Line(changes) is { } line) writer.Buffer.AppendLine().AppendLine(line);
            writer.Expand(depth);
            var signature = Unreadable.Try("signature", () => writer.Signature(root), new { inputs = Array.Empty<object>(), outputs = Array.Empty<object>() });
            return new Result(Unreadable.Try("qualified name", () => Qualified(m, root), Name(root)), Kind(root), Reflect.Get(root, "Public") as bool?, signature, writer.Buffer.ToString().TrimEnd(), changes,
                writer.Calls.Keys.ToArray(), writer.Raises.ToArray(),
                new { entities = writer.Entities.ToArray(), structures = writer.Structures.ToArray(), screens = writer.Screens.ToArray() }, unreadable.Items());
        }
        catch (System.Reflection.TargetInvocationException e)
        { throw new ModelError($"describe '{path}': {(e.InnerException ?? e).Message}"); }
    }

    sealed class Writer
    {
        public readonly StringBuilder Buffer = new();
        public readonly Dictionary<string, object> Calls = new(StringComparer.Ordinal);
        public readonly SortedSet<string> Raises = new(StringComparer.Ordinal), Entities = new(StringComparer.Ordinal), Structures = new(StringComparer.Ordinal), Screens = new(StringComparer.Ordinal);
        readonly HashSet<object> described = new();
        readonly IESpace module;
        readonly List<IESpace> producers;
        IESpace m;
        public Writer(IESpace module, List<IESpace> producers) { this.module = module; this.producers = producers; m = module; }
        void Line(int indent, string text) => Buffer.Append(' ', indent * 2).AppendLine(text);
        // One part of the text. When it cannot be read, its lines are dropped and, with an indent, one line says so.
        // `at` is the element, so the unreadable entry carries its path.
        public void Part(string part, Action write, int indent = -1, object? at = null)
        {
            var length = Buffer.Length;
            if (Unreadable.Try(() => at is null ? part : part + " at " + Unreadable.Where(at), write)) return;
            Buffer.Length = length;
            if (indent >= 0) Line(indent, part + " [unreadable]");
        }
        string TypeOf(object p) => Unreadable.Try(() => "type of " + Unreadable.Where(p), () => Model.Describe(m, Reflect.Get(p, "DataType") as ITypeSignature), "?");
        // Parameters in model order: it is the saved order, and a function action is called by position.
        object[] Parameters(object o, string key) => List(o, key).Select(p => (object)new { name = Name(p), type = TypeOf(p) }).ToArray();
        // An input's default value follows its type, as Service Studio shows it: Size: Text = "md".
        string ParametersText(object o, string key) => string.Join(", ", List(o, key).Select(p => Name(p) + ": " + TypeOf(p)
            + (key == "InputParameters" && Expr(p, "DefaultValue") is { Length: > 0 } value ? " = " + value : "")));
        public object Signature(object o) => new { inputs = Parameters(o, "InputParameters"), outputs = Parameters(o, "OutputParameters") };
        string Header(object o) => Kind(o) + " " + Name(o) + "(" + ParametersText(o, "InputParameters") + ")"
            + (ParametersText(o, "OutputParameters") is { Length: > 0 } outputs ? " -> " + outputs : "")
            + (Reflect.Get(o, "Public") is true ? " [public]" : "")
            + (Reflect.Get(o, "Fetch") is { } fetch ? (fetch.ToString() == "OnDemand" ? " [on demand]" : "")
                + (Reflect.Get(o, "ServerRequestTimeout") is int timeout ? " timeout " + timeout + " s" : "") + AfterFetch(o) : ""); // data actions
        string AfterFetch(object o) => Reflect.Get(o, "OnAfterFetch") is { } evt && Reflect.Get(evt, "Destination") is { } handler ? " on after fetch -> " + Name(handler) : "";
        string Args(object o) => string.Join(", ", List(o, "Arguments").Where(a => Expr(a, "Value").Length > 0).OrderBy(a => Name(Reflect.Get(a, "Parameter")), StringComparer.Ordinal).Select(a => Name(Reflect.Get(a, "Parameter")) + " = " + OneLine(Expr(a, "Value")) + FieldMappings.Suffix(a)));
        // An expression typed over several lines in Service Studio stays on its describe line. A very long one (a big text
        // literal) is cut, so one widget cannot fill the output: oml tree shows the whole expression.
        static string OneLine(string text)
        {
            if (text.Contains('\n')) text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*[\r\n]+\s*", " ").Trim();
            const int max = 500;
            return text.Length <= max ? text : text[..max] + " ... (" + (text.Length - max) + " more characters: oml tree)";
        }

        public void Element(object root, int indent)
        {
            described.Add(root);
            Line(indent, Header(root));
            if (Reflect.Property(root, "HTTPMethod") is not null)
                Line(indent + 1, Reflect.Get(root, "HTTPMethod") + " " + Expr(root, "URLPath"));
            var locals = ParametersText(root, "LocalVariables");
            if (locals.Length > 0) Line(indent + 1, "locals " + locals);
            TrackUses(root);
            if (root is IEmail)
            {
                Line(indent + 1, "subject " + Expr(root, "Subject"));
                Widgets(root, indent + 1);
            }
            else if (root is IScreenSignature || Inspect.IsBlock(root))
            {
                var roles = List(root, "Roles").Select(Name).Order(StringComparer.Ordinal).ToArray();
                if (roles.Length > 0) Line(indent + 1, "roles " + string.Join(", ", roles));
                foreach (var evt in List(root, "Events").OrderBy(Name, StringComparer.Ordinal))
                    Part("event " + Name(evt), () => Line(indent + 1, "event " + Name(evt) + "(" + ParametersText(evt, "InputParameters") + ")" + (Reflect.Get(evt, "IsMandatory") is true ? " [mandatory]" : "")), indent + 1, evt);
                foreach (var key in new[] { "OnInitialize", "OnReady", "OnRender", "OnDestroy", "OnAfterFetch", "OnParametersChanged" })
                    if (Reflect.Get(root, key) is { } evt && Reflect.Get(evt, "Destination") is { } handler) Line(indent + 1, key + " -> " + Name(handler));
                foreach (var aggregate in List(root, "ScreenAggregates").OrderBy(Name, StringComparer.Ordinal))
                    Part("query " + Name(aggregate), () => Line(indent + 1, Query(aggregate)), indent + 1, aggregate);
                foreach (var action in List(root, "DataActions").OrderBy(Name, StringComparer.Ordinal))
                    Part("data action " + Name(action), () => Element(action, indent + 1), indent + 1, action);
                foreach (var action in List(root, "ScreenActions").OrderBy(Name, StringComparer.Ordinal))
                    Part("action " + Name(action), () => Element(action, indent + 1), indent + 1, action);
                if (Reflect.Get(root, "Preparation") is { } preparation) Part("preparation", () => Element(preparation, indent + 1), indent + 1);
                Part("widgets", () => Widgets(root, indent + 1), indent + 1);
            }
            else if (List(root, "Attributes").Any())
            {
                foreach (var attr in List(root, "Attributes"))
                    Line(indent + 1, Name(attr) + ": " + TypeOf(attr) + (Reflect.Get(attr, "IsMandatory") is true ? " [mandatory]" : ""));
                if (root is OutSystems.Model.Data.IEntitySignature entity && entity.EntityKind.ToString() != "Static") Part("entity options", () => EntityOptions(entity, indent + 1), indent + 1);
                if (Reflect.Get(root, "EntityKind")?.ToString() == "Static")
                    foreach (var record in List(root, "Records").OrderBy(r => Reflect.Get(r, "Identifier")?.ToString(), StringComparer.Ordinal))
                        Part("record " + Reflect.Get(record, "Identifier"), () => Line(indent + 1, "record " + Reflect.Get(record, "Identifier") + ": "
                            + string.Join(", ", List(record, "AttributeValues").OrderBy(v => Name(Reflect.Get(v, "Attribute")), StringComparer.Ordinal).Select(v => Name(Reflect.Get(v, "Attribute")) + " = " + Expr(v, "Value")))), indent + 1, record);
            }
            else
            {
                var nodes = List(root, "Nodes").ToArray();
                var visited = new HashSet<object>();
                var start = nodes.FirstOrDefault(n => Kind(n) == "StartNode");
                if (start is not null) Flow(start, indent + 1, visited);
                else if (nodes.Length == 0 && Expr(root, "JavaScript").Length == 0 && Expr(root, "Statement").Length == 0) Line(indent + 1, "[signature only]");
                // Exception handlers are separate entry points, not successors of Start.
                foreach (var handler in ElementPaths.Ordered(nodes.OfType<IObjectSignature>().Where(n => Kind(n).Contains("ExceptionHandler"))))
                    Part("exception handler " + Name(handler), () =>
                    {
                        var options = (Reflect.Get(handler, "AbortTransaction") is false ? " [keep transaction]" : "") + (Reflect.Get(handler, "LogError") is false ? " [no log]" : "");
                        Line(indent + 1, "on exception " + Name(Reflect.Get(handler, "Exception")) + options);
                        Flow(handler, indent + 2, visited);
                    }, indent + 1, handler);
                // Comments are not connected to the flow: their text is the developer's own explanation.
                foreach (var comment in nodes.Where(n => Kind(n).Contains("Comment")))
                    if (CommentText(comment) is { Length: > 0 } note) Line(indent + 1, "comment: " + System.Text.RegularExpressions.Regex.Replace(note, @"\s+", " ").Trim());
                // A JavaScript node, a Script element or a SQL node described on its own: its code.
                foreach (var key in new[] { "JavaScript", "Statement" })
                    if (start is null && Expr(root, key) is { Length: > 0 } code) { Line(indent + 1, key == "Statement" ? "sql:" : "javascript:"); Code(code, indent + 2); }
            }
        }

        // Only what differs from a new entity's defaults, so plain entities stay short.
        void EntityOptions(OutSystems.Model.Data.IEntitySignature e, int indent)
        {
            if (e.IdentifierAttribute is { } id && (id.Name != "Id" || id.IsAutoNumber != OutSystems.Model.Enumerations.AutoNumber.Yes))
                Line(indent, "identifier " + id.Name + (id.IsAutoNumber == OutSystems.Model.Enumerations.AutoNumber.Yes ? " [auto number]" : ""));
            foreach (var (label, attribute) in new[] { ("label attribute", e.LabelAttribute), ("order by attribute", e.OrderByAttribute), ("is active attribute", e.IsActiveAttribute) })
                if (attribute is not null) Line(indent, label + " " + attribute.Name);
            // Model defaults for a regular entity: Expose Read Only Yes, Update Behavior All Attributes.
            if (!e.ExposeReadOnly) Line(indent, "expose read only: no");
            if (e.UpdateBehavior == OutSystems.Model.Enumerations.UpdateEntityBehaviorType.Optimized) Line(indent, "update behavior: changed attributes");
            foreach (var index in OmlCli.EntityOptions.Indexes(e).OrderBy(i => OmlCli.EntityOptions.IndexName(i), StringComparer.Ordinal))
                Line(indent, "index " + OmlCli.EntityOptions.IndexName(index) + "(" + string.Join(", ", OmlCli.EntityOptions.Columns(index)) + ")" + (Reflect.Get(index, "Unique") is true ? " [unique]" : ""));
        }

        static object? Target(object node, string kind)
        {
            if (node is not IObjectSignature obj) return null;
            return Unreadable.Try(() => $"{kind} connector of {Unreadable.Where(node)}", () =>
                ElementPaths.Children(obj).FirstOrDefault(c => ElementPaths.Kind(c).Equals(kind, StringComparison.OrdinalIgnoreCase)) is { } connector
                    ? Reflect.Get(connector, "TargetNode") : null, null);
        }

        void Flow(object? node, int indent, HashSet<object> visited)
        {
            while (node is not null)
            {
                if (!visited.Add(node)) { Line(indent, "-> continue at " + Name(node)); return; }
                var kind = Kind(node);
                if (kind == "StartNode") { node = Target(node, "Sequence"); continue; }
                var current = node;
                var stop = false;
                Part(kind + " " + Name(current), () => stop = Node(current, kind, indent, visited), indent, current);
                if (stop) return;
                node = Target(node, "Sequence");
            }
        }

        // One flow node's lines. True when the node ends this path (its branches were written).
        bool Node(object node, string kind, int indent, HashSet<object> visited)
        {
            if (kind == "IfNode")
            {
                Line(indent, "if " + Expr(node, "Condition") + ":");
                Flow(Target(node, "True"), indent + 1, visited);
                Line(indent, "else:"); Flow(Target(node, "False"), indent + 1, visited); return true;
            }
            if (kind == "SwitchNode")
            {
                Line(indent, "switch:");
                foreach (var c in List(node, "Conditions"))
                { Line(indent + 1, "case " + Expr(c, "Value") + ":"); Flow(Reflect.Get(Reflect.Get(c, "Link") ?? c, "TargetNode"), indent + 2, visited); }
                Line(indent + 1, "otherwise:"); Flow(Target(node, "Otherwise"), indent + 2, visited); return true;
            }
            if (kind == "ForEachNode")
            { Line(indent, "for each " + Expr(node, "RecordList") + ":"); Flow(Target(node, "Cycle"), indent + 1, visited); }
            else if (kind == "AssignNode")
                foreach (var a in List(node, "Assignments")) Line(indent, "set " + Expr(a, "Variable") + " = " + Expr(a, "Value") + FieldMappings.Suffix(a));
            else if (Reflect.Get(node, "Action") is { } action)
            {
                var qualified = Qualified(m, action); Calls.TryAdd(qualified, action);
                Line(indent, "call " + (EntityAction(action) ? Name(action) : qualified) + "(" + Args(node) + ")" + (Name(node) != Name(action) ? " -> " + Name(node) : "") + (EntityAction(action) ? "  [entity action]" : ""));
            }
            else if (Reflect.Property(node, "AsDatabaseAggregate") is not null) Line(indent, Query(node));
            else if (kind == "SQLNode")
            { var sql = System.Text.RegularExpressions.Regex.Replace(Expr(node, "Statement"), @"\s+", " ").Trim(); Line(indent, "sql " + Name(node) + "(" + Args(node) + ") -> " + string.Join(", ", List(node, "Outputs").Select(Name)) + ": " + (sql.Length > 200 ? sql[..200] + "..." : sql)); }
            else if (kind == "RaiseExceptionNode")
            { var ex = Reflect.Get(node, "Exception"); Raises.Add(Qualified(m, ex ?? node)); Line(indent, "raise " + Name(ex) + "(" + Expr(node, "ExceptionMessage") + ")"); }
            else if (kind.Contains("Destination")) Line(indent, "navigate to " + Name(Reflect.Get(node, "Destination")) + "(" + Args(node) + ")");
            else if (kind == "TriggerNode") Line(indent, "trigger " + Name(Reflect.Get(node, "Event")) + "(" + Args(node) + ")");
            else if (kind == "SendEmailNode" || Reflect.Property(node, "EmailScreen") is not null)
                Line(indent, "send email " + Name(Reflect.Get(node, "Email") ?? Reflect.Get(node, "EmailScreen")) + " from " + Expr(node, "From") + " to " + Expr(node, "To") + "(" + Args(node) + ")");
            // A refresh node usually has no name: its path is "refresh <data source>" (or the index form shown when that repeats).
            else if (kind == "RefreshDataNode") Line(indent, "refresh " + Name(Reflect.Get(node, "DataSource")) + RefreshHandle(node));
            else if (kind == "JavaScriptNode")
            {
                var outputs = string.Join(", ", List(node, "OutputParameters").Select(p => Name(p) + ": " + TypeOf(p)));
                Line(indent, "javascript " + Name(node) + "(" + Args(node) + ")" + (outputs.Length > 0 ? " -> " + outputs : "") + (Reflect.Get(node, "IsAsynchronous") is true ? " [async]" : "") + ":");
                Code(Expr(node, "JavaScript"), indent + 1);
            }
            else if (kind == "MessageNode") Line(indent, "message " + (Reflect.Get(node, "Type")?.ToString() ?? "Info") + ": " + Expr(node, "Message"));
            else if (kind.Contains("ExceptionHandler")) { } // its "on exception" line says it
            else if (kind == "JSONSerializeNode") Line(indent, "json serialize " + Expr(node, "Data") + " -> " + Name(node));
            else if (kind == "JSONDeserializeNode") Line(indent, "json deserialize " + Expr(node, "JSONString") + " -> " + Model.Describe(m, Reflect.Get(node, "DataType") as ITypeSignature));
            else if (kind == "EndNode") Line(indent, "end");
            else Line(indent, kind + " " + Name(node));
            return false;
        }

        // Code as written (JavaScript, SQL), line by line under its node. Tabs become two spaces, blank lines at the ends go.
        void Code(string code, int indent)
        {
            var lines = code.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "  ").Split('\n').Select(l => l.TrimEnd()).ToList();
            while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Count == 0) { Line(indent, "(empty)"); return; }
            var common = lines.Where(l => l.Length > 0).Min(l => l.Length - l.TrimStart().Length);
            const int max = 1000;
            foreach (var l in lines.Take(max)) Line(indent, l.Length >= common ? l[common..] : l);
            if (lines.Count > max) Line(indent, "... " + (lines.Count - max) + " more lines not shown");
        }

        static string CommentText(object comment)
        {
            foreach (var key in new[] { "Text", "Comment", "Label", "Description" })
                if (Text(Reflect.Get(comment, key)) is { Length: > 0 } t) return t;
            return "";
        }

        string Query(object node)
        {
            var db = Reflect.Get(node, "AsDatabaseAggregate") ?? node;
            var sources = List(db, "Sources").OrderBy(Name, StringComparer.Ordinal).ToArray();
            foreach (var s in sources) if (Reflect.Get(s, "Source") is { } source) Entities.Add(Qualified(m, source));
            var joins = List(db, "Joins").ToArray();
            var text = "query " + Name(node) + ": " + string.Join(", ", sources.Where(s => !joins.Any(j => ReferenceEquals(Reflect.Get(j, "RightSource"), s))).Select(Name));
            foreach (var j in joins) text += " " + (Reflect.Get(j, "JoinType")?.ToString() switch { "Inner" => "only with", "Left" => "with or without", _ => "with" }) + " " + Name(Reflect.Get(j, "RightSource")) + " on " + Expr(j, "Condition");
            void Add(string key, string label, Func<object, string> render) { var values = List(db, key).Select(render).ToArray(); if (key.Contains("Attributes")) Array.Sort(values, StringComparer.Ordinal); if (values.Length > 0) text += " " + label + " " + string.Join(", ", values); }
            Add("Filters", "where", f => Expr(f, "Condition"));
            // A computed group shows its output name: group by Year = Year(Order.PlacedOn).
            Add("GroupByAttributes", "group by", a => Expr(a, "Attribute") is var e && (e == Name(a) || e.EndsWith("." + Name(a))) ? e : Name(a) + " = " + e);
            Add("AggregateAttributes", "aggregate", a => Name(a) + " = " + Reflect.Get(a, "AggregationType") + "(" + Expr(a, "Attribute") + ")");
            Add("CalculatedAttributes", "calculate", a => Name(a) + " = " + Expr(a, "Value"));
            Add("FiltersInGroupBy", "having", f => Expr(f, "Condition"));
            Add("CalculatedAttributesInGroupBy", "calculate grouped", a => Name(a) + " = " + Expr(a, "Value"));
            string Sort(object a) => Expr(a, "Attribute") + (Reflect.Get(a, "SortDirection")?.ToString() == "Descending" ? " desc" : "") + (Reflect.Get(a, "IsDynamic") is true ? " [dynamic]" : "");
            Add("Sorts", "order by", Sort);
            Add("SortsInGroupBy", "order groups by", Sort);
            if (Expr(node, "MaxRecords") is { Length: > 0 } max) text += " max " + max;
            if (Reflect.Get(node, "Fetch")?.ToString() == "OnDemand") text += " [on demand]";
            if (Reflect.Get(node, "CacheInMinutes") is int cache and > 0) text += " cache " + cache + " min";
            if (Reflect.Get(node, "Timeout") is int timeout) text += " timeout " + timeout + " s";
            text += AfterFetch(node);
            return text;
        }

        void Widgets(object root, int indent)
        {
            var lines = new List<(int depth, string text)>();
            var visited = new HashSet<object>();
            void Walk(object o, int depth)
            {
                if (!visited.Add(o)) return;
                if (o is IWidgetSignature && !Unreadable.Try(() => "widget " + Unreadable.Where(o), () => lines.Add((depth, WidgetLine(o)))))
                    lines.Add((depth, ElementPaths.Kind(o) + " " + Name(o) + " [unreadable]"));
                if (o is IWidgetSignature) depth++;
                var at = lines.Count;
                if (o is IObjectSignature obj)
                    foreach (var child in Unreadable.Try(() => "children of " + Unreadable.Where(o), () => ElementPaths.Children(obj).Where(c => c is not IExpression).ToArray(), [])) Walk(child, depth);
                // An empty slot (a block's placeholder that holds nothing new) is noise.
                if (lines.Count == at && at > 0 && ElementPaths.Kind(o) == "CustomPlaceholderWidget") lines.RemoveAt(at - 1);
            }
            string WidgetLine(object o)
            {
                var properties = new List<string>();
                foreach (var key in new[] { "Value", "Text", "Variable", "Source", "SourceBlock", "Visible", "Condition" })
                    if (Reflect.Get(o, key) is { } v && (Text(v).Length > 0 || v is IObjectSignature))
                    {
                        if (key == "Text" && Expr(o, "Value") == Text(v) || key == "Visible" && Text(v).Equals("True", StringComparison.OrdinalIgnoreCase)) continue;
                        properties.Add(char.ToLowerInvariant(key[0]) + key[1..] + "=" + (Text(v).Length > 0 ? Text(v) : Name(v)));
                    }
                if (Reflect.Get(o, "TargetWidget") is IObjectSignature target) properties.Add("targetWidget=" + Name(target));
                // How it looks and behaves: classes, inline style, HTML tag and attributes, input options.
                if (Reflect.Get(o, "Style") is IExpression styleClasses && styleClasses.Text is { Length: > 0 } classes && classes != "\"\"") properties.Add("style=" + classes);
                else if (Text(Reflect.Get(o, "StyleClasses")) is { Length: > 0 } classNames && classNames != "\"\"") properties.Add("style=" + classNames);
                if (Reflect.Get(o, "CustomStyle") is string inline && inline.Trim().Length > 0) properties.Add("inlineStyle=\"" + inline.Trim() + "\"");
                if (Reflect.Get(o, "Tag") is string tag && tag.Length > 0) properties.Add("tag=" + tag);
                if (Reflect.Get(o, "Icon") is string icon && icon.Length > 0) properties.Add("icon=" + icon);
                if (Reflect.Get(o, "MarginLeft") is { } marginLeft && marginLeft.ToString() is { Length: > 0 } ml) properties.Add("marginLeft=" + ml);
                if (Reflect.Property(o, "InputType") is not null && Reflect.Get(o, "InputType")?.ToString() is { } inputType && inputType != "Text") properties.Add("type=" + inputType);
                if (Expr(o, "Prompt") is { Length: > 0 } prompt) properties.Add("prompt=" + prompt);
                if (Expr(o, "Mandatory") is { Length: > 0 } mandatory && !mandatory.Equals("False", StringComparison.OrdinalIgnoreCase)) properties.Add("mandatory=" + mandatory);
                if (Expr(o, "Enabled") is { Length: > 0 } enabled && !enabled.Equals("True", StringComparison.OrdinalIgnoreCase)) properties.Add("enabled=" + enabled);
                if (Reflect.Get(o, "IsDefault") is true) properties.Add("[default button]");
                if (Expr(o, "ConfirmationMessage") is { Length: > 0 } confirm) properties.Add("confirm=" + confirm);
                if (ElementPaths.Kind(o) == "Image")
                {
                    if (Reflect.Get(o, "Image") is IObjectSignature image) properties.Add("image=" + Name(image));
                    else if (Expr(o, "Url") is { Length: > 0 } url) properties.Add("url=" + url);
                }
                foreach (var attribute in List(o, "ExtendedProperties"))
                    if (Reflect.Get(attribute, "Property") is string attributeName && attributeName.Length > 0) properties.Add("attr " + attributeName + "=" + Expr(attribute, "Value"));
                var arguments = Args(o);
                if (arguments.Length > 0) properties.Add("args=(" + arguments + ")");
                var click = Reflect.Get(o, "OnClick");
                if (click is not null && Reflect.Get(click, "Destination") is { } destination) properties.Add("onClick=" + Name(destination) + (Args(click) is { Length: > 0 } args ? "(" + args + ")" : ""));
                if (Reflect.Get(o, "OnChange") is { } change && Reflect.Get(change, "Destination") is { } changeHandler) properties.Add("onChange=" + Name(changeHandler) + (Args(change) is { Length: > 0 } changeArgs ? "(" + changeArgs + ")" : ""));
                foreach (var evt in List(o, "Events"))
                    if (Reflect.Get(evt, "Handler") is { } handler) properties.Add(Name(evt) + "=" + Name(handler));
                foreach (var h in List(o, "EventHandlers"))
                    if (Reflect.Get(h, "Handler") is { } handler) properties.Add("event " + Name(Reflect.Get(h, "Event")) + "=" + Name(handler) + "(" + Args(h) + ")");
                return ElementPaths.Kind(o) + (Model.NameOf(o) is { Length: > 0 } name ? " " + name : "") + (properties.Count > 0 ? " " + string.Join(" ", properties) : "");
            }
            foreach (var w in List(root, "Widgets")) Walk(w, 0);
            // A big screen stays readable: past the cap, oml tree <Screen>/<path> reads the rest.
            const int max = 500;
            foreach (var line in lines.Take(max)) Line(indent + line.depth, line.text);
            if (lines.Count > max) Line(indent, "... " + (lines.Count - max) + " more widgets: oml tree <module.oml> <Screen> --depth 2");
        }

        void TrackUses(object root)
        {
            var owned = new HashSet<object>();
            void Walk(object o)
            {
                if (!owned.Add(o)) return;
                if (Reflect.Get(o, "Destination") is IScreenSignature screen) Unreadable.Try(() => "destination of " + Unreadable.Where(o), () => Screens.Add(Qualified(m, screen)));
                if (o is IObjectSignature obj) foreach (var child in Unreadable.Try(() => "children of " + Unreadable.Where(o), () => ElementPaths.Children(obj).ToArray(), [])) Walk(child);
            }
            Walk(root);
            bool Used(object element)
            {
                var pending = new Queue<object>();
                pending.Enqueue(element);
                foreach (var attribute in List(element, "Attributes")) pending.Enqueue(attribute);
                var seen = new HashSet<object>();
                while (pending.TryDequeue(out var item))
                {
                    if (!seen.Add(item)) continue;
                    foreach (var referer in List(item, "Referers"))
                    {
                        if (owned.Contains(referer)) return true;
                        if (referer is ITypeSignature) pending.Enqueue(referer);
                        else if (referer is IObjectSignature { Parent: ITypeSignature parent })
                        { pending.Enqueue(parent); pending.Enqueue(referer); }
                    }
                }
                return false;
            }
            foreach (var (owner, element) in Unreadable.Try("module elements", () => Refs.All(m).ToArray(), []))
            {
                var target = element is OutSystems.Model.Data.IEntitySignature ? Entities
                    : element is OutSystems.Model.Data.IStructureSignature ? Structures : null;
                if (target is not null && Unreadable.Try("uses of " + owner + "." + Name(element), () => Used(element), false)) target.Add(owner + "." + Name(element));
            }
        }

        public void Expand(int depth)
        {
            var pending = Calls.ToArray();
            var expanded = new HashSet<string>(StringComparer.Ordinal);
            for (var level = 0; level <= depth && pending.Length > 0; level++)
            {
                foreach (var call in pending)
                {
                    if (!expanded.Add(call.Key)) continue;
                    Part("call " + call.Key, () => ExpandOne(call, level, depth), 0);
                    m = module;
                }
                pending = Calls.Where(c => !expanded.Contains(c.Key)).ToArray();
            }
        }

        void ExpandOne(KeyValuePair<string, object> call, int level, int depth)
        {
            object action = call.Value;
            var owner = call.Key[..call.Key.IndexOf('.')];
            var producer = producers.FirstOrDefault(p => ((OutSystems.Model.Applications.IModule)p).Name == owner);
            if (producer is not null)
            {
                m = producer;
                action = Refs.All(producer).FirstOrDefault(c => c.owner == owner && Name(c.element) == Name(call.Value)).element ?? action;
            }
            var available = owner == ((OutSystems.Model.Applications.IModule)m).Name;
            if (!described.Contains(action))
            {
                Line(0, "");
                if (level < depth && available && List(action, "Nodes").Any()) Element(action, 0);
                // Say why the body is missing, so the next command is clear.
                else Line(0, Header(action) + (!available ? " [signature only: in " + owner + "; add --in <" + owner + ".oml or its folder>]"
                    : level >= depth && List(action, "Nodes").Any() ? " [signature only: --depth " + (depth + 1) + " shows it]" : " [signature only]"));
            }
        }
    }
}
