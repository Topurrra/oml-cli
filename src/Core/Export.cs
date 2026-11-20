using System.Text.Json.Nodes;
using OutSystems.Model;
using OutSystems.Model.UI;

namespace OmlCli;

// `oml export <module.oml> <Element>...`: a screen, block or email as structured JSON, for code that transforms it (the
// first is `oml migrate`). It reads the facts describe prints, with describe's readers, and writes flows as a spec `flow`
// list takes them. Keys that start with `_` are facts a spec does not take. What cannot be read is said: in a flow's
// `unsupported` (then `complete` is false) or in the element's `notes`. Nothing is guessed.
static class Export
{
    internal sealed record ExportError(string element, string error);

    // One result per name, in the order asked; a name that fails gives an ExportError in its place.
    public static List<object> Many(IESpace m, IReadOnlyList<string> paths)
    {
        var results = new List<object>();
        foreach (var path in paths)
            try { results.Add(One(m, path)); }
            catch (ModelError e) { results.Add(new ExportError(path, e.Message)); }
        return results;
    }

    internal static JsonObject One(IESpace m, string path)
    {
        try
        {
            var root = ElementPaths.Resolve(m, path);
            var kind = root is IEmail ? "Email" : Inspect.IsBlock(root) ? "Block" : root is IScreenSignature ? "Screen" : null;
            if (kind is null)
                throw new ModelError($"'{path}' is {Describe.Kind(root)} {Describe.Name(root)}: export reads screens, blocks and emails. Read other elements with oml describe.");
            if (ElementPaths.Referenced(root))
                throw new ModelError($"'{path}' is {kind} {Describe.Name(root)} of a referenced module: export it from its producer module.");
            using var unreadable = new Unreadable();
            var reader = new Reader(m);
            var element = reader.Element(root, kind);
            foreach (var item in unreadable.Items() ?? Array.Empty<Unreadable.Item>())
                if (!FactSources.Any(p => item.part.EndsWith(" property " + p, StringComparison.Ordinal)))   // the typed readers read it already
                    reader.Notes.Add($"could not read {item.part}: {item.reason}");
            element["notes"] = Strings(reader.Notes.Distinct(StringComparer.Ordinal));
            return element;
        }
        catch (System.Reflection.TargetInvocationException e)
        { throw new ModelError($"export '{path}': {(e.InnerException ?? e).Message}"); }
    }

    static JsonArray Strings(IEnumerable<string> items) => new(items.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    static string Lower(string key) => key.Length == 0 ? key : char.ToLowerInvariant(key[0]) + key[1..];

    // Widget properties that describe's facts already read (under other keys: style, inlineStyle, confirm, ...): the
    // generic reader does not add them again.
    static readonly HashSet<string> FactSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "Value", "Text", "Variable", "Source", "SourceBlock", "Visible", "Condition", "TargetWidget", "Style", "StyleClasses", "CustomStyle",
        "Tag", "Icon", "MarginLeft", "InputType", "Prompt", "Mandatory", "Enabled", "IsDefault", "ConfirmationMessage", "Image",
    };
    // "Url" is not in the list: describe writes url only for an Image, so a Link's address must come from the generic
    // reader, which skips a key the facts already wrote.

    // Node kinds the spec has but no typed reader here reads: their spec keys are read from the generic properties, by
    // name. A flag key is a boolean in the spec. `missing` is a part that cannot be read this way.
    static readonly Dictionary<string, (string spec, (string key, bool flag)[] keys, string? missing)> GenericKinds = new()
    {
        ["DownloadNode"] = ("download", new[] { ("fileContent", false), ("fileName", false), ("mimeType", false), ("saveToDisk", true) }, null),
        ["ExcelToRecordListNode"] = ("excelToRecordList", new[] { ("fileContent", false), ("sheetName", false), ("dataType", false) }, null),
        ["RecordListToExcelNode"] = ("recordListToExcel", new[] { ("recordList", false) }, "its attributeSelection is a list that the reader does not read"),
    };

    sealed class Reader
    {
        readonly IESpace m;
        readonly string own;
        public readonly List<string> Notes = new();
        public Reader(IESpace module) { m = module; own = ((OutSystems.Model.Applications.IModule)module).Name; }

        // How a spec names an element: by its name in this module (an action of the same screen or block too), and as
        // Module.Name when it lives in a referenced module.
        string RefName(object o)
        {
            if (o is IObjectSignature { Parent: { } scope } && (scope is IScreenSignature || Inspect.IsBlock(scope))) return Describe.Name(o);
            var qualified = Unreadable.Try(() => "name of " + Unreadable.Where(o), () => Describe.Qualified(m, o), Describe.Name(o));
            return qualified.StartsWith(own + ".", StringComparison.Ordinal) ? qualified[(own.Length + 1)..] : qualified;
        }

        // A built-in exception by the module property that holds it (the name a spec takes), else its spec name.
        string ExceptionName(object? exception)
        {
            if (exception is null) return "?";
            foreach (var key in Refs.SystemExceptions)
                if (ReferenceEquals(Reflect.Get(m, key), exception)) return key;
            return RefName(exception);
        }

