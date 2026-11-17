using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// The screen translator of `oml migrate`: a PURE function from the structured export of one Traditional Web screen, web block or
// email (`oml export`, contract in the migrate design notes) to the spec entry of the same thing in a Reactive Web module, plus an
// honest list of what is left to do. Nothing here reads a file, starts a process or touches a model. The rules are the owner's: build
// what can be built without guessing, build a stub where a part cannot be translated, and say so in one plain Todo sentence that
// names the element and the widget or action. A spec key is only emitted when skill/reference/spec.md has it.
static class ScreenTranslator
{
    // What the translator needs to know about the migration as a whole.
    internal sealed record Context(
        string OldModule,                                   // name of the old module, for "OldModule.Wrapper" calls
        IReadOnlyDictionary<string, string> Wrappers,       // entity action name ("CreateOrUpdateOrder") -> wrapper name ("Order_CreateOrUpdate")
        IReadOnlySet<string> ClientVariables,               // session variables that become client variables (Session.X -> Client.X)
        IReadOnlySet<string> LocalBlocks,                   // web blocks of the old module (they are translated too, same name)
        string? Layout,                                     // Reactive layout block to use, or null for none
        IReadOnlyList<string> LayoutPlaceholders);          // its placeholder names

    internal sealed record Result(
        JsonObject? Spec,                                   // one entry for the spec's `screens` or `blocks` list; null when nothing could be built
        string Kind,                                        // "Screen" | "Block" | "Email"
        string Status,                                      // "translated" (nothing left) | "partial" (built, with TODO stubs) | "worklist" (not built)
        List<string> Todo,                                  // what a person or agent must still do, one plain sentence each
        List<string> EntityActions,                         // entity actions its logic calls ("CreateOrUpdateOrder"), for the wrapper list
        List<string> SessionVariables);                     // session variables it reads or writes

    internal static Result Translate(JsonObject element, Context context)
    {
        try { return new Run(element, context).Go(); }
        catch (Exception e) when (e is InvalidOperationException or InvalidCastException or NullReferenceException or ArgumentException or FormatException or KeyNotFoundException)
        {
            // An export that does not follow the contract is not a crash of the whole migration: it is a worklist entry.
            var kind = Run.S(element["kind"]) is { Length: > 0 } k ? k : "Screen";
            return new Result(null, kind, "worklist", [$"{kind} {Run.S(element["name"]) ?? "?"}: the export could not be read ({e.Message}); rebuild it by hand from oml describe."], [], []);
        }
    }

    // ---------- one translation ----------

    sealed class Run
    {
        readonly JsonObject el;
        readonly Context cx;
        readonly string name, kindWord;
        readonly bool block;
        readonly List<string> todo = [], entityActions = [], session = [];
        readonly HashSet<string> seen = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> forms = new(StringComparer.OrdinalIgnoreCase);    // Form name -> its record local
        readonly Dictionary<string, string> tables = new(StringComparer.OrdinalIgnoreCase);   // table or list name -> its source ("GetOrders.List")
        readonly Dictionary<string, JsonObject> widgetByName = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> aggregates = new(StringComparer.OrdinalIgnoreCase);          // screen aggregates built from the Preparation
        readonly HashSet<string> actionNames = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> varTypes = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, JsonObject> extraLocals = new(StringComparer.OrdinalIgnoreCase);

        public Run(JsonObject element, Context context)
        {
            el = element; cx = context;
            block = string.Equals(S(el["kind"]), "Block", StringComparison.OrdinalIgnoreCase);
            kindWord = block ? "Block" : "Screen";
            name = S(el["name"]) ?? throw new InvalidOperationException("the element has no name");
        }

        // ---------- small helpers ----------

