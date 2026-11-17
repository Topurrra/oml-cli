// KEYS OF `oml export` READ HERE. Source of truth: src/Core/Export.cs (E, line numbers) and Describe.WidgetFacts (D). Check both sides against this table.
// The compact JSON drops null, empty arrays and {} (JsonOutput.Prune): EVERY key may be absent, absent = null/empty. Values are TEXT (expressions as typed:
// "\"x\"", True), a JSON boolean only where marked (bool).
// element  kind Screen|Block|Email, name, flow, path, moduleKind Traditional|Mobile|Reactive, title (expr), subject (Email, expr), styleSheet, javaScript (expr),
//          inputs/locals [{name,type,default,mandatory (bool, inputs),description}], access {anonymous,registered (bool),roles[]} (not Email), preparation (a flow),
//          actions [{name,inputs,locals,outputs,flow,_path}], widgets [widget], notes [text] (E 128-164). Reactive-only keys (aggregates, dataActions, onX, events) are not read.
// flow     {nodes[], complete (bool, written whenever there is a flow; = unsupported empty), unsupported [{path,kind,why}]}. Main path from Start first, comments, then
//          handlers; End nodes are not listed, a connector to one says "End"; unnamed nodes and all comments get made-up names (AssignNode -> Assign1) (E 234-298).
// node     kind = a spec kind (assign if switch forEach execute aggregate sql raiseException destination triggerEvent sendEmail refreshData javaScript message comment
//          exceptionHandler jsonSerialize jsonDeserialize ...) or, for a kind the spec lacks, the real one (AjaxRefreshNode) with _props {Property: text} and an
//          "unsupported" entry; name; next|true|false|cycle|otherwise = node name or "End"; condition; cases [{condition,next}]; assign {variable: expr}; recordList;
//          action + _ref (name, or Module.Name); args {parameter: expr}; screen; exception; message (expr); type (message: info|success|warning|error); text (comment);
//          javaScript/inputs/outputs; _async (bool, javaScript); _path. `_reads` does not exist. A Destination or Raise Exception node has no next (E 311-452).
// aggregate (preparation node): sources[] joins[{type,left,right,condition}] filters[] sorts[{attribute,descending,dynamic}] groupBy[{attribute,as}]
//          aggregates[{name,function,attribute}] calculated[{name,expression}] groupFilters groupSorts groupCalculated maxRecords startIndex (text) timeout (int) (E 486-550).
// widget   kind = CLR type name (WebContainer; a Reactive descriptor name), type = spec type (Container Input BlockInstance TableRecords Cell Row If ...: the interface name
//          without Web/Mobile and Widget, E 657-666), name (real names only), path, props {key: text}, attrs {html attribute: expr}, args {parameter: expr}, events
//          {event: handler | {action,args}} (block instance), sourceBlock, children[], true[] false[] (If), header[] row[] (table), placeholders {slot: [widget]} (empty
//          slots are absent) (E 590-653). The translator decides from `type`, then `kind` without Web/Mobile/Widget.
// props    facts (D 99-145): value text variable source sourceBlock visible (not "True") condition targetWidget style inlineStyle tag icon marginLeft inputType (not Text)
//          prompt mandatory (not "False") enabled (not "True") isDefault confirm image url (Image only) onClick onChange (handler names) and one key per Events entry;
//          then destination (screen an On Click goes to, E 619), method (a property of the click event with "Method" in its name, E 623: often absent), then every other
//          readable property named with letters only, first letter lowered (E 621). Those generic names (label, sourceRecordList, sourceRecord, ...) are not checkable here.
// NOT in the export: session variable defaults (nor in inspect), the widgets an Ajax Refresh refreshes, the click method when no property says it, a Link's own
// Url (Export.cs 53 lists Url in FactSources, so only an Image's url is written), a node's reads (`_reads`), End nodes, a block's Reactive events.