        string ModuleKind() => WebUi.IsTraditional(m) ? "Traditional"
            : m.Kind.ToString().Contains("Mobile", StringComparison.OrdinalIgnoreCase) ? "Mobile" : "Reactive";

        // The part of a tree path for `child` under `parent`, as tree prints it; its name or kind when the parent does not list it.
        static string Seg(object child, IObjectSignature parent)
        {
            if (child is not IObjectSignature element) return Describe.Name(child);
            return Unreadable.Try<string>(() => "path of " + Unreadable.Where(element),
                () => ElementPaths.Children(parent).Contains(element) ? ElementPaths.Segment(element, parent) : Model.NameOf(element) ?? ElementPaths.Kind(element),
                ElementPaths.Kind(element));
        }

        // Every readable property, read generically (ReviewModel): for what the typed readers do not read.
        SortedDictionary<string, string> GenericProps(object o)
        {
            if (o is not IObjectSignature element) return new(StringComparer.Ordinal);
            var failures = new List<Unreadable.Item>();
            var props = Unreadable.Try(() => "properties of " + Unreadable.Where(o), () => ReviewModel.Properties(m, element, failures),
                new SortedDictionary<string, string>(StringComparer.Ordinal));
            foreach (var f in failures)
                if (!FactSources.Any(p => f.part.EndsWith(" property " + p, StringComparison.Ordinal)))   // the typed readers read it already
                    Notes.Add($"could not read {f.part}: {f.reason}");
            return props;
        }

        // The elements its properties point at, read generically (ReviewModel.Targets): { "<property>": "<name>" | ["<names>"] }.
        JsonObject GenericTargets(object o)
        {
            var targets = new JsonObject();
            if (o is not IObjectSignature element) return targets;
            var failures = new List<Unreadable.Item>();
            var found = Unreadable.Try(() => "properties of " + Unreadable.Where(o), () => ReviewModel.Targets(element, failures),
                new List<(string property, bool list, string[] names)>());
            foreach (var f in failures) Notes.Add($"could not read {f.part}: {f.reason}");
            foreach (var (property, list, names) in found) targets[Lower(property)] = list ? (JsonNode?)Strings(names) : JsonValue.Create(names[0]);
            return targets;
        }

        // Spec keys read from the generic properties by name, ignoring case. A key that is not there is unsupported when required.
        void SpecKeys(object owner, JsonObject o, Action<string> unsupported, bool required, params (string key, bool flag)[] keys)
        {
            var props = GenericProps(owner);
            foreach (var (key, flag) in keys)
            {
                var hit = props.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (hit.Value is null) { if (required) unsupported($"its {key} could not be read"); continue; }
                if (!flag) o[key] = hit.Value;
                else if (bool.TryParse(hit.Value, out var value)) o[key] = value;
                else unsupported($"its {key} is '{hit.Value}', not True or False");
            }
        }

        public JsonObject Element(IObjectSignature root, string kind)
        {
            var name = Describe.Name(root);
            var flow = Inspect.Screens(m).FirstOrDefault(s => ReferenceEquals(s.screen, root)).flow ?? (root.Parent is { } parent ? Model.NameOf(parent) : null);
            var path = flow is null ? name : flow + "/" + name;
            var o = new JsonObject { ["kind"] = kind, ["name"] = name, ["flow"] = flow, ["path"] = path, ["moduleKind"] = ModuleKind() };
            // Each part is read on its own: a part that cannot be read is left out and named in notes.
            void Part(string key, Func<JsonNode?> read) => Unreadable.Try(() => key + " of " + Unreadable.Where(root), () => { o[key] = read(); });
            if (Reflect.Property(root, "Title") is not null && Describe.Text(Reflect.Get(root, "Title")) is { Length: > 0 } title) o["title"] = title;
            if (root is IEmail) o["subject"] = Describe.Expr(root, "Subject");
            Part("inputs", () => Variables(root, "InputParameters", withDefault: true));
            Part("locals", () => Variables(root, "LocalVariables", withDefault: true));
            if (kind != "Email") Part("access", () => Access(root));
            if (Reflect.Property(root, "StyleSheet") is not null) o["styleSheet"] = Reflect.Get(root, "StyleSheet") as string ?? "";
            if (Reflect.Property(root, "JavaScript") is not null) o["javaScript"] = Describe.Expr(root, "JavaScript");
            // Only a Traditional screen, block or email has a Preparation (a Reactive one has none, so no key).
            if (WebUi.IsTraditional(m))
            {
                if (kind != "Email" || Reflect.Property(root, "Preparation") is not null)
                    Part("preparation", () => Reflect.Get(root, "Preparation") is IObjectSignature preparation ? Flow(preparation, path + "/" + Seg(preparation, root)) : null);
                else Notes.Add("the email's Preparation could not be read");
            }
            Part("actions", () => new JsonArray(Describe.List(root, "ScreenActions").OrderBy(Describe.Name, StringComparer.Ordinal)
                .Select(a => (JsonNode?)ScreenAction(a, root, path)).ToArray()));
            // Reactive and Mobile parts (a Traditional screen has none of them): screen aggregates and data actions in the
            // spec's shape, lifecycle handlers and block events.
            if (Describe.List(root, "ScreenAggregates").Any())
                Part("aggregates", () => new JsonArray(Describe.List(root, "ScreenAggregates").OrderBy(Describe.Name, StringComparer.Ordinal)
                    .Select(a => (JsonNode?)ScreenAggregate(a, root, path)).ToArray()));
            if (Describe.List(root, "DataActions").Any())
                Part("dataActions", () => new JsonArray(Describe.List(root, "DataActions").OrderBy(Describe.Name, StringComparer.Ordinal)
                    .Select(a => (JsonNode?)DataAction(a, root, path)).ToArray()));
            foreach (var key in new[] { "OnInitialize", "OnReady", "OnRender", "OnDestroy", "OnParametersChanged" })
                if (Describe.Handler(root, key) is { } handler) o[Lower(key)] = Describe.Name(handler);
            if (Describe.List(root, "Events").Any())
                Part("events", () => new JsonArray(Describe.List(root, "Events").OrderBy(Describe.Name, StringComparer.Ordinal).Select(e => (JsonNode?)new JsonObject
                {
                    ["name"] = Describe.Name(e), ["mandatory"] = Reflect.Get(e, "IsMandatory") is true, ["inputs"] = Variables(e, "InputParameters", withDefault: false),
                }).ToArray()));
            Part("widgets", () => Widgets(root, path));
            return o;
        }