        internal static string? S(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString().Trim('"')) : null;
        static string Norm(string? s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);
        static bool IsTrue(string? v) => v is not null && v.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
        static bool IsFalse(string? v) => Blank(v) || v!.Trim().Equals("False", StringComparison.OrdinalIgnoreCase);
        static string Leaf(string s) => s[(s.LastIndexOfAny(['.', '/']) + 1)..];
        static string Quote(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
        static IEnumerable<JsonObject> Objs(JsonNode? n) => n is JsonArray a ? a.OfType<JsonObject>() : [];

        void Todo(string text)
        {
            var line = $"{kindWord} {name}: {text.TrimEnd('.', ' ')}.";
            if (seen.Add(line)) todo.Add(line);
        }

        // The literal a quoted expression holds, or null when it is not a plain string literal.
        static string? Literal(string expr)
        {
            var e = expr.Trim();
            if (e.Length < 2 || e[0] != '"' || e[^1] != '"') return null;
            var inner = e[1..^1];
            for (var i = 0; i < inner.Length; i++)
                if (inner[i] == '"') { if (i + 1 < inner.Length && inner[i + 1] == '"') i++; else return null; }
            return inner.Replace("\"\"", "\"");
        }

        // ---------- expression rewriting ----------

        // Rewrites the identifiers of an expression text by scanning it like the expression lexer does: string literals, #dates# and comments are
        // copied as they are, an identifier after a dot (a member) or before a colon (a named argument) is never touched. What is rewritten:
        // Session.X -> Client.X (when X is a client variable; every X is recorded), <Form>.Record -> the form's local, <Table>.List and
        // <Table>.LineCount -> the aggregate the table shows (a Reactive table is not a data source). The result is checked with the
        // expression parser: when the original parses and the rewrite does not, the original is kept and a Todo says so.
        string Rw(string? text, string at)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var sb = new StringBuilder();
            int i = 0, n = text.Length;
            var last = '\0';
            while (i < n)
            {
                var c = text[i];
                int j;
                if (c == '"')
                {
                    j = i + 1;
                    while (j < n) { if (text[j] == '"') { if (j + 1 < n && text[j + 1] == '"') { j += 2; continue; } break; } j++; }
                    j = Math.Min(j + 1, n);
                }
                else if (c == '#') { j = text.IndexOf('#', i + 1); j = j < 0 ? n : j + 1; }
                else if (c == '/' && i + 1 < n && text[i + 1] == '/') { j = text.IndexOf('\n', i); if (j < 0) j = n; }
                else if (c == '/' && i + 1 < n && text[i + 1] == '*') { j = text.IndexOf("*/", i + 2, StringComparison.Ordinal); j = j < 0 ? n : j + 2; }
                else if (char.IsDigit(c))
                {
                    j = i; while (j < n && char.IsDigit(text[j])) j++;
                    if (j + 1 < n && text[j] == '.' && char.IsDigit(text[j + 1])) { j++; while (j < n && char.IsDigit(text[j])) j++; }
                }
                else if (char.IsLetter(c) || c == '_')
                {
                    j = i; while (j < n && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;
                    var id = text[i..j];
                    var k = j; while (k < n && text[k] == ' ') k++;
                    var member = last == '.';
                    var named = k < n && text[k] == ':' && !(k + 1 < n && text[k + 1] == '=');
                    if (!member && !named && Substitute(text, id, j, at, out var replacement, out var end)) { sb.Append(replacement); i = end; last = replacement[^1]; continue; }
                }
                else { j = i + 1; }
                sb.Append(text, i, j - i);
                if (!char.IsWhiteSpace(text[j - 1])) last = text[j - 1];
                i = j;
            }
            var result = sb.ToString();
            if (result != text && Expressions.TryParse(text).Ok && !Expressions.TryParse(result).Ok)
            {
                Todo($"the expression {text} in {at} could not be rewritten safely and was left unchanged; check it for a session variable, a form record or a table name");
                return text;
            }
            return result;
        }

        // One identifier that starts a path (not a member): the rewrite, and where the scan resumes.
        bool Substitute(string text, string id, int after, string at, out string replacement, out int end)
        {
            replacement = ""; end = after;
            if (id.Equals("Session", StringComparison.OrdinalIgnoreCase) && after < text.Length && text[after] == '.')
            {
                var m = Regex.Match(text[(after + 1)..], @"^[A-Za-z_]\w*");
                if (!m.Success) return false;
                var variable = m.Value;
                if (!session.Contains(variable, StringComparer.OrdinalIgnoreCase)) session.Add(variable);
                if (cx.ClientVariables.Any(v => v.Equals(variable, StringComparison.OrdinalIgnoreCase))) { replacement = "Client"; return true; }
                Todo($"{at} uses the session variable Session.{variable}, which is not one of the client variables, so it is left as it is; keep it on the server (read it in a data action or server action) or add it to the client variables");
                return false;
            }
            if (forms.TryGetValue(id, out var local) && text.AsSpan(after).StartsWith(".Record", StringComparison.OrdinalIgnoreCase)
                && (after + 7 >= text.Length || !(char.IsLetterOrDigit(text[after + 7]) || text[after + 7] == '_')))
            { replacement = local; end = after + 7; return true; }
            if (tables.TryGetValue(id, out var source))
            {
                if (text.AsSpan(after).StartsWith(".List", StringComparison.OrdinalIgnoreCase) && (after + 5 >= text.Length || !(char.IsLetterOrDigit(text[after + 5]) || text[after + 5] == '_')))
                { replacement = source; end = after + 5; return true; }
                if (text.AsSpan(after).StartsWith(".LineCount", StringComparison.OrdinalIgnoreCase) && (after + 10 >= text.Length || !(char.IsLetterOrDigit(text[after + 10]) || text[after + 10] == '_')))
                { replacement = source + ".Length"; end = after + 10; return true; }
            }
            return false;
        }

        // Rewrites every string inside a JSON value that is an expression (keys in `skip` hold names, not expressions).
        JsonNode? RwTree(JsonNode? n, string at, params string[] skip)
        {
            switch (n)
            {
                case JsonObject o:
                    var copy = new JsonObject();
                    foreach (var (k, v) in o) copy[k] = skip.Contains(k, StringComparer.OrdinalIgnoreCase) ? v?.DeepClone() : RwTree(v, at, skip);
                    return copy;
                case JsonArray a: return new JsonArray(a.Select(x => RwTree(x, at, skip)).ToArray());
                case JsonValue v when S(v) is { } s && v.GetValueKind() == System.Text.Json.JsonValueKind.String: return JsonValue.Create(Rw(s, at));
                default: return n?.DeepClone();
            }
        }

        // ---------- the entry point ----------

        public Result Go()
        {
            var kind = S(el["kind"]) ?? "Screen";
            if (el["error"] is not null) return Worklist(kind, $"{kind} {name}: the export failed ({S(el["error"])}); rebuild it by hand from oml describe.");
            var moduleKind = S(el["moduleKind"]);
            if (moduleKind is not null && !moduleKind.Equals("Traditional", StringComparison.OrdinalIgnoreCase))
                return Worklist(kind, $"{kind} {name}: it comes from a {moduleKind} module, and oml migrate translates only Traditional Web screens, blocks and emails.");
            if (kind.Equals("Email", StringComparison.OrdinalIgnoreCase))
                return new Result(null, "Email", "worklist", [$"Email {name}: build the Reactive email with the spec's emails key (subject, inputs, widgets and styleSheet) from the exported content; this version does not translate emails."], [], []);
            if (!kind.Equals("Screen", StringComparison.OrdinalIgnoreCase) && !block)
                return Worklist(kind, $"{kind} {name}: only screens, web blocks and emails can be translated.");

            Collect(el["widgets"]);
            foreach (var a in Objs(el["actions"])) if (S(a["name"]) is { } an) actionNames.Add(an);
            foreach (var v in Objs(el["inputs"]).Concat(Objs(el["locals"]))) if (S(v["name"]) is { } vn) varTypes[vn] = S(v["type"]) ?? "Text";
            foreach (var note in el["notes"] as JsonArray ?? []) if (S(note) is { Length: > 0 } text) Todo($"the export could read it only in part: {text}");

            var spec = new JsonObject { ["name"] = name, ["flow"] = S(el["flow"]) ?? (block ? "Common" : "MainFlow") };
            if (!block && S(el["title"]) is { Length: > 0 } title) spec["title"] = Rw(title, "the title");
            if (Vars(el["inputs"], true) is { Count: > 0 } inputs) spec["inputs"] = inputs;
            var locals = Vars(el["locals"], false);

            if (!block && el["access"] is JsonObject access) spec["access"] = Access(access);
            var aggs = Preparation(el["preparation"]);

            var actions = new JsonArray();
            foreach (var a in Objs(el["actions"])) actions.Add(Action(a));

            var content = Content(spec);
            foreach (var l in extraLocals.Values) locals.Add(l);
            if (locals.Count > 0) spec["locals"] = locals;
            if (aggs.Count > 0) spec["aggregates"] = aggs;
            if (actions.Count > 0) spec["actions"] = actions;
            foreach (var (key, value) in content) spec[key] = value;
            if (S(el["styleSheet"]) is { Length: > 0 } css) spec["styleSheet"] = css;
            if (S(el["javaScript"]) is { Length: > 0 }) Todo("it has its own JavaScript, which is not carried over: add it with the spec's scripts and requiredScripts keys");

            return new Result(spec, kindWord, todo.Count == 0 ? "translated" : "partial", todo, entityActions, session);
        }

        Result Worklist(string kind, string line) => new(null, kind, "worklist", [line], [], []);

        // ---------- skeleton parts ----------

        JsonArray Vars(JsonNode? list, bool input)
        {
            var result = new JsonArray();
            foreach (var v in Objs(list))
            {
                var o = new JsonObject { ["name"] = S(v["name"]), ["type"] = S(v["type"]) ?? "Text" };
                if (input && v["mandatory"] is JsonValue m && m.TryGetValue<bool>(out var mandatory)) o["mandatory"] = mandatory;
                if (S(v["default"]) is { Length: > 0 } d) o["default"] = Rw(d, $"the default of {S(v["name"])}");
                result.Add(o);
            }
            return result;
        }

        static JsonObject Access(JsonObject a)
        {
            var o = new JsonObject
            {
                ["anonymous"] = a["anonymous"] is JsonValue an && an.TryGetValue<bool>(out var x) && x,
                ["registered"] = a["registered"] is JsonValue re && re.TryGetValue<bool>(out var y) && y,
            };
            if (a["roles"] is JsonArray { Count: > 0 } roles) o["roles"] = new JsonArray(roles.Select(r => (JsonNode?)JsonValue.Create(S(r))).ToArray());
            return o;
        }

        // Rule 2: a Preparation of aggregates only becomes screen aggregates of the same names; anything else is not translated.
        JsonArray Preparation(JsonNode? preparation)
        {
            var result = new JsonArray();
            if (preparation is not JsonObject p) return result;
            var nodes = Objs(p["nodes"]).ToList();
            if (nodes.Count == 0) return result;
            var complete = !(p["complete"] is JsonValue c && c.TryGetValue<bool>(out var b) && !b);
            if (complete && nodes.All(n => Norm(S(n["kind"])) == "aggregate"))
            {
                foreach (var n in nodes)
                {
                    var a = new JsonObject();
                    foreach (var key in new[] { "name", "sources", "joins", "filters", "sorts", "groupBy", "aggregates", "calculated", "groupFilters", "groupSorts", "groupCalculated", "maxRecords", "startIndex", "timeout" })
                        if (n[key] is { } v) a[key] = RwTree(v, $"the aggregate {S(n["name"])}", "name", "sources", "type", "function", "as", "left", "right");
                    result.Add(a);
                    if (S(n["name"]) is { } an) aggregates.Add(an);
                }
                return result;
            }
            var what = Summary(nodes);
            Todo(complete
                ? $"its Preparation does more than fetch data ({what}); lift it into one data action that calls one server action (skill/reference/traditional-to-reactive.md) and set the screen's data from its outputs, and re-create the aggregates its widgets read"
                : $"its Preparation could not be read completely ({what}); lift it into one data action that calls one server action (skill/reference/traditional-to-reactive.md)");
            return result;
        }

        static string Summary(List<JsonObject> nodes)
        {
            var parts = nodes.Take(10).Select(n =>
            {
                var kind = S(n["kind"]) ?? "node";
                var target = S(n["action"]) ?? S(n["screen"]) ?? S(n["_ref"]);
                var nm = S(n["name"]);
                return string.Join(' ', new[] { kind, nm, target is not null && target != nm ? $"({target})" : null }.Where(x => !Blank(x)));
            });
            return string.Join(", ", parts) + (nodes.Count > 10 ? $", and {nodes.Count - 10} more" : "");
        }

        // ---------- screen and block actions ----------

        static readonly HashSet<string> ActionKinds = ["assign", "if", "switch", "foreach", "execute", "destination", "raiseexception", "message", "comment", "end", "ajaxrefresh"];

        JsonObject Action(JsonObject a)
        {
            var an = S(a["name"]) ?? "Action";
            var (flow, why) = ActionFlow(an, a);
            var o = new JsonObject { ["name"] = an };
            if (Vars(a["inputs"], true) is { Count: > 0 } i) o["inputs"] = i;
            if (Vars(a["outputs"], false) is { Count: > 0 } outs) o["outputs"] = outs;
            if (Vars(a["locals"], false) is { Count: > 0 } l) o["locals"] = l;
            if (flow is null)
            {
                o["flow"] = new JsonArray(new JsonObject { ["kind"] = "comment", ["text"] = $"TODO migrate: {why}" });
                var nodes = Objs((a["flow"] as JsonObject)?["nodes"]).ToList();
                var did = nodes.Count > 0 ? $"; the original did: {Summary(nodes)}" : "";
                var notify = nodes.Any(IsNotify);
                Todo(block && notify
                    ? $"the action {an} calls Notify, which Reactive does not have; create a block event (the spec's events key, for example OnNotify), fire it in this action with a triggerEvent node and handle it on every instance with events{did}"
                    : $"the action {an} was created as a stub because {why}{did}");
            }
            else o["flow"] = flow;
            return o;
        }

        static bool IsNotify(JsonObject n) => Norm(S(n["kind"])) == "execute" && Leaf(S(n["action"]) ?? S(n["_ref"]) ?? "") is var l && (l.Equals("Notify", StringComparison.OrdinalIgnoreCase) || l.Equals("Deprecated_Notify", StringComparison.OrdinalIgnoreCase));

        (JsonArray? flow, string? why) ActionFlow(string an, JsonObject a)
        {
            if (a["flow"] is not JsonObject f) return (null, "its flow could not be read from the export");
            var nodes = Objs(f["nodes"]).ToList();
            var unsupported = Objs(f["unsupported"]).ToList();
            var complete = !(f["complete"] is JsonValue c && c.TryGetValue<bool>(out var b) && !b);
            if (unsupported.FirstOrDefault(u => Norm(S(u["kind"])) != "ajaxrefresh") is { } bad)
                return (null, $"it contains {S(bad["kind"])} ({S(bad["why"]) ?? "unsupported"}) at {S(bad["path"])}");
            if (!complete && unsupported.Count == 0) return (null, "the export marks its flow as incomplete");
            foreach (var n in nodes)
            {
                var kind = Norm(S(n["kind"]));
                var nn = S(n["name"]) ?? S(n["kind"]);
                if (!ActionKinds.Contains(kind))
                    return (null, kind is "aggregate" or "sql" ? $"it contains the {S(n["kind"])} node {nn}, which needs a server action" : $"it contains a {S(n["kind"])} node ({nn}) that has no client equivalent here");
                if (IsNotify(n)) return (null, $"it calls Notify at {nn}");
                if (kind == "destination" && (S(n["screen"]) is not { Length: > 0 } scr || scr.Contains('.')))
                    return (null, $"its Destination {nn} does not name a screen of this module");
            }
            if (nodes.Any(n => Norm(S(n["kind"])) == "ajaxrefresh")) nodes = DropRefreshes(nodes);
            var result = new JsonArray();
            foreach (var n in nodes) result.Add(Node(n, an));
            return (result, null);
        }

        // Ajax Refresh nodes: a table or list fed by a screen aggregate gets refreshData nodes in its place; any other refresh is removed and
        // its incoming connectors join its outgoing one. Every default "next" is made explicit first, so removing a node cannot change the path.
        List<JsonObject> DropRefreshes(List<JsonObject> source)
        {
            var nodes = source.Select(n => (JsonObject)n.DeepClone()).ToList();
            static bool Defaults(JsonObject n) => Norm(S(n["kind"])) is "assign" or "execute" or "message" or "foreach" or "ajaxrefresh";
            string? Following(int index) => nodes.Skip(index + 1).FirstOrDefault(x => Norm(S(x["kind"])) != "comment") is { } nx ? S(nx["name"]) : null;
            for (var i = 0; i < nodes.Count; i++)
                if (Defaults(nodes[i]) && nodes[i]["next"] is null && Following(i) is { } follow) nodes[i]["next"] = follow;
            foreach (var r in nodes.Where(n => Norm(S(n["kind"])) == "ajaxrefresh").ToList())
            {
                var rName = S(r["name"]);
                var target = S(r["next"]) ?? "End";
                var refreshes = RefreshTargets(r).Select((agg, k) => new JsonObject { ["kind"] = "refreshData", ["name"] = k == 0 ? rName : $"{rName}_{k + 1}", ["dataSource"] = agg }).ToList();
                var at = nodes.IndexOf(r);
                if (refreshes.Count > 0)
                {
                    for (var k = 0; k < refreshes.Count; k++) refreshes[k]["next"] = k + 1 < refreshes.Count ? S(refreshes[k + 1]["name"]) : target;
                    nodes.RemoveAt(at); nodes.InsertRange(at, refreshes);
                    continue;
                }
                foreach (var n in nodes)
                {
                    foreach (var key in new[] { "next", "true", "false", "cycle", "otherwise" }) if (S(n[key]) == rName) n[key] = target;
                    if (n["cases"] is JsonArray cs) foreach (var cse in Objs(cs)) if (S(cse["next"]) == rName) cse["next"] = target;
                }
                nodes.RemoveAt(at);
                if (at == 0 && nodes.FindIndex(n => S(n["name"]) == target) is var t and > 0) { var first = nodes[t]; nodes.RemoveAt(t); nodes.Insert(0, first); }
            }
            return nodes;
        }

        // The aggregates an Ajax Refresh refreshes: those that feed a table or list in the refreshed widget (or the widget itself).
        IEnumerable<string> RefreshTargets(JsonObject refresh)
        {
            JsonObject? widget = null;
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "kind", "name", "next", "_path" };
            IEnumerable<string?> candidates = refresh.Where(kv => !skip.Contains(kv.Key) && kv.Key != "_reads").SelectMany(kv => kv.Value is JsonArray a ? a.Select(S) : new[] { S(kv.Value) })
                .Concat((refresh["_reads"] as JsonArray ?? []).Select(S));
            foreach (var cand in candidates)
                if (!Blank(cand) && (widgetByName.TryGetValue(cand!, out widget) || widgetByName.TryGetValue(Leaf(cand!), out widget))) break;
            if (widget is null) return [];
            var found = new List<string>();
            void Walk(JsonObject w)
            {
                if (S(w["name"]) is { } wn && tables.TryGetValue(wn, out var src) && Regex.Match(src, @"^(\w+)\.List$") is { Success: true } m && aggregates.Contains(m.Groups[1].Value) && !found.Contains(m.Groups[1].Value)) found.Add(m.Groups[1].Value);
                foreach (var child in Children(w)) Walk(child);
            }
            Walk(widget);
            return found;
        }