using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// The screen translator of `oml migrate`: a PURE function from the structured export of one Traditional Web screen, web block or
// email (`oml export`, the keys are in the table above) to the spec entry of the same thing in a Reactive Web module, plus an
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

    // For the plan: is this exported widget a Placeholder (of a layout block)? Decided like every widget, by `type` then `kind`.
    internal static bool IsPlaceholder(JsonObject widget) => Run.WType(widget) == "placeholder";

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
        // A flag as the export writes it: a JSON boolean (mandatory, complete, _async) or the text True/False of an expression.
        static bool? Flag(JsonNode? n) => n is JsonValue v ? v.TryGetValue<bool>(out var b) ? b : S(v) is { } t && bool.TryParse(t.Trim(), out var tb) ? tb : null : null;
        // The widget kind that decides its translation: `type` (the spec's name, Export.WidgetType), else `kind` without the Web or Mobile
        // prefix and the Widget suffix (the CLR or interface name), compared without case, spaces and punctuation.
        internal static string WType(JsonObject w)
        {
            var t = Norm(S(w["type"]) is { Length: > 0 } ty ? ty : S(w["kind"]));
            foreach (var prefix in new[] { "web", "mobile" }) if (t.StartsWith(prefix, StringComparison.Ordinal) && t.Length > prefix.Length) { t = t[prefix.Length..]; break; }
            return t.EndsWith("widget", StringComparison.Ordinal) && t.Length > 6 ? t[..^6] : t;
        }
        // A node kind without the Node suffix: a spec kind (assign, forEach) or the real kind of an unsupported node (AjaxRefreshNode).
        static string NodeKind(JsonObject n) { var k = Norm(S(n["kind"])); return k.EndsWith("node", StringComparison.Ordinal) && k.Length > 4 ? k[..^4] : k; }
        // Export writes `complete` whenever there is a flow, and means "nothing unsupported"; read it that way when it is missing.
        static bool Complete(JsonObject flow) => Flag(flow["complete"]) ?? !Objs(flow["unsupported"]).Any();

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
                return new Result(null, "Email", "worklist", [$"Email {name}: build the Reactive email with the spec's emails key (subject{(S(el["subject"]) is { Length: > 0 } subject ? " " + subject : "")}, inputs, widgets and styleSheet) from the exported content; this version does not translate emails."], [], []);
            if (!kind.Equals("Screen", StringComparison.OrdinalIgnoreCase) && !block)
                return Worklist(kind, $"{kind} {name}: only screens, web blocks and emails can be translated.");

            Collect(el["widgets"]);
            foreach (var a in Objs(el["actions"])) if (S(a["name"]) is { } an) actionNames.Add(an);
            foreach (var v in Objs(el["inputs"]).Concat(Objs(el["locals"]))) if (S(v["name"]) is { } vn) varTypes[vn] = S(v["type"]) ?? "Text";
            // The method of a click is decided in Click (a destination means Navigate, an action means Submit), which says so when it cannot: the note is not repeated.
            foreach (var note in el["notes"] as JsonArray ?? []) if (S(note) is { Length: > 0 } text && !text.Contains("the method of its On Click")) Todo($"the export could read it only in part: {text}");

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
                if (input && Flag(v["mandatory"]) is { } mandatory) o["mandatory"] = mandatory;
                if (S(v["default"]) is { Length: > 0 } d) o["default"] = Rw(d, $"the default of {S(v["name"])}");
                if (S(v["description"]) is { Length: > 0 } description) o["description"] = description;
                result.Add(o);
            }
            return result;
        }

        static JsonObject Access(JsonObject a)
        {
            var o = new JsonObject
            {
                ["anonymous"] = Flag(a["anonymous"]) == true,
                ["registered"] = Flag(a["registered"]) == true,
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
            var complete = Complete(p);
            // Comments are listed after the main path and change nothing.
            var calls = nodes.Where(n => NodeKind(n) != "comment").ToList();
            if (complete && calls.All(n => NodeKind(n) == "aggregate"))
            {
                foreach (var n in calls)
                {
                    var a = new JsonObject();
                    foreach (var key in new[] { "name", "sources", "joins", "filters", "sorts", "groupBy", "aggregates", "calculated", "groupFilters", "groupSorts", "groupCalculated", "maxRecords", "startIndex", "timeout" })
                        if (n[key] is { } v) a[key] = RwTree(v, $"the aggregate {S(n["name"])}", "name", "sources", "type", "function", "as", "left", "right");
                    result.Add(a);
                    if (S(n["name"]) is { } an) aggregates.Add(an);
                }
                return result;
            }
            var what = Summary(calls);
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

        static readonly HashSet<string> ActionKinds = ["assign", "if", "switch", "foreach", "execute", "destination", "raiseexception", "message", "comment", "javascript", "end", "ajaxrefresh"];

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

        static bool IsNotify(JsonObject n) => NodeKind(n) == "execute" && Leaf(S(n["action"]) ?? S(n["_ref"]) ?? "") is var l && (l.Equals("Notify", StringComparison.OrdinalIgnoreCase) || l.Equals("Deprecated_Notify", StringComparison.OrdinalIgnoreCase));

        // The Ajax Refresh node is the one unsupported thing that is handled (DropRefreshes); its entry says so in `why` (Export.cs 445), so another
        // reason on a node of that kind (a repeated name) still makes a stub.
        static bool AjaxEntry(JsonObject u) => NodeKind(u) == "ajaxrefresh" && (S(u["why"]) ?? "").Contains("Ajax Refresh node", StringComparison.Ordinal);

        (JsonArray? flow, string? why) ActionFlow(string an, JsonObject a)
        {
            if (a["flow"] is not JsonObject f) return (null, "its flow could not be read from the export");
            var nodes = Objs(f["nodes"]).ToList();
            var unsupported = Objs(f["unsupported"]).ToList();
            if (unsupported.FirstOrDefault(u => !AjaxEntry(u)) is { } bad)
                return (null, $"it contains {S(bad["kind"])} ({S(bad["why"]) ?? "unsupported"}) at {S(bad["path"])}");
            if (!Complete(f) && unsupported.Count == 0) return (null, "the export marks its flow as incomplete");
            foreach (var n in nodes)
            {
                var kind = NodeKind(n);
                var nn = S(n["name"]) ?? S(n["kind"]);
                if (!ActionKinds.Contains(kind))
                    return (null, kind is "aggregate" or "sql" ? $"it contains the {S(n["kind"])} node {nn}, which needs a server action" : $"it contains a {S(n["kind"])} node ({nn}) that has no client equivalent here");
                if (IsNotify(n)) return (null, $"it calls Notify at {nn}");
                if (kind == "destination" && (S(n["screen"]) is not { Length: > 0 } scr || scr.Contains('.')))
                    return (null, $"its Destination {nn} does not name a screen of this module");
            }
            if (nodes.Any(n => NodeKind(n) == "ajaxrefresh")) nodes = DropRefreshes(nodes, an);
            var result = new JsonArray();
            foreach (var n in nodes) result.Add(Node(n, an));
            return (result, null);
        }

        // Ajax Refresh nodes: the export cannot read which widgets they refresh (Export.cs 446), so each is removed and its incoming connectors join its
        // outgoing one, and a Todo says so. Every default "next" is made explicit first, so removing a node cannot change the path.
        List<JsonObject> DropRefreshes(List<JsonObject> source, string an)
        {
            var nodes = source.Select(n => (JsonObject)n.DeepClone()).ToList();
            static bool Defaults(JsonObject n) => NodeKind(n) is "assign" or "execute" or "message" or "foreach" or "javascript" or "ajaxrefresh";
            string? Following(int index) => nodes.Skip(index + 1).FirstOrDefault(x => NodeKind(x) != "comment") is { } nx ? S(nx["name"]) : null;
            for (var i = 0; i < nodes.Count; i++)
                if (Defaults(nodes[i]) && nodes[i]["next"] is null && Following(i) is { } follow) nodes[i]["next"] = follow;
            foreach (var r in nodes.Where(n => NodeKind(n) == "ajaxrefresh").ToList())
            {
                var rName = S(r["name"]);
                var target = S(r["next"]) ?? "End";
                Todo($"the action {an} had the Ajax Refresh node {rName}, which was removed because the export cannot read which widgets it refreshed (a Reactive aggregate refetches only when its inputs change); if a table or list no longer updates, add a refreshData node for the aggregate behind it");
                var at = nodes.IndexOf(r);
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

        static readonly string[] Connectors = ["next", "true", "false", "cycle", "otherwise"];

        // One node of a translatable client flow, in the spec's own shape (keys of that kind only, no `_` facts).
        JsonObject Node(JsonObject n, string an)
        {
            var kind = NodeKind(n);
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
                case "comment": o["kind"] = "comment"; o["text"] = S(n["text"]) ?? ""; break;   // its name is made up (Comment1) and nothing connects to it
                case "javascript":
                    o["kind"] = "javaScript"; Copy("name", "javaScript", "inputs", "outputs"); Args(); Copy("next");
                    if (Flag(n["_async"]) == true) Todo($"{at} has the JavaScript node {S(n["name"])}, which is asynchronous in the original; the spec has no key for that, so set it in Service Studio");
                    break;
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
                var kind = WType(w);
                if (!Blank(wn))
                {
                    widgetByName[wn!] = w;
                    if (kind is "form" or "editrecord" or "showrecord") forms[wn!] = wn + "Record";
                    if (kind is "tablerecords" or "listrecords" && Prop(w, "source", "sourceRecordList") is { } src && Regex.IsMatch(src, @"^\w+\.List$")) tables[wn!] = src;
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
            var kind = S(w["type"]) ?? S(w["kind"]) ?? "widget";
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
            if (Prop(w, used, "style") is { } style) o["style"] = Rw(style, at);
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
            if (Prop(w, used, "visible") is { } v && !IsTrue(v)) o["visible"] = Rw(v, Label(w));
            if (Prop(w, used, "enabled") is { } e && !IsTrue(e)) o["enabled"] = Rw(e, Label(w));
        }

        // Expression, Text and BlockInstance have no Visible in Reactive: a Container carries it.
        JsonObject Wrapped(JsonObject w, HashSet<string> used, JsonObject o)
        {
            if (Prop(w, used, "visible") is { } v && !IsTrue(v))
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
            var kind = WType(w);
            JsonObject? o;
            switch (kind)
            {
                case "container":
                    o = New("Container", w, used); Visible(w, used, o);
                    if (Ws(w["children"]) is { Count: > 0 } kids) o["children"] = kids;
                    break;
                case "expression":
                    o = New("Expression", w, used);
                    o["value"] = Rw(Prop(w, used, "value") ?? "\"\"", Label(w));
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
                    if (Prop(w, used, "targetWidget") is { } target)
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
                    if (Prop(w, used, "icon") is { } icon) Todo($"{Label(w)} shows the icon {icon}, which the spec cannot set; choose it in Service Studio");
                    break;
                case "placeholder": o = New("Placeholder", w, used); break;
                case "upload": case "fileupload":
                    o = New("Upload", w, used); Visible(w, used, o);
                    if (Prop(w, used, "fileContent", "variable") is { } fc) o["fileContent"] = Rw(fc, Label(w));
                    if (Prop(w, used, "fileName", "filename") is { } fnm) o["fileName"] = Rw(fnm, Label(w));
                    if (ActionRef(w, used, "its On Change", "onChange") is { } up) o["onChange"] = up;
                    break;
                case "checkbox":
                    o = New("Checkbox", w, used); Visible(w, used, o);
                    if (Prop(w, used, "variable") is { } cv) o["variable"] = Rw(cv, Label(w));
                    if (ActionRef(w, used, "its On Change", "onChange") is { } cc) o["onChange"] = cc;
                    break;
                case "combobox": case "combo": case "dropdown": o = Combo(w, used); break;
                case "radiobutton": case "radiogroup":
                    o = TodoBox(w, $"is a Radio Button (variable {Prop(w, "variable") ?? "none"}, value {Prop(w, "value", "valueText") ?? "none"}); the spec has no Radio Group widget, so build the group in Service Studio", false);
                    used.UnionWith(["variable", "value", "valuetext", "style", "visible", "enabled", "display", "onchange", "mandatory"]);
                    break;
                case "tablerecords": o = Records(w, used, true); break;
                case "listrecords": o = Records(w, used, false); break;
                case "blockinstance": o = Instance(w, used); break;
                case "form": case "editrecord": case "showrecord": o = Form(w, used); break;
                case "popup": o = Popup(w, used, w["children"]); break;
                case "editabletable": o = TodoBox(w, "is an Editable Table, which Reactive does not have; rebuild it as a Table with inputs"); break;
                case "listbox": o = TodoBox(w, "is a List Box, which Reactive does not have; rebuild it as a list of Checkboxes"); break;
                case "comment": return;
                case "row": case "tablerow": case "cell": case "headercell": case "rowcell":
                    foreach (var c in Objs(w["children"])) One(c, into);   // a Reactive Form has no rows and cells: the content moves up
                    return;
                default:
                    o = TodoBox(w, $"is a {S(w["type"]) ?? S(w["kind"])} widget that has no Reactive translation here; rebuild it (its content is kept inside)");
                    used.UnionWith(w["props"] is JsonObject props ? props.Select(kv => kv.Key.ToLowerInvariant()) : []);
                    break;
            }
            if (kind is not ("editabletable" or "listbox" or "radiobutton" or "radiogroup" or "popup")) Unhandled(w, used);
            into.Add(o);
        }

        // Rule 7: mandatory and the input type are carried; other validation types and messages are not.
        JsonObject Input(JsonObject w, HashSet<string> used, bool area)
        {
            var at = Label(w);
            var o = New(area ? "TextArea" : "Input", w, used); Visible(w, used, o);
            if (Prop(w, used, "variable") is { } v) o["variable"] = Rw(v, at);
            if (Prop(w, used, "mandatory") is { } m && !IsFalse(m)) o["mandatory"] = Rw(m, at);
            if (Prop(w, used, "prompt") is { } pr) o["prompt"] = Rw(pr, at);
            if (Prop(w, used, "maxLength") is { } ml && int.TryParse(ml, out var len)) o["maxLength"] = len;
            if (ActionRef(w, used, "its On Change", "onChange") is { } oc) o["onChange"] = oc;
            if (!area)
            {
                string? type = null;
                foreach (var raw in new[] { Prop(w, used, "inputType", "type"), Prop(w, used, "validationType") })   // inputType is a fact, the others generic properties
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

        // Rule 5: Button and Link. Navigate -> onClick destination with args; Submit and AjaxSubmit -> onClick calls the client action. The export says the
        // method only when a property of the click event has "Method" in its name (Export.cs 623); without it a screen `destination` means Navigate
        // (Export.cs 619 writes it only for a screen) and an On Click handler without one means a call of a screen action (Submit).
        JsonObject Click(JsonObject w, HashSet<string> used, bool button)
        {
            var at = Label(w);
            var o = New(button ? "Button" : "Link", w, used); Visible(w, used, o);
            var method = Norm(Prop(w, used, "method"));
            var dest = Prop(w, used, "destination");
            var onClick = Prop(w, used, "onClick");
            var args = w["args"] as JsonObject;
            if (method == "") method = dest is not null ? "navigate" : onClick is not null ? "submit" : "";
            if (method == "navigate")
            {
                var screen = dest ?? onClick;
                if (screen is null) Todo($"{at} navigates, but the export gives no destination; set its On Click");
                else if (screen.Contains('.') || Regex.IsMatch(screen, @"^\w+://"))
                    Todo($"{at} navigates to {screen}, which is not a screen of this module (a Reactive screen cannot link to a Traditional screen or a URL this way); set its On Click");
                else
                {
                    var d = new JsonObject { ["destination"] = Leaf(screen) };
                    if (args is { Count: > 0 }) d["args"] = RwTree(args, at);
                    o["onClick"] = d;
                }
            }
            else if (method is "submit" or "ajaxsubmit")
            {
                if (ActionRef(w, used, "its On Click", "onClick") is { } action)
                {
                    o["onClick"] = action;
                    if (args is { Count: > 0 }) o["args"] = RwTree(args, at);
                }
                else if (onClick is null) Todo($"{at} submits, but the export gives no action; set its On Click");
            }
            else if (method == "") Todo($"{at} has no On Click in the export (it could not read the method, and a link to an address is not exported); set its On Click");
            else Todo($"{at} has the method {Prop(w, "method")}, which has no Reactive translation here; set its On Click");
            if (Prop(w, used, "confirm") is { } confirm) Todo($"{at} asks the user to confirm with {confirm}, and the spec has no key for a confirmation; add a confirmation popup or a JavaScript confirm before its action");
            var children = Ws(w["children"]);
            if (children.Count == 0 && Prop(w, used, "label", "text", "value") is { } label) children.Add(LabelWidget(label, x => Rw(x, at)));
            if (children.Count > 0) o["children"] = children;
            return o;
        }

        JsonObject If(JsonObject w, HashSet<string> used)
        {
            var cond = Prop(w, used, "condition");
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
            var source = Prop(w, used, "source", "sourceList");
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
            var source = Prop(w, used, "source", "sourceRecordList");
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
                if (WType(w).EndsWith("cell"))
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
            var viaProp = Prop(w, used, "sourceBlock");   // the fact repeats the widget's own sourceBlock key (Export.cs 632)
            var source = S(w["sourceBlock"]) ?? viaProp ?? "";
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
            // An event handler of the instance: `events` {event: handler | {action,args}} (Export.cs 605), or a prop named after an Events entry.
            var handlers = (w["events"] as JsonObject ?? []).Select(kv => (name: kv.Key, handler: S(kv.Value is JsonObject h ? h["action"] : kv.Value))).ToList();
            if (Prop(w, used, "notify", "onNotify") is { } notify) handlers.Add(("Notify", notify));
            foreach (var (evt, handler) in handlers)
                Todo($"{at} handles {(evt.StartsWith("On", StringComparison.OrdinalIgnoreCase) ? evt : "On" + evt)} with {handler}, which cannot be translated by rule; create an event on the block {local} (the spec's events key), fire it with triggerEvent where the block called Notify, and set events on this instance to call {handler}");
            return Wrapped(w, used, o);
        }

        // Rule 6: Edit Record, Show Record and Form become a Form with a local variable for the record.
        JsonObject Form(JsonObject w, HashSet<string> used)
        {
            var at = Label(w);
            var formName = !Blank(S(w["name"])) ? S(w["name"])! : "Form";
            var local = formName + "Record";
            var source = Prop(w, used, "source", "sourceRecord");
            string? type = null;   // the record type is the type of the source expression; the export does not give it
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
            var wrapper = top.Count == 1 && top[0] is JsonObject only && WType(only) == "blockinstance" && only["placeholders"] is JsonObject { Count: > 0 } && IsLayoutInstance(only) ? only : null;
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
            return Leaf(source).Contains("layout", StringComparison.OrdinalIgnoreCase)
                || !cx.LocalBlocks.Any(b => b.Equals(Leaf(source), StringComparison.OrdinalIgnoreCase));
        }
    }

    // ---------- self-test ----------

    // Built-in samples in the form Export.cs writes (the table at the top): `kind` and `type` on every widget, no `_reads`, empty values left out, expressions
    // as text, node names made up where the original had none. Each rule of the translator is exercised at least once; the failures come back.
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
        //    a button that submits through Ajax, and two actions with an Ajax Refresh node (the export cannot say what it refreshes).
        var orders = Parse("""
        { "kind": "Screen", "name": "Orders", "flow": "MainFlow", "path": "MainFlow/Orders", "moduleKind": "Traditional", "title": "\"Orders\"",
          "inputs": [ { "name": "CustomerId", "type": "Customer Identifier", "mandatory": false, "default": "" } ],
          "locals": [ { "name": "Search", "type": "Text", "default": "\"\"" } ],
          "access": { "anonymous": false, "registered": true, "roles": ["OrderManager"] },
          "styleSheet": ".orders { margin: 0; }", "javaScript": "",
          "preparation": { "complete": true, "nodes": [
            { "kind": "aggregate", "name": "GetOrders", "sources": ["Order"], "filters": ["Order.Name like \"%\" + Search + \"%\""],
              "sorts": [ { "attribute": "Order.Id", "descending": true } ], "maxRecords": "50", "next": "End", "_path": "MainFlow/Orders/Preparation/GetOrders" } ] },
          "actions": [
            { "name": "DoSearch", "_path": "MainFlow/Orders/DoSearch", "flow": { "complete": false,
              "unsupported": [ { "path": "MainFlow/Orders/DoSearch/AjaxRefresh1", "kind": "AjaxRefreshNode", "why": "a spec flow has no Ajax Refresh node (Traditional Web only); the properties that could be read are in _props, but the widgets it refreshes are a list that the reader does not read" } ],
              "nodes": [
                { "kind": "assign", "name": "Reset", "assign": { "Search": "Trim(Search)" }, "next": "AjaxRefresh1", "_path": "MainFlow/Orders/DoSearch/Reset" },
                { "kind": "AjaxRefreshNode", "name": "AjaxRefresh1", "_props": { "Visible": "True" }, "next": "End", "_path": "MainFlow/Orders/DoSearch/AjaxRefresh1" } ] } },
            { "name": "Tidy", "flow": { "complete": false,
              "unsupported": [ { "path": "p", "kind": "AjaxRefreshNode", "why": "a spec flow has no Ajax Refresh node (Traditional Web only); the properties that could be read are in _props, but the widgets it refreshes are a list that the reader does not read" } ],
              "nodes": [
                { "kind": "assign", "name": "A", "assign": { "Search": "\"\"" }, "next": "R" },
                { "kind": "AjaxRefreshNode", "name": "R", "next": "B" },
                { "kind": "assign", "name": "B", "assign": { "Search": "\"x\"" }, "next": "End" } ] } } ],
          "widgets": [ { "kind": "WebBlockInstance", "type": "BlockInstance", "name": "Layout", "props": { "sourceBlock": "Layouts.Main" }, "sourceBlock": "Layouts.Main", "placeholders": {
            "Title": [ { "kind": "WebText", "type": "Text", "props": { "text": "Orders" } } ],
            "MainContent": [
              { "kind": "WebContainer", "type": "Container", "name": "Header", "props": { "style": "\"header\"" }, "children": [
                { "kind": "WebInput", "type": "Input", "name": "SearchInput", "props": { "variable": "Search", "onChange": "DoSearch" } },
                { "kind": "WebButton", "type": "Button", "name": "SearchButton", "props": { "method": "AjaxSubmit", "onClick": "DoSearch" }, "children": [ { "kind": "WebText", "type": "Text", "props": { "text": "Search" } } ] },
                { "kind": "WebExpression", "type": "Expression", "name": "Count", "props": { "value": "OrdersTable.LineCount" } } ] },
              { "kind": "WebTableRecords", "type": "TableRecords", "name": "OrdersTable", "props": { "source": "GetOrders.List", "style": "\"table\"" },
                "header": [ { "kind": "WebCell", "type": "Cell", "children": [ { "kind": "WebText", "type": "Text", "props": { "text": "Name" } } ] } ],
                "row": [ { "kind": "WebCell", "type": "Cell", "children": [
                  { "kind": "WebLink", "type": "Link", "name": "Open", "props": { "method": "Navigate", "onClick": "OrderDetail", "destination": "OrderDetail" }, "args": { "OrderId": "GetOrders.List.Current.Order.Id" },
                    "children": [ { "kind": "WebExpression", "type": "Expression", "props": { "value": "GetOrders.List.Current.Order.Name" } } ] } ] } ] } ] } } ] }
        """);
        var r = Translate(orders, layout);
        Check(r.Status == "partial" && r.Todo.Count == 2 && Has(r.Todo, "DoSearch", "Ajax Refresh", "cannot read which widgets") && Has(r.Todo, "Tidy", "node R"), $"Orders: status {r.Status}, todo [{string.Join(" | ", r.Todo)}] (partial: only the two removed Ajax Refresh nodes are left)");
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
        Check(At(s, "actions", 0, "flow")!.AsArray().Count == 1 && Text(s, "actions", 0, "flow", 0, "next") == "End" && At(s, "actions", 0, "flow", 0, "_path") is null, "Orders: an Ajax Refresh node is removed (the export cannot read its widgets) and the connector before it goes to End");
        Check(Text(s, "actions", 1, "flow", 0, "next") == "B" && At(s, "actions", 1, "flow")!.AsArray().Count == 2, "Orders: the connectors around a removed Ajax Refresh are joined");
        Check(Text(s, "placeholders", "MainContent", 0, "children", 1, "onClick") == "DoSearch" && Text(s, "placeholders", "MainContent", 0, "children", 0, "onChange") == "DoSearch", "Orders: submit buttons and inputs call the client action");

        // Layout placeholder with no match, and no layout at all (rule 1).
        var layoutless = Translate(orders, plain);
        Check(At(layoutless.Spec, "layout") is null && At(layoutless.Spec, "placeholders") is null && Text(layoutless.Spec, "widgets", 0, "type") == "Text" && At(layoutless.Spec, "widgets")!.AsArray().Count == 3, "Orders without a layout: one flat widgets list");
        var odd = (JsonObject)orders.DeepClone(); ((JsonObject)At(odd, "widgets", 0, "placeholders")!)["Sidebar"] = JsonNode.Parse("""[ { "kind": "WebText", "type": "Text", "props": { "text": "Side" } } ]""");
        var oddResult = Translate(odd, layout);
        Check(Has(oddResult.Todo, "Sidebar", "MainContent") && Text(oddResult.Spec, "placeholders", "MainContent", 2, "text") == "Side", "Orders with an unmatched placeholder: its content goes to the main placeholder and a Todo says so");

        // 2. A detail screen: Edit Record form, save action with a wrapper, feedback message and redirect, validation.
        var detail = Parse("""
        { "kind": "Screen", "name": "OrderDetail", "flow": "MainFlow", "moduleKind": "Traditional", "title": "\"Order\"",
          "inputs": [ { "name": "OrderId", "type": "Order Identifier", "mandatory": true, "default": "" } ], "preparation": { "complete": true, "nodes": [ { "kind": "aggregate", "name": "GetOrder", "sources": ["Order"], "filters": ["Order.Id = OrderId"] } ] },
          "actions": [ { "name": "Save", "flow": { "complete": true, "nodes": [
            { "kind": "execute", "name": "Store", "action": "CreateOrUpdateOrder", "args": { "Source": "OrderForm.Record" }, "next": "Msg", "_ref": "CreateOrUpdateOrder" },
            { "kind": "execute", "name": "Msg", "action": "RichWidgets.Feedback_Message", "_ref": "RichWidgets.Feedback_Message", "args": { "Message": "\"Saved\"", "Type": "Entities.MessageType.Success" }, "next": "Go" },
            { "kind": "destination", "name": "Go", "screen": "Orders", "args": { "CustomerId": "OrderForm.Record.CustomerId" } } ] } } ],
          "widgets": [ { "kind": "WebEditRecord", "type": "EditRecord", "name": "OrderForm", "props": { "sourceRecord": "GetOrder.List.Current.Order" }, "children": [
            { "kind": "WebRow", "type": "Row", "children": [ { "kind": "WebCell", "type": "Cell", "children": [
              { "kind": "WebInput", "type": "Input", "name": "NameInput", "props": { "variable": "OrderForm.Record.Name", "mandatory": "True", "inputType": "Text" } },
              { "kind": "WebInput", "type": "Input", "name": "EmailInput", "props": { "variable": "OrderForm.Record.Email", "validationType": "Email" } },
              { "kind": "WebInput", "type": "Input", "name": "PhoneInput", "props": { "variable": "OrderForm.Record.Phone", "validationType": "Phone Number" } },
              { "kind": "WebExpression", "type": "Expression", "name": "Valid", "props": { "value": "NameInput.Valid and not OrderForm.Valid" } },
              { "kind": "WebButton", "type": "Button", "name": "SaveButton", "props": { "method": "Submit", "onClick": "Save", "confirm": "\"Sure?\"" }, "children": [ { "kind": "WebText", "type": "Text", "props": { "text": "Save" } } ] } ] } ] } ] } ] }
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
        { "kind": "Screen", "name": "Dash", "flow": "MainFlow", "moduleKind": "Traditional", "preparation": { "complete": true, "nodes": [
            { "kind": "if", "name": "IsAdmin", "condition": "Role", "true": "Load", "false": "End" },
            { "kind": "execute", "name": "Load", "action": "LoadDashboard", "_ref": "LoadDashboard" },
            { "kind": "aggregate", "name": "GetStats", "sources": ["Stat"] } ] },
          "actions": [], "widgets": [ { "kind": "WebText", "type": "Text", "props": { "text": "Hi" } } ] }
        """);
        var pr = Translate(prep, plain);
        Check(pr.Status == "partial" && At(pr.Spec, "aggregates") is null && Text(pr.Spec, "widgets", 0, "text") == "Hi" && Has(pr.Todo, "Preparation", "if IsAdmin", "execute Load", "data action"), "Dash: the Preparation is not translated, the Todo lists its nodes and asks for a data action");

        // 4. A screen action with an aggregate becomes a stub that keeps the widget binding valid (rule 3).
        var stub = Parse("""
        { "kind": "Screen", "name": "Report", "flow": "MainFlow", "moduleKind": "Traditional", "locals": [ { "name": "Total", "type": "Integer", "default": "" } ],
          "preparation": null,
          "actions": [ { "name": "Calc", "inputs": [ { "name": "Year", "type": "Integer", "mandatory": true } ], "locals": [ { "name": "Tmp", "type": "Integer", "default": "0" } ], "flow": { "complete": true, "nodes": [
            { "kind": "aggregate", "name": "CountOrders", "sources": ["Order"] }, { "kind": "assign", "name": "Set", "assign": { "Total": "CountOrders.Count" } } ] } },
            { "name": "Odd", "flow": { "complete": false, "unsupported": [ { "path": "x", "kind": "CommitTransactionNode", "why": "a spec flow has no CommitTransactionNode node; the properties that could be read are in _props" } ],
              "nodes": [ { "kind": "CommitTransactionNode", "name": "CommitTransaction1", "next": "End", "_path": "x" } ] } } ],
          "widgets": [ { "kind": "WebButton", "type": "Button", "name": "Go", "props": { "method": "Submit", "onClick": "Calc" }, "children": [ { "kind": "WebText", "type": "Text", "props": { "text": "Go" } } ] } ] }
        """);
        var st = Translate(stub, plain);
        Check(st.Status == "partial" && Text(st.Spec, "actions", 0, "name") == "Calc" && Text(st.Spec, "actions", 0, "inputs", 0, "name") == "Year" && Text(st.Spec, "actions", 0, "locals", 0, "name") == "Tmp"
            && Text(st.Spec, "actions", 0, "flow", 0, "kind") == "comment" && (Text(st.Spec, "actions", 0, "flow", 0, "text") ?? "").StartsWith("TODO migrate:") && At(st.Spec, "actions", 0, "flow")!.AsArray().Count == 1, "Report: an action with an aggregate is a stub with its inputs and locals and one TODO comment");
        Check(Text(st.Spec, "widgets", 0, "onClick") == "Calc" && Has(st.Todo, "Calc", "aggregate", "CountOrders") && Has(st.Todo, "Odd", "CommitTransaction"), "Report: the button still calls the stub and the Todo says what the original did; an unsupported node makes a stub too");

        // 5. A web block with a Notify; the block has a placeholder and an input.
        var blockInput = Parse("""
        { "kind": "Block", "name": "OrderCard", "flow": "Common", "moduleKind": "Traditional", "inputs": [ { "name": "OrderId", "type": "Order Identifier", "mandatory": true, "default": "" } ], "access": { "anonymous": true, "registered": true }, "preparation": null,
          "actions": [ { "name": "Pick", "flow": { "complete": true, "nodes": [ { "kind": "execute", "name": "Tell", "action": "Notify", "args": { "Message": "\"picked\"" } } ] } } ],
          "widgets": [ { "kind": "WebLink", "type": "Link", "name": "PickLink", "props": { "onClick": "Pick" }, "children": [ { "kind": "WebText", "type": "Text", "props": { "text": "Pick" } } ] }, { "kind": "WebPlaceholder", "type": "Placeholder", "name": "Extra" } ] }
        """);
        var b = Translate(blockInput, layout);
        Check(b.Kind == "Block" && b.Status == "partial" && At(b.Spec, "access") is null && At(b.Spec, "layout") is null && At(b.Spec, "title") is null, "OrderCard: a block has no layout, title or access");
        Check(Text(b.Spec, "actions", 0, "flow", 0, "kind") == "comment" && Has(b.Todo, "Pick", "Notify", "events", "triggerEvent"), "OrderCard: the action with Notify is a stub and the Todo names the event to create");
        Check(Text(b.Spec, "widgets", 1, "type") == "Placeholder" && Text(b.Spec, "widgets", 0, "onClick") == "Pick", "OrderCard: widgets and the placeholder are carried");

        // 6. Block instances: a local block, one of another module, and an OnNotify handler.
        var inst = Parse("""
        { "kind": "Screen", "name": "Cards", "flow": "MainFlow", "moduleKind": "Traditional", "preparation": null,
          "actions": [ { "name": "Got", "flow": { "complete": true, "nodes": [] } } ],
          "widgets": [
            { "kind": "WebBlockInstance", "type": "BlockInstance", "name": "Card1", "sourceBlock": "OrderCard", "events": { "Notify": "Got" }, "args": { "OrderId": "OrderId" }, "placeholders": { "Extra": [ { "kind": "WebText", "type": "Text", "props": { "text": "x" } } ] } },
            { "kind": "WebBlockInstance", "type": "BlockInstance", "name": "Tip", "sourceBlock": "RichWidgets.Tooltip", "args": { "Text": "\"hi\"" }, "placeholders": { "Content": [ { "kind": "WebText", "type": "Text", "props": { "text": "inner" } } ] } } ] }
        """);
        var i6 = Translate(inst, plain);
        Check(Text(i6.Spec, "widgets", 0, "type") == "BlockInstance" && Text(i6.Spec, "widgets", 0, "sourceBlock") == "OrderCard" && Text(i6.Spec, "widgets", 0, "args", "OrderId") == "OrderId" && Text(i6.Spec, "widgets", 0, "placeholders", "Extra", 0, "text") == "x", "Cards: an instance of a local block keeps its name, args and placeholder content");
        Check(Has(i6.Todo, "Card1", "OnNotify", "events"), "Cards: OnNotify on an instance is a Todo that names the event");
        Check(Text(i6.Spec, "widgets", 1, "type") == "Container" && (Text(i6.Spec, "widgets", 1, "children", 0, "text") ?? "").StartsWith("TODO migrate") && Text(i6.Spec, "widgets", 1, "children", 1, "text") == "inner" && Has(i6.Todo, "Tip", "RichWidgets.Tooltip"), "Cards: an instance of a block of another module is a Todo container that keeps its content");

        // 7. Session variables, with and without a client variable; a string literal is not touched.
        var sess = Parse("""
        { "kind": "Screen", "name": "Hello", "flow": "MainFlow", "moduleKind": "Traditional", "preparation": null,
          "actions": [ { "name": "Remember", "flow": { "complete": true, "nodes": [ { "kind": "assign", "name": "Keep", "assign": { "Session.Lang": "\"en\"", "Session.Last": "Session.Last + 1" } } ] } } ],
          "widgets": [ { "kind": "WebExpression", "type": "Expression", "name": "Who", "props": { "value": "\"Session.Lang: \" + Session.Lang + Session.UserName + MyEntity.Session" } } ] }
        """);
        var se = Translate(sess, plain);
        Check(Text(se.Spec, "widgets", 0, "value") == "\"Session.Lang: \" + Client.Lang + Session.UserName + MyEntity.Session", "Hello: Session.<client variable> becomes Client.<it>, other session reads and string literals stay");
        Check(Text(se.Spec, "actions", 0, "flow", 0, "assign", "Client.Lang") == "\"en\"" && Text(se.Spec, "actions", 0, "flow", 0, "assign", "Session.Last") == "Session.Last + 1", "Hello: assignment targets are rewritten too");
        Check(se.SessionVariables.Order().SequenceEqual(["Lang", "Last", "UserName"]) && Has(se.Todo, "Session.UserName") && Has(se.Todo, "Session.Last") && !Has(se.Todo, "Session.Lang,"), "Hello: every session variable is recorded and the non-client ones are Todo lines");

        // 8. Widgets: Editable Table, an unknown kind, a combo box, a radio button, a checkbox, an image, an upload, a popup, an unescaped expression.
        var misc = Parse("""
        { "kind": "Screen", "name": "Misc", "flow": "MainFlow", "moduleKind": "Traditional", "locals": [ { "name": "Pick", "type": "Integer", "default": "" } ], "preparation": { "complete": true, "nodes": [ { "kind": "aggregate", "name": "GetKinds", "sources": ["Kind"] } ] },
          "actions": [], "widgets": [
            { "kind": "WebEditableTable", "type": "EditableTable", "name": "Grid", "children": [ { "kind": "WebText", "type": "Text", "props": { "text": "cell" } } ] },
            { "kind": "WebTabs", "type": "Tabs", "name": "TabSet", "children": [ { "kind": "WebText", "type": "Text", "props": { "text": "kept" } } ] },
            { "kind": "WebCombo", "type": "Combo", "name": "KindBox", "props": { "variable": "Pick", "source": "GetKinds.List", "valueAttribute": "GetKinds.List.Current.Kind.Id", "labelAttribute": "GetKinds.List.Current.Kind.Name", "emptyValue": "\"none\"" } },
            { "kind": "WebRadioButton", "type": "RadioButton", "name": "Opt", "props": { "variable": "Pick", "value": "1" } },
            { "kind": "WebCheckbox", "type": "Checkbox", "name": "Agree", "props": { "variable": "Flag", "visible": "Pick > 0" } },
            { "kind": "WebImage", "type": "Image", "name": "Logo", "props": { "image": "Logo" } },
            { "kind": "WebUpload", "type": "Upload", "name": "File", "props": { "variable": "Data", "fileName": "FileName" } },
            { "kind": "WebPopup", "type": "Popup", "name": "Dlg", "children": [ { "kind": "WebText", "type": "Text", "props": { "text": "inside" } } ] },
            { "kind": "WebExpression", "type": "Expression", "name": "Raw", "props": { "value": "Html", "escapeContent": "No", "visible": "Pick = 1" } },
            { "kind": "WebIf", "type": "If", "name": "Branch", "props": { "condition": "Pick = 1" }, "true": [ { "kind": "WebText", "type": "Text", "props": { "text": "yes" } } ], "false": [ { "kind": "WebText", "type": "Text", "props": { "text": "no" } } ] },
            { "kind": "WebListRecords", "type": "ListRecords", "name": "KindList", "props": { "source": "GetKinds.List" }, "row": [ { "kind": "WebExpression", "type": "Expression", "props": { "value": "GetKinds.List.Current.Kind.Name" } } ] } ] }
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

        // 10. Every node kind of a client flow: a server action call that stays as it is, an async JavaScript node, a comment with a made-up name, and the
        //     screen's own JavaScript, which is not carried. End nodes are not listed: a connector says "End".
        var flows = Parse("""
        { "kind": "Screen", "name": "Flows", "flow": "MainFlow", "moduleKind": "Traditional", "javaScript": "alert(1);",
          "actions": [ { "name": "Run", "inputs": [ { "name": "Id", "type": "Integer", "mandatory": true, "default": "" } ], "flow": { "complete": true, "nodes": [
            { "kind": "if", "name": "Ok", "condition": "Id > 0", "true": "Each", "false": "Bad", "_path": "p" },
            { "kind": "forEach", "name": "Each", "recordList": "Items.List", "cycle": "Place", "next": "Pick" },
            { "kind": "execute", "name": "Place", "action": "PlaceOrder", "args": { "Id": "Items.List.Current.Item.Id", "Qty": "2" }, "next": "Each", "_ref": "PlaceOrder" },
            { "kind": "switch", "name": "Pick", "cases": [ { "condition": "Id = 1", "next": "Js" } ], "otherwise": "Bad" },
            { "kind": "javaScript", "name": "Js", "javaScript": "alert(1);", "_async": true, "next": "Say" },
            { "kind": "message", "name": "Say", "message": "\"Done\"", "type": "success", "next": "End" },
            { "kind": "raiseException", "name": "Bad", "exception": "InvalidOrder", "message": "\"Bad id\"" },
            { "kind": "comment", "name": "Comment1", "text": "note" } ] } } ] }
        """);
        var fl = Translate(flows, plain);
        var fn = At(fl.Spec, "actions", 0, "flow")!.AsArray();
        Check(fl.Status == "partial" && fl.Todo.Count == 2 && Has(fl.Todo, "own JavaScript") && Has(fl.Todo, "Js", "asynchronous"), "Flows: the screen's JavaScript and the async JavaScript node are left (a flow of supported nodes is translated whole)");
        Check(fn.Count == 8 && Text(fn[0], "kind") == "if" && Text(fn[1], "kind") == "forEach" && Text(fn[1], "recordList") == "Items.List" && Text(fn[2], "action") == "PlaceOrder" && Text(fn[2], "args", "Qty") == "2"
            && Text(fn[3], "cases", 0, "next") == "Js" && Text(fn[3], "otherwise") == "Bad" && Text(fn[4], "kind") == "javaScript" && Text(fn[4], "javaScript") == "alert(1);" && At(fn[4], "_async") is null && Text(fn[4], "next") == "Say"
            && Text(fn[5], "type") == "success" && Text(fn[5], "next") == "End" && Text(fn[6], "kind") == "raiseException" && Text(fn[7], "kind") == "comment" && At(fn[7], "name") is null && At(fn[0], "_path") is null && fl.EntityActions.Count == 0,
            "Flows: if, forEach, a server action call, switch, javaScript, message, raiseException and comment are carried without _ keys");

        // 9. Emails, other module kinds and a failed export are worklist entries.
        var email = Translate(Parse("""{ "kind": "Email", "name": "Welcome", "flow": "Emails", "moduleKind": "Traditional", "subject": "\"Hello\"" }"""), layout);
        Check(email.Spec is null && email.Status == "worklist" && email.Kind == "Email" && Has(email.Todo, "Welcome", "emails", "subject \"Hello\""), "Welcome: an email is a worklist entry that names the emails key");
        var reactive = Translate(Parse("""{ "kind": "Screen", "name": "R", "moduleKind": "Reactive" }"""), layout);
        Check(reactive.Spec is null && reactive.Status == "worklist", "R: a Reactive element is not translated");
        var failed = Translate(Parse("""{ "element": "X", "name": "X", "kind": "Screen", "error": "no such screen" }"""), layout);
        Check(failed.Spec is null && failed.Status == "worklist", "X: an element that failed to export is a worklist entry");
        var broken = Translate(Parse("""{ "kind": "Screen", "name": "Broken", "moduleKind": "Traditional", "actions": [ { "name": "A", "flow": "not an object" } ], "widgets": "no" }"""), layout);
        Check(broken.Status is "partial" or "worklist", "Broken: an unreadable input does not throw");

        // 11. tests/export-sample.json (embedded as ExportSampleJson).
        //     No layout, the block WebCard is local, the entity action has its wrapper: what is left is filling the form's record. The two "method could not
        //     be read" notes are not Todo lines: a screen destination is a Navigate, an action an ordinary Submit.
        var sampleContext = new Context("Shop", new Dictionary<string, string> { ["CreateOrUpdateWebItem"] = "WebItem_CreateOrUpdate" }, new HashSet<string>(), new HashSet<string> { "WebCard" }, null, []);
        var sm = Translate(Parse(ExportSampleJson), sampleContext);
        var ss = sm.Spec;
        Check(sm.Status == "partial" && sm.Todo.Count == 1 && Has(sm.Todo, "ItemForm", "ItemFormRecord") && !Has(sm.Todo, "method"), $"Sample: only the form record is left: [{string.Join(" | ", sm.Todo)}]");
        Check(Text(ss, "name") == "WebItems" && Text(ss, "flow") == "MainFlow" && Text(ss, "title") == "\"Web items\"" && Text(ss, "access", "registered") == "true" && Text(ss, "locals", 1, "name") == "Item" && Text(ss, "locals", 2, "name") == "ItemFormRecord" && Text(ss, "locals", 2, "type") == "WebItem", "Sample: element facts and the form's record local");
        Check(Text(ss, "aggregates", 0, "name") == "FetchItems" && Text(ss, "aggregates", 0, "sources", 0) == "WebItem" && Text(ss, "aggregates", 0, "maxRecords") == "10" && At(ss, "aggregates", 0, "_path") is null, "Sample: the Preparation aggregate");
        Check(Text(ss, "actions", 0, "flow", 0, "assign", "Item.Name") == "ItemName" && Text(ss, "actions", 0, "flow", 1, "action") == "Shop.WebItem_CreateOrUpdate" && Text(ss, "actions", 0, "flow", 1, "next") == "End" && sm.EntityActions.SequenceEqual(["CreateOrUpdateWebItem"]), "Sample: the Save action, its entity action through the wrapper");
        Check(Text(ss, "widgets", 0, "type") == "Container" && Text(ss, "widgets", 0, "style") == "\"editor\"" && Text(ss, "widgets", 0, "children", 0, "variable") == "ItemName" && Text(ss, "widgets", 0, "children", 1, "onClick") == "Save" && Text(ss, "widgets", 0, "children", 1, "children", 0, "text") == "Save", "Sample: container, input and the button whose method is inferred");
        Check(Text(ss, "widgets", 1, "type") == "BlockInstance" && Text(ss, "widgets", 1, "sourceBlock") == "WebCard" && Text(ss, "widgets", 1, "args", "Caption") == "\"Summary\"", "Sample: the block instance");
        Check(Text(ss, "widgets", 2, "type") == "If" && Text(ss, "widgets", 2, "condition") == "not FetchItems.List.Empty" && Text(ss, "widgets", 2, "trueBranch", 0, "source") == "FetchItems.List" && Text(ss, "widgets", 2, "falseBranch", 0, "text") == "No items"
            && Text(ss, "widgets", 2, "trueBranch", 0, "row", 0, "children", 0, "onClick", "destination") == "WebDetail" && Text(ss, "widgets", 2, "trueBranch", 0, "row", 0, "children", 0, "onClick", "args", "ItemId") == "FetchItems.List.Current.WebItem.Id", "Sample: the If with a table in its true branch and the navigating link");
        Check(Text(ss, "widgets", 3, "type") == "List" && Text(ss, "widgets", 3, "source") == "FetchItems.List" && Text(ss, "widgets", 4, "type") == "Form" && Text(ss, "widgets", 4, "children", 0, "text") == "Edit item", "Sample: the list and the form (rows and cells are flattened)");
        return failures;
    }

    // A COPY of tests/export-sample.json (the hand-built export of a Traditional screen; the launcher cannot read tests/ at run time). Keep the two in step:
    // when the file changes, paste it here again and check the assertions of SelfTest (section 11).
    const string ExportSampleJson = """"
{
  "kind": "Screen",
  "name": "WebItems",
  "flow": "MainFlow",
  "path": "MainFlow/WebItems",
  "moduleKind": "Traditional",
  "title": "\"Web items\"",
  "locals": [
    { "name": "ItemName", "type": "Text", "default": "" },
    { "name": "Item", "type": "WebItem", "default": "" }
  ],
  "access": { "anonymous": false, "registered": true },
  "styleSheet": "",
  "javaScript": "",
  "preparation": {
    "nodes": [
      {
        "kind": "aggregate",
        "name": "FetchItems",
        "sources": ["WebItem"],
        "maxRecords": "10",
        "next": "End",
        "_path": "MainFlow/WebItems/WebPreparation[0]/FetchItems"
      }
    ],
    "complete": true
  },
  "actions": [
    {
      "name": "Save",
      "flow": {
        "nodes": [
          {
            "kind": "assign",
            "name": "SetName",
            "assign": { "Item.Name": "ItemName" },
            "next": "CreateOrUpdateWebItem",
            "_path": "MainFlow/WebItems/Save/SetName"
          },
          {
            "kind": "execute",
            "name": "CreateOrUpdateWebItem",
            "action": "CreateOrUpdateWebItem",
            "args": { "Source": "Item" },
            "next": "End",
            "_path": "MainFlow/WebItems/Save/CreateOrUpdateWebItem",
            "_ref": "CreateOrUpdateWebItem"
          }
        ],
        "complete": true
      },
      "_path": "MainFlow/WebItems/Save"
    }
  ],
  "widgets": [
    {
      "kind": "WebContainer",
      "type": "Container",
      "name": "Editor",
      "path": "MainFlow/WebItems/Editor",
      "props": { "style": "\"editor\"" },
      "children": [
        {
          "kind": "WebInput",
          "type": "Input",
          "name": "NameInput",
          "path": "MainFlow/WebItems/Editor/NameInput",
          "props": { "variable": "ItemName" }
        },
        {
          "kind": "WebButton",
          "type": "Button",
          "name": "SaveButton",
          "path": "MainFlow/WebItems/Editor/SaveButton",
          "props": { "onClick": "Save", "label": "\"Save\"" }
        }
      ]
    },
    {
      "kind": "WebBlockInstance",
      "type": "BlockInstance",
      "name": "Summary",
      "path": "MainFlow/WebItems/Summary",
      "props": { "sourceBlock": "WebCard" },
      "args": { "Caption": "\"Summary\"" },
      "sourceBlock": "WebCard"
    },
    {
      "kind": "WebIf",
      "type": "If",
      "path": "MainFlow/WebItems/WebIf[0]",
      "props": { "condition": "not FetchItems.List.Empty" },
      "true": [
        {
          "kind": "WebTableRecords",
          "type": "TableRecords",
          "name": "ItemsTable",
          "path": "MainFlow/WebItems/WebIf[0]/WebIfBranch[0]/ItemsTable",
          "props": { "sourceRecordList": "FetchItems.List" },
          "header": [
            {
              "kind": "WebCell",
              "type": "Cell",
              "path": "MainFlow/WebItems/WebIf[0]/WebIfBranch[0]/ItemsTable/WebTableRow[0]/WebCell[0]",
              "children": [
                {
                  "kind": "WebText",
                  "type": "Text",
                  "path": "MainFlow/WebItems/WebIf[0]/WebIfBranch[0]/ItemsTable/WebTableRow[0]/WebCell[0]/WebText[0]",
                  "props": { "text": "Name" }
                }
              ]
            }
          ],
          "row": [
            {
              "kind": "WebCell",
              "type": "Cell",
              "path": "MainFlow/WebItems/WebIf[0]/WebIfBranch[0]/ItemsTable/WebTableRow[1]/WebCell[0]",
              "children": [
                {
                  "kind": "WebLink",
                  "type": "Link",
                  "name": "OpenDetail",
                  "path": "MainFlow/WebItems/WebIf[0]/WebIfBranch[0]/ItemsTable/WebTableRow[1]/WebCell[0]/OpenDetail",
                  "props": { "onClick": "WebDetail", "destination": "WebDetail" },
                  "args": { "ItemId": "FetchItems.List.Current.WebItem.Id" },
                  "children": [
                    {
                      "kind": "WebExpression",
                      "type": "Expression",
                      "path": "MainFlow/WebItems/WebIf[0]/WebIfBranch[0]/ItemsTable/WebTableRow[1]/WebCell[0]/OpenDetail/WebExpression[0]",
                      "props": { "value": "FetchItems.List.Current.WebItem.Name" }
                    }
                  ]
                }
              ]
            }
          ]
        }
      ],
      "false": [
        {
          "kind": "WebText",
          "type": "Text",
          "path": "MainFlow/WebItems/WebIf[0]/WebIfBranch[1]/WebText[0]",
          "props": { "text": "No items" }
        }
      ]
    },
    {
      "kind": "WebListRecords",
      "type": "ListRecords",
      "name": "ItemsList",
      "path": "MainFlow/WebItems/ItemsList",
      "props": { "sourceRecordList": "FetchItems.List" },
      "children": [
        {
          "kind": "WebExpression",
          "type": "Expression",
          "path": "MainFlow/WebItems/ItemsList/WebExpression[0]",
          "props": { "value": "FetchItems.List.Current.WebItem.Name" }
        }
      ]
    },
    {
      "kind": "WebEditRecord",
      "type": "EditRecord",
      "name": "ItemForm",
      "path": "MainFlow/WebItems/ItemForm",
      "props": { "sourceRecord": "Item" },
      "children": [
        {
          "kind": "WebRow",
          "type": "Row",
          "path": "MainFlow/WebItems/ItemForm/WebRow[0]",
          "children": [
            {
              "kind": "WebCell",
              "type": "Cell",
              "path": "MainFlow/WebItems/ItemForm/WebRow[0]/WebCell[0]",
              "children": [
                {
                  "kind": "WebText",
                  "type": "Text",
                  "path": "MainFlow/WebItems/ItemForm/WebRow[0]/WebCell[0]/WebText[0]",
                  "props": { "text": "Edit item" }
                }
              ]
            }
          ]
        }
      ]
    }
  ],
  "notes": [
    "MainFlow/WebItems/Editor/SaveButton: the method of its On Click (Submit, Ajax Submit or Navigate) could not be read",
    "MainFlow/WebItems/WebIf[0]/WebIfBranch[0]/ItemsTable/WebTableRow[1]/WebCell[0]/OpenDetail: the method of its On Click (Submit, Ajax Submit or Navigate) could not be read"
  ]
}
"""";
}