        // Inputs, locals and outputs in the spec's variable shape. withDefault: always give "default" (an element's
        // inputs and locals); else only when there is one, so a node's variables go into a spec unchanged.
        JsonArray Variables(object owner, string key, bool withDefault)
        {
            var list = new JsonArray();
            foreach (var p in Describe.List(owner, key))
            {
                var v = new JsonObject { ["name"] = Describe.Name(p), ["type"] = Describe.TypeOf(m, p) };
                if (key == "InputParameters" && Reflect.Get(p, "IsMandatory") is bool mandatory) v["mandatory"] = mandatory;
                if (key != "OutputParameters" && Describe.Expr(p, "DefaultValue") is var value && (withDefault || value.Length > 0)) v["default"] = value;
                if (Reflect.Get(p, "Description") is string { Length: > 0 } description) v["description"] = description;
                list.Add(v);
            }
            return list;
        }

        // Who may open it. The model widens access by itself: Anonymous includes Registered and every role.
        JsonObject Access(object root)
        {
            var roles = Describe.List(root, "Roles").ToList();
            var anonymous = Reflect.Get(m, "AnonymousRole");
            var registered = Reflect.Get(m, "RegisteredRole");
            return new JsonObject
            {
                ["anonymous"] = anonymous is not null && roles.Contains(anonymous),
                ["registered"] = registered is not null && roles.Contains(registered),
                ["roles"] = Strings(roles.Where(r => !Equals(r, anonymous) && !Equals(r, registered)).Select(RefName).Order(StringComparer.Ordinal)),
            };
        }

        JsonObject ScreenAction(object action, IObjectSignature owner, string ownerPath)
        {
            var path = ownerPath + "/" + Seg(action, owner);
            var o = new JsonObject
            {
                ["name"] = Describe.Name(action),
                ["inputs"] = Variables(action, "InputParameters", withDefault: false),
                ["locals"] = Variables(action, "LocalVariables", withDefault: false),
                ["outputs"] = Variables(action, "OutputParameters", withDefault: false),
                ["flow"] = action is IObjectSignature body ? Flow(body, path) : null,
            };
            o["_path"] = path;
            return o;
        }

        JsonObject DataAction(object action, IObjectSignature owner, string ownerPath)
        {
            var o = ScreenAction(action, owner, ownerPath);
            if (Reflect.Get(action, "Fetch")?.ToString() is { } fetch) o["fetch"] = fetch == "OnDemand" ? "onDemand" : "atStart";
            if (Reflect.Get(action, "ServerRequestTimeout") is int timeout) o["timeout"] = timeout;
            if (Describe.Handler(action, "OnAfterFetch") is { } after) o["onAfterFetch"] = Describe.Name(after);
            return o;
        }

        JsonObject ScreenAggregate(object aggregate, IObjectSignature owner, string ownerPath)
        {
            var path = ownerPath + "/" + Seg(aggregate, owner);
            var o = new JsonObject { ["name"] = Describe.Name(aggregate) };
            Query(aggregate, o, why => Notes.Add($"{path}: {why}"));
            o["_path"] = path;
            return o;
        }

        // ---- flows: the nodes in the format of a spec `flow` list ----