        static readonly string[] Connectors = ["next", "true", "false", "cycle", "otherwise"];

        // One node of a translatable client flow, in the spec's own shape (keys of that kind only, no `_` facts).
        JsonObject Node(JsonObject n, string an)
        {
            var kind = Norm(S(n["kind"]));
            var at = $"the action {an}";
            var o = new JsonObject();
            void Copy(params string[] keys) { foreach (var k in keys) if (n[k] is { } v && S(v) is not { Length: 0 }) o[k] = v.DeepClone(); }
            void Expr(string key) { if (S(n[key]) is { } s) o[key] = Rw(s, at); }
            void Args()
            {
                if (n["args"] is JsonObject args && args.Count > 0) o["args"] = RwTree(args, at);
            }
            switch (kind)
            {
                case "assign":
                    o["kind"] = "assign"; Copy("name");
                    var map = new JsonObject();
                    foreach (var (k, v) in n["assign"] as JsonObject ?? []) map[Rw(k, at)] = Rw(S(v), at);
                    o["assign"] = map; Copy("next");
                    break;
                case "if": o["kind"] = "if"; Copy("name"); Expr("condition"); Copy("true", "false"); break;
                case "switch":
                    o["kind"] = "switch"; Copy("name");
                    var cases = new JsonArray();
                    foreach (var c in Objs(n["cases"])) cases.Add(new JsonObject { ["condition"] = Rw(S(c["condition"]), at), ["next"] = S(c["next"]) });
                    o["cases"] = cases; Copy("otherwise");
                    break;
                case "foreach": o["kind"] = "forEach"; Copy("name"); Expr("recordList"); Copy("cycle", "next"); break;
                case "execute": Execute(n, o, at); break;
                case "destination": o["kind"] = "destination"; Copy("name"); o["screen"] = Leaf(S(n["screen"])!); Args(); break;
                case "raiseexception": o["kind"] = "raiseException"; Copy("name", "exception"); Expr("message"); break;
                case "message": o["kind"] = "message"; Copy("name"); Expr("message"); Copy("type", "next"); if (o["type"] is null) o["type"] = "info"; break;
                case "comment": o["kind"] = "comment"; o["text"] = S(n["text"]) ?? ""; break;
                case "end": o["kind"] = "end"; Copy("name"); break;
                case "refreshdata": o["kind"] = "refreshData"; Copy("name", "dataSource", "next"); Args(); break;
            }
            return o;
        }

        static readonly Regex EntityAction = new(@"^(CreateOrUpdate|Create|Update|Delete)([A-Za-z][A-Za-z0-9_]*)$", RegexOptions.CultureInvariant);

        void Execute(JsonObject n, JsonObject o, string at)
        {
            var action = S(n["action"]) ?? S(n["_ref"]) ?? "";
            var nodeName = S(n["name"]);
            var leaf = Leaf(action);
            if (leaf.Equals("Feedback_Message", StringComparison.OrdinalIgnoreCase))
            {
                var args = n["args"] as JsonObject;
                string? Arg(string key) => args?.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value is { } v ? S(v) : null;
                var type = MessageType(Arg("Type"));
                o["kind"] = "message";
                if (nodeName is not null) o["name"] = nodeName;
                o["message"] = Rw(Arg("Message") ?? "\"\"", at);
                o["type"] = type ?? "info";
                if (type is null && Arg("Type") is { } raw) Todo($"{at} shows a message whose type {raw} is not Info, Success, Warning or Error, so it was built as info; set the type");
                if (S(n["next"]) is { } nx) o["next"] = nx;
                return;
            }
            o["kind"] = "execute";
            if (nodeName is not null) o["name"] = nodeName;
            var argKeys = (n["args"] as JsonObject)?.Select(kv => kv.Key).ToList() ?? [];
            var wrapper = cx.Wrappers.FirstOrDefault(kv => kv.Key.Equals(action, StringComparison.OrdinalIgnoreCase));
            var m = EntityAction.Match(action);
            var isEntity = wrapper.Key is not null
                || (m.Success && !action.Contains('.') && argKeys.Count == 1 && (m.Groups[1].Value == "Delete" ? argKeys[0] == "Id" : argKeys[0] == "Source"));
            if (isEntity)
            {
                var entityName = wrapper.Key ?? action;
                if (!entityActions.Contains(entityName, StringComparer.OrdinalIgnoreCase)) entityActions.Add(entityName);
                if (wrapper.Key is not null) o["action"] = $"{cx.OldModule}.{wrapper.Value}";
                else
                {
                    o["action"] = action;
                    Todo($"{at} calls the entity action {action}, which a Reactive module does not have and for which no wrapper is known; add the wrapper {m.Groups[2].Value}_{m.Groups[1].Value} to the old module and call it as {cx.OldModule}.{m.Groups[2].Value}_{m.Groups[1].Value}");
                }
                o["name"] = nodeName ?? action;
            }
            else o["action"] = action;
            if (n["args"] is JsonObject args2 && args2.Count > 0) o["args"] = RwTree(args2, at);
            if (S(n["next"]) is { } next) o["next"] = next;
        }

        static string? MessageType(string? expr)
        {
            if (expr is null) return null;
            var t = (Literal(expr) ?? Leaf(expr.Trim())).Trim().ToLowerInvariant();
            return t is "info" or "success" or "warning" or "error" ? t : null;
        }

        // ---------- widgets ----------

        static IEnumerable<JsonObject> Children(JsonObject w)
        {
            foreach (var key in new[] { "children", "true", "false", "header", "row" }) foreach (var c in Objs(w[key])) yield return c;
            if (w["placeholders"] is JsonObject ph) foreach (var (_, list) in ph) foreach (var c in Objs(list)) yield return c;
        }

        // Before anything is translated: names that rewrites and refreshes need (forms, tables and lists, every widget by name).
        void Collect(JsonNode? list)
        {
            foreach (var w in Objs(list))
            {
                var wn = S(w["name"]);
                var kind = Norm(S(w["kind"]));
                if (!Blank(wn))
                {
                    widgetByName[wn!] = w;
                    if (kind is "form" or "editrecord" or "showrecord") forms[wn!] = wn + "Record";
                    if (kind is "tablerecords" or "listrecords" && Prop(w, "source", "sourceRecordList", "recordList") is { } src && Regex.IsMatch(src, @"^\w+\.List$")) tables[wn!] = src;
                }
                foreach (var c in Children(w)) Collect(new JsonArray(c.DeepClone()));
            }
        }

        // A property of the widget as text (the first of the names with a value, case-insensitive).
        static string? Prop(JsonObject w, params string[] names) => Prop(w, null, names);

        static string? Prop(JsonObject w, HashSet<string>? used, params string[] names)
        {
            if (w["props"] is not JsonObject p) return null;
            foreach (var nm in names) foreach (var kv in p) if (kv.Key.Equals(nm, StringComparison.OrdinalIgnoreCase)) used?.Add(kv.Key.ToLowerInvariant());
            foreach (var nm in names) foreach (var kv in p) if (kv.Key.Equals(nm, StringComparison.OrdinalIgnoreCase) && S(kv.Value) is { } s && !Blank(s)) return s;
            return null;
        }

        string Label(JsonObject w)
        {
            var kind = S(w["kind"]) ?? "widget";
            return !Blank(S(w["name"])) ? $"the widget {S(w["name"])} ({kind})" : S(w["path"]) is { Length: > 0 } p ? $"the {kind} widget at {p}" : $"a {kind} widget";
        }

        // Properties that were read but have no key in the spec: one Todo per widget.
        void Unhandled(JsonObject w, HashSet<string> used)
        {
            if (w["props"] is not JsonObject p) return;
            var left = p.Where(kv => !used.Contains(kv.Key.ToLowerInvariant()) && !Blank(S(kv.Value))).Select(kv => kv.Key).ToList();
            if (left.Count > 0) Todo($"{Label(w)} has the properties {string.Join(", ", left)}, which were not carried over because the spec has no key for them");
        }

        JsonObject New(string type, JsonObject w, HashSet<string> used)
        {
            var o = new JsonObject { ["type"] = type };
            if (!Blank(S(w["name"]))) o["name"] = S(w["name"]);
            var at = Label(w);
            if (Prop(w, used, "style", "styleClasses") is { } style) o["style"] = Rw(style, at);
            var attrs = new JsonObject();
            foreach (var (k, v) in w["attrs"] as JsonObject ?? [])
            {
                if (k.Equals("id", StringComparison.OrdinalIgnoreCase)) { Todo($"{at} has an HTML attribute id, which Reactive refuses (the platform sets it); use the widget name in expressions instead"); continue; }
                if (S(v) is { } s) attrs[k] = Rw(s, at);
            }
            if (Prop(w, used, "inlineStyle") is { } inline) attrs["style"] = Rw(inline, at);
            if (attrs.Count > 0) o["attributes"] = attrs;
            return o;
        }

        void Visible(JsonObject w, HashSet<string> used, JsonObject o)
        {
            if (Prop(w, used, "visible", "display") is { } v && !IsTrue(v)) o["visible"] = Rw(v, Label(w));
            if (Prop(w, used, "enabled") is { } e && !IsTrue(e)) o["enabled"] = Rw(e, Label(w));
        }

