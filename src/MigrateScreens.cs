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
// _targets (optional, a node the spec cannot express): {property: element name | [names]}, what an AjaxRefreshNode refreshes; inspect's session variable rows may have `default` (optional).
// NOT in the export: the widgets an Ajax Refresh refreshes when `_targets` is absent, the click method when no property says it, a Link's own
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
        IReadOnlyList<string> LayoutPlaceholders,           // its placeholder names
        IReadOnlySet<string>? NotifyingBlocks = null,       // blocks of the old module that call Notify (they get a Notify event)
        // Server actions a translated screen may call, as it calls them (a copied action "Name", a wrapper "Old.Order_Create"): output name -> type.
        // Consecutive calls of these in a screen action are merged into one server action (one request).
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? ServerOutputs = null);

    // The blocks among the exports whose actions call Notify (or Deprecated_Notify): in Reactive each gets a Notify event.
    internal static HashSet<string> NotifyingBlocks(IEnumerable<JsonObject> exports) =>
        new(exports.Where(e => string.Equals(Run.S(e["kind"]), "Block", StringComparison.OrdinalIgnoreCase) && Run.S(e["name"]) is not null
                && (e["actions"] as JsonArray ?? []).OfType<JsonObject>().Any(a => ((a["flow"] as JsonObject)?["nodes"] as JsonArray ?? []).OfType<JsonObject>().Any(Run.IsNotify)))
            .Select(e => Run.S(e["name"])!), StringComparer.OrdinalIgnoreCase);

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

    // The user exception that merged server actions raise when the user lacks the screen's roles; the plan adds it to the new module.
    internal const string AccessDenied = "AccessDenied";

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
        readonly Dictionary<string, JsonObject> aggDefs = new(StringComparer.OrdinalIgnoreCase);              // aggregate name -> its Preparation node
        readonly HashSet<string> prepWrites = new(StringComparer.OrdinalIgnoreCase);                          // variables the lifted Preparation logic assigns
        JsonObject? initAction;                                                                               // the client action behind onInitialize: { name, flow }
        readonly Dictionary<string, JsonObject> afterFetch = new(StringComparer.OrdinalIgnoreCase);           // aggregate name -> the client action behind its onAfterFetch
        readonly List<(string agg, string form, string expr)> afterFills = [];                                // form record = aggregate record, after each fetch
        readonly List<(string form, string expr)> initFills = [];                                             // form record = input or local, at initialize
        // Lists that page or sort the RichWidgets way (List_Navigation, List_Counter, List_SortColumn): table name -> the locals and handlers built.
        sealed record Paging(string Agg, string Start, string Max, string Handler);
        sealed record Sorting(string Agg, string Local, string Handler, List<string> Columns);
        readonly Dictionary<string, Paging> paging = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Sorting> sorting = new(StringComparer.OrdinalIgnoreCase);
        readonly List<JsonObject> extraActions = [];
        string? currentTable;
        bool notifies;                                                                                        // a block action calls Notify: the block gets the Notify event
        readonly HashSet<string> notifyHandlers = new(StringComparer.OrdinalIgnoreCase);                      // actions that handle a block's Notify
        string? currentAction;                                                                                 // the table whose header cells are being built

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
            var raw = S(w["type"]);
            if (Norm(raw) is "web" or "mobile") raw = null;   // an older export named some widgets only by their prefix: the kind is the widget
            var t = Norm(raw is { Length: > 0 } ? raw : S(w["kind"]));
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
            text = ListCalls(text);
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
            if (paging.TryGetValue(id, out var pg) && text.AsSpan(after).StartsWith(".LineCount", StringComparison.OrdinalIgnoreCase)
                && (after + 10 >= text.Length || !(char.IsLetterOrDigit(text[after + 10]) || text[after + 10] == '_')))
            { replacement = pg.Max; end = after + 10; return true; }
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

        // ---------- lists that page and sort (RichWidgets List_Navigation, List_Counter, List_SortColumn) ----------

        static readonly Regex StartIndexCall = new(@"\b(?:RichWidgets\.)?List_Navigation_GetStartIndex\s*\(\s*(\w+)\.Id\b[^()]*\)", RegexOptions.CultureInvariant);
        static readonly Regex OrderByCall = new(@"\b(?:RichWidgets\.)?List_SortColumn_GetOrderBy\s*\(\s*(\w+)\.Id\s*,\s*(""(?:[^""]|"""")*"")\s*\)", RegexOptions.CultureInvariant);

        // The calls of the RichWidgets list functions for a list that pages or sorts here become its locals.
        string ListCalls(string text)
        {
            if (currentAction is not null && notifyHandlers.Contains(currentAction) && text.Contains("NotifyGetMessage", StringComparison.Ordinal))
                text = Regex.Replace(text, @"\b(?:\(System\)\.)?NotifyGetMessage\s*\(\s*\)", "Message");
            if (paging.Count > 0 && text.Contains("List_Navigation_GetStartIndex", StringComparison.Ordinal))
                text = StartIndexCall.Replace(text, m => paging.TryGetValue(m.Groups[1].Value, out var p) ? p.Start : m.Value);
            if (sorting.Count > 0 && text.Contains("List_SortColumn_GetOrderBy", StringComparison.Ordinal))
                text = OrderByCall.Replace(text, m => sorting.TryGetValue(m.Groups[1].Value, out var s) ? s.Local : m.Value);
            return text;
        }

        // "{Contact}.[Name] DESC" (the SQL spelling of the RichWidgets sort) as an aggregate sort: "Contact.Name DESC".
        static string SortText(string sql) => Regex.Replace(sql, @"\{(\w+)\}\.\[(\w+)\]", "$1.$2");

        string FreeName(string wanted, string table, int count) =>
            count == 1 && !varTypes.ContainsKey(wanted) && !actionNames.Contains(wanted) && !extraLocals.ContainsKey(wanted) ? wanted : table + wanted;

        // Before the Preparation is read: a table or list fed by a Preparation aggregate that pages with List_Navigation (its Start Index reads
        // List_Navigation_GetStartIndex, or a List_Navigation instance names it) gets Integer locals StartIndex and MaxRecords (its Line Count), which
        // the aggregate uses, and a GoToPage handler for the OutSystems UI Pagination block. One whose aggregate sorts by List_SortColumn_GetOrderBy
        // gets a Text local TableSort (the default order, as an aggregate sort) and an OnSort handler. With several such lists, names get the table's name first.
        void ListPatterns()
        {
            var prep = Objs((el["preparation"] as JsonObject)?["nodes"]).Where(n => NodeKind(n) == "aggregate" && S(n["name"]) is not null)
                .ToDictionary(n => S(n["name"])!, StringComparer.OrdinalIgnoreCase);
            var navTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in widgetByName.Values.Concat(AllWidgets(el["widgets"])))
                if (WType(w) == "blockinstance" && Leaf(S(w["sourceBlock"]) ?? Prop(w, "sourceBlock") ?? "").Equals("List_Navigation", StringComparison.OrdinalIgnoreCase))
                    foreach (var (k, v) in w["args"] as JsonObject ?? [])
                        if (k.StartsWith("ListWidget", StringComparison.OrdinalIgnoreCase) && Regex.Match(S(v) ?? "", @"^\s*(\w+)\.Id\s*$") is { Success: true } t) navTargets.Add(t.Groups[1].Value);
            var paged = new List<(string table, string agg, string lines)>();
            var sorted = new List<(string table, string agg, string order)>();
            foreach (var (table, source) in tables)
            {
                var agg = source.Split('.')[0];
                if (!prep.TryGetValue(agg, out var def) || !widgetByName.TryGetValue(table, out var w)) continue;
                var lines = Prop(w, "lineCount");
                if (lines is not null && int.TryParse(lines.Trim(), out var n) && n > 0
                    && ((Prop(w, "startIndex") ?? "").Contains("List_Navigation_GetStartIndex", StringComparison.Ordinal) || navTargets.Contains(table)))
                    paged.Add((table, agg, lines.Trim()));
                foreach (var s in Objs(def["sorts"]))
                    if (OrderByCall.Match(S(s["attribute"]) ?? "") is { Success: true } m && m.Groups[1].Value.Equals(table, StringComparison.OrdinalIgnoreCase))
                    { sorted.Add((table, agg, Literal(m.Groups[2].Value) ?? "")); break; }
            }
            foreach (var (table, agg, lines) in paged)
            {
                string start = FreeName("StartIndex", table, paged.Count), max = FreeName("MaxRecords", table, paged.Count), handler = FreeName("GoToPage", table, paged.Count);
                extraLocals[start] = new JsonObject { ["name"] = start, ["type"] = "Integer", ["default"] = "0" };
                extraLocals[max] = new JsonObject { ["name"] = max, ["type"] = "Integer", ["default"] = lines };
                varTypes[start] = varTypes[max] = "Integer";
                extraActions.Add(new JsonObject
                {
                    ["name"] = handler,
                    ["inputs"] = new JsonArray(new JsonObject { ["name"] = "NewStartIndex", ["type"] = "Integer", ["mandatory"] = true }),
                    ["flow"] = new JsonArray(
                        new JsonObject { ["kind"] = "assign", ["name"] = "SetStartIndex", ["assign"] = new JsonObject { [start] = "NewStartIndex" }, ["next"] = "Refresh" + agg },
                        new JsonObject { ["kind"] = "refreshData", ["name"] = "Refresh" + agg, ["dataSource"] = agg }),
                });
                actionNames.Add(handler);
                paging[table] = new Paging(agg, start, max, handler);
            }
            foreach (var (table, agg, order) in sorted)
            {
                string local = FreeName("TableSort", table, sorted.Count), handler = FreeName("OnSort", table, sorted.Count);
                extraLocals[local] = new JsonObject { ["name"] = local, ["type"] = "Text", ["default"] = Quote(SortText(order)) };
                varTypes[local] = "Text";
                var flow = new JsonArray(
                    new JsonObject { ["kind"] = "if", ["name"] = "SameColumn", ["condition"] = $"{local} = ClickedColumn", ["true"] = "Descending", ["false"] = "Ascending" },
                    new JsonObject { ["kind"] = "assign", ["name"] = "Descending", ["assign"] = new JsonObject { [local] = "ClickedColumn + \" DESC\"" }, ["next"] = "Refresh" + agg },
                    new JsonObject { ["kind"] = "assign", ["name"] = "Ascending", ["assign"] = new JsonObject { [local] = "ClickedColumn" }, ["next"] = "Refresh" + agg },
                    new JsonObject { ["kind"] = "refreshData", ["name"] = "Refresh" + agg, ["dataSource"] = agg });
                if (paging.TryGetValue(table, out var p))   // a new order starts again at the first page
                {
                    ((JsonObject)flow[1]!)["assign"]![p.Start] = "0";
                    ((JsonObject)flow[2]!)["assign"]![p.Start] = "0";
                }
                extraActions.Add(new JsonObject
                {
                    ["name"] = handler,
                    ["inputs"] = new JsonArray(new JsonObject { ["name"] = "ClickedColumn", ["type"] = "Text", ["mandatory"] = true }),
                    ["flow"] = flow,
                });
                actionNames.Add(handler);
                sorting[table] = new Sorting(agg, local, handler, []);
            }
        }

        static IEnumerable<JsonObject> AllWidgets(JsonNode? list)
        {
            foreach (var w in Objs(list)) { yield return w; foreach (var c in AllWidgets(new JsonArray(Children(w).Select(x => (JsonNode)x.DeepClone()).ToArray()))) yield return c; }
        }

        // A RichWidgets list block for a list that pages or sorts here: List_Navigation becomes the OutSystems UI Pagination block, List_Counter an
        // Expression with the same "x to y of z records" text, List_SortColumn in a header cell goes (the column is noted for the Sort Attribute).
        // Null when the block is not one of these or its list is not known: then the usual translation runs.
        (bool handled, JsonObject? widget) ListBlock(JsonObject w, HashSet<string> used, string source)
        {
            var leaf = Leaf(source);
            var args = w["args"] as JsonObject ?? [];
            string? Arg(string prefix) => args.FirstOrDefault(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Value is { } v ? S(v) : null;
            string? TableOf(params string[] exprs)
            {
                foreach (var e in exprs.Where(x => x is not null))
                {
                    if (Regex.Match(e, @"^\s*(\w+)\.(Id|LineCount)\s*$") is { Success: true } m && paging.ContainsKey(m.Groups[1].Value)) return m.Groups[1].Value;
                    if (StartIndexCall.Match(e) is { Success: true } c && paging.ContainsKey(c.Groups[1].Value)) return c.Groups[1].Value;
                }
                return null;
            }
            if (leaf.Equals("List_Navigation", StringComparison.OrdinalIgnoreCase) && TableOf(Arg("ListWidget")!, Arg("LineCount")!) is { } nt)
            {
                var p = paging[nt];
                var o = new JsonObject
                {
                    ["type"] = "BlockInstance", ["sourceBlock"] = "OutSystemsUI.Pagination",
                    ["args"] = new JsonObject { ["StartIndex"] = p.Start, ["MaxRecords"] = p.Max, ["TotalCount"] = $"LongIntegerToInteger({p.Agg}.Count)" },
                    ["events"] = new JsonObject { ["OnNavigate"] = p.Handler },
                };
                if (!Blank(S(w["name"]))) o["name"] = S(w["name"]);
                return (true, Wrapped(w, used, o));
            }
            if (leaf.Equals("List_Counter", StringComparison.OrdinalIgnoreCase) && TableOf(Arg("LineCount")!, Arg("StartIndex")!) is { } ct)
            {
                var p = paging[ct];
                var count = $"LongIntegerToInteger({p.Agg}.Count)";
                var o = new JsonObject
                {
                    ["type"] = "Expression",
                    ["value"] = $"If({count} = 0, \"0 records\", ({p.Start} + 1) + \" to \" + Min({p.Start} + {p.Max}, {count}) + \" of \" + {count} + \" records\")",
                };
                if (!Blank(S(w["name"]))) o["name"] = S(w["name"]);
                return (true, Wrapped(w, used, o));
            }
            if (leaf.Equals("List_SortColumn", StringComparison.OrdinalIgnoreCase) && currentTable is not null && sorting.TryGetValue(currentTable, out var st))
            {
                if (Arg("Column") is { } col) st.Columns.Add(SortText(Literal(col) ?? col));
                return (true, null);
            }
            return (false, null);
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
            ListPatterns();
            foreach (var w in AllWidgets(el["widgets"]))
                if (WType(w) == "blockinstance" && w["events"] is JsonObject evs && evs["Notify"] is { } h && cx.NotifyingBlocks?.Contains(Leaf(S(w["sourceBlock"]) ?? "")) == true
                    && S(h is JsonObject ho ? ho["action"] : h) is { } hn) notifyHandlers.Add(hn);
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
            foreach (var a in extraActions) actions.Add(a);   // list handlers and actions that widgets needed (after the widgets)
            Lifecycle(aggs, actions, spec);
            foreach (var l in extraLocals.Values) locals.Add(l);
            if (locals.Count > 0) spec["locals"] = locals;
            if (aggs.Count > 0) spec["aggregates"] = aggs;
            if (actions.Count > 0) spec["actions"] = actions;
            foreach (var (key, value) in content) spec[key] = value;
            if (serverActions.Count > 0) spec["_serverActions"] = new JsonArray(serverActions.Select(x => (JsonNode?)x).ToArray());
            if (block && notifies)
                spec["events"] = new JsonArray(new JsonObject { ["name"] = "Notify", ["mandatory"] = false,
                    ["inputs"] = new JsonArray(new JsonObject { ["name"] = "Message", ["type"] = "Text", ["mandatory"] = false }) });
            if (S(el["styleSheet"]) is { Length: > 0 } css) spec["styleSheet"] = css;
            // Its own JavaScript becomes a script of the new module that it requires (the plan writes scripts/<Name>Script.js). Reactive loads no jQuery
            // and has no osjs: code that uses them must be rewritten.
            if (S(el["javaScript"]) is { Length: > 0 } js)
            {
                spec["_javaScript"] = js;
                if (Regex.IsMatch(js, @"\bjQuery\b|\bosjs\b|\$\s*\(|\$\.\w"))
                    Todo($"its JavaScript is now the script {name}Script, but it uses jQuery or osjs, which Reactive does not load; rewrite it with plain JavaScript (document.querySelector, addEventListener)");
            }

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

        // The Preparation. Aggregates become screen aggregates of the same names (a complete flow always keeps them). A flow with logic is lifted into
        // onInitialize / an aggregate's onAfterFetch only when Lift proves it can be (see there); otherwise the logic is left, with a Todo that says why.
        JsonArray Preparation(JsonNode? preparation)
        {
            var result = new JsonArray();
            if (preparation is not JsonObject p) return result;
            var nodes = Objs(p["nodes"]).ToList();
            if (nodes.Count == 0) return result;
            // AddFaviconTag adds the page's favicon in Traditional; a Reactive app shows the module's icon. The node goes, its connectors pass through.
            foreach (var fav in nodes.Where(n => NodeKind(n) == "execute" && Leaf(S(n["action"]) ?? S(n["_ref"]) ?? "").Equals("AddFaviconTag", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var fn = S(fav["name"]);
                var after = S(fav["next"]) ?? (nodes.IndexOf(fav) + 1 < nodes.Count ? S(nodes[nodes.IndexOf(fav) + 1]["name"]) : null) ?? "End";
                nodes = nodes.Select(n => (JsonObject)n.DeepClone()).Where(n => S(n["name"]) != fn).ToList();
                foreach (var n in nodes)
                {
                    foreach (var key in Connectors) if (S(n[key]) == fn) n[key] = after;
                    foreach (var cse in Objs(n["cases"])) if (S(cse["next"]) == fn) cse["next"] = after;
                }
                if (nodes.Count == 0) return result;
            }
            var unsupported = Objs(p["unsupported"]).ToList();
            var complete = Complete(p) || (unsupported.Count > 0 && unsupported.All(AjaxEntry));
            // Comments are listed after the main path and change nothing.
            var calls = nodes.Where(n => NodeKind(n) != "comment").ToList();
            if (!complete)
            {
                Todo($"its Preparation could not be read completely ({Summary(calls)}); lift it into one data action that calls one server action (skill/reference/traditional-to-reactive.md)");
                return result;
            }
            foreach (var n in calls.Where(n => NodeKind(n) == "aggregate"))
            {
                var a = new JsonObject();
                foreach (var key in new[] { "name", "sources", "joins", "filters", "sorts", "groupBy", "aggregates", "calculated", "groupFilters", "groupSorts", "groupCalculated", "maxRecords", "startIndex", "timeout" })
                    if (n[key] is { } v) a[key] = RwTree(v, $"the aggregate {S(n["name"])}", "name", "sources", "type", "function", "as", "left", "right");
                if (paging.Values.FirstOrDefault(pg => pg.Agg.Equals(S(n["name"]), StringComparison.OrdinalIgnoreCase)) is { } pg)
                { a["maxRecords"] = pg.Max; a["startIndex"] = pg.Start; }
                result.Add(a);
                if (S(n["name"]) is { } an) { aggregates.Add(an); aggDefs[an] = n; }
            }
            var logic = calls.Where(n => NodeKind(n) != "aggregate").ToList();
            if (logic.Count == 0) return result;
            var why = Lift(calls, nodes.Where(n => NodeKind(n) == "comment").ToList(), logic);
            if (why is not null)
                Todo($"its Preparation logic was not translated ({why}); the aggregates were kept, and the nodes left are {Summary(logic)}; a data action is the usual home for this logic (skill/reference/traditional-to-reactive.md): move it there and set the screen's data from its outputs");
            return result;
        }

        // ---------- expression paths (what a node reads) ----------

        // The dotted paths an expression starts: `GetOrders.List.Current.Order.Id` is one path, a member after a dot or a named argument is not
        // one, and string literals, #dates# and comments are skipped (the same scan as Rw).
        static List<string> Chains(string? text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;
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
                    var k = j; while (k < n && text[k] == ' ') k++;
                    var named = k < n && text[k] == ':' && !(k + 1 < n && text[k + 1] == '=');
                    if (last != '.' && !named)
                    {
                        while (j + 1 < n && text[j] == '.' && (char.IsLetter(text[j + 1]) || text[j + 1] == '_'))
                        { j++; while (j < n && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++; }
                        result.Add(text[i..j]);
                    }
                }
                else j = i + 1;
                if (!char.IsWhiteSpace(text[j - 1])) last = text[j - 1];
                i = j;
            }
            return result;
        }

        // The variable a path names: its first name, and for Session.X the two (a session variable is one variable, not "Session").
        static string Root(string chain)
        {
            var parts = chain.Split('.');
            return parts[0].Equals("Session", StringComparison.OrdinalIgnoreCase) && parts.Length > 1 ? parts[0] + "." + parts[1] : parts[0];
        }

        // The expression texts of a flow node: conditions, messages, lists, assignment targets and values, argument values, case conditions.
        static IEnumerable<string> NodeExprs(JsonObject n)
        {
            foreach (var key in new[] { "condition", "message", "recordList" }) if (S(n[key]) is { } t) yield return t;
            if (n["assign"] is JsonObject a) foreach (var (k, v) in a) { yield return k; if (S(v) is { } t) yield return t; }
            if (n["args"] is JsonObject args) foreach (var (_, v) in args) if (S(v) is { } t) yield return t;
            foreach (var c in Objs(n["cases"])) if (S(c["condition"]) is { } t) yield return t;
        }

        // Every text an aggregate evaluates (filters, sorts, joins, calculated attributes, limits), not its names.
        static IEnumerable<string> AggregateExprs(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var (k, v) in o)
                        if (!(k is "name" or "sources" or "type" or "function" or "as" or "left" or "right" or "kind" or "next" or "_path"))
                            foreach (var t in AggregateExprs(v)) yield return t;
                    break;
                case JsonArray arr: foreach (var x in arr) foreach (var t in AggregateExprs(x)) yield return t; break;
                case JsonValue v when S(v) is { } s: yield return s; break;
            }
        }

        static readonly string[] LiftKinds = ["assign", "if", "switch", "execute", "destination", "raiseexception"];

        string UniqueAction(string baseName) { var u = baseName; for (var i = 2; actionNames.Contains(u); i++) u = baseName + i; actionNames.Add(u); return u; }

        // Rule 1. The Preparation has logic besides its aggregates. Returns null when the logic was lifted, else why it was not (the caller says so).
        // Reactive aggregates fetch on their own, so the logic cannot run "between" them as in Traditional. The nodes that read no aggregate and come
        // before the first one that does go to onInitialize; the rest go to the onAfterFetch of the ONE aggregate they all read. Anything that would
        // need more than that (two aggregates read, SQL, an order that matters because a node writes what a filter reads, a branch that sends some
        // paths around the aggregates, a flow that cannot be cut cleanly in two) is not translated: a wrong order is worse than a Todo.
        string? Lift(List<JsonObject> calls, List<JsonObject> comments, List<JsonObject> logic)
        {
            var bad = logic.Where(n => !LiftKinds.Contains(NodeKind(n))).ToList();
            if (bad.Count > 0) return $"{Summary(bad)} cannot be client logic here" + (bad.Any(n => NodeKind(n) == "sql") ? " (a SQL node needs a server action)" : "");
            var names = calls.Select(n => S(n["name"])).ToList();
            if (names.Any(Blank) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count) return "its nodes have no unique names";
            foreach (var d in logic.Where(n => NodeKind(n) == "destination"))
                if (!External(d) && (S(d["screen"]) is not { Length: > 0 } scr || scr.Contains('.'))) return $"its Destination {S(d["name"])} does not name a screen of this module";   // an External URL is RedirectToURL
            var byName = calls.ToDictionary(n => S(n["name"])!, StringComparer.OrdinalIgnoreCase);
            var order = names.Select(x => x!).ToList();

            string Implicit(JsonObject n) { var i = order.FindIndex(x => x.Equals(S(n["name"]), StringComparison.OrdinalIgnoreCase)); return i + 1 < order.Count ? order[i + 1] : "End"; }
            List<string> Succ(JsonObject n) => NodeKind(n) switch
            {
                "if" => [S(n["true"]) ?? "End", S(n["false"]) ?? "End"],
                "switch" => Objs(n["cases"]).Select(c => S(c["next"]) ?? "End").Append(S(n["otherwise"]) ?? "End").ToList(),
                "destination" or "raiseexception" => [],
                _ => [S(n["next"]) ?? Implicit(n)],
            };
            if (calls.SelectMany(Succ).Any(t => t != "End" && !byName.ContainsKey(t))) return "a connector names a node that is not in the Preparation";
            string Resolve(string t) { for (var g = 0; g < 200 && t != "End" && NodeKind(byName[t]) == "aggregate"; g++) t = Succ(byName[t])[0]; return t; }
            var entry = Resolve(order[0]);
            if (entry == "End") return "no logic node is reached";

            var reach = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var todo = new Stack<string>(); todo.Push(entry);
            while (todo.Count > 0) { var t = todo.Pop(); if (t == "End" || !reach.Add(t)) continue; foreach (var x in Succ(byName[t])) todo.Push(Resolve(x)); }
            if (logic.FirstOrDefault(n => !reach.Contains(S(n["name"])!)) is { } lost) return $"the node {S(lost["name"])} is not reached";

            // An aggregate on only some paths would be fetched on all of them in Reactive.
            foreach (var g in calls.Where(n => NodeKind(n) == "aggregate"))
            {
                var gn = S(g["name"])!; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { gn }; var stack = new Stack<string>(); stack.Push(order[0]); var end = false;
                while (stack.Count > 0) { var t = stack.Pop(); if (t == "End") { end = true; break; } if (!seen.Add(t)) continue; foreach (var x in Succ(byName[t])) stack.Push(x); }
                if (end) return $"the aggregate {gn} is not on every path of the Preparation, and a Reactive aggregate always fetches";
            }

            var aggNames = calls.Where(n => NodeKind(n) == "aggregate").Select(n => S(n["name"])!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var logicNames = logic.Select(n => S(n["name"])!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<string> ReadsOf(IEnumerable<JsonObject> group, HashSet<string> among) =>
                group.SelectMany(NodeExprs).SelectMany(Chains).Select(c => c.Split('.')[0]).Where(among.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // What the aggregates read must not change under them, nor come from a node of the logic.
            var filterRoots = calls.Where(n => NodeKind(n) == "aggregate").SelectMany(n => AggregateExprs(n)).SelectMany(Chains).Select(Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var writes = logic.Where(n => NodeKind(n) == "assign").SelectMany(n => (n["assign"] as JsonObject ?? []).Select(kv => Chains(kv.Key).FirstOrDefault())).Where(c => c is not null).Select(c => Root(c!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (writes.FirstOrDefault(filterRoots.Contains) is { } clash) return $"it assigns {clash}, which an aggregate reads in its filter or sort, so the order of the assignment and the fetch matters";
            if (logicNames.FirstOrDefault(x => filterRoots.Contains(x)) is { } fromNode) return $"an aggregate reads the output of the node {fromNode}";

            var first = logic.FindIndex(n => ReadsOf([n], aggNames).Count > 0);
            var groupA = first < 0 ? logic : logic.Take(first).ToList();
            var groupB = first < 0 ? [] : logic.Skip(first).ToList();
            var inA = groupA.Select(n => S(n["name"])!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var inB = groupB.Select(n => S(n["name"])!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string? bEntry = null; string? target = null;
            if (groupB.Count > 0)
            {
                var read = ReadsOf(groupB, aggNames);
                if (read.Count != 1) return $"the nodes after the first aggregate read {read.Count} aggregates ({string.Join(", ", read.Order())}) and a handler belongs to one";
                target = read.First();
                if (groupB.SelectMany(NodeExprs).SelectMany(Chains).Select(c => c.Split('.')[0]).FirstOrDefault(inA.Contains) is { } crossed) return $"the nodes after the first aggregate read the output of {crossed}, which runs earlier";
                if (groupB.Any(n => Succ(n).Select(Resolve).Any(inA.Contains))) return "the flow goes back from the nodes that read an aggregate to the ones that do not";
                if (groupA.Count > 0)
                {
                    if (!inA.Contains(entry)) return "the Preparation starts with a node that reads an aggregate, after others that do not";
                    if (groupA.Any(n => NodeKind(n) is "destination" or "raiseexception")) return "a path of the nodes before the first aggregate ends in a Destination or Raise Exception, so the later nodes would not always run";
                    var exits = groupA.SelectMany(n => Succ(n).Select(Resolve)).Where(t => !inA.Contains(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (exits.Count != 1 || !inB.Contains(exits[0])) return "the nodes before the first aggregate do not all lead into the same node after it, so the flow cannot be cut in two";
                    bEntry = exits[0];
                }
                else bEntry = entry;
            }
            else if (!inA.Contains(entry)) return "no logic node is reached";

            // Proven: build the client actions. Every connector is made explicit (through the removed aggregates) and one that leaves the group says End.
            JsonObject Flow(string actionName, List<JsonObject> group, string groupEntry, HashSet<string> members)
            {
                string To(string t) { t = t == "End" ? t : Resolve(t); return t != "End" && members.Contains(t) ? S(byName[t]["name"])! : "End"; }
                var flow = new JsonArray();
                foreach (var n in group.OrderBy(n => S(n["name"])!.Equals(groupEntry, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                {
                    var c = (JsonObject)n.DeepClone();
                    switch (NodeKind(c))
                    {
                        case "if": c["true"] = To(S(c["true"]) ?? "End"); c["false"] = To(S(c["false"]) ?? "End"); break;
                        case "switch":
                            foreach (var cs in Objs(c["cases"])) cs["next"] = To(S(cs["next"]) ?? "End");
                            c["otherwise"] = To(S(c["otherwise"]) ?? "End"); break;
                        case "destination" or "raiseexception": break;
                        default: c["next"] = To(S(c["next"]) ?? Implicit(n)); break;
                    }
                    flow.Add(Node(c, actionName));
                }
                return new JsonObject { ["name"] = actionName, ["flow"] = flow };
            }
            if (groupA.Count > 0) initAction = Flow(UniqueAction("OnInitialize"), groupA, entry, inA);
            if (groupB.Count > 0) afterFetch[target!] = Flow(UniqueAction(target + "OnAfterFetch"), groupB, bEntry!, inB);
            // Comments of the Preparation go to the first handler, after its nodes.
            if ((initAction ?? afterFetch.GetValueOrDefault(target ?? "")) is { } home && home["flow"] is JsonArray hf)
                foreach (var cm in comments) hf.Add(new JsonObject { ["kind"] = "comment", ["text"] = S(cm["text"]) ?? "" });
            foreach (var w in writes) prepWrites.Add(w);
            var calling = logic.Where(n => NodeKind(n) == "execute" && !Leaf(S(n["action"]) ?? S(n["_ref"]) ?? "").Equals("Feedback_Message", StringComparison.OrdinalIgnoreCase)).Select(n => S(n["name"])).ToList();
            if (calling.Count > 0)
            {
                var homes = new[] { initAction is null ? null : $"the client action {S(initAction["name"])} (onInitialize, runs once)", groupB.Count == 0 ? null : $"the client action {S(afterFetch[target!]["name"])} (onAfterFetch of {target}, runs after every fetch)" }.Where(x => x is not null);
                Todo($"its Preparation logic became {string.Join(" and ", homes)} and calls server actions ({string.Join(", ", calling)}): each call costs a request, so these are candidates for a data action");
            }
            return null;
        }

        // Rule 2 and the lifecycle: a form record filled from an input or local at initialize, or from an aggregate after its fetch, becomes an assign
        // at the start of the handler (made if there is none); then the handlers are added to the actions and named by their keys.
        void Lifecycle(JsonArray aggs, JsonArray actions, JsonObject spec)
        {
            JsonObject Prepend(JsonObject? action, string baseName, List<(string form, string expr)> fills)
            {
                action ??= new JsonObject { ["name"] = UniqueAction(baseName), ["flow"] = new JsonArray() };
                var flow = (JsonArray)action["flow"]!;
                var used = flow.OfType<JsonObject>().Select(n => S(n["name"])).Where(x => x is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var made = new List<JsonObject>();
                foreach (var (form, expr) in fills)
                {
                    var nm = "Fill" + form; for (var i = 2; !used.Add(nm); i++) nm = "Fill" + form + i;
                    made.Add(new JsonObject { ["kind"] = "assign", ["name"] = nm, ["assign"] = new JsonObject { [form] = expr } });
                }
                for (var i = 0; i < made.Count; i++)
                {
                    var next = i + 1 < made.Count ? S(made[i + 1]["name"]) : flow.Count > 0 ? S(flow[0]!["name"]) : null;
                    if (next is not null) made[i]["next"] = next;
                }
                for (var i = made.Count - 1; i >= 0; i--) flow.Insert(0, made[i]);
                return action;
            }
            if (initFills.Count > 0) initAction = Prepend(initAction, "OnInitialize", initFills);
            foreach (var g in afterFills.GroupBy(f => f.agg, StringComparer.OrdinalIgnoreCase))
                afterFetch[g.Key] = Prepend(afterFetch.GetValueOrDefault(g.Key), g.Key + "OnAfterFetch", g.Select(f => (f.form, f.expr)).ToList());
            foreach (var a in aggs.OfType<JsonObject>())
                if (S(a["name"]) is { } an && afterFetch.TryGetValue(an, out var act)) { a["onAfterFetch"] = S(act["name"]); actions.Add(act); }
            if (initAction is not null) { actions.Add(initAction); spec["onInitialize"] = S(initAction["name"]); }
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

        static readonly HashSet<string> ActionKinds = ["assign", "if", "switch", "foreach", "execute", "destination", "raiseexception", "message", "comment", "javascript", "end", "ajaxrefresh", "exceptionhandler", "refreshdata"];

        JsonObject Action(JsonObject a)
        {
            var an = S(a["name"]) ?? "Action";
            currentAction = an;
            var (flow, why) = ActionFlow(an, a);
            currentAction = null;
            var o = new JsonObject { ["name"] = an };
            if (Vars(a["inputs"], true) is { Count: > 0 } i) o["inputs"] = i;
            if (notifyHandlers.Contains(an) && (a["flow"]?.ToJsonString() ?? "").Contains("NotifyGetMessage", StringComparison.Ordinal))
            {
                var ins = o["inputs"] as JsonArray ?? new JsonArray();
                if (!ins.OfType<JsonObject>().Any(x => string.Equals(S(x["name"]), "Message", StringComparison.OrdinalIgnoreCase)))
                    ins.Add(new JsonObject { ["name"] = "Message", ["type"] = "Text", ["mandatory"] = false });
                o["inputs"] = ins;
            }
            if (Vars(a["outputs"], false) is { Count: > 0 } outs) o["outputs"] = outs;
            if (Vars(a["locals"], false) is { Count: > 0 } l) o["locals"] = l;
            if (flow is null)
            {
                o["flow"] = new JsonArray(new JsonObject { ["kind"] = "comment", ["text"] = $"TODO migrate: {why}" });
                var nodes = Objs((a["flow"] as JsonObject)?["nodes"]).ToList();
                var did = nodes.Count > 0 ? $"; the original did: {Summary(nodes)}" : "";
                var notify = nodes.Any(IsNotify);
                var queries = nodes.Where(n => NodeKind(n) is "aggregate" or "sql").ToList();
                if (block && notify)
                    Todo($"the action {an} calls Notify, which Reactive does not have; create a block event (the spec's events key, for example OnNotify), fire it in this action with a triggerEvent node and handle it on every instance with events{did}");
                else if (queries.Count > 0 && why!.Contains("needs a server action"))
                {
                    // What the later nodes read from the queries: the paths that start with a query node's name, as typed.
                    var queryNames = queries.Select(n => S(n["name"])).Where(x => !Blank(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var reads = nodes.Where(n => NodeKind(n) is not ("aggregate" or "sql")).SelectMany(NodeExprs).SelectMany(Chains).Where(c => queryNames.Contains(c.Split('.')[0])).Distinct().ToList();
                    Todo($"the action {an} was created as a stub because it contains {Summary(queries)}, which need a server action; create a server action that runs this query and returns what the later nodes read ({(reads.Count > 0 ? string.Join(", ", reads) : "nothing is read from it")}), call it here, and replace those reads{did}");
                }
                else Todo($"the action {an} was created as a stub because {why}{did}");
            }
            else { o["flow"] = flow; Merge(o); }
            return o;
        }

        internal static bool IsNotify(JsonObject n) => NodeKind(n) == "execute" && Leaf(S(n["action"]) ?? S(n["_ref"]) ?? "") is var l && (l.Equals("Notify", StringComparison.OrdinalIgnoreCase) || l.Equals("Deprecated_Notify", StringComparison.OrdinalIgnoreCase));

        // The Ajax Refresh node is the one unsupported thing that is handled (DropRefreshes); its entry says so in `why` (Export.cs 445), so another
        // reason on a node of that kind (a repeated name) still makes a stub.
        bool Handled(JsonObject u) => AjaxEntry(u)
            || NodeKind(u) == "refreshdata" && Regex.Match(S(u["why"]) ?? "", @"^it refreshes (\w+), which is not an aggregate or data action of a screen or block \(a Preparation aggregate\)") is { Success: true } m && aggregates.Contains(m.Groups[1].Value);

        static bool AjaxEntry(JsonObject u) => NodeKind(u) == "ajaxrefresh" && (S(u["why"]) ?? "").Contains("Ajax Refresh node", StringComparison.Ordinal)
            || NodeKind(u) == "destination" && (S(u["why"]) ?? "").Contains("ExternalURL", StringComparison.Ordinal);   // it becomes RedirectToURL
        static bool External(JsonObject n) => NodeKind(n) == "destination" && S(n["_ref"]) == "ExternalURL";

        (JsonArray? flow, string? why) ActionFlow(string an, JsonObject a)
        {
            if (a["flow"] is not JsonObject f) return (null, "its flow could not be read from the export");
            var nodes = Objs(f["nodes"]).ToList();
            var unsupported = Objs(f["unsupported"]).ToList();
            if (unsupported.FirstOrDefault(u => !Handled(u)) is { } bad)
                return (null, $"it contains {S(bad["kind"])} ({S(bad["why"]) ?? "unsupported"}) at {S(bad["path"])}");
            if (!Complete(f) && unsupported.Count == 0) return (null, "the export marks its flow as incomplete");
            foreach (var n in nodes)
            {
                var kind = NodeKind(n);
                var nn = S(n["name"]) ?? S(n["kind"]);
                if (!ActionKinds.Contains(kind))
                    return (null, kind is "aggregate" or "sql" ? $"it contains the {S(n["kind"])} node {nn}, which needs a server action" : $"it contains a {S(n["kind"])} node ({nn}) that has no client equivalent here");
                if (IsNotify(n) && !block) return (null, $"it calls Notify at {nn}");
                if (kind == "destination" && !External(n) && (S(n["screen"]) is not { Length: > 0 } scr || scr.Contains('.')))
                    return (null, $"its Destination {nn} does not name a screen of this module");
            }
            if (nodes.Any(n => NodeKind(n) == "ajaxrefresh")) nodes = DropRefreshes(nodes, an);
            var result = new JsonArray();
            foreach (var n in nodes) result.Add(Node(n, an));
            return (result, null);
        }

        // Ajax Refresh nodes. The export cannot express them, but names in `_targets` (when the export has them) say which widgets they refresh. A table or
        // list fed by a screen aggregate, or a widget that holds such widgets or shows an aggregate's values, becomes a refreshData node for each of those
        // aggregates; a widget fed by nothing that needs refreshing (Reactive re-renders bound values by itself) removes the node silently; with no `_targets`,
        // or a name that is not found, the node is removed with a Todo. Every default "next" is made explicit first, so changing a node cannot change the path.
        List<JsonObject> DropRefreshes(List<JsonObject> source, string an)
        {
            var nodes = source.Select(n => (JsonObject)n.DeepClone()).ToList();
            static bool Defaults(JsonObject n) => NodeKind(n) is "assign" or "execute" or "message" or "foreach" or "javascript" or "ajaxrefresh";
            string? Following(int index) => nodes.Skip(index + 1).FirstOrDefault(x => NodeKind(x) != "comment") is { } nx ? S(nx["name"]) : null;
            for (var i = 0; i < nodes.Count; i++)
                if (Defaults(nodes[i]) && nodes[i]["next"] is null && Following(i) is { } follow) nodes[i]["next"] = follow;
            foreach (var r in nodes.Where(n => NodeKind(n) == "ajaxrefresh").ToList())
            {
                var rName = S(r["name"])!;
                var target = S(r["next"]) ?? "End";
                var (aggs, unknown, known) = RefreshTargets(r);
                aggs.RemoveAll(a => nodes.Any(x => NodeKind(x) == "refreshdata" && string.Equals(S(x["dataSource"]), a, StringComparison.OrdinalIgnoreCase)));
                if (!known)
                    Todo($"the action {an} had the Ajax Refresh node {rName}, which was removed because the export cannot read which widgets it refreshed (a Reactive aggregate refetches only when its inputs change); if a table or list no longer updates, add a refreshData node for the aggregate behind it");
                else if (unknown.Count > 0)
                    Todo($"the action {an} had the Ajax Refresh node {rName}, whose target {string.Join(", ", unknown)} could not be matched to a widget of this {kindWord.ToLowerInvariant()} that is safe to read; {(aggs.Count > 0 ? "the others became refreshData nodes, " : "the node was removed, ")}so if something no longer updates, add a refreshData node for the aggregate behind it");
                var at = nodes.IndexOf(r);
                var made = aggs.Select((agg, i) => new JsonObject { ["kind"] = "refreshData", ["name"] = aggs.Count == 1 ? rName : $"{rName}_{agg}", ["dataSource"] = agg }).ToList();
                for (var i = 0; i < made.Count; i++) made[i]["next"] = i + 1 < made.Count ? S(made[i + 1]["name"]) : target;
                var entry = made.Count > 0 ? S(made[0]["name"])! : target;
                foreach (var n in nodes)
                {
                    foreach (var key in new[] { "next", "true", "false", "cycle", "otherwise" }) if (S(n[key]) == rName) n[key] = entry;
                    if (n["cases"] is JsonArray cs) foreach (var cse in Objs(cs)) if (S(cse["next"]) == rName) cse["next"] = entry;
                }
                nodes.RemoveAt(at);
                nodes.InsertRange(at, made);
                if (at == 0 && made.Count == 0 && nodes.FindIndex(n => S(n["name"]) == target) is var t and > 0) { var first = nodes[t]; nodes.RemoveAt(t); nodes.Insert(0, first); }
            }
            return nodes;
        }

        // The screen aggregates an Ajax Refresh node refreshes, from its `_targets` {property: name | [names]}: known is false when there are no targets to
        // read. `unknown` lists the names that cannot be decided: not a widget of this element, or a widget that holds a block instance, a table or list
        // whose source is not readable, or one fed by an aggregate that was not built.
        (List<string> aggs, List<string> unknown, bool known) RefreshTargets(JsonObject r)
        {
            var aggs = new List<string>(); var unknown = new List<string>();
            var names = new List<string>();
            if (r["_targets"] is JsonObject t)
                foreach (var (_, v) in t.Where(kv => !kv.Key.Equals("target", StringComparison.OrdinalIgnoreCase)))   // target is the next node
                    if (v is JsonArray a) names.AddRange(a.Select(x => S(x)).Where(x => !Blank(x))!);
                    else if (S(v) is { Length: > 0 } one) names.Add(one);
            if (names.Count == 0) return (aggs, unknown, false);
            foreach (var nm in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!widgetByName.TryGetValue(nm, out var w)) { unknown.Add(nm); continue; }
                var found = new List<string>(); var ok = true;
                void Walk(JsonObject x)
                {
                    var kind = WType(x);
                    // A block of this module may hold data of its own; the list and library blocks hold none (ListBlock, UiLibraryMap).
                    if (kind == "blockinstance" && S(x["sourceBlock"]) is var sb && !(UiLibraryMap.IsLibrary(sb ?? "") || Regex.IsMatch(Leaf(sb ?? ""), "^List_(Navigation|Counter|SortColumn)$")))
                        ok = false;
                    if (kind is "tablerecords" or "listrecords")
                    {
                        var src = Prop(x, "source", "sourceRecordList");
                        var head = src is null ? null : Chains(src).FirstOrDefault()?.Split('.')[0];
                        if (src is null || (head is not null && Regex.IsMatch(src.Trim(), @"^\w+\.List$") && !aggregates.Contains(head))) ok = false;
                    }
                    foreach (var text in (x["props"] as JsonObject ?? []).Concat(x["attrs"] as JsonObject ?? []).Concat(x["args"] as JsonObject ?? []).Select(kv => S(kv.Value)))
                        foreach (var c in Chains(text))
                        {
                            var head = c.Split('.')[0];
                            if (aggregates.Contains(head)) found.Add(aggregates.First(g => g.Equals(head, StringComparison.OrdinalIgnoreCase)));
                            else if (tables.TryGetValue(head, out var tsrc) && aggregates.Contains(tsrc.Split('.')[0])) found.Add(tsrc.Split('.')[0]);
                        }
                    foreach (var child in Children(x)) Walk(child);
                }
                Walk(w);
                if (!ok) { unknown.Add(nm); continue; }
                foreach (var g in found) if (!aggs.Contains(g, StringComparer.OrdinalIgnoreCase)) aggs.Add(g);
            }
            return (aggs, unknown, true);
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
                case "destination" when External(n):   // Reactive opens an address with the RedirectToURL client action
                    o["kind"] = "execute"; Copy("name"); o["action"] = "RedirectToURL";
                    o["args"] = new JsonObject { ["URL"] = Rw(S((n["args"] as JsonObject)?["URL"]) ?? "\"\"", at) }; o["next"] = "End";
                    break;
                case "destination": o["kind"] = "destination"; Copy("name"); o["screen"] = Leaf(S(n["screen"])!); Args(); break;
                case "exceptionhandler": o["kind"] = "exceptionHandler"; Copy("name", "exception", "next"); break;   // client actions have handlers too; abort transaction is server only
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

        // ---------- consecutive server calls become one server action ----------

        readonly List<JsonObject> serverActions = [];   // the merged server actions, hoisted to the spec's serverActions by the plan (_serverActions)
        static readonly string[] NameKeys = ["kind", "name", "next", "true", "false", "cycle", "otherwise", "action", "dataSource", "event", "screen", "exception", "type"];

        // A Traditional screen action ran its server calls in one request and one transaction; a Reactive client action that calls server actions
        // one after another pays a request for each and commits each on its own. Two or more calls in a row (each reached only from the one before)
        // become one server action, as Service Studio's converter does. Its inputs are the screen and action variables the calls read (by name and
        // type); its outputs are the call outputs that later nodes read (<Node>_<Output>). It starts with the screen's access check, because a server
        // action that a screen calls is a public endpoint. A run that reads anything else (a widget, an aggregate, a client variable) stays as it is.
        void Merge(JsonObject action)
        {
            if (cx.ServerOutputs is not { Count: > 0 } outs || action["flow"] is not JsonArray flow) return;
            var an = S(action["name"]) ?? "Action";
            var nodes = flow.OfType<JsonObject>().ToList();
            var real = nodes.Where(n => S(n["kind"]) != "comment").ToList();
            string NextOf(JsonObject n) => S(n["next"]) ?? (real.IndexOf(n) + 1 < real.Count ? S(real[real.IndexOf(n) + 1]["name"]) ?? "End" : "End");
            IEnumerable<string> Targets(JsonObject n)
            {
                switch (S(n["kind"]))
                {
                    case "if": yield return S(n["true"]) ?? "End"; yield return S(n["false"]) ?? "End"; break;
                    case "switch": foreach (var c in Objs(n["cases"])) yield return S(c["next"]) ?? "End"; yield return S(n["otherwise"]) ?? "End"; break;
                    case "forEach": yield return S(n["cycle"]) ?? "End"; yield return NextOf(n); break;
                    case "destination" or "raiseException" or "end": break;
                    default: yield return NextOf(n); break;
                }
            }
            bool IsServer(JsonObject n) => S(n["kind"]) == "execute" && S(n["name"]) is not null && S(n["action"]) is { } a && outs.ContainsKey(a);
            var preds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in real) foreach (var t in Targets(n)) preds[t] = preds.GetValueOrDefault(t) + 1;
            var byName = real.Where(n => S(n["name"]) is not null).GroupBy(n => S(n["name"])!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in Objs(action["inputs"]).Concat(Objs(action["locals"]))) if (S(v["name"]) is { } vn) inputs[vn] = S(v["type"]) ?? "Text";
            var used = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
            var count = 0;
            foreach (var start in real.ToList())
            {
                if (used.Contains(start) || !IsServer(start)) continue;
                var run = new List<JsonObject> { start };
                for (var cur = start; ;)
                {
                    var nx = NextOf(cur);
                    if (nx == "End" || !byName.TryGetValue(nx, out var nn) || !IsServer(nn) || preds.GetValueOrDefault(nx) != 1 || used.Contains(nn)) break;
                    run.Add(nn); cur = nn;
                }
                if (run.Count < 2) continue;
                if (MergeRun(an, flow, real, run, inputs, outs, NextOf(run[^1]), ++count) is false) count--;
                foreach (var n in run) used.Add(n);
            }
        }

        // One run: returns false (and changes nothing) when the calls read something a server action cannot get as an input.
        bool MergeRun(string an, JsonArray flow, List<JsonObject> real, List<JsonObject> run, Dictionary<string, string> actionVars,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> outs, string after, int index)
        {
            var runNames = run.Select(n => S(n["name"])!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var inputs = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var serverRoots = new[] { "Entities", "Site", "Roles", "Resources", "True", "False" };
            foreach (var n in run)
                foreach (var text in Strings(n))
                    foreach (var chain in Chains(text))
                    {
                        var root = chain.Split('.')[0];
                        if (Regex.IsMatch(text, @"(?<![\w.])" + Regex.Escape(chain) + @"\s*\(")) continue;   // a function call
                        if (serverRoots.Contains(root, StringComparer.OrdinalIgnoreCase)) continue;
                        if (runNames.Contains(root)) { if (run.FindIndex(x => S(x["name"])!.Equals(root, StringComparison.OrdinalIgnoreCase)) >= run.IndexOf(n)) return false; continue; }
                        if (actionVars.TryGetValue(root, out var t) || varTypes.TryGetValue(root, out t)) { inputs[root] = t; continue; }
                        return false;   // a widget, an aggregate, a client variable or something unknown
                    }
            // What later nodes read of the calls: Node.Output, each one an output of the server action.
            var outputs = new SortedDictionary<string, (string node, string output, string type)>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in real.Where(x => !run.Contains(x)))
                foreach (var text in Strings(n))
                    foreach (var chain in Chains(text))
                    {
                        var parts = chain.Split('.');
                        if (parts.Length < 2 || !runNames.Contains(parts[0])) continue;
                        var node = run.First(x => S(x["name"])!.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
                        if (!outs.TryGetValue(S(node["action"])!, out var sig) || !sig.TryGetValue(parts[1], out var type)) return false;
                        outputs[$"{S(node["name"])}_{parts[1]}"] = (S(node["name"])!, parts[1], type);
                    }
            var serverName = UniqueServer($"{name}_{an}" + (index > 1 ? index.ToString() : ""));
            var callName = an + "OnServer" + (index > 1 ? index.ToString() : "");
            // The server action: the access check, the calls (their names kept), the outputs.
            var body = new JsonArray();
            foreach (var check in AccessCheck(S(run[0]["name"])!)) body.Add(check);
            for (var i = 0; i < run.Count; i++)
            {
                var c = (JsonObject)run[i].DeepClone();
                c["next"] = i + 1 < run.Count ? S(run[i + 1]["name"]) : outputs.Count > 0 ? "SetOutputs" : "End";
                body.Add(c);
            }
            if (outputs.Count > 0)
                body.Add(new JsonObject { ["kind"] = "assign", ["name"] = "SetOutputs", ["assign"] = new JsonObject(outputs.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create($"{kv.Value.node}.{kv.Value.output}")))), ["next"] = "End" });
            var sa = new JsonObject { ["name"] = serverName, ["description"] = $"The server calls of the screen action {an} of {name}, in one request (oml migrate)." };
            if (inputs.Count > 0) sa["inputs"] = new JsonArray(inputs.Select(kv => (JsonNode?)new JsonObject { ["name"] = kv.Key, ["type"] = kv.Value, ["mandatory"] = false }).ToArray());
            if (outputs.Count > 0) sa["outputs"] = new JsonArray(outputs.Select(kv => (JsonNode?)new JsonObject { ["name"] = kv.Key, ["type"] = kv.Value.type }).ToArray());
            sa["flow"] = body;
            serverActions.Add(sa);
            // The client action: one call where the run was; connectors to the run's first node and reads of its outputs follow.
            var call = new JsonObject { ["kind"] = "execute", ["name"] = callName, ["action"] = serverName };
            if (inputs.Count > 0) call["args"] = new JsonObject(inputs.Keys.Select(k => KeyValuePair.Create(k, (JsonNode?)JsonValue.Create(k))));
            call["next"] = after;
            var at = flow.IndexOf(run[0]);
            foreach (var n in run) flow.Remove(n);
            flow.Insert(at, call);
            var startName = S(run[0]["name"])!;
            foreach (var n in flow.OfType<JsonObject>().Where(x => !ReferenceEquals(x, call)))
            {
                foreach (var key in Connectors) if (S(n[key]) is { } t && t.Equals(startName, StringComparison.OrdinalIgnoreCase)) n[key] = callName;
                foreach (var cse in Objs(n["cases"])) if (S(cse["next"]) is { } t2 && t2.Equals(startName, StringComparison.OrdinalIgnoreCase)) cse["next"] = callName;
                if (outputs.Count > 0) ReplaceReads(n, outputs.Values.Select(o => (o.node, o.output)).ToList(), callName);
            }
            return true;
        }

        // The access check at the start of a merged server action: no signed-in user raises NotRegisteredException; a user without one of the
        // screen's roles raises the user exception AccessDenied, which the plan adds to the new module (a Raise Exception node cannot raise
        // SecurityException, and a spec cannot name a role's Not<Role> exception). An anonymous screen has none; a block does not know its screens' roles, so it says so.
        List<JsonObject> AccessCheck(string firstNode)
        {
            var checks = new List<JsonObject>();
            if (block) { Todo($"a merged server action of this block has no access check: add the roles of the screens that use the block"); return checks; }
            var access = el["access"] as JsonObject;
            if (access is null || Flag(access["anonymous"]) == true) return checks;
            var roles = (access["roles"] as JsonArray ?? []).Select(r => S(r)).Where(r => !Blank(r)).Select(r => Leaf(r!)).ToList();
            checks.Add(new JsonObject { ["kind"] = "if", ["name"] = "SignedIn", ["condition"] = "GetUserId() <> NullIdentifier()", ["true"] = roles.Count > 0 ? "HasRole" : firstNode, ["false"] = "NotSignedIn" });
            checks.Add(new JsonObject { ["kind"] = "raiseException", ["name"] = "NotSignedIn", ["exception"] = "NotRegisteredException", ["message"] = "\"Sign in first.\"" });
            if (roles.Count > 0)
            {
                checks.Add(new JsonObject { ["kind"] = "if", ["name"] = "HasRole", ["condition"] = string.Join(" or ", roles.Select(r => $"Check{r}Role()")), ["true"] = firstNode, ["false"] = "NoRole" });
                checks.Add(new JsonObject { ["kind"] = "raiseException", ["name"] = "NoRole", ["exception"] = AccessDenied, ["message"] = "\"You cannot do this.\"" });
            }
            return checks;
        }

        string UniqueServer(string wanted)
        {
            var nm = wanted; var i = 2;
            while (cx.ServerOutputs?.ContainsKey(nm) == true || serverActions.Any(s => string.Equals(S(s["name"]), nm, StringComparison.OrdinalIgnoreCase))) nm = wanted + i++;
            return nm;
        }

        // Every expression text of a spec node (names, connectors and kinds are not expressions).
        static IEnumerable<string> Strings(JsonNode? n, string? key = null)
        {
            switch (n)
            {
                case JsonObject o: foreach (var (k, v) in o) { if (key is null && NameKeys.Contains(k)) continue; foreach (var s in Strings(v, k)) yield return s; } break;
                case JsonArray a: foreach (var x in a) foreach (var s in Strings(x, key)) yield return s; break;
                case JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String: yield return v.ToString(); break;
            }
        }

        // Node.Output -> Call.Node_Output in every expression of a node (keys of an assign map are expressions too).
        static void ReplaceReads(JsonObject n, List<(string node, string output)> reads, string call)
        {
            string Fix(string text)
            {
                foreach (var (node, output) in reads)
                    text = Regex.Replace(text, @"(?<![\w.])" + Regex.Escape(node) + @"\." + Regex.Escape(output) + @"(?!\w)", $"{call}.{node}_{output}");
                return text;
            }
            foreach (var (k, v) in n.ToList())
            {
                if (NameKeys.Contains(k)) continue;
                if (v is JsonValue jv && jv.GetValueKind() == System.Text.Json.JsonValueKind.String) n[k] = Fix(jv.ToString());
                else if (v is JsonObject jo)
                {
                    var copy = new JsonObject();
                    foreach (var (k2, v2) in jo) copy[Fix(k2)] = v2 is JsonValue sv && sv.GetValueKind() == System.Text.Json.JsonValueKind.String ? Fix(sv.ToString()) : v2?.DeepClone();
                    n[k] = copy;
                }
            }
        }

        static readonly Regex EntityAction = new(@"^(CreateOrUpdate|Create|Update|Delete)([A-Za-z][A-Za-z0-9_]*)$", RegexOptions.CultureInvariant);

        void Execute(JsonObject n, JsonObject o, string at)
        {
            var action = S(n["action"]) ?? S(n["_ref"]) ?? "";
            var nodeName = S(n["name"]);
            var leaf = Leaf(action);
            if (block && IsNotify(n))   // the block's Notify event (Go adds it): the parent's handler gets the message as the input Message
            {
                o["kind"] = "triggerEvent";
                if (nodeName is not null) o["name"] = nodeName;
                o["event"] = "Notify";
                if ((n["args"] as JsonObject)?.FirstOrDefault().Value is { } msg && S(msg) is { Length: > 0 } mt) o["args"] = new JsonObject { ["Message"] = Rw(mt, at) };
                if (S(n["next"]) is { } tn) o["next"] = tn;
                notifies = true;
                return;
            }
            if (leaf.Equals("Feedback_Message", StringComparison.OrdinalIgnoreCase))
            {
                var args = n["args"] as JsonObject;
                string? Arg(string key) => args?.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value is { } v ? S(v) : null;
                var typeArg = Arg("Type") ?? Arg("MessageType");   // the RichWidgets action names its inputs MessageText and MessageType
                var type = MessageType(typeArg);
                o["kind"] = "message";
                if (nodeName is not null) o["name"] = nodeName;
                o["message"] = Rw(Arg("Message") ?? Arg("MessageText") ?? "\"\"", at);
                o["type"] = type ?? "info";
                if (type is null && typeArg is { } raw) Todo($"{at} shows a message whose type {raw} is not Info, Success, Warning or Error, so it was built as info; set the type");
                if (S(n["next"]) is { } nx) o["next"] = nx;
                return;
            }
            // A Traditional UI library action with an OutSystems UI client action (by the map), called directly or through a local copy of RichWidgets.
            if ((UiLibraryMap.IsLibrary(action) || !action.Contains('.')) && UiLibraryMap.Action(action.Contains('.') ? action : "RichWidgets." + leaf) is { ReactiveKind: "clientAction", Reactive: { Length: > 0 } ra } row)
            {
                o["kind"] = "execute";
                if (nodeName is not null) o["name"] = nodeName;
                o["action"] = ra;
                var mapped = new JsonObject();
                foreach (var (k, v) in n["args"] as JsonObject ?? [])
                {
                    var target = row.Inputs.FirstOrDefault(kv => kv.Key.Equals(k, StringComparison.OrdinalIgnoreCase));
                    if (S(target.Value) is { Length: > 0 } to) mapped[to] = Rw(S(v), at);
                    else Todo($"{at} calls {action} with {k} = {S(v)}, which {ra} has no input for");
                }
                if (leaf.Equals("Input_FocusFirstInvalid", StringComparison.OrdinalIgnoreCase))
                {
                    if (forms.Count == 1) mapped["WidgetId"] = forms.Keys.First() + ".Id";
                    else Todo($"{at} calls Input_FocusFirstInvalid, now {ra}: set its WidgetId to the Id of the form or container to search");
                }
                if (mapped.Count > 0) o["args"] = mapped;
                if (S(n["next"]) is { } ln) o["next"] = ln;
                return;
            }
            if (leaf.Equals("List_Navigation_ResetStartIndex", StringComparison.OrdinalIgnoreCase)
                && (n["args"] as JsonObject)?.Select(kv => Regex.Match(S(kv.Value) ?? "", @"^\s*(\w+)\.Id\s*$")).FirstOrDefault(x => x.Success && paging.ContainsKey(x.Groups[1].Value)) is { } rt)
            {
                o["kind"] = "assign";
                if (nodeName is not null) o["name"] = nodeName;
                o["assign"] = new JsonObject { [paging[rt.Groups[1].Value].Start] = "0" };
                if (S(n["next"]) is { } rn) o["next"] = rn;
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

        // WIDGET FACTS AND THE SPEC: for every fact of a widget that the translator reads, the spec key that carries it, or null when the spec has none
        // (then a Todo says what to set by hand). The widget code asks Carry, so turning a fact on is one line here: put the key's name in the table.
        // A widget key is the model property name with its first letter lowered, set through the generic path (Core's finding). Null stays a Todo with a hint.
        // radioGroup would be a widget type (the key's value is the type of the widget that replaces a Radio Button), imageSize is the attributes map
        // (width and height are written inside it), the others are property keys.
        internal static readonly Dictionary<string, string?> SpecKeys = new(StringComparer.Ordinal)
        {
            ["confirm"] = "confirmationMessage", // Button, Link: the confirmation text (expression)
            ["linkUrl"] = null,             // Link: an external address
            ["icon"] = "icon",                // Icon: the icon name (plain text)
            ["radioGroup"] = null,          // Radio Button: a Radio Group widget
            ["moreInputTypes"] = "inputType",  // Input: types other than Password, Email, Number (Phone Number, ...), passed through as typed
            ["prompt"] = "prompt",          // Input: the prompt (placeholder) text
            ["maxLength"] = "maxLength",    // Input, Text Area: the maximum length
            ["validationMessage"] = null,   // Input: the validation message
            ["isDefault"] = "isDefault",      // Button: the default button of the form (true/false)
            ["imageUrl"] = null,            // Image: an address as its source
            ["imageSize"] = "attributes",   // Image: its width and height, as attributes.width / attributes.height
            ["tableSort"] = null,           // Table Records: sorting by a column
            ["tablePaging"] = null,         // Table Records: lines per page
            ["dropdownEmpty"] = null,       // Dropdown: the empty entry
        };

        // Writes the fact under its spec key when there is one (true), else says in a Todo what was left (false).
        bool Carry(JsonObject o, string fact, JsonNode? value, string todo)
        {
            if (SpecKeys[fact] is { } key) { o[key] = value; return true; }
            Todo(todo);
            return false;
        }

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
                    // A form on the whole record of a Preparation aggregate binds to it, as Reactive scaffolding does (no local); others get a local.
                    if (kind is "form" or "editrecord" or "showrecord")
                        forms[wn!] = Prop(w, "source", "sourceRecord")?.Trim() is { } fs && Regex.Match(fs, @"^(\w+)\.List\.Current$") is { Success: true } fm && PrepAggregate(fm.Groups[1].Value) ? fs : wn + "Record";
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
            if (Prop(w, used, "style") is { } style) o["style"] = StyleText(style);
            var attrs = new JsonObject();
            foreach (var (k, v) in w["attrs"] as JsonObject ?? [])
            {
                if (k.Equals("id", StringComparison.OrdinalIgnoreCase)) { Todo($"{at} has an HTML attribute id, which Reactive refuses (the platform sets it); use the widget name in expressions instead"); continue; }
                if (S(v) is { } s) attrs[k] = Rw(s, at);
            }
            if (Prop(w, used, "inlineStyle") is { } inline) attrs["style"] = StyleText(inline);
            var css = new List<string>();
            if (Prop(w, used, "width") is { } wd && Css("width", wd) is { } cw) css.Add(cw);
            if (Prop(w, used, "height") is { } ht && Css("height", ht) is { } ch) css.Add(ch);
            if (Prop(w, used, "verticalAlignment") is { } va && Norm(va) is "top" or "middle" or "bottom") css.Add("vertical-align: " + Norm(va));
            if (Prop(w, used, "marginLeft") is { } ml && Regex.IsMatch(ml.Trim(), @"^[1-9]\d*(px|%)?$")) css.Add("margin-left: " + (Regex.IsMatch(ml.Trim(), @"^\d+$") ? ml.Trim() + "px" : ml.Trim()));
            if (css.Count > 0)
            {
                var add = string.Join("; ", css);
                attrs["style"] = S(attrs["style"]) is not { } had ? Quote(add)
                    : Literal(had) is { } lit ? Quote(lit.TrimEnd().TrimEnd(';') + "; " + add) : had + " + " + Quote("; " + add);
            }
            if (Prop(w, used, "align") is { } al && Norm(al) is "left" or "center" or "right" or "justify") AddClass(o, "text-align-" + Norm(al));
            if (Prop(w, used, "title") is { } ti) attrs["title"] = Rw(ti, at);
            Quiet(w, used);
            if (attrs.Count > 0) o["attributes"] = attrs;
            return o;
        }

        // Style Classes and the inline style of a Traditional widget are plain text; in Reactive both are expressions: "btn btn-primary" in quotes.
        // A value that is a string literal already is kept.
        static string StyleText(string value) => Literal(value.Trim()) is not null ? value.Trim() : Quote(value.Trim());

        // A Traditional width or height as CSS: 40char is 40ch, a number is pixels, px and % stay. (fill parent) and empty are the Reactive default.
        static string? Css(string prop, string value)
        {
            var v = value.Trim();
            if (Regex.Match(v, @"^(\d+)\s*char$", RegexOptions.IgnoreCase) is { Success: true } m) return prop == "width" ? $"{prop}: {m.Groups[1].Value}ch" : null;
            if (Regex.IsMatch(v, @"^\d+(\.\d+)?(px|%)$", RegexOptions.IgnoreCase)) return prop == "height" && v.EndsWith('%') ? null : $"{prop}: {v}";
            if (prop == "width" && Regex.IsMatch(v, @"^\d+$")) return $"{prop}: {v}px";
            return null;   // (fill parent), Null, an Input height in lines: nothing to carry
        }

        // A class added to the widget's Style Classes, whether that is a literal or an expression.
        static void AddClass(JsonObject o, string cls)
        {
            var had = S(o["style"]);
            o["style"] = had is null ? Quote(cls) : Literal(had) is { } lit ? Quote((lit + " " + cls).Trim()) : had + " + " + Quote(" " + cls);
        }

        // Properties with no Reactive meaning: design-time text, Ajax and validation-parent settings, Edit Record and table layout, values that are the default.
        static readonly string[] Dropped = ["example", "isAjaxRefreshed", "validationParent", "cellSpacing", "displayColumns", "lineSeparator", "effectiveCaptionWidth", "effectiveValueWidth", "masterEntity",
            "description", "marginLeft", "isEditable", "labelsPosition"];   // a widget description is a design note; Adaptive and 0 margins are the default
        static readonly string[] DefaultStyles = ["evenLineStyle=TableRecords_EvenLine", "oddLineStyle=TableRecords_OddLine", "headerStyle=TableRecords_Header",
            "labelStyle=EditRecord_Label", "labelStyle=EditRecord_Caption", "valueStyle=EditRecord_Value", "labelStyle=ShowRecord_Label", "valueStyle=ShowRecord_Value"];
        void Quiet(JsonObject w, HashSet<string> used)
        {
            if (w["props"] is not JsonObject p) return;
            foreach (var (k, v) in p)
            {
                var val = S(v)?.Trim() ?? "";
                if (Dropped.Contains(k, StringComparer.OrdinalIgnoreCase)
                    || k.Equals("align", StringComparison.OrdinalIgnoreCase) || k.Equals("verticalAlignment", StringComparison.OrdinalIgnoreCase)
                    || k.Equals("width", StringComparison.OrdinalIgnoreCase) || k.Equals("height", StringComparison.OrdinalIgnoreCase)
                    || k.Equals("display", StringComparison.OrdinalIgnoreCase) && IsTrue(val)
                    || k.Equals("textLines", StringComparison.OrdinalIgnoreCase) && val == "1"
                    || k.Equals("startIndex", StringComparison.OrdinalIgnoreCase) && val == "0"
                    || k.Equals("lineCount", StringComparison.OrdinalIgnoreCase) && WType(w) == "listrecords"
                    || k.Equals("type", StringComparison.OrdinalIgnoreCase) && WType(w) == "image"
                    // the record type of a list or form (the source decides it in Reactive), and the default Traditional styles of tables and records
                    || k.Equals("type", StringComparison.OrdinalIgnoreCase) && val.StartsWith("List of ", StringComparison.Ordinal)
                    || k.Equals("recordDefinition", StringComparison.OrdinalIgnoreCase)
                    || DefaultStyles.Contains(k + "=" + val.Trim('"'), StringComparer.OrdinalIgnoreCase)
                    // an input's Null Value that is the type's own null: Reactive shows an empty input for it
                    || k.Equals("nullValue", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(val, @"^(Null\w*\(\)|#1900-01-01( 00:00:00)?#|#00:00:00#|0|""""|)$"))
                    used.Add(k.ToLowerInvariant());
            }
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
                case "input": case "textarea":   // an Input of more than one line is a Text Area in Reactive
                    var multi = Prop(w, used, "textLines") is { } tl && int.TryParse(tl.Trim(), out var lines) && lines > 1;
                    o = Input(w, used, kind == "textarea" || multi); break;
                case "inputpassword":   // a Password Input of Traditional Web is an Input of type Password
                    o = Input(w, used, false); o["inputType"] = "Password"; used.Add("passwordtype"); break;
                case "customplaceholder": case "placeholdercontent":   // the content area of a Form or a block instance: its widgets move up
                    foreach (var c in Objs(w["children"])) One(c, into);
                    return;
                case "button": case "link": o = Click(w, used, kind == "button"); break;
                case "if": o = If(w, used); break;
                case "image":
                    o = New("Image", w, used);
                    if (Prop(w, used, "image", "resource", "imageName") is { } image)
                    { o["image"] = image; Todo($"{Label(w)} shows the image {image}, which must exist in the new module (images are not copied by oml migrate)"); }
                    if (Prop(w, used, "url") is { } url) Carry(o, "imageUrl", Rw(url, Label(w)), $"{Label(w)} shows the image at {url}, which the spec cannot set; set the source in Service Studio");
                    var sizes = new JsonObject();
                    foreach (var dim in new[] { "width", "height" }) if (Prop(w, used, dim) is { } dv) sizes[dim] = dv;
                    if (sizes.Count > 0)
                    {
                        if (Run.SpecKeys["imageSize"] is { } sizeKey) { var map = o[sizeKey] as JsonObject ?? new JsonObject(); o[sizeKey] = map; foreach (var (dk, dv2) in sizes) map[dk] = Rw(S(dv2), Label(w)); }
                        else Todo($"{Label(w)} has the size {string.Join(", ", sizes.Select(kv => kv.Key + " " + kv.Value))}, which the spec cannot set; set it in Service Studio");
                    }
                    if (Prop(w, used, "alt", "altText", "alternativeText") is { } alt) o["attributes"] = new JsonObject { ["alt"] = Rw(alt, Label(w)) };
                    Visible(w, used, o);
                    break;
                case "icon":
                    o = New("Icon", w, used); Visible(w, used, o);
                    if (Prop(w, used, "icon") is { } icon) Carry(o, "icon", icon, $"{Label(w)} shows the icon {icon}, which the spec cannot set; choose it in Service Studio");
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
                case "radiobutton" when SpecKeys["radioGroup"] is { } radioType:
                    o = New(radioType, w, used); Visible(w, used, o);
                    if (Prop(w, used, "variable") is { } rv) o["variable"] = Rw(rv, Label(w));
                    if (Prop(w, used, "value", "valueText") is { } rval) o["value"] = Rw(rval, Label(w));
                    if (ActionRef(w, used, "its On Change", "onChange") is { } rc) o["onChange"] = rc;
                    break;
                case "radiobutton": case "radiogroup":
                    o = TodoBox(w, $"is a Radio Button (variable {Prop(w, "variable") ?? "none"}, value {Prop(w, "value", "valueText") ?? "none"}); the spec has no Radio Group widget, so build the group in Service Studio", false);
                    used.UnionWith(["variable", "value", "valuetext", "style", "visible", "enabled", "display", "onchange", "mandatory"]);
                    break;
                case "tablerecords": o = Records(w, used, true); break;
                case "listrecords": o = Records(w, used, false); break;
                case "blockinstance": o = Instance(w, used); if (o is null) return; break;   // a library block Reactive does not need
                case "form": case "editrecord": case "showrecord": o = Form(w, used); break;
                case "popup": o = Popup(w, used, w["children"]); break;
                case "editabletable": o = TodoBox(w, "is an Editable Table, which Reactive does not have; rebuild it as a Table with inputs"); break;
                case "listbox": o = TodoBox(w, "is a List Box, which Reactive does not have; rebuild it as a list of Checkboxes"); break;
                case "comment": return;
                case "table": o = HtmlTable(w, used); break;   // a plain HTML table (layout): the same table as HTML elements
                case "row": case "tablerow": case "cell": case "headercell": case "rowcell": case "tablecell":
                    foreach (var c in Objs(w["children"])) One(c, into);   // a Reactive Form has no rows and cells: the content moves up
                    return;
                default:
                    o = TodoBox(w, $"is a {S(w["type"]) ?? S(w["kind"])} widget that has no Reactive translation here; rebuild it (its content is kept inside)");
                    used.UnionWith(w["props"] is JsonObject props ? props.Select(kv => kv.Key.ToLowerInvariant()) : []);
                    break;
            }
            if (kind is not ("editabletable" or "listbox" or "popup") && !(kind is "radiobutton" or "radiogroup" && SpecKeys["radioGroup"] is null)) Unhandled(w, used);
            into.Add(o);
        }

        // Rule 7: mandatory and the input type are carried; other validation types and messages are not.
        JsonObject Input(JsonObject w, HashSet<string> used, bool area)
        {
            var at = Label(w);
            var o = New(area ? "TextArea" : "Input", w, used); Visible(w, used, o);
            if (Prop(w, used, "variable") is { } v) o["variable"] = Rw(v, at);
            if (Prop(w, used, "mandatory", "isMandatory") is { } m && !IsFalse(m)) o["mandatory"] = Rw(m, at);
            if (Prop(w, used, "prompt") is { } pr) Carry(o, "prompt", Rw(pr, at), $"{at} has the prompt {pr}, which the spec cannot set; set it in Service Studio");
            if (Prop(w, used, "maxLength") is { } ml && int.TryParse(ml, out var len)) Carry(o, "maxLength", len, $"{at} has the maximum length {len}, which the spec cannot set; set it in Service Studio");
            if (ActionRef(w, used, "its On Change", "onChange") is { } oc) o["onChange"] = oc;
            if (area && Prop(w, used, "type", "inputType") is { } at2 && Norm(at2) is not ("text" or "")) Todo($"{at} has the input type {at2}, which a Text Area does not have");
            if (!area)
            {
                string? type = null;
                foreach (var raw in new[] { Prop(w, used, "inputType", "type"), Prop(w, used, "validationType") })   // inputType is a fact, the others generic properties
                {
                    if (Blank(raw)) continue;
                    var t = Norm(raw);
                    var mapped = t switch { "password" => "Password", "email" => "Email", "number" or "integer" or "decimal" or "longinteger" => "Number", _ => null };
                    if (mapped is not null) type ??= mapped;
                    else if (t is not ("text" or "none" or "default" or "noneselected")) Carry(o, "moreInputTypes", raw, $"{at} has the input or validation type {raw}, which the spec cannot express (it has Password, Email and Number); set it in Service Studio");
                }
                if (type is not null) o["inputType"] = type;
            }
            if (Prop(w, used, "validationMessage") is { } vm) Carry(o, "validationMessage", Rw(vm, at), $"{at} has the validation message {vm}, which is not a widget property; assign <Input>.ValidationMessage in a client action instead");
            return o;
        }

        // Rule 5: Button and Link. Navigate -> onClick destination with args; Submit and AjaxSubmit -> onClick calls the client action. The export says the
        // method only when a property of the click event has "Method" in its name (Export.cs 623); without it a screen `destination` means Navigate
        // (Export.cs 619 writes it only for a screen) and an On Click handler without one means a call of a screen action (Submit).
        JsonObject Click(JsonObject w, HashSet<string> used, bool button)
        {
            var at = Label(w);
            // Navigate to an External URL: a link is an HTML a element with href (the spec has no external target); a button runs a client
            // action that calls RedirectToURL.
            if ((Prop(w, "destination") ?? Prop(w, "onClick")) == "ExternalURL" && (w["args"] as JsonObject)?.FirstOrDefault(kv => kv.Key.Equals("URL", StringComparison.OrdinalIgnoreCase)).Value is { } urlNode && S(urlNode) is { } url)
            {
                Prop(w, used, "destination", "onClick", "method");
                if (!button)
                {
                    var a = New("AdvancedHtml", w, used);
                    a["tag"] = "a";
                    var attrs = a["attributes"] as JsonObject ?? new JsonObject();
                    attrs["href"] = Rw(url, at);
                    a["attributes"] = attrs;
                    if (Ws(w["children"]) is { Count: > 0 } kids) a["children"] = kids;
                    else if (Prop(w, used, "label", "text", "value") is { } lbl) a["children"] = new JsonArray(LabelWidget(lbl, x => Rw(x, at)));
                    return Wrapped(w, used, a);
                }
                var handler = "Open" + (Blank(S(w["name"])) ? "Url" + (extraActions.Count + 1) : S(w["name"]));
                extraActions.Add(new JsonObject
                {
                    ["name"] = handler,
                    ["flow"] = new JsonArray(new JsonObject { ["kind"] = "execute", ["name"] = "Redirect", ["action"] = "RedirectToURL", ["args"] = new JsonObject { ["URL"] = Rw(url, at) }, ["next"] = "End" }),
                });
                actionNames.Add(handler);
                var b = New("Button", w, used); Visible(w, used, b);
                b["onClick"] = handler;
                if (Ws(w["children"]) is { Count: > 0 } bk) b["children"] = bk;
                else if (Prop(w, used, "label", "text", "value") is { } bl) b["children"] = new JsonArray(LabelWidget(bl, x => Rw(x, at)));
                return b;
            }
            var o = New(button ? "Button" : "Link", w, used); Visible(w, used, o);
            var method = Norm(Prop(w, used, "method"));
            var dest = Prop(w, used, "destination");
            var linkUrl = button ? null : Prop(w, used, "url");
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
            else if (method is "submit" or "ajaxsubmit" or "ajax")   // Ajax and Submit are the same in Reactive: On Click runs a client action
            {
                if (ActionRef(w, used, "its On Click", "onClick") is { } action)
                {
                    o["onClick"] = action;
                    if (args is { Count: > 0 }) o["args"] = RwTree(args, at);
                }
                else if (onClick is null) Todo($"{at} submits, but the export gives no action; set its On Click");
            }
            else if (method == "" && linkUrl is not null) { }   // its address is carried or reported below
            else if (method == "") Todo($"{at} has no On Click in the export (it could not read the method, and a link to an address is not exported); set its On Click");
            else Todo($"{at} has the method {Prop(w, "method")}, which has no Reactive translation here; set its On Click");
            if (Prop(w, used, "confirm") is { } confirm) Carry(o, "confirm", Rw(confirm, at), $"{at} asks the user to confirm with {confirm}, and the spec has no key for a confirmation; add a confirmation popup or a JavaScript confirm before its action");
            if (button && Prop(w, used, "isDefault") is { } isDef && !IsFalse(isDef)) Carry(o, "isDefault", IsTrue(isDef) ? JsonValue.Create(true) : Rw(isDef, at), $"{at} is the default button of its form, and the spec has no key for that; set it in Service Studio");
            if (linkUrl is not null) Carry(o, "linkUrl", Rw(linkUrl, at), $"{at} links to the address {linkUrl}, which the spec cannot set; use an AdvancedHtml widget with tag a and attributes.href (untested), or set the link's address in Service Studio");
            var children = Ws(w["children"]);
            if (children.Count == 0 && Prop(w, used, "label", "text", "value") is { } label) children.Add(LabelWidget(label, x => Rw(x, at)));
            if (children.Count > 0) o["children"] = children;
            return o;
        }

        // A Traditional HTML Table with its rows and cells: Reactive has no Table widget for layout, so each part is an HTML element
        // (table, tr, td) that keeps its style, size and span. Rows and cells of an Edit Record still move up into the Form.
        JsonObject HtmlTable(JsonObject w, HashSet<string> used)
        {
            JsonObject Element(JsonObject x, HashSet<string> u, string tag)
            {
                var e = New("AdvancedHtml", x, u);
                e["tag"] = tag;
                foreach (var (span, attr) in new[] { ("colSpan", "colspan"), ("columnSpan", "colspan"), ("rowSpan", "rowspan") })
                    if (Prop(x, u, span) is { } sv && int.TryParse(sv, out var n) && n > 1)
                        (e["attributes"] as JsonObject ?? (JsonObject)(e["attributes"] = new JsonObject()))[attr] = Quote(n.ToString());
                return e;
            }
            var table = Element(w, used, "table");
            var rows = new JsonArray();
            foreach (var r in Objs(w["children"]))
            {
                if (WType(r) is not ("row" or "tablerow")) { One(r, rows); continue; }
                var ru = new HashSet<string>();
                var tr = Element(r, ru, "tr");
                var cells = new JsonArray();
                foreach (var c in Objs(r["children"]))
                {
                    if (WType(c) is not ("cell" or "headercell" or "rowcell" or "tablecell")) { One(c, cells); continue; }
                    var cu = new HashSet<string>();
                    var td = Element(c, cu, WType(c) == "headercell" ? "th" : "td");
                    if (Ws(c["children"]) is { Count: > 0 } kids) td["children"] = kids;
                    Unhandled(c, cu);
                    cells.Add(td);
                }
                if (cells.Count > 0) tr["children"] = cells;
                Unhandled(r, ru);
                rows.Add(tr);
            }
            if (rows.Count > 0) table["children"] = rows;
            return table;
        }

        JsonObject If(JsonObject w, HashSet<string> used)
        {
            var cond = Prop(w, used, "condition");
            if (cond is null) return TodoBox(w, "is an If without a readable condition");
            if (cond.Contains("InWebBlockPreview", StringComparison.Ordinal))
                return new JsonObject { ["type"] = "Container", ["children"] = Ws(w["false"]) };   // the true branch shows only in the block preview
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
            if (Prop(w, used, "mandatory", "isMandatory") is { } m && !IsFalse(m)) o["mandatory"] = Rw(m, at);
            var source = Prop(w, used, "source", "sourceList");
            if (source is not null) o["list"] = Rw(source, at);
            string Option(string expr) => Regex.Replace(Rw(expr, at), @"^\w+\.List\.Current\.", "");
            if (Prop(w, used, "valueAttribute", "optionValue", "optionsValue", "value") is { } val) o["values"] = Option(val);
            if (Prop(w, used, "labelAttribute", "optionLabel", "optionsText", "label", "text") is { } lab) o["labels"] = Option(lab);
            if (ActionRef(w, used, "its On Change", "onChange") is { } oc) o["onChange"] = oc;
            if (source is null) Todo($"{at} has no source list in the export; set its list, labels and values");
            if (Prop(w, used, "specialList", "hasSpecialList", "emptyValue", "emptyLabel", "emptyCaption", "emptyText") is { } special)
                Carry(o, "dropdownEmpty", Rw(special, at), $"{at} uses a special list or Empty Value setting ({special}), which the spec cannot express; build a data action that adds the entry to the options list");
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
            var tn = S(w["name"]);
            var paged = tn is not null && paging.ContainsKey(tn);
            if (paged) { Prop(w, used, "lineCount"); Prop(w, used, "startIndex"); }   // the aggregate pages with the locals (ListPatterns)
            if (table)
            {
                if (Prop(w, used, "sortable", "sortColumn", "sortAttribute") is { } sort) Carry(o, "tableSort", Rw(sort, at), $"{at} sorts by {sort}, which the spec cannot set; set the sorting in Service Studio");
                if (Prop(w, used, "lineCount", "linesPerPage", "pagination") is { } lines && !paged)
                    Todo($"{at} shows {lines} lines at a time, but no List_Navigation pages it here; set Max. Records of its aggregate, or add the OutSystems UI Pagination block");
                var showHeader = Prop(w, used, "showHeader");
                var before = currentTable; currentTable = tn;
                if (!IsFalse(showHeader) || showHeader is null) o["headerRow"] = Cells(w["header"], "HeaderCell");
                currentTable = before;
                o["row"] = Cells(w["row"], "RowCell");
                if (tn is not null && sorting.TryGetValue(tn, out var st))
                    Todo($"{at} sorts by clicking its header: in Service Studio set the table's On Sort to {st.Handler} (built, with the local {st.Local})"
                        + (st.Columns.Count > 0 ? $" and the Sort Attribute of its header cells: {string.Join(", ", st.Columns)}" : " and the Sort Attribute of each sortable header cell"));
            }
            else o["children"] = Ws(w["row"] ?? w["children"]);
            // Empty Message: Reactive shows nothing for an empty list; an If shows the text instead.
            if (Prop(w, used, "emptyMessage") is { } empty && Literal(empty) is not "")
            {
                var list = Rw(source, at);
                return new JsonObject
                {
                    ["type"] = "If", ["condition"] = $"{list}.Empty",
                    ["trueBranch"] = new JsonArray(new JsonObject { ["type"] = "Expression", ["value"] = Rw(empty, at) }),
                    ["falseBranch"] = new JsonArray(o),
                };
            }
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
                    if (Prop(w, used, "style", "styleClasses") is { } st) cell["style"] = StyleText(st);
                    if (Prop(w, used, "verticalAlignment") is { } va && Norm(va) is "top" or "middle" or "bottom")
                        cell["attributes"] = new JsonObject { ["style"] = Quote("vertical-align: " + Norm(va)) };
                    Quiet(w, used);
                    cell["children"] = Ws(w["children"]);
                    Unhandled(w, used);
                    result.Add(cell);
                }
                else result.Add(new JsonObject { ["type"] = cellType, ["children"] = Ws(new JsonArray(w.DeepClone())) });
            }
            return result;
        }

        // Rule 5 and 8: web block instances. Same name for a block of this module; a Traditional block of another module cannot be used.
        JsonObject? Instance(JsonObject w, HashSet<string> used)
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
            if (ListBlock(w, used, source) is { handled: true } lb) return lb.widget;
            var isLocal = !local.Contains('.') && cx.LocalBlocks.Any(b => b.Equals(local, StringComparison.OrdinalIgnoreCase));
            if (!isLocal && UiLibraryMap.IsLibrary(source) && UiLibraryMap.Block(source) is { } mapped) return Library(w, used, source, mapped);
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
            var notifyBlock = cx.NotifyingBlocks?.Contains(local) == true;
            foreach (var (evt, handler) in handlers.Where(h => notifyBlock && h.name.Equals("Notify", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var ev = (w["events"] as JsonObject)?["Notify"];
                o["events"] = new JsonObject { ["Notify"] = ev is JsonObject ej && ej["args"] is JsonObject ea && ea.Count > 0
                    ? new JsonObject { ["action"] = handler, ["args"] = RwTree(ea, at) } : JsonValue.Create(handler) };
                handlers.Remove((evt, handler));
            }
            foreach (var (evt, handler) in handlers)
                Todo($"{at} handles {(evt.StartsWith("On", StringComparison.OrdinalIgnoreCase) ? evt : "On" + evt)} with {handler}, which cannot be translated by rule; create an event on the block {local} (the spec's events key), fire it with triggerEvent where the block called Notify, and set events on this instance to call {handler}");
            return Wrapped(w, used, o);
        }

        // A block of a Traditional UI library (OutSystemsUIWeb, RichWidgets, OutSystemsChartsWeb), by the map in UiLibraryMap. A block that
        // Reactive does not need is dropped (its content, if any, stays in a Container). A block with a checked Reactive counterpart becomes
        // that block, with its inputs and placeholders renamed; what has no counterpart is a Todo that says what to do. Others are a Todo box
        // with the map's note. The Reactive library must be referenced by the new module (the plan lists it).
        JsonObject? Library(JsonObject w, HashSet<string> used, string source, UiLibraryMap.Row map)
        {
            var at = Label(w);
            var phs = w["placeholders"] as JsonObject ?? [];
            if (map.Dropped)
            {
                var kept = new JsonArray();
                foreach (var (_, list) in phs) foreach (var c in Ws(list).OfType<JsonObject>()) kept.Add(c.DeepClone());
                return kept.Count == 0 ? null : new JsonObject { ["type"] = "Container", ["children"] = kept };
            }
            if (map.Reactive == "Icon" && map.ReactiveKind == "widget")
            {
                var name = (w["args"] as JsonObject)?.FirstOrDefault(kv => kv.Key.Equals("Name", StringComparison.OrdinalIgnoreCase)).Value is { } nv ? S(nv) : null;
                if (name is not null && Regex.Match(name.Trim(), @"^Entities\.IconName\.(\w+)$") is { Success: true } icon)
                {
                    var iw = new JsonObject { ["type"] = "Icon" };
                    if (!Blank(S(w["name"]))) iw["name"] = S(w["name"]);
                    Carry(iw, "icon", icon.Groups[1].Value.Replace('_', '-'), $"{at} shows the icon {icon.Groups[1].Value}, which the spec cannot set; choose it in Service Studio");
                    if ((w["args"] as JsonObject)?.Any(kv => kv.Key.Equals("Size", StringComparison.OrdinalIgnoreCase)) == true)
                        Todo($"{at} sets the icon size; set Size on the Icon widget in Service Studio (Reactive's default is 2x, Traditional's 1x)");
                    return Wrapped(w, used, iw);
                }
            }
            if (!map.ByCode)
                return TodoBox(w, $"is an instance of {source}; in Reactive use {map.Reactive ?? "nothing like it"} ({map.ReactiveKind}). {map.Notes}".TrimEnd());
            var o = new JsonObject { ["type"] = "BlockInstance" };
            if (!Blank(S(w["name"]))) o["name"] = S(w["name"]);
            o["sourceBlock"] = map.Reactive;
            var args = new JsonObject();
            var ruleSaid = false;
            foreach (var (k, v) in w["args"] as JsonObject ?? [])
            {
                var value = S(v);
                if (Blank(value)) continue;
                var target = map.Inputs.FirstOrDefault(kv => kv.Key.Equals(k, StringComparison.OrdinalIgnoreCase));
                if (target.Key is null) { Todo($"{at} ({source}) passes {k} = {value}, an input the map does not know; check it against {map.Reactive}"); continue; }
                if (S(target.Value) is not { Length: > 0 } to) { Todo($"{at} ({source}) passes {k} = {value}, which {map.Reactive} has no input for. {map.Notes}".TrimEnd()); continue; }
                args[to] = Rw(value!, at);
                if (!ruleSaid && map.Rules.Length > 0 && value!.Contains("Entities.", StringComparison.Ordinal))
                {
                    Todo($"{at} ({source} -> {map.Reactive}) passes static entity values; check them: {map.Rules}");
                    ruleSaid = true;
                }
            }
            if (args.Count > 0) o["args"] = args;
            var outPh = new JsonObject();
            var orphans = new JsonArray();
            foreach (var (k, list) in phs)
            {
                var content = Ws(list);
                if (content.Count == 0) continue;
                var target = map.Placeholders.FirstOrDefault(kv => kv.Key.Equals(k, StringComparison.OrdinalIgnoreCase));
                if (S(target.Value) is { Length: > 0 } to) { outPh[to] = content; continue; }
                Todo($"{at} ({source}) has content in its placeholder {k}, which {map.Reactive} has no place for; it was kept next to the block. {map.Notes}".TrimEnd());
                foreach (var c in content) orphans.Add(c?.DeepClone());
            }
            if (outPh.Count > 0) o["placeholders"] = outPh;
            foreach (var (evt, _) in w["events"] as JsonObject ?? [])
                Todo($"{at} ({source}) handles {evt}; set the matching event of {map.Reactive} by hand");
            var built = Wrapped(w, used, o);
            return orphans.Count == 0 ? built : new JsonObject { ["type"] = "Container", ["children"] = new JsonArray(new JsonNode?[] { built }.Concat(orphans).ToArray()) };
        }

        // Rule 6: Edit Record, Show Record and Form become a Form with a local variable for the record.
        JsonObject Form(JsonObject w, HashSet<string> used)
        {
            var at = Label(w);
            var formName = !Blank(S(w["name"])) ? S(w["name"])! : "Form";
            var local = formName + "Record";
            var source = Prop(w, used, "source", "sourceRecord");
            if (source is not null && forms.TryGetValue(formName, out var bound) && bound == source.Trim())   // bound to the aggregate's record (Collect)
            {
                var f = New("Form", w, used); Visible(w, used, f);
                f["children"] = Ws(w["children"]);
                return f;
            }
            string? type = null;   // the record type is the type of the source expression; the export does not give it
            if (type is null && source is not null)
            {
                if (varTypes.TryGetValue(source.Trim(), out var vt)) type = vt;
                else if (Regex.Match(source.Trim(), @"^\w+\.List\.Current\.(\w+)$") is { Success: true } m) type = m.Groups[1].Value;
            }
            var l = new JsonObject { ["name"] = local, ["type"] = type ?? "Text" };
            // How the record gets filled, when that can be proven: an input of the same type is the local's default; a local (that the Preparation does not
            // assign) is copied at onInitialize; a record of a screen aggregate whose entity is one of its sources is copied in the aggregate's onAfterFetch.
            var src = source?.Trim();
            var filled = false;
            if (src is not null && Regex.IsMatch(src, @"^\w+$") && varTypes.ContainsKey(src))
            {
                if (IsInput(src)) { l["default"] = src; filled = true; }
                else if (!prepWrites.Contains(src)) { initFills.Add((local, src)); filled = true; }
            }
            else if (src is not null && Regex.Match(src, @"^(\w+)\.List\.Current\.(\w+)$") is { Success: true } am && aggDefs.TryGetValue(am.Groups[1].Value, out var def) && EntityOf(def, am.Groups[2].Value))
            { afterFills.Add((S(def["name"])!, local, Rw(src, at))); filled = true; }
            extraLocals[local] = l;
            varTypes[local] = type ?? "Text";
            if (type is null) Todo($"{at} needs the local variable {local} for its record, but the export gives no record type; set the type of {local}");
            if (source is not null && !filled)
                Todo(Regex.IsMatch(src!, @"^\w+\.List\.Current$")
                    ? $"{at} shows the record {source}, whose type is the aggregate's own record structure, which a local variable cannot have; fill the local variable {local} from {source}.<the entity> after the data is fetched, for example in the aggregate's onAfterFetch action"
                    : $"{at} shows the record {source}; fill the local variable {local} from it after the data is fetched, for example in the aggregate's onAfterFetch action");
            var o = New("Form", w, used); Visible(w, used, o);
            o["children"] = Ws(w["children"]);
            return o;
        }

        bool PrepAggregate(string name) => Objs((el["preparation"] as JsonObject)?["nodes"]).Any(n => NodeKind(n) == "aggregate" && string.Equals(S(n["name"]), name, StringComparison.OrdinalIgnoreCase));

        // Is `entity` one of the plain sources of the aggregate (not a grouped one), so that List.Current.<entity> is a record of that entity?
        static bool EntityOf(JsonObject aggregateNode, string entity) =>
            aggregateNode["groupBy"] is not JsonArray { Count: > 0 } && aggregateNode["aggregates"] is not JsonArray { Count: > 0 }
            && aggregateNode["sources"] is JsonArray sources && sources.Any(x => S(x) is { } t && t.Split(' ')[0].Equals(entity, StringComparison.OrdinalIgnoreCase));

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
    internal static List<string> SelfTest(bool show = false)
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
        Check(d.Status == "translated", "OrderDetail: translated (the record is filled, the confirm text and the phone type are carried)");
        Check(Text(d.Spec, "locals", 0, "name") == "OrderFormRecord" && Text(d.Spec, "locals", 0, "type") == "Order", "OrderDetail: the form gets the local OrderFormRecord of the record's type");
        Check(Text(d.Spec, "actions", 0, "flow", 0, "action") == "Shop.Order_CreateOrUpdate" && Text(d.Spec, "actions", 0, "flow", 0, "args", "Source") == "OrderFormRecord" && Text(d.Spec, "actions", 0, "flow", 0, "name") == "Store", "OrderDetail: the entity action calls the wrapper and <Form>.Record is rewritten in actions");
        Check(d.EntityActions.SequenceEqual(["CreateOrUpdateOrder"]), "OrderDetail: the entity action is recorded");
        Check(Text(d.Spec, "actions", 0, "flow", 1, "kind") == "message" && Text(d.Spec, "actions", 0, "flow", 1, "type") == "success" && Text(d.Spec, "actions", 0, "flow", 1, "message") == "\"Saved\"", "OrderDetail: Feedback_Message becomes a message node with the same text and type");
        Check(Text(d.Spec, "actions", 0, "flow", 2, "kind") == "destination" && Text(d.Spec, "actions", 0, "flow", 2, "args", "CustomerId") == "OrderFormRecord.CustomerId", "OrderDetail: the redirect is kept with rewritten arguments");
        var inputs = At(d.Spec, "widgets", 0, "children")!.AsArray();
        Check(Text(d.Spec, "widgets", 0, "type") == "Form" && Text(inputs[0], "variable") == "OrderFormRecord.Name" && Text(inputs[0], "mandatory") == "True" && At(inputs[0], "inputType") is null, "OrderDetail: the form and the first input are carried (mandatory, no input type for text)");
        Check(Text(inputs[1], "inputType") == "Email" && Text(inputs[2], "inputType") == "Phone Number", "OrderDetail: the Email validation type becomes an input type, Phone Number is passed through as typed");
        Check(Text(inputs[3], "value") == "NameInput.Valid and not OrderForm.Valid" || Text(inputs[3], "value") == "NameInput.Valid and not OrderFormRecord.Valid", "OrderDetail: <Input>.Valid stays");
        Check(d.Todo.Count == 0 && Text(inputs[4], "confirmationMessage") == "\"Sure?\"", $"OrderDetail: the confirm text is carried and the record is filled by code, so nothing is left: [{string.Join(" | ", d.Todo)}]");
        // Rule 2: the form's source is a record of a screen aggregate whose entity is a source: an assign opens its onAfterFetch action.
        Check(Text(d.Spec, "aggregates", 0, "onAfterFetch") == "GetOrderOnAfterFetch" && Text(d.Spec, "actions", 1, "name") == "GetOrderOnAfterFetch" && Text(d.Spec, "actions", 1, "flow", 0, "kind") == "assign"
            && Text(d.Spec, "actions", 1, "flow", 0, "assign", "OrderFormRecord") == "GetOrder.List.Current.Order" && At(d.Spec, "actions", 1, "flow")!.AsArray().Count == 1 && At(d.Spec, "actions", 1, "flow", 0, "next") is null, "OrderDetail: the form record is assigned from the aggregate in its onAfterFetch action");
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
        Check(pr.Status == "partial" && Text(pr.Spec, "aggregates", 0, "name") == "GetStats" && At(pr.Spec, "onInitialize") is null && At(pr.Spec, "actions") is null && Text(pr.Spec, "widgets", 0, "text") == "Hi" && Has(pr.Todo, "Preparation", "if IsAdmin", "execute Load", "not on every path", "aggregates were kept", "data action"),
            $"Dash: an aggregate on only one branch: the logic is not translated, the aggregate is kept, the Todo lists the nodes and asks for a data action: [{string.Join(" | ", pr.Todo)}]");

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
        Check(Has(st.Todo, "Calc", "aggregate CountOrders", "create a server action that runs this query and returns what the later nodes read (CountOrders.Count)", "replace those reads"), $"Report (rule 6): the Todo names the query node and what the later nodes read: [{string.Join(" | ", st.Todo)}]");
        Check(Text(st.Spec, "widgets", 0, "onClick") == "Calc" && Has(st.Todo, "Calc", "aggregate", "CountOrders") && Has(st.Todo, "Odd", "CommitTransaction"), "Report: the button still calls the stub and the Todo says what the original did; an unsupported node makes a stub too");

        // 5. A web block with a Notify; the block has a placeholder and an input.
        var blockInput = Parse("""
        { "kind": "Block", "name": "OrderCard", "flow": "Common", "moduleKind": "Traditional", "inputs": [ { "name": "OrderId", "type": "Order Identifier", "mandatory": true, "default": "" } ], "access": { "anonymous": true, "registered": true }, "preparation": null,
          "actions": [ { "name": "Pick", "flow": { "complete": true, "nodes": [ { "kind": "execute", "name": "Tell", "action": "Notify", "args": { "Message": "\"picked\"" } } ] } } ],
          "widgets": [ { "kind": "WebLink", "type": "Link", "name": "PickLink", "props": { "onClick": "Pick" }, "children": [ { "kind": "WebText", "type": "Text", "props": { "text": "Pick" } } ] }, { "kind": "WebPlaceholder", "type": "Placeholder", "name": "Extra" } ] }
        """);
        var b = Translate(blockInput, layout);
        Check(b.Kind == "Block" && At(b.Spec, "access") is null && At(b.Spec, "layout") is null && At(b.Spec, "title") is null, "OrderCard: a block has no layout, title or access");
        Check(Text(b.Spec, "actions", 0, "flow", 0, "kind") == "triggerEvent" && Text(b.Spec, "actions", 0, "flow", 0, "event") == "Notify" && Text(b.Spec, "actions", 0, "flow", 0, "args", "Message") == "\"picked\""
            && Text(b.Spec, "events", 0, "name") == "Notify" && Text(b.Spec, "events", 0, "inputs", 0, "name") == "Message" && !b.Todo.Any(t => t.Contains("Notify")),
            $"OrderCard: Notify is a triggerEvent of the block's Notify event (input Message): {b.Spec?.ToJsonString()}");
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
        Check(Has(i6.Todo, "Card1", "OnNotify", "events"), "Cards: OnNotify on an instance of a block that is not known to notify is a Todo that names the event");
        // With the block known to call Notify (the plan reads that from the exports), the handler is the instance's Notify event and reads the message as its input.
        var inst2 = Parse(inst.ToJsonString().Replace("\"nodes\":[]", "\"nodes\": [ { \"kind\": \"message\", \"name\": \"Show\", \"message\": \"NotifyGetMessage()\", \"type\": \"info\" } ]"));
        var i6b = Translate(inst2, plain with { NotifyingBlocks = new HashSet<string> { "OrderCard" } });
        Check(Text(i6b.Spec, "widgets", 0, "events", "Notify") == "Got" && !i6b.Todo.Any(t => t.Contains("Card1"))
            && Text(i6b.Spec, "actions", 0, "inputs", 0, "name") == "Message" && Text(i6b.Spec, "actions", 0, "flow", 0, "message") == "Message",
            $"Cards: a Notify handler is the instance's Notify event and NotifyGetMessage() is its input Message: {i6b.Spec?.ToJsonString()} [{string.Join(" | ", i6b.Todo)}]");
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
        //     screen's own JavaScript, carried as a script but with a Todo because it uses jQuery. End nodes are not listed: a connector says "End".
        var flows = Parse("""
        { "kind": "Screen", "name": "Flows", "flow": "MainFlow", "moduleKind": "Traditional", "javaScript": "$('.note').hide();",
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
        Check(fl.Status == "partial" && fl.Todo.Count == 2 && Has(fl.Todo, "FlowsScript", "jQuery") && Text(fl.Spec, "_javaScript") == "$('.note').hide();" && Has(fl.Todo, "Js", "asynchronous"),
            $"Flows: the screen's jQuery JavaScript (carried as a script) and the async JavaScript node are left (a flow of supported nodes is translated whole): [{string.Join(" | ", fl.Todo)}]");
        Check(fn.Count == 8 && Text(fn[0], "kind") == "if" && Text(fn[1], "kind") == "forEach" && Text(fn[1], "recordList") == "Items.List" && Text(fn[2], "action") == "PlaceOrder" && Text(fn[2], "args", "Qty") == "2"
            && Text(fn[3], "cases", 0, "next") == "Js" && Text(fn[3], "otherwise") == "Bad" && Text(fn[4], "kind") == "javaScript" && Text(fn[4], "javaScript") == "alert(1);" && At(fn[4], "_async") is null && Text(fn[4], "next") == "Say"
            && Text(fn[5], "type") == "success" && Text(fn[5], "next") == "End" && Text(fn[6], "kind") == "raiseException" && Text(fn[7], "kind") == "comment" && At(fn[7], "name") is null && At(fn[0], "_path") is null && fl.EntityActions.Count == 0,
            "Flows: if, forEach, a server action call, switch, javaScript, message, raiseException and comment are carried without _ keys");

        // 12. Rule 1: a Preparation with logic. An assign that reads an input comes before the aggregate (onInitialize); an if on the aggregate, a server call and an
        //     assign come after it (the onAfterFetch of that one aggregate). The aggregate keeps its name, so the widgets' expressions work.
        var customers = Parse("""
        { "kind": "Screen", "name": "Customers", "flow": "MainFlow", "moduleKind": "Traditional",
          "inputs": [ { "name": "Prefix", "type": "Text", "mandatory": false, "default": "" }, { "name": "CustomerId", "type": "Customer Identifier", "mandatory": false, "default": "" } ],
          "locals": [ { "name": "Title", "type": "Text", "default": "" }, { "name": "Note", "type": "Text", "default": "" } ],
          "preparation": { "complete": true, "nodes": [
            { "kind": "assign", "name": "SetTitle", "assign": { "Title": "Prefix + \" orders\"" }, "next": "GetOrders" },
            { "kind": "aggregate", "name": "GetOrders", "sources": ["Order"], "filters": ["Order.CustomerId = CustomerId"], "next": "Any" },
            { "kind": "if", "name": "Any", "condition": "GetOrders.List.Empty", "true": "NoOrders", "false": "Stats" },
            { "kind": "execute", "name": "Stats", "action": "LoadStats", "_ref": "LoadStats", "args": { "Count": "GetOrders.List.Length" }, "next": "End" },
            { "kind": "assign", "name": "NoOrders", "assign": { "Note": "\"No orders for \" + Session.Lang" }, "next": "End" } ] },
          "widgets": [ { "kind": "WebExpression", "type": "Expression", "name": "Heading", "props": { "value": "Title" } },
            { "kind": "WebTableRecords", "type": "TableRecords", "name": "OrdersTable", "props": { "source": "GetOrders.List" }, "header": [], "row": [ { "kind": "WebCell", "type": "Cell", "children": [ { "kind": "WebExpression", "type": "Expression", "props": { "value": "GetOrders.List.Current.Order.Id" } } ] } ] } ] }
        """);
        var cu = Translate(customers, plain);
        var cs = cu.Spec;
        Check(cu.Status == "partial" && cu.Todo.Count == 1 && Has(cu.Todo, "Stats", "OnInitialize", "GetOrdersOnAfterFetch", "request", "data action"), $"Customers: the only Todo is the server call in the handlers: [{string.Join(" | ", cu.Todo)}]");
        Check(Text(cs, "onInitialize") == "OnInitialize" && Text(cs, "aggregates", 0, "name") == "GetOrders" && Text(cs, "aggregates", 0, "onAfterFetch") == "GetOrdersOnAfterFetch" && At(cs, "actions")!.AsArray().Count == 2, "Customers: onInitialize and the aggregate's onAfterFetch name two actions of the screen");
        var init = At(cs, "actions")!.AsArray().First(a => Text(a, "name") == "OnInitialize"); var after = At(cs, "actions")!.AsArray().First(a => Text(a, "name") == "GetOrdersOnAfterFetch");
        Check(At(init, "flow")!.AsArray().Count == 1 && Text(init, "flow", 0, "name") == "SetTitle" && Text(init, "flow", 0, "assign", "Title") == "Prefix + \" orders\"" && Text(init, "flow", 0, "next") == "End", "Customers: onInitialize holds the assign, which leaves the flow (End) where the aggregate and the later nodes were");
        Check(At(after, "flow")!.AsArray().Count == 3 && Text(after, "flow", 0, "name") == "Any" && Text(after, "flow", 0, "kind") == "if" && Text(after, "flow", 0, "true") == "NoOrders" && Text(after, "flow", 0, "false") == "Stats"
            && Text(after, "flow", 1, "kind") == "execute" && Text(after, "flow", 1, "action") == "LoadStats" && Text(after, "flow", 1, "next") == "End" && Text(after, "flow", 2, "assign", "Note") == "\"No orders for \" + Client.Lang" && Text(after, "flow", 2, "next") == "End", "Customers: the onAfterFetch action starts at the if and keeps its branches, the server call and the assign, with explicit connectors");

        //    The unsafe cases: the aggregates are kept, the logic is not translated, and the Todo names the nodes and why.
        JsonObject Prep(string nodes, string locals = "") => Parse("{ \"kind\": \"Screen\", \"name\": \"P\", \"flow\": \"MainFlow\", \"moduleKind\": \"Traditional\", " + locals + "\"preparation\": { \"complete\": true, \"nodes\": [" + nodes + "] }, \"widgets\": [] }");
        void NotLifted(string what, string nodes, string[] words, int aggregatesKept = 1)
        {
            var x = Translate(Prep(nodes), plain);
            Check(x.Status == "partial" && At(x.Spec, "onInitialize") is null && At(x.Spec, "actions") is null && At(x.Spec, "aggregates")!.AsArray().Count == aggregatesKept && At(x.Spec, "aggregates", 0, "onAfterFetch") is null
                && Has(x.Todo, new[] { "Preparation logic was not translated", "aggregates were kept", "data action" }.Concat(words).ToArray()), $"{what}: kept the aggregates, left the logic, said why: [{string.Join(" | ", x.Todo)}]");
        }
        NotLifted("two aggregates read after the first", """
            { "kind": "aggregate", "name": "GetA", "sources": ["A"], "next": "GetB" }, { "kind": "aggregate", "name": "GetB", "sources": ["B"], "next": "Sum" },
            { "kind": "assign", "name": "Sum", "assign": { "Total": "GetA.List.Length + GetB.List.Length" }, "next": "End" }
            """, ["assign Sum", "read 2 aggregates", "GetA, GetB"], 2);
        NotLifted("SQL in the Preparation", """
            { "kind": "sql", "name": "Raw", "next": "GetX" }, { "kind": "aggregate", "name": "GetX", "sources": ["X"], "next": "End" }
            """, ["sql Raw", "SQL node needs a server action"]);
        NotLifted("an assignment to what a filter reads", """
            { "kind": "assign", "name": "Clean", "assign": { "Search": "Trim(Search)" }, "next": "GetX" },
            { "kind": "aggregate", "name": "GetX", "sources": ["X"], "filters": ["X.Name like Search"], "next": "End" }
            """, ["assign Clean", "assigns Search", "order"]);
        NotLifted("a filter that reads the output of a node", """
            { "kind": "execute", "name": "Load", "action": "LoadLimit", "_ref": "LoadLimit", "next": "GetX" },
            { "kind": "aggregate", "name": "GetX", "sources": ["X"], "maxRecords": "Load.Limit", "next": "End" }
            """, ["execute Load", "reads the output of the node Load"]);
        NotLifted("a Destination before the aggregate and a node after it", """
            { "kind": "if", "name": "Gone", "condition": "Flag", "true": "Away", "false": "GetX" }, { "kind": "destination", "name": "Away", "screen": "Home" },
            { "kind": "aggregate", "name": "GetX", "sources": ["X"], "next": "Use" }, { "kind": "assign", "name": "Use", "assign": { "N": "GetX.List.Length" }, "next": "End" }
            """, ["if Gone", "ends in a Destination"]);

        // 13. Rule 2: forms. A record of a screen aggregate (done above, in OrderDetail); an input is the local's default; a local is copied at initialize unless the
        //     Preparation assigns it; an aggregate's own record binds the form directly (Reactive scaffolding); a function result or an unknown source stay Todo lines.
        var forms = Parse("""
        { "kind": "Screen", "name": "Forms", "flow": "MainFlow", "moduleKind": "Traditional",
          "inputs": [ { "name": "Seed", "type": "Order", "mandatory": false, "default": "" } ], "locals": [ { "name": "Draft", "type": "Order", "default": "" }, { "name": "Built", "type": "Order", "default": "" } ],
          "preparation": { "complete": true, "nodes": [ { "kind": "aggregate", "name": "GetOrder", "sources": ["Order"], "next": "Make" }, { "kind": "assign", "name": "Make", "assign": { "Built.Name": "\"x\"" }, "next": "End" } ] },
          "widgets": [
            { "kind": "WebEditRecord", "type": "EditRecord", "name": "FromInput", "props": { "sourceRecord": "Seed" } },
            { "kind": "WebEditRecord", "type": "EditRecord", "name": "FromLocal", "props": { "sourceRecord": "Draft" } },
            { "kind": "WebEditRecord", "type": "EditRecord", "name": "FromBuilt", "props": { "sourceRecord": "Built" } },
            { "kind": "WebEditRecord", "type": "EditRecord", "name": "FromCurrent", "props": { "sourceRecord": "GetOrder.List.Current" } },
            { "kind": "WebEditRecord", "type": "EditRecord", "name": "FromOther", "props": { "sourceRecord": "Lookup.Record" } } ] }
        """);
        var fo = Translate(forms, plain);
        Check(Text(fo.Spec, "locals", 2, "name") == "FromInputRecord" && Text(fo.Spec, "locals", 2, "default") == "Seed" && Text(fo.Spec, "locals", 2, "type") == "Order", "Forms: an input of the same type is the record local's default");
        Check(Text(fo.Spec, "onInitialize") == "OnInitialize" && Text(fo.Spec, "actions", 0, "flow", 0, "assign", "FromLocalRecord") == "Draft", "Forms: a local that nothing assigns is copied in onInitialize");
        Check(fo.Todo.Count == 3 && Has(fo.Todo, "FromBuilt", "FromBuiltRecord") && !fo.Todo.Any(t => t.Contains("FromCurrent")) && Has(fo.Todo, "FromOther", "Lookup.Record")
            && !(At(fo.Spec, "locals") as JsonArray)!.OfType<JsonObject>().Any(l => Text(l, "name") == "FromCurrentRecord"),
            $"Forms: a local the Preparation assigns and an unknown source are Todo lines; the aggregate's own record binds the form without a local: [{string.Join(" | ", fo.Todo)}]");
        Check(At(fo.Spec, "actions")!.AsArray().Count == 1 && Text(fo.Spec, "actions", 0, "flow", 0, "name") == "FillFromLocalRecord" && Text(fo.Spec, "actions", 0, "flow", 1, "name") == "Make", "Forms: the Preparation's own logic (Make) is onInitialize, and the fill is its first node, connected to it");

        // 14. Rule 3: an Ajax Refresh node with `_targets`: a table fed by an aggregate, a container holding two lists, a container fed by nothing, no targets, a name
        //     that is not a widget.
        const string ajaxWhy = "a spec flow has no Ajax Refresh node (Traditional Web only); the properties that could be read are in _props";
        string RefreshAction(string name, string targets) => $$"""
            { "name": "{{name}}", "flow": { "complete": false, "unsupported": [ { "path": "p", "kind": "AjaxRefreshNode", "why": "{{ajaxWhy}}" } ], "nodes": [
              { "kind": "assign", "name": "Set", "assign": { "Msg": "\"x\"" }, "next": "Ajax" },
              { "kind": "AjaxRefreshNode", "name": "Ajax", "_props": { "Visible": "True" }{{targets}}, "next": "End" } ] } }
            """;
        var refresh = Parse("""
        { "kind": "Screen", "name": "Refresh", "flow": "MainFlow", "moduleKind": "Traditional", "locals": [ { "name": "Msg", "type": "Text", "default": "" } ],
          "preparation": { "complete": true, "nodes": [ { "kind": "aggregate", "name": "GetOrders", "sources": ["Order"], "next": "GetCustomers" }, { "kind": "aggregate", "name": "GetCustomers", "sources": ["Customer"], "next": "End" } ] },
          "actions": [ ACTIONS ],
          "widgets": [
            { "kind": "WebTableRecords", "type": "TableRecords", "name": "OrdersTable", "props": { "source": "GetOrders.List" }, "header": [], "row": [] },
            { "kind": "WebContainer", "type": "Container", "name": "Panel", "children": [
              { "kind": "WebListRecords", "type": "ListRecords", "name": "OrderList", "props": { "source": "GetOrders.List" }, "row": [] },
              { "kind": "WebListRecords", "type": "ListRecords", "name": "CustomerList", "props": { "source": "GetCustomers.List" }, "row": [] } ] },
            { "kind": "WebContainer", "type": "Container", "name": "Plain", "children": [ { "kind": "WebExpression", "type": "Expression", "props": { "value": "Msg" } } ] } ] }
        """.Replace("ACTIONS", string.Join(",\n", new[] {
            RefreshAction("ToTable", ", \"_targets\": { \"Widget\": \"OrdersTable\" }"), RefreshAction("ToPanel", ", \"_targets\": { \"Widgets\": [\"Panel\"] }"),
            RefreshAction("ToPlain", ", \"_targets\": { \"Widget\": \"Plain\" }"), RefreshAction("ToNothing", ""), RefreshAction("ToGhost", ", \"_targets\": { \"Widget\": \"Ghost\" }") })));
        var rf = Translate(refresh, plain);
        var rfa = At(rf.Spec, "actions")!.AsArray();
        Check(rfa.Count == 5 && At(rfa[0], "flow")!.AsArray().Count == 2 && Text(rfa[0], "flow", 0, "next") == "Ajax" && Text(rfa[0], "flow", 1, "kind") == "refreshData" && Text(rfa[0], "flow", 1, "dataSource") == "GetOrders" && Text(rfa[0], "flow", 1, "next") == "End" && Text(rfa[0], "flow", 1, "name") == "Ajax",
            "Refresh: a table fed by an aggregate becomes a refreshData node of that aggregate, in the node's place");
        Check(At(rfa[1], "flow")!.AsArray().Count == 3 && Text(rfa[1], "flow", 0, "next") == "Ajax_GetOrders" && Text(rfa[1], "flow", 1, "dataSource") == "GetOrders" && Text(rfa[1], "flow", 1, "next") == "Ajax_GetCustomers"
            && Text(rfa[1], "flow", 2, "dataSource") == "GetCustomers" && Text(rfa[1], "flow", 2, "next") == "End", "Refresh: a container that holds two lists gives two refreshData nodes in a chain");
        Check(At(rfa[2], "flow")!.AsArray().Count == 1 && Text(rfa[2], "flow", 0, "next") == "End" && At(rfa[2], "flow", 0, "_path") is null, "Refresh: a container fed by nothing that needs refreshing is removed without a Todo");
        Check(At(rfa[3], "flow")!.AsArray().Count == 1 && At(rfa[4], "flow")!.AsArray().Count == 1 && rf.Todo.Count == 2 && Has(rf.Todo, "ToNothing", "cannot read which widgets") && Has(rf.Todo, "ToGhost", "Ghost") && !Has(rf.Todo, "ToTable") && !Has(rf.Todo, "ToPlain"),
            $"Refresh: no `_targets`, or a name that is not a widget, removes the node with a Todo: [{string.Join(" | ", rf.Todo)}]");

        // 15. Rule 5: the table of widget facts. Every fact is there; turning one on writes the value under the key instead of a Todo.
        var facts = new[] { "confirm", "linkUrl", "icon", "radioGroup", "moreInputTypes", "prompt", "maxLength", "validationMessage", "isDefault", "imageUrl", "imageSize", "tableSort", "tablePaging", "dropdownEmpty" };
        var on = new[] { "confirm", "icon", "moreInputTypes", "prompt", "maxLength", "isDefault", "imageSize" };
        Check(facts.All(Run.SpecKeys.ContainsKey) && Run.SpecKeys.Count == facts.Length && Run.SpecKeys.Where(kv => kv.Value is null).Select(kv => kv.Key).Order().SequenceEqual(facts.Except(on).Order()), "Facts: every widget fact is in the table; the ones with a known spec key are on, the others are null");
        var widgetFacts = Parse("""
        { "kind": "Screen", "name": "Facts", "flow": "MainFlow", "moduleKind": "Traditional", "preparation": null, "actions": [ { "name": "Act", "flow": { "complete": true, "nodes": [] } } ],
          "widgets": [
            { "kind": "WebButton", "type": "Button", "name": "Go", "props": { "method": "Submit", "onClick": "Act", "confirm": "\"Sure?\"", "isDefault": "True" }, "children": [ { "kind": "WebText", "type": "Text", "props": { "text": "Go" } } ] },
            { "kind": "WebLink", "type": "Link", "name": "Site", "props": { "url": "\"https://example.com\"" }, "children": [ { "kind": "WebText", "type": "Text", "props": { "text": "Site" } } ] },
            { "kind": "WebIcon", "type": "Icon", "name": "Star", "props": { "icon": "\"star\"" } },
            { "kind": "WebInput", "type": "Input", "name": "Phone", "props": { "variable": "PhoneNo", "validationType": "Phone Number", "validationMessage": "\"Bad\"", "prompt": "\"Phone\"", "maxLength": "20" } },
            { "kind": "WebImage", "type": "Image", "name": "Pic", "props": { "url": "\"https://example.com/a.png\"", "width": "100", "height": "50" } },
            { "kind": "WebRadioButton", "type": "RadioButton", "name": "Opt", "props": { "variable": "Pick", "value": "1" } } ] }
        """);
        var before = Translate(widgetFacts, plain);
        var bw = At(before.Spec, "widgets")!.AsArray();
        Check(before.Todo.Count == 4 && Has(before.Todo, "Site", "https://example.com", "AdvancedHtml") && Has(before.Todo, "Phone", "validation message", "ValidationMessage") && Has(before.Todo, "Pic", "image at") && Has(before.Todo, "Opt", "Radio")
            && Text(bw[0], "confirmationMessage") == "\"Sure?\"" && Text(bw[0], "isDefault") == "true" && Text(bw[2], "icon") == "\"star\"" && Text(bw[3], "inputType") == "Phone Number" && Text(bw[3], "prompt") == "\"Phone\"" && Text(bw[3], "maxLength") == "20"
            && Text(bw[4], "attributes", "width") == "100" && Text(bw[4], "attributes", "height") == "50", $"Facts: the facts with a key are written, the others are Todo lines with a hint: [{string.Join(" | ", before.Todo)}]");
        var saved = new Dictionary<string, string?>(Run.SpecKeys);
        try
        {
            foreach (var f in new[] { "linkUrl", "validationMessage", "imageUrl" }) Run.SpecKeys[f] = f + "Key";
            Run.SpecKeys["radioGroup"] = "RadioGroup";
            var after2 = Translate(widgetFacts, plain);
            var wf = At(after2.Spec, "widgets")!.AsArray();
            Check(after2.Todo.Count == 0 && Text(wf[1], "linkUrlKey") == "\"https://example.com\"" && Text(wf[3], "validationMessageKey") == "\"Bad\"" && Text(wf[4], "imageUrlKey") == "\"https://example.com/a.png\"" && Text(wf[5], "type") == "RadioGroup" && Text(wf[5], "variable") == "Pick",
                $"Facts: turning a fact on is one table entry: the value is written under the key and the Todo goes: [{string.Join(" | ", after2.Todo)}]");
        }
        finally { foreach (var (k, v) in saved) Run.SpecKeys[k] = v; }

        if (show)
        {
            var indented = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            Console.WriteLine("--- screen entry: Preparation with logic (onInitialize and onAfterFetch)\n" + cs!.ToJsonString(indented));
            Console.WriteLine("--- screen entry: form filled from an aggregate (onAfterFetch)\n" + d.Spec!.ToJsonString(indented));
            Console.WriteLine("--- screen entry: forms filled from an input, a local or nothing\n" + fo.Spec!.ToJsonString(indented));
            Console.WriteLine("--- screen entry: Ajax Refresh nodes with targets\n" + rf.Spec!.ToJsonString(indented));
        }

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
        //     No layout, the block WebCard is local, the entity action has its wrapper: the form's record is a local, so onInitialize copies it. The two "method
        //     could not be read" notes are not Todo lines: a screen destination is a Navigate, an action an ordinary Submit.
        var sampleContext = new Context("Shop", new Dictionary<string, string> { ["CreateOrUpdateWebItem"] = "WebItem_CreateOrUpdate" }, new HashSet<string>(), new HashSet<string> { "WebCard" }, null, []);
        var sm = Translate(Parse(ExportSampleJson), sampleContext);
        var ss = sm.Spec;
        Check(sm.Status == "translated" && sm.Todo.Count == 0, $"Sample: nothing is left (the form's record is a local, copied at initialize): [{string.Join(" | ", sm.Todo)}]");
        Check(Text(ss, "onInitialize") == "OnInitialize" && Text(ss, "actions", 1, "name") == "OnInitialize" && Text(ss, "actions", 1, "flow", 0, "assign", "ItemFormRecord") == "Item", "Sample: the form record is copied from the local Item in onInitialize");
        Check(Text(ss, "name") == "WebItems" && Text(ss, "flow") == "MainFlow" && Text(ss, "title") == "\"Web items\"" && Text(ss, "access", "registered") == "true" && Text(ss, "locals", 1, "name") == "Item" && Text(ss, "locals", 2, "name") == "ItemFormRecord" && Text(ss, "locals", 2, "type") == "WebItem", "Sample: element facts and the form's record local");
        Check(Text(ss, "aggregates", 0, "name") == "FetchItems" && Text(ss, "aggregates", 0, "sources", 0) == "WebItem" && Text(ss, "aggregates", 0, "maxRecords") == "10" && At(ss, "aggregates", 0, "_path") is null, "Sample: the Preparation aggregate");
        Check(Text(ss, "actions", 0, "flow", 0, "assign", "Item.Name") == "ItemName" && Text(ss, "actions", 0, "flow", 1, "action") == "Shop.WebItem_CreateOrUpdate" && Text(ss, "actions", 0, "flow", 1, "next") == "End" && sm.EntityActions.SequenceEqual(["CreateOrUpdateWebItem"]), "Sample: the Save action, its entity action through the wrapper");
        Check(Text(ss, "widgets", 0, "type") == "Container" && Text(ss, "widgets", 0, "style") == "\"editor\"" && Text(ss, "widgets", 0, "children", 0, "variable") == "ItemName" && Text(ss, "widgets", 0, "children", 1, "onClick") == "Save" && Text(ss, "widgets", 0, "children", 1, "children", 0, "text") == "Save", "Sample: container, input and the button whose method is inferred");
        Check(Text(ss, "widgets", 1, "type") == "BlockInstance" && Text(ss, "widgets", 1, "sourceBlock") == "WebCard" && Text(ss, "widgets", 1, "args", "Caption") == "\"Summary\"", "Sample: the block instance");
        Check(Text(ss, "widgets", 2, "type") == "If" && Text(ss, "widgets", 2, "condition") == "not FetchItems.List.Empty" && Text(ss, "widgets", 2, "trueBranch", 0, "source") == "FetchItems.List" && Text(ss, "widgets", 2, "falseBranch", 0, "text") == "No items"
            && Text(ss, "widgets", 2, "trueBranch", 0, "row", 0, "children", 0, "onClick", "destination") == "WebDetail" && Text(ss, "widgets", 2, "trueBranch", 0, "row", 0, "children", 0, "onClick", "args", "ItemId") == "FetchItems.List.Current.WebItem.Id", "Sample: the If with a table in its true branch and the navigating link");
        Check(Text(ss, "widgets", 3, "type") == "List" && Text(ss, "widgets", 3, "source") == "FetchItems.List" && Text(ss, "widgets", 4, "type") == "Form" && Text(ss, "widgets", 4, "children", 0, "text") == "Edit item", "Sample: the list and the form (rows and cells are flattened)");

        // 16. Layout and Traditional-only properties, the block preview, a plain HTML table, the Ajax link method and an image named only "Web".
        var lay = Translate(Parse("""
        { "kind": "Block", "name": "Panel", "flow": "Common", "path": "Common/Panel", "access": {}, "widgets": [
          { "kind": "Container", "name": "Box", "props": { "align": "Center", "width": "40char", "display": "ShowBox", "example": "x", "isAjaxRefreshed": "False" }, "children": [] },
          { "kind": "If", "props": { "condition": "\"InWebBlockPreview\"=\"True\"" }, "true": [ { "kind": "Image", "type": "Web", "props": { "type": "Static" } } ],
            "false": [ { "kind": "Expression", "props": { "value": "Total", "example": "1 2 3" } } ] },
          { "kind": "Table", "props": { "width": "100%", "cellSpacing": "0" }, "children": [
            { "kind": "TableRow", "children": [ { "kind": "TableCell", "props": { "columnSpan": "1", "verticalAlignment": "Top" }, "children": [ { "kind": "Text", "props": { "text": "Name" } } ] } ] } ] },
          { "kind": "Link", "name": "More", "props": { "method": "Ajax", "onClick": "ShowMore", "title": "\"Show more\"" }, "children": [] } ],
          "actions": [ { "name": "ShowMore", "flow": [] } ] }
        """), new Context("Shop", new Dictionary<string, string>(), new HashSet<string>(), new HashSet<string>(), null, []));
        var lw = lay.Spec;
        Check(Text(lw, "widgets", 0, "style") == "\"text-align-center\"" && Text(lw, "widgets", 0, "attributes", "style") == "\"width: 40ch\"" && Text(lw, "widgets", 0, "visible") == "ShowBox",
            "Layout: align is a class, a width in characters is CSS, Display is Visible");
        Check(Text(lw, "widgets", 1, "type") == "Container" && Text(lw, "widgets", 1, "children", 0, "type") == "Expression" && At(lw, "widgets", 1, "children", 1) is null,
            "Block preview: only the branch that runs is kept, the preview image is gone");
        Check(Text(lw, "widgets", 2, "type") == "AdvancedHtml" && Text(lw, "widgets", 2, "tag") == "table" && Text(lw, "widgets", 2, "children", 0, "tag") == "tr"
            && Text(lw, "widgets", 2, "children", 0, "children", 0, "tag") == "td" && Text(lw, "widgets", 2, "children", 0, "children", 0, "attributes", "style") == "\"vertical-align: top\""
            && Text(lw, "widgets", 2, "children", 0, "children", 0, "children", 0, "text") == "Name", "HTML table: table, tr and td elements that keep their style and content");
        Check(Text(lw, "widgets", 3, "onClick") == "ShowMore" && Text(lw, "widgets", 3, "attributes", "title") == "\"Show more\"", "Ajax link: On Click runs the action; the title is an attribute");
        Check(!lay.Todo.Any(t => t.Contains("example") || t.Contains("isAjaxRefreshed") || t.Contains("cellSpacing") || t.Contains("no Reactive translation") || t.Contains("align")),
            $"Layout: nothing left about design-time or layout properties: [{string.Join(" | ", lay.Todo)}]");

        // 17. Traditional UI library blocks, by the map: a Card becomes OutSystemsUI.Card, a wait box is dropped, an icon becomes the Icon widget.
        var lib = Translate(Parse("""
        { "kind": "Block", "name": "Tiles", "flow": "Common", "path": "Common/Tiles", "access": {}, "widgets": [
          { "kind": "WebBlockInstance", "sourceBlock": "OutSystemsUIWeb.Card", "args": { "ExtendedClass": "\"tile\"" },
            "placeholders": { "Content": [ { "kind": "Text", "props": { "text": "Inside" } } ] } },
          { "kind": "WebBlockInstance", "sourceBlock": "RichWidgets.Feedback_AjaxWait" },
          { "kind": "WebBlockInstance", "sourceBlock": "RichWidgets.Icon", "args": { "Name": "Entities.IconName.exclamation_circle" } } ] }
        """), new Context("Shop", new Dictionary<string, string>(), new HashSet<string>(), new HashSet<string>(), null, []));
        var lt = lib.Spec;
        Check(Text(lt, "widgets", 0, "sourceBlock") == "OutSystemsUI.Card" && Text(lt, "widgets", 0, "args", "ExtendedClass") == "\"tile\"" && Text(lt, "widgets", 0, "placeholders", "Content", 0, "text") == "Inside",
            "Library: a Traditional Card is an OutSystemsUI Card with its input and placeholder");
        Check(Text(lt, "widgets", 1, "type") == "Icon" && At(lt, "widgets", 2) is null && !lib.Todo.Any(t => t.Contains("Feedback_AjaxWait")),
            $"Library: the wait box is dropped and the RichWidgets icon is an Icon widget: [{string.Join(" | ", lib.Todo)}]");

        // 18. A list screen as Service Studio scaffolds it in Traditional: List_Navigation pages it, List_SortColumn sorts it, List_Counter counts,
        // a search action resets the page and refreshes the Preparation aggregate. All of it becomes native Reactive: locals, the aggregate's paging
        // and dynamic sort, OutSystems UI Pagination with its handler, an OnSort handler, an If for the Empty Message.
        var list = Translate(Parse("""
        { "kind": "Screen", "name": "Contacts", "flow": "MainFlow", "path": "MainFlow/Contacts", "moduleKind": "Traditional", "access": { "registered": true },
          "preparation": { "complete": true, "nodes": [
            { "kind": "aggregate", "name": "GetContacts", "sources": ["Contact"],
              "sorts": [ { "attribute": "List_SortColumn_GetOrderBy(ContactTable.Id, \"{Contact}.[Name]\")", "dynamic": true } ], "next": "End" } ] },
          "actions": [ { "name": "Search", "inputs": [ { "name": "ResetPagination", "type": "Boolean" } ], "flow": { "complete": false, "nodes": [
              { "kind": "if", "name": "If1", "condition": "ResetPagination", "true": "ResetStartIndex", "false": "RefreshData1" },
              { "kind": "execute", "name": "ResetStartIndex", "action": "RichWidgets.List_Navigation_ResetStartIndex", "args": { "ListWidgetId": "ContactTable.Id" }, "next": "RefreshData1" },
              { "kind": "refreshData", "name": "RefreshData1", "dataSource": "GetContacts", "next": "AjaxRefresh1" },
              { "kind": "AjaxRefreshNode", "name": "AjaxRefresh1", "_targets": { "target": "End", "widgetOrWebBlock": "TableWrapper" }, "next": "End" } ],
            "unsupported": [
              { "kind": "RefreshDataNode", "path": "MainFlow/Contacts/Search/RefreshQuery[0]", "why": "it refreshes GetContacts, which is not an aggregate or data action of a screen or block (a Preparation aggregate); a spec refreshData names one of those" },
              { "kind": "AjaxRefreshNode", "path": "MainFlow/Contacts/Search/AjaxRefresh[0]", "why": "the spec has no Ajax Refresh node" } ] } } ],
          "widgets": [ { "kind": "Container", "name": "TableWrapper", "children": [
            { "kind": "TableRecords", "name": "ContactTable", "props": { "sourceRecordList": "GetContacts.List", "lineCount": "20", "startIndex": "List_Navigation_GetStartIndex(ContactTable.Id, True)",
                "emptyMessage": "\"No contacts\"", "showHeader": "True", "evenLineStyle": "TableRecords_EvenLine", "type": "List of Contact Record" },
              "header": [ { "kind": "TableRecordsHeaderCell", "props": { "verticalAlignment": "Null" }, "children": [ { "kind": "Text", "props": { "text": "Name" } },
                { "kind": "WebBlockInstance", "sourceBlock": "RichWidgets.List_SortColumn", "args": { "Column": "\"{Contact}.[Name]\"" } } ] } ],
              "row": [ { "kind": "TableRecordsDataCell", "children": [ { "kind": "Expression", "props": { "value": "GetContacts.List.Current.Contact.Name" } } ] } ] },
            { "kind": "WebBlockInstance", "sourceBlock": "RichWidgets.List_Counter", "args": { "LineCount": "ContactTable.LineCount", "StartIndex": "List_Navigation_GetStartIndex(ContactTable.Id, True)", "TotalRowCount": "GetContacts.Count" } },
            { "kind": "WebBlockInstance", "sourceBlock": "RichWidgets.List_Navigation", "args": { "ListWidgetId": "ContactTable.Id", "LineCount": "ContactTable.LineCount", "TotalRowCount": "GetContacts.Count", "MaxPageNumbers": "5" } },
            { "kind": "TextArea", "name": "Notes", "props": { "variable": "Note", "textLines": "3", "type": "Text" } } ] } ] }
        """), new Context("Shop", new Dictionary<string, string>(), new HashSet<string>(), new HashSet<string>(), null, []));
        var ls = list.Spec;
        Check(Text(ls, "aggregates", 0, "maxRecords") == "MaxRecords" && Text(ls, "aggregates", 0, "startIndex") == "StartIndex" && Text(ls, "aggregates", 0, "sorts", 0, "attribute") == "TableSort"
            && At(ls, "locals") is JsonArray locs && locs.OfType<JsonObject>().Any(l => Text(l, "name") == "MaxRecords" && Text(l, "default") == "20")
            && locs.OfType<JsonObject>().Any(l => Text(l, "name") == "TableSort" && Text(l, "default") == "\"Contact.Name\""),
            $"List: the aggregate pages and sorts with the locals: {ls.ToJsonString()}");
        var wrap = At(ls, "widgets", 0, "children") as JsonArray;
        Check(Text(wrap, 0, "type") == "If" && Text(wrap, 0, "condition") == "GetContacts.List.Empty" && Text(wrap, 0, "falseBranch", 0, "type") == "TableRecords"
            && At(wrap, 0, "falseBranch", 0, "headerRow", 0, "children", 1) is null
            && Text(wrap, 1, "type") == "Expression" && (Text(wrap, 1, "value") ?? "").Contains("StartIndex + 1")
            && Text(wrap, 2, "sourceBlock") == "OutSystemsUI.Pagination" && Text(wrap, 2, "events", "OnNavigate") == "GoToPage" && Text(wrap, 3, "type") == "TextArea",
            $"List: empty message, counter, Pagination, no sort block in the header, text area: {wrap?.ToJsonString()}");
        var search = (At(ls, "actions") as JsonArray)!.OfType<JsonObject>().First(a => Text(a, "name") == "Search");
        Check(Text(search, "flow", 1, "kind") == "assign" && Text(search, "flow", 1, "assign", "StartIndex") == "0" && Text(search, "flow", 2, "kind") == "refreshData"
            && (At(search, "flow") as JsonArray)!.Count == 3,
            $"List: the search resets the page and refreshes once: {search.ToJsonString()}");
        Check(list.Todo.Count == 1 && list.Todo[0].Contains("On Sort to OnSort") && list.Todo[0].Contains("Contact.Name"),
            $"List: one Todo is left, for On Sort and the Sort Attribute: [{string.Join(" | ", list.Todo)}]");

        // 19. Two server calls in a row become one server action (one request, one transaction) with the screen's access check; a run that
        //     reads a widget stays as it is. A missing role raises the user exception AccessDenied.
        var mergeIn = Parse("""
        { "kind": "Screen", "name": "Orders", "flow": "MainFlow", "moduleKind": "Traditional", "access": { "registered": true, "roles": ["Manager"] },
          "locals": [ { "name": "Draft", "type": "Order" } ],
          "widgets": [ { "kind": "Input", "name": "NameInput", "props": { "variable": "Draft.Name" } } ],
          "actions": [
            { "name": "Save", "inputs": [ { "name": "Note", "type": "Text" } ], "flow": { "complete": true, "nodes": [
              { "kind": "if", "name": "Ok", "condition": "Draft.Name <> \"\"", "true": "Create", "false": "End" },
              { "kind": "execute", "name": "Create", "action": "CreateDraft", "args": { "Order": "Draft", "Note": "Note" }, "next": "Audit" },
              { "kind": "execute", "name": "Audit", "action": "WriteAudit", "args": { "OrderId": "Create.Id" }, "next": "Show" },
              { "kind": "message", "name": "Show", "message": "\"Saved \" + Create.Id", "type": "info", "next": "End" } ] } },
            { "name": "Check", "flow": { "complete": true, "nodes": [
              { "kind": "execute", "name": "First", "action": "CreateDraft", "args": { "Order": "Draft", "Note": "NameInput.Value" }, "next": "Second" },
              { "kind": "execute", "name": "Second", "action": "WriteAudit", "args": { "OrderId": "First.Id" }, "next": "End" } ] } } ] }
        """);
        var mergeCx = new Context("Shop", new Dictionary<string, string>(), new HashSet<string>(), new HashSet<string>(), null, [],
            ServerOutputs: new Dictionary<string, IReadOnlyDictionary<string, string>> { ["CreateDraft"] = new Dictionary<string, string> { ["Id"] = "Order Identifier" }, ["WriteAudit"] = new Dictionary<string, string>() });
        var mg = Translate(mergeIn, mergeCx).Spec!;
        var saveFlow = (At(mg, "actions") as JsonArray)!.OfType<JsonObject>().First(a => Text(a, "name") == "Save")["flow"] as JsonArray;
        var srv = At(mg, "_serverActions", 0) as JsonObject;
        Check(srv is not null && Text(srv, "name") == "Orders_Save" && Text(srv, "inputs", 0, "name") == "Draft" && Text(srv, "inputs", 0, "type") == "Order" && Text(srv, "inputs", 1, "name") == "Note"
            && Text(srv, "outputs", 0, "name") == "Create_Id" && Text(srv, "outputs", 0, "type") == "Order Identifier"
            && Text(srv, "flow", 0, "condition") == "GetUserId() <> NullIdentifier()" && Text(srv, "flow", 2, "condition") == "CheckManagerRole()" && Text(srv, "flow", 3, "exception") == "AccessDenied"
            && (At(srv, "flow") as JsonArray)!.OfType<JsonObject>().Any(n => Text(n, "name") == "Audit" && Text(n, "next") == "SetOutputs"),
            $"Merge: one server action with inputs, the output read later and the access check: {srv?.ToJsonString()}");
        Check(saveFlow!.Count == 3 && Text(saveFlow, 0, "true") == "SaveOnServer" && Text(saveFlow, 1, "action") == "Orders_Save" && Text(saveFlow, 1, "args", "Draft") == "Draft"
            && Text(saveFlow, 1, "next") == "Show" && Text(saveFlow, 2, "message") == "\"Saved \" + SaveOnServer.Create_Id",
            $"Merge: the client action calls it once and reads its output: {saveFlow.ToJsonString()}");
        var checkFlow = (At(mg, "actions") as JsonArray)!.OfType<JsonObject>().First(a => Text(a, "name") == "Check")["flow"] as JsonArray;
        Check(checkFlow!.Count == 2 && At(mg, "_serverActions", 1) is null, $"Merge: calls that read a widget stay as they are: {checkFlow.ToJsonString()}");
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