        // The flow of an action or a preparation. The main path from Start comes first (its first node is the one Start
        // leads to, as a spec takes it), then comments, then each exception handler with its path. End nodes are not
        // listed: a connector to one is "End". A node without a name gets one (Kind1, Kind2, ...) for the connectors.
        JsonObject Flow(IObjectSignature owner, string ownerPath)
        {
            var nodes = Describe.List(owner, "Nodes").ToArray();
            var unsupported = new JsonArray();
            // Default equality, as describe's walker compares nodes.
            var names = new Dictionary<object, string>();
            var taken = new HashSet<string>(nodes.Select(Model.NameOf).OfType<string>(), StringComparer.Ordinal) { "Start", "End" };
            string NodeName(object node)
            {
                if (names.TryGetValue(node, out var known)) return known;
                var kind = Describe.Kind(node);
                string name;
                if (kind == "EndNode") name = "End";
                else if (kind == "StartNode") name = "Start";
                else if (!kind.Contains("Comment", StringComparison.Ordinal) && Model.NameOf(node) is { } given) name = given;
                else
                {
                    var stem = kind.EndsWith("Node", StringComparison.Ordinal) && kind.Length > 4 ? kind[..^4] : kind;
                    var i = 1;
                    while (!taken.Add(stem + i)) i++;
                    name = stem + i;
                }
                return names[node] = name;
            }
            foreach (var node in nodes) NodeName(node);
            var repeated = names.Values.Where(n => n is not ("Start" or "End")).GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

            var order = new List<object>();
            var seen = new HashSet<object>();
            void Walk(object? from)
            {
                var stack = new Stack<object>();
                if (from is not null) stack.Push(from);
                while (stack.TryPop(out var current))
                {
                    if (!seen.Add(current)) continue;
                    var kind = Describe.Kind(current);
                    if (kind is not ("StartNode" or "EndNode")) order.Add(current);
                    var next = Successors(current, kind);
                    for (var i = next.Count - 1; i >= 0; i--) stack.Push(next[i]);
                }
            }
            Walk(nodes.FirstOrDefault(n => Describe.Kind(n) == "StartNode"));
            foreach (var comment in nodes.Where(n => Describe.Kind(n).Contains("Comment", StringComparison.Ordinal)))
                if (seen.Add(comment)) order.Add(comment);
            var mainCount = order.Count;
            foreach (var handler in ElementPaths.Ordered(nodes.OfType<IObjectSignature>().Where(n => Describe.Kind(n).Contains("ExceptionHandler", StringComparison.Ordinal))))
                Walk(handler);
            // Nodes no path reaches (the model warns about them): listed with the main path, so their End is the main End.
            var stray = nodes.Where(n => !seen.Contains(n) && Describe.Kind(n) is not ("StartNode" or "EndNode")).ToList();
            order.InsertRange(mainCount, stray);

            string? TargetName(object? target) => target is null ? null : NodeName(target);
            var list = new JsonArray();
            foreach (var node in order)
            {
                var path = ownerPath + "/" + Seg(node, owner);
                void Unsupported(string why) => unsupported.Add(new JsonObject { ["path"] = path, ["kind"] = Describe.Kind(node), ["why"] = why });
                if (stray.Contains(node)) Unsupported("no path from Start or an exception handler reaches it");
                var name = NodeName(node);
                if (repeated.Contains(name)) Unsupported($"another node of this flow is also named {name}; a spec names each node once");
                list.Add(Unreadable.Try(() => "node " + path, () => Node(node, name, path, TargetName, Unsupported), new JsonObject { ["kind"] = Describe.Kind(node), ["name"] = name, ["_path"] = path }));
            }
            return new JsonObject { ["nodes"] = list, ["complete"] = unsupported.Count == 0, ["unsupported"] = unsupported };
        }

        static List<object> Successors(object node, string kind)
        {
            var next = new List<object?>();
            if (kind == "IfNode") { next.Add(Describe.Target(node, "True")); next.Add(Describe.Target(node, "False")); }
            else if (kind == "SwitchNode") { next.AddRange(Describe.List(node, "Conditions").Select(Describe.CaseTarget)); next.Add(Describe.Target(node, "Otherwise")); }
            else if (kind == "ForEachNode") { next.Add(Describe.Target(node, "Cycle")); next.Add(Describe.Target(node, "Sequence")); }
            else next.Add(Describe.Target(node, "Sequence"));
            return next.OfType<object>().ToList();
        }