        // Expression, Text and BlockInstance have no Visible in Reactive: a Container carries it.
        JsonObject Wrapped(JsonObject w, HashSet<string> used, JsonObject o)
        {
            if (Prop(w, used, "visible", "display") is { } v && !IsTrue(v))
                return new JsonObject { ["type"] = "Container", ["visible"] = Rw(v, Label(w)), ["children"] = new JsonArray(o) };
            return o;
        }

        JsonObject TodoBox(JsonObject w, string what, bool withChildren = true)
        {
            Todo($"{Label(w)} {what}");
            var children = new JsonArray(new JsonObject { ["type"] = "Text", ["text"] = $"TODO migrate: {Label(w)} {what}" });
            if (withChildren) foreach (var c in Children(w)) One(c, children);
            var o = new JsonObject { ["type"] = "Container" };
            if (!Blank(S(w["name"]))) o["name"] = S(w["name"]);
            o["children"] = children;
            return o;
        }

        // A widget label (a Text or an Expression) from an expression text.
        static JsonObject LabelWidget(string expr, Func<string, string> rewrite) =>
            Literal(expr) is { } lit ? new JsonObject { ["type"] = "Text", ["text"] = lit } : new JsonObject { ["type"] = "Expression", ["value"] = rewrite(expr) };

        JsonArray Ws(JsonNode? widgets) { var a = new JsonArray(); foreach (var w in Objs(widgets)) One(w, a); return a; }

        // A name of an action of this element for an event (onClick, onChange), or null when there is none.
        string? ActionRef(JsonObject w, HashSet<string> used, string at, params string[] names)
        {
            var a = Prop(w, used, names);
            if (a is null) return null;
            a = Leaf(a);
            if (actionNames.Contains(a)) return actionNames.First(x => x.Equals(a, StringComparison.OrdinalIgnoreCase));
            Todo($"{Label(w)} calls {a} from {at}, but this {kindWord.ToLowerInvariant()} has no action of that name");
            return null;
        }

        void One(JsonObject w, JsonArray into)
        {
            var used = new HashSet<string>();
            var kind = Norm(S(w["kind"]));
            JsonObject? o;
            switch (kind)
            {
                case "container":
                    o = New("Container", w, used); Visible(w, used, o);
                    if (Ws(w["children"]) is { Count: > 0 } kids) o["children"] = kids;
                    break;
                case "expression":
                    o = New("Expression", w, used);
                    o["value"] = Rw(Prop(w, used, "value", "expression") ?? "\"\"", Label(w));
                    if (Prop(w, used, "escapeContent", "escape") is { } esc && (esc.Trim().Equals("No", StringComparison.OrdinalIgnoreCase) || esc.Trim().Equals("False", StringComparison.OrdinalIgnoreCase)))
                        Todo($"{Label(w)} has escaping turned off, so it shows unescaped content (an XSS risk) and was built as an ordinary Expression; sanitize the content or build an unescaped expression block by hand");
                    o = Wrapped(w, used, o);
                    break;
                case "text":
                    o = New("Text", w, used); o["text"] = Prop(w, used, "text", "value") ?? ""; o = Wrapped(w, used, o);
                    break;
                case "label":
                    o = New("Label", w, used);
                    o["children"] = new JsonArray(Prop(w, used, "text") is { } lt ? new JsonObject { ["type"] = "Text", ["text"] = lt } : LabelWidget(Prop(w, used, "value", "label") ?? "\"\"", x => Rw(x, Label(w))));
                    if (Prop(w, used, "inputWidget", "targetWidget", "for") is { } target)
                    {
                        if (widgetByName.ContainsKey(target)) o["targetWidget"] = target;
                        else Todo($"{Label(w)} describes the input {target}, which is not a widget of this {kindWord.ToLowerInvariant()}; set its targetWidget");
                    }
                    break;
                case "input": case "textarea": o = Input(w, used, kind == "textarea"); break;
                case "button": case "link": o = Click(w, used, kind == "button"); break;
                case "if": o = If(w, used); break;
                case "image":
                    o = New("Image", w, used);
                    if (Prop(w, used, "image", "resource", "imageName") is { } image)
                    { o["image"] = image; Todo($"{Label(w)} shows the image {image}, which must exist in the new module (images are not copied by oml migrate)"); }
                    if (Prop(w, used, "url") is { } url) Todo($"{Label(w)} shows the image at {url}, which the spec cannot set; set the source in Service Studio");
                    if (Prop(w, used, "alt", "altText", "alternativeText") is { } alt) o["attributes"] = new JsonObject { ["alt"] = Rw(alt, Label(w)) };
                    Visible(w, used, o);
                    break;
                case "icon":
                    o = New("Icon", w, used); Visible(w, used, o);
                    if (Prop(w, used, "icon", "name", "iconName") is { } icon) Todo($"{Label(w)} shows the icon {icon}, which the spec cannot set; choose it in Service Studio");
                    break;
                case "placeholder": o = New("Placeholder", w, used); break;
                case "upload":
                    o = New("Upload", w, used); Visible(w, used, o);
                    if (Prop(w, used, "fileContent", "variable") is { } fc) o["fileContent"] = Rw(fc, Label(w));
                    if (Prop(w, used, "fileName", "filename") is { } fnm) o["fileName"] = Rw(fnm, Label(w));
                    if (ActionRef(w, used, "its On Change", "onChange", "onUpload") is { } up) o["onChange"] = up;
                    break;
                case "checkbox":
                    o = New("Checkbox", w, used); Visible(w, used, o);
                    if (Prop(w, used, "variable") is { } cv) o["variable"] = Rw(cv, Label(w));
                    if (ActionRef(w, used, "its On Change", "onChange") is { } cc) o["onChange"] = cc;
                    break;
                case "combobox": o = Combo(w, used); break;
                case "radiobutton": case "radio":
                    o = TodoBox(w, $"is a Radio Button (variable {Prop(w, "variable") ?? "none"}, value {Prop(w, "value", "valueText") ?? "none"}); the spec has no Radio Group widget, so build the group in Service Studio", false);
                    used.UnionWith(["variable", "value", "valuetext", "style", "visible", "enabled", "display", "onchange", "mandatory"]);
                    break;
                case "tablerecords": o = Records(w, used, true); break;
                case "listrecords": o = Records(w, used, false); break;
                case "blockinstance": case "webblock": case "block": o = Instance(w, used); break;
                case "form": case "editrecord": case "showrecord": o = Form(w, used); break;
                case "popup": o = Popup(w, used, w["children"]); break;
                case "editabletable": o = TodoBox(w, "is an Editable Table, which Reactive does not have; rebuild it as a Table with inputs"); break;
                case "listbox": o = TodoBox(w, "is a List Box, which Reactive does not have; rebuild it as a list of Checkboxes"); break;
                case "comment": return;
                case "row": case "cell": case "headercell": case "rowcell":
                    foreach (var c in Objs(w["children"])) One(c, into);   // a Reactive Form has no rows and cells: the content moves up
                    return;
                default:
                    o = TodoBox(w, $"is a {S(w["kind"])} widget that has no Reactive translation here; rebuild it (its content is kept inside)");
                    used.UnionWith(w["props"] is JsonObject props ? props.Select(kv => kv.Key.ToLowerInvariant()) : []);
                    break;
            }
            if (kind is not ("editabletable" or "listbox" or "radiobutton" or "radio" or "popup")) Unhandled(w, used);
            into.Add(o);
        }

        // Rule 7: mandatory and the input type are carried; other validation types and messages are not.
        JsonObject Input(JsonObject w, HashSet<string> used, bool area)
        {
            var at = Label(w);
            var o = New(area ? "TextArea" : "Input", w, used); Visible(w, used, o);
            if (Prop(w, used, "variable") is { } v) o["variable"] = Rw(v, at);
            if (Prop(w, used, "mandatory") is { } m && !IsFalse(m)) o["mandatory"] = Rw(m, at);
            if (Prop(w, used, "prompt", "placeholder") is { } pr) o["prompt"] = Rw(pr, at);
            if (Prop(w, used, "maxLength", "length") is { } ml && int.TryParse(ml, out var len)) o["maxLength"] = len;
            if (ActionRef(w, used, "its On Change", "onChange") is { } oc) o["onChange"] = oc;
            if (!area)
            {
                string? type = null;
                foreach (var raw in new[] { Prop(w, used, "inputType", "type"), Prop(w, used, "validationType") })
                {
                    if (Blank(raw)) continue;
                    var t = Norm(raw);
                    var mapped = t switch { "password" => "Password", "email" => "Email", "number" or "integer" or "decimal" or "longinteger" => "Number", _ => null };
                    if (mapped is not null) type ??= mapped;
                    else if (t is not ("text" or "none" or "default" or "noneselected")) Todo($"{at} has the input or validation type {raw}, which the spec cannot express (it has Password, Email and Number); set it in Service Studio");
                }
                if (type is not null) o["inputType"] = type;
            }
            if (Prop(w, used, "validationMessage") is { } vm) Todo($"{at} has the validation message {vm}, which the spec cannot set on a widget; set <Input>.ValidationMessage in the client action instead");
            return o;
        }

        // Rule 5: Button and Link. Navigate -> onClick destination with args; Submit and AjaxSubmit -> onClick calls the client action.
        JsonObject Click(JsonObject w, HashSet<string> used, bool button)
        {
            var at = Label(w);
            var o = New(button ? "Button" : "Link", w, used); Visible(w, used, o);
            var method = Norm(Prop(w, used, "method"));
            var dest = Prop(w, used, "destination");
            var onClick = Prop(w, used, "onClick");
            var url = Prop(w, used, "url");
            var args = w["args"] as JsonObject;
            Prop(w, used, "ajaxRefresh", "ajaxRefreshTargets", "refresh");   // Reactive refreshes bound data by itself
            if (method == "") method = dest is not null && onClick is null ? "navigate" : onClick is not null ? "submit" : "";
            if (method == "navigate")
            {
                var screen = dest ?? onClick;
                if (screen is null && url is null) Todo($"{at} navigates, but the export gives no destination; set its On Click");
                else if (screen is not null && (screen.Contains('.') || Regex.IsMatch(screen, @"^\w+://")))
                    Todo($"{at} navigates to {screen}, which is not a screen of this module (a Reactive screen cannot link to a Traditional screen or a URL this way); set its On Click");
                else if (screen is not null)
                {
                    var d = new JsonObject { ["destination"] = Leaf(screen) };
                    if (args is { Count: > 0 }) d["args"] = RwTree(args, at);
                    o["onClick"] = d;
                }
                if (url is not null && screen is null) Todo($"{at} links to the external address {url}, which the spec cannot set on a Link; set it in Service Studio");
            }
            else if (method is "submit" or "ajaxsubmit")
            {
                if (ActionRef(w, used, "its On Click", "onClick", "destination") is { } action)
                {
                    o["onClick"] = action;
                    if (args is { Count: > 0 }) o["args"] = RwTree(args, at);
                }
                else if (onClick is null && dest is null) Todo($"{at} submits, but the export gives no action; set its On Click");
            }
            else Todo($"{at} has the method {method}, which has no Reactive translation here; set its On Click");
            if (Prop(w, used, "confirm") is { } confirm) Todo($"{at} asks the user to confirm with {confirm}, and the spec has no key for a confirmation; add a confirmation popup or a JavaScript confirm before its action");
            var children = Ws(w["children"]);
            if (children.Count == 0 && Prop(w, used, "label", "text", "value") is { } label) children.Add(LabelWidget(label, x => Rw(x, at)));
            if (children.Count > 0) o["children"] = children;
            return o;
        }