        // One node in the spec's format, in the order of describe's node reader (Describe.Writer.Node).
        JsonObject Node(object n, string name, string path, Func<object?, string?> target, Action<string> unsupported)
        {
            var kind = Describe.Kind(n);
            var o = new JsonObject();
            string? reference = null;
            void Spec(string specKind) { o["kind"] = specKind; o["name"] = name; }
            void Connect(string key, string connector)
            {
                if (target(Describe.Target(n, connector)) is { } to) o[key] = to;
                else unsupported($"its {connector} connector leads nowhere");
            }
            void Args(object owner) { if (ArgsOf(owner, unsupported) is { Count: > 0 } args) o["args"] = args; }

            if (kind == "IfNode") { Spec("if"); o["condition"] = Describe.Expr(n, "Condition"); Connect("true", "True"); Connect("false", "False"); }
            else if (kind == "SwitchNode")
            {
                Spec("switch");
                var cases = new JsonArray();
                foreach (var c in Describe.List(n, "Conditions"))
                {
                    var next = target(Describe.CaseTarget(c));
                    if (next is null) unsupported("a case leads nowhere");
                    cases.Add(new JsonObject { ["condition"] = Describe.Expr(c, "Value"), ["next"] = next });
                }
                o["cases"] = cases;
                Connect("otherwise", "Otherwise");
            }
            else if (kind == "ForEachNode") { Spec("forEach"); o["recordList"] = Describe.Expr(n, "RecordList"); Connect("cycle", "Cycle"); Connect("next", "Sequence"); }
            else if (kind == "AssignNode")
            {
                Spec("assign");
                var assign = new JsonObject();
                foreach (var a in Describe.List(n, "Assignments"))
                {
                    var variable = Describe.Expr(a, "Variable");
                    if (FieldMappings.Suffix(a) is { Length: > 0 } fields) unsupported($"it sets {variable} field by field ({fields.Trim()}); a spec assign takes one expression per variable");
                    if (assign.ContainsKey(variable)) unsupported($"it sets {variable} twice; a spec assign sets each variable once");
                    else assign[variable] = Describe.Expr(a, "Value");
                }
                o["assign"] = assign;
                Connect("next", "Sequence");
            }
            else if (Reflect.Get(n, "Action") is { } action)
            {
                Spec("execute");
                reference = RefName(action);
                o["action"] = reference;
                Args(n);
                Connect("next", "Sequence");
            }
            else if (Reflect.Property(n, "AsDatabaseAggregate") is not null) { Spec("aggregate"); Query(n, o, unsupported); Connect("next", "Sequence"); }
            else if (kind == "SQLNode") { Spec("sql"); Sql(n, o, unsupported); Connect("next", "Sequence"); }
            else if (kind == "RaiseExceptionNode")
            {
                Spec("raiseException");
                reference = ExceptionName(Reflect.Get(n, "Exception"));
                o["exception"] = reference;
                o["message"] = Describe.Expr(n, "ExceptionMessage");
            }
            else if (kind.Contains("Destination", StringComparison.Ordinal))
            {
                Spec("destination");
                var destination = Reflect.Get(n, "Destination");
                if (destination is not null) reference = RefName(destination);
                if (destination is IScreenSignature) o["screen"] = reference;
                else unsupported($"it goes to {Describe.Name(destination)}, not to a screen; a spec destination names a screen");
                Args(n);
            }
            else if (kind == "TriggerNode") { Spec("triggerEvent"); o["event"] = Describe.Name(Reflect.Get(n, "Event")); Args(n); Connect("next", "Sequence"); }
            else if (kind == "SendEmailNode" || Reflect.Property(n, "EmailScreen") is not null)
            {
                Spec("sendEmail");
                var email = Reflect.Get(n, "Email") ?? Reflect.Get(n, "EmailScreen");
                o["email"] = email is null ? "?" : RefName(email);
                o["from"] = Describe.Expr(n, "From");
                o["to"] = Describe.Expr(n, "To");
                SpecKeys(n, o, unsupported, false, ("cc", false), ("bcc", false), ("logContent", true));
                Args(n);
                Connect("next", "Sequence");
            }
            else if (kind == "RefreshDataNode")
            {
                Spec("refreshData");
                var source = Reflect.Get(n, "DataSource");
                o["dataSource"] = Describe.Name(source);
                if (source is IObjectSignature { Parent: { } holder } && !(holder is IScreenSignature || Inspect.IsBlock(holder)))
                    unsupported($"it refreshes {Describe.Name(source)}, which is not an aggregate or data action of a screen or block (a Preparation aggregate); a spec refreshData names one of those");
                Args(n);
                Connect("next", "Sequence");
            }
            else if (kind == "JavaScriptNode")
            {
                Spec("javaScript");
                o["javaScript"] = Describe.Expr(n, "JavaScript");
                if (Variables(n, "InputParameters", withDefault: false) is { Count: > 0 } inputs) o["inputs"] = inputs;
                if (Variables(n, "OutputParameters", withDefault: false) is { Count: > 0 } outputs) o["outputs"] = outputs;
                Args(n);
                if (Reflect.Get(n, "IsAsynchronous") is true) o["_async"] = true;
                Connect("next", "Sequence");
            }
            else if (kind == "MessageNode")
            {
                Spec("message");
                o["message"] = Describe.Expr(n, "Message");
                o["type"] = (Reflect.Get(n, "Type")?.ToString() ?? "Info").ToLowerInvariant();
                Connect("next", "Sequence");
            }
            else if (kind.Contains("Comment", StringComparison.Ordinal)) { Spec("comment"); o["text"] = Describe.CommentText(n); SpecKeys(n, o, unsupported, false, ("isReminder", true)); }
            else if (kind.Contains("ExceptionHandler", StringComparison.Ordinal))
            {
                Spec("exceptionHandler");
                o["exception"] = ExceptionName(Reflect.Get(n, "Exception"));
                if (Reflect.Get(n, "AbortTransaction") is bool abort) o["abortTransaction"] = abort;
                if (Reflect.Get(n, "LogError") is bool log) o["logError"] = log;
                Connect("next", "Sequence");
            }
            else if (kind == "JSONSerializeNode") { Spec("jsonSerialize"); o["data"] = Describe.Expr(n, "Data"); Connect("next", "Sequence"); }
            else if (kind == "JSONDeserializeNode") { Spec("jsonDeserialize"); o["jsonString"] = Describe.Expr(n, "JSONString"); o["dataType"] = Describe.TypeOf(m, n); Connect("next", "Sequence"); }
            else if (GenericKinds.TryGetValue(kind, out var shape))
            {
                Spec(shape.spec);
                SpecKeys(n, o, unsupported, true, shape.keys);
                if (shape.missing is { } missing) unsupported(missing);
                if (Describe.Target(n, "Sequence") is not null) Connect("next", "Sequence");
            }
            else
            {
                // A kind the spec cannot build: its real kind, what could be read, and why it is unsupported.
                o["kind"] = kind;
                o["name"] = name;
                var props = new JsonObject();
                foreach (var (key, value) in GenericProps(n)) props[key] = value;
                o["_props"] = props;
                // The elements it points at (an Ajax Refresh node's widgets), which _props cannot hold because they are lists.
                if (GenericTargets(n) is { Count: > 0 } targets) o["_targets"] = targets;
                if (Describe.Target(n, "Sequence") is not null) Connect("next", "Sequence");
                unsupported(kind.Contains("Ajax", StringComparison.Ordinal)
                    ? "a spec flow has no Ajax Refresh node (Traditional Web only); the properties that could be read are in _props, and the widgets it refreshes are in _targets when they could be read"
                    : $"a spec flow has no {kind} node; the properties that could be read are in _props, and the elements it points at in _targets");
            }
            o["_path"] = path;
            if (reference is not null) o["_ref"] = reference;
            return o;
        }

        // The arguments that have a value, by parameter name, as a spec's "args" takes them.
        JsonObject ArgsOf(object owner, Action<string> unsupported)
        {
            var args = new JsonObject();
            foreach (var a in Describe.ArgItems(owner)) args[Describe.ArgName(a)] = Describe.Expr(a, "Value");
            foreach (var a in Describe.List(owner, "Arguments"))
                if (FieldMappings.Suffix(a) is { Length: > 0 } fields)
                    unsupported($"its argument {Describe.ArgName(a)} is set field by field ({fields.Trim()}); a spec argument takes one expression");
            return args;
        }

        // An SQL node: statement, parameters (in their order, with the argument that fills each), outputs, max records.
        void Sql(object n, JsonObject o, Action<string> unsupported)
        {
            o["sql"] = Describe.Expr(n, "Statement");
            var arguments = Describe.List(n, "Arguments").ToList();
            var parameters = Describe.List(n, "InputParameters").ToList();
            if (parameters.Count == 0) parameters = arguments.Select(a => Reflect.Get(a, "Parameter")).OfType<object>().OrderBy(Describe.Name, StringComparer.Ordinal).ToList();
            var list = new JsonArray();
            foreach (var p in parameters)
            {
                var argument = arguments.FirstOrDefault(a => ReferenceEquals(Reflect.Get(a, "Parameter"), p));
                var entry = new JsonObject { ["name"] = Describe.Name(p), ["type"] = Describe.TypeOf(m, p), ["value"] = argument is null ? "" : Describe.Expr(argument, "Value") };
                SpecKeys(p, entry, unsupported, false, ("expandInline", true));
                list.Add(entry);
            }
            if (list.Count > 0) o["params"] = list;
            o["outputs"] = Strings(Describe.List(n, "Outputs").Select(Describe.Name));
            if (Describe.Expr(n, "MaxRecords") is { Length: > 0 } max) o["maxRecords"] = max;
        }