        JsonObject If(JsonObject w, HashSet<string> used)
        {
            var cond = Prop(w, used, "condition", "value", "expression");
            if (cond is null) return TodoBox(w, "is an If without a readable condition");
            var o = new JsonObject { ["type"] = "If", ["condition"] = Rw(cond, Label(w)) };
            if (!Blank(S(w["name"]))) o["name"] = S(w["name"]);
            o["trueBranch"] = Ws(w["true"]);
            if (Ws(w["false"]) is { Count: > 0 } f) o["falseBranch"] = f;
            return o;
        }

        // Rule 5: Combo Box -> Dropdown (list, labels and values are evaluated in the option record, not as List.Current.X.Y).
        JsonObject Combo(JsonObject w, HashSet<string> used)
        {
            var at = Label(w);
            var o = New("Dropdown", w, used); Visible(w, used, o);
            if (Prop(w, used, "variable") is { } v) o["variable"] = Rw(v, at);
            if (Prop(w, used, "mandatory") is { } m && !IsFalse(m)) o["mandatory"] = Rw(m, at);
            var source = Prop(w, used, "source", "list", "sourceList");
            if (source is not null) o["list"] = Rw(source, at);
            string Option(string expr) => Regex.Replace(Rw(expr, at), @"^\w+\.List\.Current\.", "");
            if (Prop(w, used, "valueAttribute", "optionValue", "optionsValue", "value") is { } val) o["values"] = Option(val);
            if (Prop(w, used, "labelAttribute", "optionLabel", "optionsText", "label", "text") is { } lab) o["labels"] = Option(lab);
            if (ActionRef(w, used, "its On Change", "onChange") is { } oc) o["onChange"] = oc;
            if (source is null) Todo($"{at} has no source list in the export; set its list, labels and values");
            if (Prop(w, used, "specialList", "hasSpecialList", "emptyValue", "emptyLabel", "emptyCaption", "emptyText") is { } special)
                Todo($"{at} uses a special list or Empty Value setting ({special}), which the spec cannot express; build a data action that adds the entry to the options list");
            return o;
        }

        // Rule 5: Table Records -> table, List Records -> list, with the same source, header cells and row cells.
        JsonObject Records(JsonObject w, HashSet<string> used, bool table)
        {
            var at = Label(w);
            var source = Prop(w, used, "source", "sourceRecordList", "recordList");
            if (source is null) return TodoBox(w, $"is a {(table ? "Table" : "List")} Records without a readable source");
            var o = New(table ? "TableRecords" : "List", w, used); Visible(w, used, o);
            o["source"] = Rw(source, at);
            if (table)
            {
                o["headerRow"] = Cells(w["header"], "HeaderCell");
                o["row"] = Cells(w["row"], "RowCell");
            }
            else o["children"] = Ws(w["row"] ?? w["children"]);
            return o;
        }

        JsonArray Cells(JsonNode? widgets, string cellType)
        {
            var result = new JsonArray();
            foreach (var w in Objs(widgets))
            {
                if (Norm(S(w["kind"])).EndsWith("cell"))
                {
                    var used = new HashSet<string>();
                    var cell = new JsonObject { ["type"] = cellType };
                    if (Prop(w, used, "style", "styleClasses") is { } st) cell["style"] = Rw(st, Label(w));
                    cell["children"] = Ws(w["children"]);
                    Unhandled(w, used);
                    result.Add(cell);
                }
                else result.Add(new JsonObject { ["type"] = cellType, ["children"] = Ws(new JsonArray(w.DeepClone())) });
            }
            return result;
        }

        // Rule 5 and 8: web block instances. Same name for a block of this module; a Traditional block of another module cannot be used.
        JsonObject Instance(JsonObject w, HashSet<string> used)
        {
            var at = Label(w);
            var source = S(w["sourceBlock"]) ?? Prop(w, used, "sourceBlock") ?? "";
            var dot = source.IndexOf('.');
            var local = dot > 0 && source[..dot].Equals(cx.OldModule, StringComparison.OrdinalIgnoreCase) ? source[(dot + 1)..] : source;
            if (Regex.IsMatch(Leaf(source), @"^Popup(_Editor|_Info|_Help)?$", RegexOptions.IgnoreCase) && dot > 0 && !cx.LocalBlocks.Contains(local))
            {
                var inner = new JsonArray();
                if (w["placeholders"] is JsonObject ph) foreach (var (_, list) in ph) foreach (var c in Objs(list)) inner.Add(c.DeepClone());
                return Popup(w, used, inner);
            }
            var isLocal = !local.Contains('.') && cx.LocalBlocks.Any(b => b.Equals(local, StringComparison.OrdinalIgnoreCase));
            if (!isLocal)
                return TodoBox(w, $"is an instance of {(Blank(source) ? "an unknown block" : source)}, which is not a web block of this module, and a Traditional block cannot be used in a Reactive screen; replace it with a Reactive block or widget");
            var o = new JsonObject { ["type"] = "BlockInstance" };
            if (!Blank(S(w["name"]))) o["name"] = S(w["name"]);
            o["sourceBlock"] = cx.LocalBlocks.First(b => b.Equals(local, StringComparison.OrdinalIgnoreCase));
            if (w["args"] is JsonObject args && args.Count > 0) o["args"] = RwTree(args, at);
            if (w["placeholders"] is JsonObject phs && phs.Count > 0)
            {
                var map = new JsonObject();
                foreach (var (k, list) in phs) map[k] = Ws(list);
                o["placeholders"] = map;
            }
            if (Prop(w, used, "onNotify") is { } handler)
                Todo($"{at} handles OnNotify with {handler}, which cannot be translated by rule; create an event on the block {local} (the spec's events key), fire it with triggerEvent where the block called Notify, and set events on this instance to call {handler}");
            return Wrapped(w, used, o);
        }

        // Rule 6: Edit Record, Show Record and Form become a Form with a local variable for the record.
        JsonObject Form(JsonObject w, HashSet<string> used)
        {
            var at = Label(w);
            var formName = !Blank(S(w["name"])) ? S(w["name"])! : "Form";
            var local = formName + "Record";
            var source = Prop(w, used, "source", "sourceRecord");
            var type = Prop(w, used, "recordType", "entity", "dataType");
            if (type is null && source is not null)
            {
                if (varTypes.TryGetValue(source.Trim(), out var vt)) type = vt;
                else if (Regex.Match(source.Trim(), @"^\w+\.List\.Current\.(\w+)$") is { Success: true } m) type = m.Groups[1].Value;
            }
            var l = new JsonObject { ["name"] = local, ["type"] = type ?? "Text" };
            if (source is not null && varTypes.ContainsKey(source.Trim()) && Regex.IsMatch(source.Trim(), @"^\w+$") && IsInput(source.Trim())) l["default"] = source.Trim();
            extraLocals[local] = l;
            varTypes[local] = type ?? "Text";
            if (type is null) Todo($"{at} needs the local variable {local} for its record, but the export gives no record type; set the type of {local}");
            if (source is not null && l["default"] is null)
                Todo($"{at} shows the record {source}; fill the local variable {local} from it after the data is fetched, for example in the aggregate's onAfterFetch action");
            var o = New("Form", w, used); Visible(w, used, o);
            o["children"] = Ws(w["children"]);
            return o;
        }

        bool IsInput(string variable) => Objs(el["inputs"]).Any(i => S(i["name"])?.Equals(variable, StringComparison.OrdinalIgnoreCase) == true);

        // Rule 5: Popup has a Reactive widget, shown by a Boolean local.
        JsonObject Popup(JsonObject w, HashSet<string> used, JsonNode? content)
        {
            var popupName = !Blank(S(w["name"])) ? S(w["name"])! : "Popup";
            var show = popupName + "Show";
            extraLocals[show] = new JsonObject { ["name"] = show, ["type"] = "Boolean", ["default"] = "False" };
            var o = new JsonObject { ["type"] = "Popup", ["name"] = popupName, ["showPopup"] = show };
            var children = Ws(content);
            if (children.Count > 0) o["children"] = children;
            Todo($"{Label(w)} was built as a Popup shown by the new local variable {show}; set {show} to True in the action that opens it and to False to close it, and carry over its title and size by hand");
            used.UnionWith(w["props"] is JsonObject props ? props.Select(kv => kv.Key.ToLowerInvariant()) : []);
            return o;
        }

        // ---------- top level content and layout ----------

        // The widgets of the element as spec keys: `widgets`, or `layout` with `placeholders` (rule 1).
        List<(string key, JsonNode value)> Content(JsonObject spec)
        {
            var result = new List<(string, JsonNode)>();
            var top = el["widgets"] as JsonArray ?? [];
            var groups = new List<(string ph, JsonArray widgets)>();
            var wrapper = top.Count == 1 && top[0] is JsonObject only && Norm(S(only["kind"])).Contains("blockinstance") && only["placeholders"] is JsonObject { Count: > 0 } && IsLayoutInstance(only) ? only : null;
            if (wrapper is not null) foreach (var (k, list) in (JsonObject)wrapper["placeholders"]!) groups.Add((k, list as JsonArray ?? []));
            else
            {
                if (el["placeholders"] is JsonObject given) foreach (var (k, list) in given) groups.Add((k, list as JsonArray ?? []));
                groups.Add(("", top));
            }
            if (block || cx.Layout is null)
            {
                var all = new JsonArray();
                foreach (var (_, ws) in groups) foreach (var w in Objs(ws)) One(w, all);
                if (all.Count > 0) result.Add(("widgets", all));
                return result;
            }
            var main = cx.LayoutPlaceholders.FirstOrDefault(p => Regex.IsMatch(p, @"^(MainContent|Content|Main)$", RegexOptions.IgnoreCase)) ?? "MainContent";
            var placeholders = new JsonObject();
            foreach (var (ph, ws) in groups)
            {
                var target = cx.LayoutPlaceholders.FirstOrDefault(p => p.Equals(ph, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    target = main;
                    if (ph.Length > 0 && !Regex.IsMatch(ph, @"^(MainContent|Content|Main)$", RegexOptions.IgnoreCase))
                        Todo($"the content of the layout placeholder {ph} has no placeholder of that name in the layout {cx.Layout} and was put into {main}; move it");
                }
                if (placeholders[target] is not JsonArray list) placeholders[target] = list = new JsonArray();
                foreach (var w in Objs(ws)) One(w, list);
            }
            result.Add(("layout", JsonValue.Create(cx.Layout)!));
            var filled = new JsonObject();
            foreach (var (k, v) in placeholders) if (v is JsonArray { Count: > 0 } a) filled[k] = a.DeepClone();
            if (filled.Count > 0) result.Add(("placeholders", filled));
            return result;
        }

        bool IsLayoutInstance(JsonObject w)
        {
            var source = S(w["sourceBlock"]) ?? Prop(w, "sourceBlock") ?? "";
            return S(el["layout"]) is { Length: > 0 } declared && Leaf(declared).Equals(Leaf(source), StringComparison.OrdinalIgnoreCase)
                || Leaf(source).Contains("layout", StringComparison.OrdinalIgnoreCase)
                || !cx.LocalBlocks.Any(b => b.Equals(Leaf(source), StringComparison.OrdinalIgnoreCase));
        }
    }