        // An aggregate's spec keys (flow aggregate or screen aggregate), from the same parts describe's query line reads.
        void Query(object node, JsonObject o, Action<string> unsupported)
        {
            var db = Describe.QueryBody(node);
            var sources = new List<string>();
            foreach (var s in Describe.List(db, "Sources").OrderBy(Describe.Name, StringComparer.Ordinal))
            {
                if (Reflect.Get(s, "Source") is not { } entity) { unsupported($"its source {Describe.Name(s)} could not be read"); continue; }
                if (Describe.Name(s) != Describe.Name(entity)) unsupported($"its source {Describe.Name(s)} is {Describe.Name(entity)} under another name; spec sources are entity names");
                sources.Add(RefName(entity));
            }
            o["sources"] = Strings(sources);
            var joins = new JsonArray();
            foreach (var j in Describe.List(db, "Joins"))
            {
                if (Reflect.Get(j, "JoinType")?.ToString() is not ("Inner" or "Left" or "Full")) unsupported($"its join with {Describe.Name(Reflect.Get(j, "RightSource"))} has the type {Reflect.Get(j, "JoinType")}, which a spec does not take");
                joins.Add(new JsonObject
                {
                    ["type"] = Describe.JoinWords(j), ["left"] = Describe.Name(Reflect.Get(j, "LeftSource")),
                    ["right"] = Describe.Name(Reflect.Get(j, "RightSource")), ["condition"] = Describe.Expr(j, "Condition"),
                });
            }
            if (joins.Count > 0) o["joins"] = joins;
            void Texts(string key, string list, string property)
            {
                var values = Describe.List(db, list).Select(x => Describe.Expr(x, property)).ToList();
                if (values.Count > 0) o[key] = Strings(values);
            }
            void Sorts(string key, string list)
            {
                var sorts = new JsonArray();
                foreach (var a in Describe.List(db, list))
                {
                    var sort = new JsonObject { ["attribute"] = Describe.Expr(a, "Attribute") };
                    if (Describe.Descending(a)) sort["descending"] = true;
                    if (Reflect.Get(a, "IsDynamic") is true) sort["dynamic"] = true;
                    sorts.Add(sort);
                }
                if (sorts.Count > 0) o[key] = sorts;
            }
            void Named(string key, string list, Func<object, JsonObject> read)
            {
                var items = new JsonArray(Describe.List(db, list).Select(x => (JsonNode?)read(x)).ToArray());
                if (items.Count > 0) o[key] = items;
            }
            Texts("filters", "Filters", "Condition");
            Sorts("sorts", "Sorts");
            Named("groupBy", "GroupByAttributes", a => new JsonObject { ["attribute"] = Describe.Expr(a, "Attribute"), ["as"] = Describe.Name(a) });
            Named("aggregates", "AggregateAttributes", a =>
            {
                var function = Reflect.Get(a, "AggregationType")?.ToString() ?? "";
                if (!new[] { "count", "sum", "avg", "min", "max" }.Contains(function.ToLowerInvariant()))
                    unsupported($"its output {Describe.Name(a)} uses the function '{function}', which a spec does not take (Count, Sum, Avg, Min, Max)");
                return new JsonObject { ["name"] = Describe.Name(a), ["function"] = function, ["attribute"] = Describe.Expr(a, "Attribute") };
            });
            Named("calculated", "CalculatedAttributes", a => new JsonObject { ["name"] = Describe.Name(a), ["expression"] = Describe.Expr(a, "Value") });
            Texts("groupFilters", "FiltersInGroupBy", "Condition");
            Sorts("groupSorts", "SortsInGroupBy");
            Named("groupCalculated", "CalculatedAttributesInGroupBy", a => new JsonObject { ["name"] = Describe.Name(a), ["expression"] = Describe.Expr(a, "Value") });
            if (Describe.Expr(node, "MaxRecords") is { Length: > 0 } max) o["maxRecords"] = max;
            if (GenericProps(node).FirstOrDefault(p => p.Key == "StartIndex").Value is { Length: > 0 } start && start != "0") o["startIndex"] = start;
            if (Reflect.Get(node, "Fetch")?.ToString() is { } fetch) o["fetch"] = fetch == "OnDemand" ? "onDemand" : "atStart";
            if (Describe.Handler(node, "OnAfterFetch") is { } after) o["onAfterFetch"] = Describe.Name(after);
            if (Reflect.Get(node, "CacheInMinutes") is int cache && cache > 0) o["cacheInMinutes"] = cache;
            if (Reflect.Get(node, "Timeout") is int timeout && timeout > 0) o["timeout"] = timeout;
        }

        // ---- widgets: the tree describe walks, with If branches, table rows and placeholders under their own keys ----

        JsonArray Widgets(IObjectSignature root, string rootPath)
        {
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var list = new JsonArray();
            foreach (var w in Describe.List(root, "Widgets").OfType<IObjectSignature>())
            {
                if (!visited.Add(w)) continue;
                var path = rootPath + "/" + Seg(w, root);
                if (w is IWidgetSignature) list.Add(Widget(w, path, visited));
                else Collect(w, path, list, visited, [], new JsonObject(), new Dictionary<string, JsonArray>());
            }
            return list;
        }

        // The widgets under `parent` (not itself) go into `into`, through the parts that only hold widgets; an area of
        // the widget being read (an If branch, a table row, a placeholder) puts what it holds under that area's key.
        void Collect(IObjectSignature parent, string parentPath, JsonArray into, HashSet<object> visited,
            List<(object area, string key, string? placeholder)> areas, JsonObject placeholders, Dictionary<string, JsonArray> buckets)
        {
            foreach (var c in Unreadable.Try(() => "children of " + Unreadable.Where(parent), () => ElementPaths.Children(parent).Where(x => x is not IExpression).ToArray(), []))
            {
                if (!visited.Add(c)) continue;
                var path = parentPath + "/" + Seg(c, parent);
                var area = areas.FirstOrDefault(a => ReferenceEquals(a.area, c));
                if (area.area is not null)
                {
                    JsonArray target;
                    if (area.key == "placeholders") placeholders[area.placeholder ?? "?"] = target = new JsonArray();
                    else target = buckets[area.key];
                    Collect(c, path, target, visited, areas, placeholders, buckets);
                }
                else if (c is IWidgetSignature) into.Add(Widget(c, path, visited));
                else Collect(c, path, into, visited, areas, placeholders, buckets);
            }
        }

        JsonObject Widget(IObjectSignature w, string path, HashSet<object> visited)
        {
            var kind = ElementPaths.Kind(w);
            var o = new JsonObject { ["kind"] = kind, ["type"] = WidgetType(w) };
            if (Model.NameOf(w) is { Length: > 0 } name) o["name"] = name;
            o["path"] = path;
            void Note(string why) => Notes.Add($"{path}: {why}");

            var props = new JsonObject();
            var attrs = new JsonObject();
            var events = new JsonObject();
            object? click = null;
            foreach (var f in Unreadable.Try(() => "widget " + Unreadable.Where(w), () => Describe.WidgetFacts(w), new List<Describe.WidgetFact>()))
            {
                if (f.Group == "attr") attrs[f.Key] = f.Value;
                else if (f.Group == "event")
                    events[f.Key] = f.Source is { } handler && ArgsOf(handler, Note) is { Count: > 0 } handlerArgs
                        ? (JsonNode?)new JsonObject { ["action"] = f.Value, ["args"] = handlerArgs } : JsonValue.Create(f.Value);
                else if (f.Group == "prop" && Lower(f.Key) is { Length: > 0 } key && !props.ContainsKey(key))
                {
                    props[key] = f.Value;
                    if (f.Key == "onClick") click = f.Source;
                }
            }
            // The arguments of a block instance, or of the screen its On Click goes to.
            var args = ArgsOf(w, Note);
            if (click is not null)
                foreach (var (key, value) in ArgsOf(click, Note))
                    if (!args.ContainsKey(key)) args[key] = value?.DeepClone();
            if (click is not null && Reflect.Get(click, "Destination") is IScreenSignature screen) props["destination"] = RefName(screen);
            // Every other readable property, read generically (method, label, line count, ...).
            foreach (var (key, value) in GenericProps(w))
                if (System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z]+$") && !FactSources.Contains(key) && !props.ContainsKey(Lower(key))) props[Lower(key)] = value;
            if (click is not null && !props.ContainsKey("method"))
                foreach (var (key, value) in GenericProps(click))
                    if (key.Contains("Method", StringComparison.Ordinal)) { props["method"] = value; break; }
            if (click is not null && WebUi.IsTraditional(m) && !props.ContainsKey("method"))
                Note("the method of its On Click (Submit, Ajax Submit or Navigate) could not be read");
            // A Traditional block instance's On Notify: the action that runs when the block calls Notify (Reactive: a block event).
            if (!events.ContainsKey("Notify") && Reflect.Property(w, "OnNotify") is not null && Reflect.Get(w, "OnNotify") is { } onNotify
                && Reflect.Get(onNotify, "Destination") is IObjectSignature notifyTarget)
                events["Notify"] = ArgsOf(onNotify, Note) is { Count: > 0 } notifyArgs
                    ? new JsonObject { ["action"] = Describe.Name(notifyTarget), ["args"] = notifyArgs } : JsonValue.Create(Describe.Name(notifyTarget));
            if (props.Count > 0) o["props"] = props;
            if (attrs.Count > 0) o["attrs"] = attrs;
            if (args.Count > 0) o["args"] = args;
            if (events.Count > 0) o["events"] = events;
            if (Reflect.Get(w, "SourceBlock") is { } block) o["sourceBlock"] = RefName(block);

            // Content: an If's branches, a table's header and row, a block instance's placeholders, else children.
            var areas = new List<(object area, string key, string? placeholder)>();
            void Area(object? area, string key, string? placeholder = null) { if (area is not null) areas.Add((area, key, placeholder)); }
            Area(Reflect.Get(w, "TrueBranch"), "true");
            Area(Reflect.Get(w, "FalseBranch"), "false");
            if (kind.Contains("Table", StringComparison.Ordinal)) { Area(Reflect.Get(w, "HeaderRow"), "header"); Area(Reflect.Get(w, "DataRow") ?? Reflect.Get(w, "Row"), "row"); }
            foreach (var slot in Describe.List(w, "PlaceholdersContent")) Area(slot, "placeholders", Describe.Name(Reflect.Get(slot, "Placeholder")));
            var children = new JsonArray();
            var placeholders = new JsonObject();
            var buckets = new Dictionary<string, JsonArray> { ["true"] = new(), ["false"] = new(), ["header"] = new(), ["row"] = new() };
            Collect(w, path, children, visited, areas, placeholders, buckets);
            if (children.Count > 0) o["children"] = children;
            foreach (var (key, bucket) in buckets) if (bucket.Count > 0) o[key] = bucket;
            if (placeholders.Count > 0) o["placeholders"] = placeholders;
            if (kind.Contains("If", StringComparison.Ordinal) && !areas.Any(a => a.key is "true" or "false") && children.Count > 0)
                Note("its True and False branches could not be told apart: the widgets of both are under children");
            if (kind.EndsWith("TableRecords", StringComparison.Ordinal) && !areas.Any(a => a.key is "header" or "row") && children.Count > 0)
                Note("its header and row could not be told apart: the widgets of both are under children");
            return o;
        }

        // The spec's widget type for this widget: a Reactive descriptor's name, else its model interface without the
        // Web or Mobile prefix and the Widget suffix (WebBlockInstanceWidget -> BlockInstance).
        static string WidgetType(IObjectSignature w)
        {
            var kind = ElementPaths.Kind(w);
            if (kind != w.GetType().Name) return kind;
            var type = ReviewModel.Kind(w);
            if (type.EndsWith("Widget", StringComparison.Ordinal) && type.Length > 6) type = type[..^6];
            foreach (var prefix in new[] { "Web", "Mobile" })
                if (type.StartsWith(prefix, StringComparison.Ordinal) && type.Length > prefix.Length) return type[prefix.Length..];
            return type is "Web" or "Mobile" ? kind : type;   // an interface named only by its prefix: the kind names the widget (Image)
        }
    }
}