    // ---------- self-test ----------

    // Built-in samples that follow the export contract exactly. Each rule of the translator is exercised at least once; the failures come back.
    internal static List<string> SelfTest()
    {
        var failures = new List<string>();
        void Check(bool ok, string what) { if (!ok) failures.Add(what); }
        static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
        // A value of a spec entry by path (keys and array indexes); null when it is not there.
        static JsonNode? At(JsonNode? n, params object[] path)
        {
            foreach (var p in path)
                n = p is int i ? (n as JsonArray is { } a && i < a.Count ? a[i] : null) : (n as JsonObject)?[(string)p];
            return n;
        }
        static string? Text(JsonNode? n, params object[] path) => Run.S(At(n, path));
        static bool Has(List<string> lines, params string[] parts) => lines.Any(l => parts.All(p => l.Contains(p, StringComparison.OrdinalIgnoreCase)));

        var layout = new Context("Shop", new Dictionary<string, string> { ["CreateOrUpdateOrder"] = "Order_CreateOrUpdate" },
            new HashSet<string> { "Lang" }, new HashSet<string> { "OrderCard", "Badge" }, "LayoutTopMenu", ["Title", "Actions", "MainContent"]);
        var plain = layout with { Layout = null, LayoutPlaceholders = [] };

        // 1. A list screen: aggregate-only Preparation, layout with placeholders, a table, a link that navigates with an argument,
        //    a button that submits through Ajax, an Ajax-refreshing search action and an Ajax Refresh of a widget with no aggregate.
        var orders = Parse("""
        { "kind": "Screen", "name": "Orders", "flow": "MainFlow", "path": "MainFlow/Orders", "moduleKind": "Traditional", "title": "\"Orders\"",
          "inputs": [ { "name": "CustomerId", "type": "Customer Identifier", "mandatory": false, "default": "" } ],
          "locals": [ { "name": "Search", "type": "Text", "default": "\"\"" } ],
          "access": { "anonymous": false, "registered": true, "roles": ["OrderManager"] },
          "styleSheet": ".orders { margin: 0; }", "javaScript": "",
          "preparation": { "complete": true, "unsupported": [], "nodes": [
            { "kind": "aggregate", "name": "GetOrders", "sources": ["Order"], "filters": ["Order.Name like \"%\" + Search + \"%\""],
              "sorts": [ { "attribute": "Order.Id", "descending": true } ], "maxRecords": 50, "next": "End", "_path": "MainFlow/Orders/Preparation/GetOrders", "_reads": ["Search"] } ] },
          "actions": [
            { "name": "DoSearch", "inputs": [], "locals": [], "outputs": [], "flow": { "complete": false,
              "unsupported": [ { "path": "MainFlow/Orders/DoSearch/Refresh1", "kind": "AjaxRefresh", "why": "no spec node" } ],
              "nodes": [
                { "kind": "assign", "name": "Reset", "assign": { "Search": "Trim(Search)" }, "next": "Refresh1" },
                { "kind": "AjaxRefresh", "name": "Refresh1", "widget": "OrdersTable", "next": "End", "_path": "MainFlow/Orders/DoSearch/Refresh1" } ] } },
            { "name": "Tidy", "inputs": [], "locals": [], "outputs": [], "flow": { "complete": false,
              "unsupported": [ { "path": "p", "kind": "AjaxRefresh", "why": "no spec node" } ],
              "nodes": [
                { "kind": "assign", "name": "A", "assign": { "Search": "\"\"" }, "next": "R" },
                { "kind": "AjaxRefresh", "name": "R", "widget": "Header", "next": "B" },
                { "kind": "assign", "name": "B", "assign": { "Search": "\"x\"" }, "next": "End" } ] } } ],
          "widgets": [ { "kind": "BlockInstance", "name": "Layout", "sourceBlock": "Layouts.Main", "placeholders": {
            "Title": [ { "kind": "Text", "name": "", "props": { "text": "Orders" } } ],
            "MainContent": [
              { "kind": "Container", "name": "Header", "props": { "style": "\"header\"" }, "children": [
                { "kind": "Input", "name": "SearchInput", "props": { "variable": "Search", "onChange": "DoSearch" } },
                { "kind": "Button", "name": "SearchButton", "props": { "method": "AjaxSubmit", "onClick": "DoSearch", "ajaxRefresh": "OrdersTable" }, "children": [ { "kind": "Text", "props": { "text": "Search" } } ] },
                { "kind": "Expression", "name": "Count", "props": { "value": "OrdersTable.LineCount" } } ] },
              { "kind": "TableRecords", "name": "OrdersTable", "props": { "source": "GetOrders.List", "style": "\"table\"" },
                "header": [ { "kind": "HeaderCell", "children": [ { "kind": "Text", "props": { "text": "Name" } } ] } ],
                "row": [ { "kind": "RowCell", "children": [
                  { "kind": "Link", "name": "Open", "props": { "method": "Navigate", "destination": "OrderDetail" }, "args": { "OrderId": "GetOrders.List.Current.Order.Id" },
                    "children": [ { "kind": "Expression", "props": { "value": "GetOrders.List.Current.Order.Name" } } ] } ] } ] } ] } } ],
          "notes": [] }
        """);
        var r = Translate(orders, layout);
        Check(r.Status == "translated" && r.Todo.Count == 0, $"Orders: status {r.Status}, todo [{string.Join(" | ", r.Todo)}] (translated, nothing left)");
        var s = r.Spec;
        Check(Text(s, "name") == "Orders" && Text(s, "flow") == "MainFlow" && Text(s, "title") == "\"Orders\"" && Text(s, "styleSheet") == ".orders { margin: 0; }", "Orders: name, flow, title and styleSheet are carried");
        Check(Text(s, "access", "registered") == "true" && Text(s, "access", "roles", 0) == "OrderManager", "Orders: access comes from roles and registered");
        Check(Text(s, "inputs", 0, "name") == "CustomerId" && Text(s, "locals", 0, "default") == "\"\"", "Orders: inputs and locals are carried");
        Check(Text(s, "aggregates", 0, "name") == "GetOrders" && At(s, "aggregates", 0, "kind") is null && At(s, "aggregates", 0, "next") is null && At(s, "aggregates", 0, "_path") is null, "Orders: the aggregate keeps its name and loses connectors and _ keys");
        Check(Text(s, "layout") == "LayoutTopMenu" && Text(s, "placeholders", "Title", 0, "text") == "Orders" && At(s, "placeholders", "MainContent", 1, "type") is { } t && Run.S(t) == "TableRecords", "Orders: widgets go into the placeholders they came from");
        Check(Text(s, "placeholders", "MainContent", 0, "children", 2, "value") == "GetOrders.List.Length", "Orders: Table.LineCount is rewritten to the aggregate count");
        var table = At(s, "placeholders", "MainContent", 1);
        Check(Text(table, "source") == "GetOrders.List" && Text(table, "headerRow", 0, "type") == "HeaderCell" && Text(table, "row", 0, "children", 0, "onClick", "destination") == "OrderDetail"
            && Text(table, "row", 0, "children", 0, "onClick", "args", "OrderId") == "GetOrders.List.Current.Order.Id", "Orders: table keeps source, header cells, row cells and the navigating link with its argument");
        Check(Text(s, "actions", 0, "flow", 1, "kind") == "refreshData" && Text(s, "actions", 0, "flow", 1, "dataSource") == "GetOrders" && Text(s, "actions", 0, "flow", 1, "name") == "Refresh1", "Orders: an Ajax Refresh of a table fed by an aggregate becomes refreshData");
        Check(Text(s, "actions", 1, "flow", 0, "next") == "B" && At(s, "actions", 1, "flow")!.AsArray().Count == 2, "Orders: any other Ajax Refresh is removed and its connectors are joined");
        Check(Text(s, "placeholders", "MainContent", 0, "children", 1, "onClick") == "DoSearch" && Text(s, "placeholders", "MainContent", 0, "children", 0, "onChange") == "DoSearch", "Orders: submit buttons and inputs call the client action");

        // Layout placeholder with no match, and no layout at all (rule 1).
        var layoutless = Translate(orders, plain);
        Check(At(layoutless.Spec, "layout") is null && At(layoutless.Spec, "placeholders") is null && Text(layoutless.Spec, "widgets", 0, "type") == "Text" && At(layoutless.Spec, "widgets")!.AsArray().Count == 3, "Orders without a layout: one flat widgets list");
        var odd = (JsonObject)orders.DeepClone(); ((JsonObject)At(odd, "widgets", 0, "placeholders")!)["Sidebar"] = JsonNode.Parse("""[ { "kind": "Text", "props": { "text": "Side" } } ]""");
        var oddResult = Translate(odd, layout);
        Check(Has(oddResult.Todo, "Sidebar", "MainContent") && Text(oddResult.Spec, "placeholders", "MainContent", 2, "text") == "Side", "Orders with an unmatched placeholder: its content goes to the main placeholder and a Todo says so");

        // 2. A detail screen: Edit Record form, save action with a wrapper, feedback message and redirect, validation.
        var detail = Parse("""
        { "kind": "Screen", "name": "OrderDetail", "flow": "MainFlow", "moduleKind": "Traditional", "title": "\"Order\"",
          "inputs": [ { "name": "OrderId", "type": "Order Identifier", "mandatory": true, "default": "" } ], "locals": [],
          "preparation": { "complete": true, "unsupported": [], "nodes": [ { "kind": "aggregate", "name": "GetOrder", "sources": ["Order"], "filters": ["Order.Id = OrderId"] } ] },
          "actions": [ { "name": "Save", "inputs": [], "locals": [], "outputs": [], "flow": { "complete": true, "unsupported": [], "nodes": [
            { "kind": "execute", "name": "Store", "action": "CreateOrUpdateOrder", "args": { "Source": "OrderForm.Record" }, "next": "Msg", "_ref": "CreateOrUpdateOrder" },
            { "kind": "execute", "name": "Msg", "action": "Feedback_Message", "_ref": "RichWidgets.Feedback_Message", "args": { "Message": "\"Saved\"", "Type": "Entities.MessageType.Success" }, "next": "Go" },
            { "kind": "destination", "name": "Go", "screen": "Orders", "args": { "CustomerId": "OrderForm.Record.CustomerId" } } ] } } ],
          "widgets": [ { "kind": "EditRecord", "name": "OrderForm", "props": { "source": "GetOrder.List.Current.Order" }, "children": [
            { "kind": "Row", "children": [ { "kind": "Cell", "children": [
              { "kind": "Input", "name": "NameInput", "props": { "variable": "OrderForm.Record.Name", "mandatory": "True", "inputType": "Text" } },
              { "kind": "Input", "name": "EmailInput", "props": { "variable": "OrderForm.Record.Email", "validationType": "Email" } },
              { "kind": "Input", "name": "PhoneInput", "props": { "variable": "OrderForm.Record.Phone", "validationType": "Phone Number" } },
              { "kind": "Expression", "name": "Valid", "props": { "value": "NameInput.Valid and not OrderForm.Valid" } },
              { "kind": "Button", "name": "SaveButton", "props": { "method": "Submit", "onClick": "Save", "confirm": "\"Sure?\"" }, "children": [ { "kind": "Text", "props": { "text": "Save" } } ] } ] } ] } ] } ] }
        """);
        var d = Translate(detail, plain);
        Check(d.Status == "partial", "OrderDetail: partial (the form must be filled, the phone type and the confirm text are Todo)");
        Check(Text(d.Spec, "locals", 0, "name") == "OrderFormRecord" && Text(d.Spec, "locals", 0, "type") == "Order", "OrderDetail: the form gets the local OrderFormRecord of the record's type");
        Check(Text(d.Spec, "actions", 0, "flow", 0, "action") == "Shop.Order_CreateOrUpdate" && Text(d.Spec, "actions", 0, "flow", 0, "args", "Source") == "OrderFormRecord" && Text(d.Spec, "actions", 0, "flow", 0, "name") == "Store", "OrderDetail: the entity action calls the wrapper and <Form>.Record is rewritten in actions");
        Check(d.EntityActions.SequenceEqual(["CreateOrUpdateOrder"]), "OrderDetail: the entity action is recorded");
        Check(Text(d.Spec, "actions", 0, "flow", 1, "kind") == "message" && Text(d.Spec, "actions", 0, "flow", 1, "type") == "success" && Text(d.Spec, "actions", 0, "flow", 1, "message") == "\"Saved\"", "OrderDetail: Feedback_Message becomes a message node with the same text and type");
        Check(Text(d.Spec, "actions", 0, "flow", 2, "kind") == "destination" && Text(d.Spec, "actions", 0, "flow", 2, "args", "CustomerId") == "OrderFormRecord.CustomerId", "OrderDetail: the redirect is kept with rewritten arguments");
        var inputs = At(d.Spec, "widgets", 0, "children")!.AsArray();
        Check(Text(d.Spec, "widgets", 0, "type") == "Form" && Text(inputs[0], "variable") == "OrderFormRecord.Name" && Text(inputs[0], "mandatory") == "True" && At(inputs[0], "inputType") is null, "OrderDetail: the form and the first input are carried (mandatory, no input type for text)");
        Check(Text(inputs[1], "inputType") == "Email" && At(inputs[2], "inputType") is null && Has(d.Todo, "PhoneInput", "Phone Number"), "OrderDetail: the Email validation type becomes an input type, Phone Number is a Todo");
        Check(Text(inputs[3], "value") == "NameInput.Valid and not OrderForm.Valid" || Text(inputs[3], "value") == "NameInput.Valid and not OrderFormRecord.Valid", "OrderDetail: <Input>.Valid stays");
        Check(Has(d.Todo, "SaveButton", "confirm") && Has(d.Todo, "OrderForm", "OrderFormRecord", "GetOrder.List.Current.Order"), "OrderDetail: the confirm text and filling the record local are Todo lines");
        var noWrapper = Translate(detail, plain with { Wrappers = new Dictionary<string, string>() });
        Check(noWrapper.EntityActions.SequenceEqual(["CreateOrUpdateOrder"]) && Text(noWrapper.Spec, "actions", 0, "flow", 0, "action") == "CreateOrUpdateOrder" && Has(noWrapper.Todo, "Order_CreateOrUpdate", "wrapper"), "OrderDetail without a wrapper: the node stays, the entity action is recorded and a Todo names the wrapper");

        // 3. A Preparation with an if and a server call is not translated (rule 2); the screen is still built.
        var prep = Parse("""
        { "kind": "Screen", "name": "Dash", "flow": "MainFlow", "moduleKind": "Traditional", "inputs": [], "locals": [],
          "preparation": { "complete": true, "unsupported": [], "nodes": [
            { "kind": "if", "name": "IsAdmin", "condition": "Role", "true": "Load", "false": "End" },
            { "kind": "execute", "name": "Load", "action": "LoadDashboard", "args": {} },
            { "kind": "aggregate", "name": "GetStats", "sources": ["Stat"] } ] },
          "actions": [], "widgets": [ { "kind": "Text", "props": { "text": "Hi" } } ] }
        """);
        var pr = Translate(prep, plain);
        Check(pr.Status == "partial" && At(pr.Spec, "aggregates") is null && Text(pr.Spec, "widgets", 0, "text") == "Hi" && Has(pr.Todo, "Preparation", "if IsAdmin", "execute Load", "data action"), "Dash: the Preparation is not translated, the Todo lists its nodes and asks for a data action");

        // 4. A screen action with an aggregate becomes a stub that keeps the widget binding valid (rule 3).
        var stub = Parse("""
        { "kind": "Screen", "name": "Report", "flow": "MainFlow", "moduleKind": "Traditional", "inputs": [], "locals": [ { "name": "Total", "type": "Integer", "default": "" } ],
          "preparation": null,
          "actions": [ { "name": "Calc", "inputs": [ { "name": "Year", "type": "Integer", "mandatory": true } ], "locals": [ { "name": "Tmp", "type": "Integer", "default": "0" } ], "outputs": [], "flow": { "complete": true, "unsupported": [], "nodes": [
            { "kind": "aggregate", "name": "CountOrders", "sources": ["Order"] }, { "kind": "assign", "name": "Set", "assign": { "Total": "CountOrders.Count" } } ] } },
            { "name": "Odd", "inputs": [], "locals": [], "outputs": [], "flow": { "complete": false, "unsupported": [ { "path": "x", "kind": "CommitTransaction", "why": "no spec node" } ], "nodes": [] } } ],
          "widgets": [ { "kind": "Button", "name": "Go", "props": { "method": "Submit", "onClick": "Calc" }, "children": [ { "kind": "Text", "props": { "text": "Go" } } ] } ] }
        """);
        var st = Translate(stub, plain);
        Check(st.Status == "partial" && Text(st.Spec, "actions", 0, "name") == "Calc" && Text(st.Spec, "actions", 0, "inputs", 0, "name") == "Year" && Text(st.Spec, "actions", 0, "locals", 0, "name") == "Tmp"
            && Text(st.Spec, "actions", 0, "flow", 0, "kind") == "comment" && (Text(st.Spec, "actions", 0, "flow", 0, "text") ?? "").StartsWith("TODO migrate:") && At(st.Spec, "actions", 0, "flow")!.AsArray().Count == 1, "Report: an action with an aggregate is a stub with its inputs and locals and one TODO comment");
        Check(Text(st.Spec, "widgets", 0, "onClick") == "Calc" && Has(st.Todo, "Calc", "aggregate", "CountOrders") && Has(st.Todo, "Odd", "CommitTransaction"), "Report: the button still calls the stub and the Todo says what the original did; an unsupported node makes a stub too");

        // 5. A web block with a Notify; the block has a placeholder and an input.
        var blockInput = Parse("""
        { "kind": "Block", "name": "OrderCard", "flow": "Common", "moduleKind": "Traditional", "inputs": [ { "name": "OrderId", "type": "Order Identifier", "mandatory": true, "default": "" } ], "locals": [],
          "access": { "anonymous": true, "registered": true, "roles": [] }, "preparation": null,
          "actions": [ { "name": "Pick", "inputs": [], "locals": [], "outputs": [], "flow": { "complete": true, "unsupported": [], "nodes": [ { "kind": "execute", "name": "Tell", "action": "Notify", "args": { "Message": "\"picked\"" } } ] } } ],
          "widgets": [ { "kind": "Link", "name": "PickLink", "props": { "method": "Submit", "onClick": "Pick" }, "children": [ { "kind": "Text", "props": { "text": "Pick" } } ] }, { "kind": "Placeholder", "name": "Extra" } ] }
        """);
        var b = Translate(blockInput, layout);
        Check(b.Kind == "Block" && b.Status == "partial" && At(b.Spec, "access") is null && At(b.Spec, "layout") is null && At(b.Spec, "title") is null, "OrderCard: a block has no layout, title or access");
        Check(Text(b.Spec, "actions", 0, "flow", 0, "kind") == "comment" && Has(b.Todo, "Pick", "Notify", "events", "triggerEvent"), "OrderCard: the action with Notify is a stub and the Todo names the event to create");
        Check(Text(b.Spec, "widgets", 1, "type") == "Placeholder" && Text(b.Spec, "widgets", 0, "onClick") == "Pick", "OrderCard: widgets and the placeholder are carried");

        // 6. Block instances: a local block, one of another module, and an OnNotify handler.
        var inst = Parse("""
        { "kind": "Screen", "name": "Cards", "flow": "MainFlow", "moduleKind": "Traditional", "inputs": [], "locals": [], "preparation": null,
          "actions": [ { "name": "Got", "inputs": [], "locals": [], "outputs": [], "flow": { "complete": true, "unsupported": [], "nodes": [] } } ],
          "widgets": [
            { "kind": "BlockInstance", "name": "Card1", "sourceBlock": "OrderCard", "props": { "onNotify": "Got" }, "args": { "OrderId": "OrderId" }, "placeholders": { "Extra": [ { "kind": "Text", "props": { "text": "x" } } ] } },
            { "kind": "BlockInstance", "name": "Tip", "sourceBlock": "RichWidgets.Tooltip", "args": { "Text": "\"hi\"" }, "placeholders": { "Content": [ { "kind": "Text", "props": { "text": "inner" } } ] } } ] }
        """);
        var i6 = Translate(inst, plain);
        Check(Text(i6.Spec, "widgets", 0, "type") == "BlockInstance" && Text(i6.Spec, "widgets", 0, "sourceBlock") == "OrderCard" && Text(i6.Spec, "widgets", 0, "args", "OrderId") == "OrderId" && Text(i6.Spec, "widgets", 0, "placeholders", "Extra", 0, "text") == "x", "Cards: an instance of a local block keeps its name, args and placeholder content");
        Check(Has(i6.Todo, "Card1", "OnNotify", "events"), "Cards: OnNotify on an instance is a Todo that names the event");
        Check(Text(i6.Spec, "widgets", 1, "type") == "Container" && (Text(i6.Spec, "widgets", 1, "children", 0, "text") ?? "").StartsWith("TODO migrate") && Text(i6.Spec, "widgets", 1, "children", 1, "text") == "inner" && Has(i6.Todo, "Tip", "RichWidgets.Tooltip"), "Cards: an instance of a block of another module is a Todo container that keeps its content");

        // 7. Session variables, with and without a client variable; a string literal is not touched.
        var sess = Parse("""
        { "kind": "Screen", "name": "Hello", "flow": "MainFlow", "moduleKind": "Traditional", "inputs": [], "locals": [], "preparation": null,
          "actions": [ { "name": "Remember", "inputs": [], "locals": [], "outputs": [], "flow": { "complete": true, "unsupported": [], "nodes": [ { "kind": "assign", "name": "Keep", "assign": { "Session.Lang": "\"en\"", "Session.Last": "Session.Last + 1" } } ] } } ],
          "widgets": [ { "kind": "Expression", "name": "Who", "props": { "value": "\"Session.Lang: \" + Session.Lang + Session.UserName + MyEntity.Session" } } ] }
        """);
        var se = Translate(sess, plain);
        Check(Text(se.Spec, "widgets", 0, "value") == "\"Session.Lang: \" + Client.Lang + Session.UserName + MyEntity.Session", "Hello: Session.<client variable> becomes Client.<it>, other session reads and string literals stay");
        Check(Text(se.Spec, "actions", 0, "flow", 0, "assign", "Client.Lang") == "\"en\"" && Text(se.Spec, "actions", 0, "flow", 0, "assign", "Session.Last") == "Session.Last + 1", "Hello: assignment targets are rewritten too");
        Check(se.SessionVariables.Order().SequenceEqual(["Lang", "Last", "UserName"]) && Has(se.Todo, "Session.UserName") && Has(se.Todo, "Session.Last") && !Has(se.Todo, "Session.Lang,"), "Hello: every session variable is recorded and the non-client ones are Todo lines");

        // 8. Widgets: Editable Table, an unknown kind, a combo box, a radio button, a checkbox, an image, an upload, a popup, an unescaped expression.
        var misc = Parse("""
        { "kind": "Screen", "name": "Misc", "flow": "MainFlow", "moduleKind": "Traditional", "inputs": [], "locals": [ { "name": "Pick", "type": "Integer", "default": "" } ], "preparation": { "complete": true, "unsupported": [], "nodes": [ { "kind": "aggregate", "name": "GetKinds", "sources": ["Kind"] } ] },
          "actions": [], "widgets": [
            { "kind": "EditableTable", "name": "Grid", "children": [ { "kind": "Text", "props": { "text": "cell" } } ] },
            { "kind": "Tabs", "name": "TabSet", "children": [ { "kind": "Text", "props": { "text": "kept" } } ] },
            { "kind": "Combo Box", "name": "KindBox", "props": { "variable": "Pick", "source": "GetKinds.List", "valueAttribute": "GetKinds.List.Current.Kind.Id", "labelAttribute": "GetKinds.List.Current.Kind.Name", "emptyValue": "\"none\"" } },
            { "kind": "RadioButton", "name": "Opt", "props": { "variable": "Pick", "value": "1" } },
            { "kind": "CheckBox", "name": "Agree", "props": { "variable": "Flag", "visible": "Pick > 0" } },
            { "kind": "Image", "name": "Logo", "props": { "image": "Logo" } },
            { "kind": "Upload", "name": "File", "props": { "variable": "Data", "fileName": "FileName" } },
            { "kind": "Popup", "name": "Dlg", "children": [ { "kind": "Text", "props": { "text": "inside" } } ] },
            { "kind": "Expression", "name": "Raw", "props": { "value": "Html", "escapeContent": "No", "visible": "Pick = 1" } },
            { "kind": "If", "name": "Branch", "props": { "condition": "Pick = 1" }, "true": [ { "kind": "Text", "props": { "text": "yes" } } ], "false": [ { "kind": "Text", "props": { "text": "no" } } ] },
            { "kind": "ListRecords", "name": "KindList", "props": { "source": "GetKinds.List" }, "row": [ { "kind": "Expression", "props": { "value": "GetKinds.List.Current.Kind.Name" } } ] } ] }
        """);
        var m = Translate(misc, plain);
        var w = At(m.Spec, "widgets")!.AsArray();
        Check(m.Status == "partial" && Text(w[0], "type") == "Container" && Text(w[0], "children", 1, "text") == "cell" && Has(m.Todo, "Grid", "Editable Table"), "Misc: an Editable Table is a Todo container that keeps its content");
        Check(Text(w[1], "type") == "Container" && Text(w[1], "children", 1, "text") == "kept" && Has(m.Todo, "TabSet", "Tabs"), "Misc: an unknown kind is a Todo container with its children inside");
        Check(Text(w[2], "type") == "Dropdown" && Text(w[2], "list") == "GetKinds.List" && Text(w[2], "values") == "Kind.Id" && Text(w[2], "labels") == "Kind.Name" && Has(m.Todo, "KindBox", "special list"), "Misc: a combo box becomes a Dropdown with option-record labels and values, the Empty Value is a Todo");
        Check(Text(w[3], "type") == "Container" && Has(m.Todo, "Opt", "Radio"), "Misc: a Radio Button is a Todo container (the spec has no Radio Group)");
        Check(Text(w[4], "type") == "Checkbox" && Text(w[4], "visible") == "Pick > 0" && Text(w[4], "variable") == "Flag", "Misc: Check Box becomes Checkbox with its visibility");
        Check(Text(w[5], "type") == "Image" && Text(w[5], "image") == "Logo", "Misc: an Image keeps its resource name");
        Check(Text(w[6], "type") == "Upload" && Text(w[6], "fileContent") == "Data" && Text(w[6], "fileName") == "FileName", "Misc: an Upload keeps its variables");
        Check(Text(w[7], "type") == "Popup" && Text(w[7], "showPopup") == "DlgShow" && Text(m.Spec, "locals", 1, "name") == "DlgShow", "Misc: a Popup is a Popup with a Boolean local that shows it");
        Check(Text(w[8], "type") == "Container" && Text(w[8], "visible") == "Pick = 1" && Text(w[8], "children", 0, "type") == "Expression" && Has(m.Todo, "Raw", "unescaped"), "Misc: an unescaped Expression is carried in a Container that holds Visible, with a Todo");
        Check(Text(w[9], "type") == "If" && Text(w[9], "trueBranch", 0, "text") == "yes" && Text(w[9], "falseBranch", 0, "text") == "no", "Misc: an If keeps both branches");
        Check(Text(w[10], "type") == "List" && Text(w[10], "source") == "GetKinds.List" && Text(w[10], "children", 0, "value") == "GetKinds.List.Current.Kind.Name", "Misc: List Records becomes a List with the row content");

        // 10. Every node kind of a client flow, a server action call that stays as it is, JavaScript that is not carried.
        var flows = Parse("""
        { "kind": "Screen", "name": "Flows", "flow": "MainFlow", "moduleKind": "Traditional", "inputs": [], "locals": [], "preparation": null, "javaScript": "alert(1);",
          "actions": [ { "name": "Run", "inputs": [ { "name": "Id", "type": "Integer", "mandatory": true, "default": "" } ], "locals": [], "outputs": [], "flow": { "complete": true, "unsupported": [], "nodes": [
            { "kind": "if", "name": "Ok", "condition": "Id > 0", "true": "Each", "false": "Bad", "_path": "p" },
            { "kind": "forEach", "name": "Each", "recordList": "Items.List", "cycle": "Place", "next": "Pick" },
            { "kind": "execute", "name": "Place", "action": "PlaceOrder", "args": { "Id": "Items.List.Current.Item.Id", "Qty": "2" }, "next": "Each", "_ref": "PlaceOrder" },
            { "kind": "switch", "name": "Pick", "cases": [ { "condition": "Id = 1", "next": "Done" } ], "otherwise": "Bad" },
            { "kind": "comment", "text": "note" },
            { "kind": "raiseException", "name": "Bad", "exception": "InvalidOrder", "message": "\"Bad id\"" },
            { "kind": "end", "name": "Done" } ] } } ],
          "widgets": [] }
        """);
        var fl = Translate(flows, plain);
        var fn = At(fl.Spec, "actions", 0, "flow")!.AsArray();
        Check(fl.Status == "partial" && Has(fl.Todo, "JavaScript") && fl.Todo.Count == 1, "Flows: only the JavaScript is left (a flow of supported nodes is translated whole)");
        Check(fn.Count == 7 && Text(fn[0], "kind") == "if" && Text(fn[1], "kind") == "forEach" && Text(fn[1], "recordList") == "Items.List" && Text(fn[2], "action") == "PlaceOrder" && Text(fn[2], "args", "Qty") == "2"
            && Text(fn[3], "cases", 0, "next") == "Done" && Text(fn[3], "otherwise") == "Bad" && Text(fn[5], "kind") == "raiseException" && Text(fn[6], "kind") == "end" && At(fn[0], "_path") is null && fl.EntityActions.Count == 0, "Flows: if, forEach, a server action call, switch, comment, raiseException and end are carried");

        // 9. Emails, other module kinds and a failed export are worklist entries.
        var email = Translate(Parse("""{ "kind": "Email", "name": "Welcome", "flow": "Emails", "moduleKind": "Traditional", "inputs": [], "locals": [], "widgets": [] }"""), layout);
        Check(email.Spec is null && email.Status == "worklist" && email.Kind == "Email" && Has(email.Todo, "Welcome", "emails"), "Welcome: an email is a worklist entry that names the emails key");
        var reactive = Translate(Parse("""{ "kind": "Screen", "name": "R", "moduleKind": "Reactive" }"""), layout);
        Check(reactive.Spec is null && reactive.Status == "worklist", "R: a Reactive element is not translated");
        var failed = Translate(Parse("""{ "element": "X", "name": "X", "kind": "Screen", "error": "no such screen" }"""), layout);
        Check(failed.Spec is null && failed.Status == "worklist", "X: an element that failed to export is a worklist entry");
        var broken = Translate(Parse("""{ "kind": "Screen", "name": "Broken", "moduleKind": "Traditional", "actions": [ { "name": "A", "flow": "not an object" } ], "widgets": "no" }"""), layout);
        Check(broken.Status is "partial" or "worklist", "Broken: an unreadable input does not throw");
        return failures;
    }
}
