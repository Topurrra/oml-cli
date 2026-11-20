using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// The pure decisions of `oml migrate plan` that go beyond data and logic: screens, web blocks and emails (export, translate, the
// 4-screens.json spec and the worklist), session variables, the layout, style sheets and the content no inspect section lists.
// Assemble is the whole plan as a function of what the child processes print: the shell (Migrate.cs) only passes `run` and writes
// the files, and the selftest passes canned answers instead of child processes, so the real order of calls is what is tested.
static partial class Migrate
{
    // ---------- session variables and other content ----------

    static readonly HashSet<string> BasicTypes = new(["Text", "Integer", "Long Integer", "Decimal", "Boolean", "Date", "Time", "Date Time", "Phone Number", "Email", "Currency"], StringComparer.OrdinalIgnoreCase);
    internal static bool IsBasicType(string type) => BasicTypes.Contains(type.Trim()) || type.Trim().EndsWith(" Identifier", StringComparison.OrdinalIgnoreCase);

    // The launcher cannot call the secret-name test of oml check (it lives in Core, private), so this is a small one of its own: the same
    // words, plus pin, ssn and card. It looks at the words of a CamelCase or snake_case name, so ShippingMethod is not a pin.
    internal static bool SensitiveName(string name)
    {
        var words = Regex.Matches(name, @"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+").Select(m => m.Value.ToLowerInvariant()).ToArray();
        var joined = string.Concat(words);
        return new[] { "password", "passwd", "secret", "token", "apikey", "privatekey", "accesskey", "clientkey", "connectionstring", "credential" }.Any(joined.Contains)
            || words.Any(w => w is "pin" or "ssn" or "card");
    }

    // A session variable is "client" when only screens and blocks use it, it holds a basic type and its name is not sensitive; the reasons
    // otherwise are said in plain words. serverActions are the copied server and service actions whose describe text names Session.<Name>.
    internal static (string decision, string why) DecideSession(string name, string type, string[] serverActions, bool usedByScreens, bool hasDefault = false)
    {
        var why = new List<string>();
        if (serverActions.Length > 0) why.Add($"used by copied server logic ({string.Join(", ", serverActions)}): client variables do not exist on the server, so the value must come in as an input parameter or be read on the server");
        if (!IsBasicType(type)) why.Add($"its type ({(type.Length > 0 ? type : "unknown")}) is not a basic type");
        if (SensitiveName(name)) why.Add("its name looks sensitive and a client variable can be read by the user: never put it in the browser");
        if (!usedByScreens && serverActions.Length == 0) why.Add("no screen or block was found using it, so check by hand whether it is still needed");
        return why.Count == 0 ? ("client", "used only by screens and blocks and a basic type: becomes a client variable of the same name in 4-screens.json" + (hasDefault ? ", with its default value" : "; its default value is not read (this inspect does not give it), so set it by hand if it had one")) : ("manual", string.Join("; ", why));
    }

    // Variable -> the actions whose describe text names Session.<Variable> as a whole identifier. texts: action query (Kind:Name) -> text.
    internal static Dictionary<string, string[]> SessionUse(IReadOnlyDictionary<string, string> texts, IEnumerable<string> variables)
    {
        var use = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in variables)
        {
            var re = new Regex(@"(?<![\w.])Session\." + Regex.Escape(v) + @"(?!\w)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            var actions = texts.Where(t => re.IsMatch(t.Value)).Select(t => t.Key[(t.Key.IndexOf(':') + 1)..]).Order(StringComparer.Ordinal).ToArray();
            if (actions.Length > 0) use[v] = actions;
        }
        return use;
    }

    // What becomes of an element that no inspect section lists (otherContent), by its kind name. Inspect writes ReviewModel.Kind, that is the model
    // interface name without its leading I and without "Signature" (ISOAPClient -> SOAPClient, IEmail -> Email); a name with the I is accepted too.
    internal static (string decision, string why) DecideOther(string kind)
    {
        bool Has(string w) => kind.Contains(w, StringComparison.OrdinalIgnoreCase);
        if (Has("WebService") && !Has("Reference")) return ("stay", "exposed SOAP web service: stays in the old module, so its consumers keep working; copy it by hand if the new module must expose it");
        if (Has("SOAP") || Has("WebReference") || Has("Integration")) return ("copy", "consumed SOAP or other integration element: copied by name; its Service Center configuration does not follow, set it again for the new module");
        if (Has("Email")) return ("translate", "email: exported and translated like a screen (it lands in 4-screens.md)");
        if (Has("Entry")) return ("manual", "entry point: Reactive has no entry points; the URL of a screen or a redirect screen replaces it");
        return ("manual", "not covered by oml migrate: rebuild or check it by hand");
    }

    // ---------- export, translate, worklist ----------

    internal sealed record Item(Screen S, string Status, List<string> Todo, JsonObject? Spec, List<string> EntityActions, List<string> Sessions, string? Error);

    // At most 25 elements per `oml export` step, so no single command line or output grows without limit.
    internal static List<List<Screen>> ExportChunks(IEnumerable<Screen> screens) => screens.Chunk(25).Select(c => c.ToList()).ToList();

    // The element or error entry for every screen, by its describe file name. One name prints an object, several an array in the order asked;
    // a name that fails is {element, error}; a step that failed as a whole (no array of the right size) gives every name its error.
    internal static Dictionary<string, JsonObject> ParseExports(JsonNode? batch, List<List<Screen>> chunks)
    {
        var results = (P(batch, "results") as JsonArray)?.ToArray() ?? [];
        var map = new Dictionary<string, JsonObject>();
        for (var i = 0; i < chunks.Count; i++)
        {
            var output = i < results.Length ? P(results[i], "output") : null;
            JsonNode?[]? entries = output is JsonArray arr && arr.Count == chunks[i].Count ? arr.ToArray() : chunks[i].Count == 1 && output is JsonObject ? [output] : null;
            var why = (output as JsonObject) is { } o && S(o, "error") is { } e ? e : "no result from oml export";
            for (var j = 0; j < chunks[i].Count; j++)
                map[chunks[i][j].File] = entries?[j] as JsonObject ?? new JsonObject { ["element"] = chunks[i][j].Query, ["error"] = why };
        }
        return map;
    }

    // The translator on every exported element. A failed export, an answer that does not make sense or a translator that throws becomes
    // a worklist item with the reason, so one bad element never stops the plan.
    internal static List<Item> TranslateAll(IEnumerable<Screen> screens, IReadOnlyDictionary<string, JsonObject> exports, ScreenTranslator.Context ctx, Func<JsonObject, ScreenTranslator.Context, ScreenTranslator.Result> translate)
    {
        var items = new List<Item>();
        foreach (var s in screens)
        {
            if (!exports.TryGetValue(s.File, out var exp) || exp["error"] is not null || exp["kind"] is null)
            { var why = exports.TryGetValue(s.File, out var x) && S(x, "error") is { } m ? m : "no export"; items.Add(new Item(s, "worklist", [$"oml export could not read it ({why}): rebuild it from {s.File}."], null, [], [], why)); continue; }
            try
            {
                var res = translate(exp, ctx);
                var status = res.Status is "translated" or "partial" or "worklist" ? res.Status : "worklist";
                var todo = res.Todo.ToList();
                if (status != "worklist" && res.Spec is null) { status = "worklist"; todo.Add("the translator built nothing: rebuild it from its description."); }
                items.Add(new Item(s, status, todo, status == "worklist" ? null : res.Spec, res.EntityActions.ToList(), res.SessionVariables.ToList(), null));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            { items.Add(new Item(s, "worklist", [$"the translator failed on it ({e.Message}): rebuild it from {s.File}."], null, [], [], e.Message)); }
        }
        return items;
    }

    static Dictionary<string, (string entity, string op)> EntityActionMap(IEnumerable<string> entities)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var e in entities) foreach (var op in EntityOps) map.TryAdd(op + e, (e, op));
        return map;
    }

    // From a first translation pass: the entity actions the screens call (wrappers to make) and the session variables they use.
    internal static Extra ExtraFrom(IEnumerable<Item> items, string[] entities, IReadOnlyDictionary<string, string[]> serverSession)
    {
        var map = EntityActionMap(entities);
        var writes = new List<Call>(); var sessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
        {
            foreach (var a in it.EntityActions.Distinct()) if (map.TryGetValue(a, out var hit)) writes.Add(new Call(it.S.Name, "", "", hit.entity, hit.op));
            foreach (var v in it.Sessions) sessions.Add(v.StartsWith("Session.", StringComparison.OrdinalIgnoreCase) ? v[8..] : v);
        }
        return new Extra(writes, serverSession, sessions);
    }

    // The translator's context: what it must know about the migration as a whole.
    internal static ScreenTranslator.Context MakeContext(Result r, string? layout, IReadOnlyList<string> placeholders) =>
        new(r.OldName, new Dictionary<string, string>(r.WrapperMap), new HashSet<string>(r.SessionDecisions.Where(d => d.Decision == "client").Select(d => d.Name), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(r.Screens.Where(s => s.Kind == "Block").Select(s => s.Name), StringComparer.OrdinalIgnoreCase), layout, placeholders);

    // 4-screens.json: client variables, then emails, blocks and screens (the non-null specs), applied to the new module after 3-new-module.json.
    internal static JsonObject? ScreensSpec(Result r, IEnumerable<Item> items)
    {
        var spec = new JsonObject();
        if (r.ClientVariables.Count > 0) spec["clientVariables"] = r.ClientVariables.DeepClone();
        foreach (var (kind, key) in new[] { ("Email", "emails"), ("Block", "blocks"), ("Screen", "screens") })
        {
            var list = items.Where(i => i.S.Kind == kind && i.Spec is not null).Select(i => (JsonNode?)i.Spec!.DeepClone()).ToArray();
            if (list.Length > 0) spec[key] = new JsonArray(list);
        }
        return spec.Count == 0 ? null : spec;
    }

    static readonly string[] Statuses = ["translated", "partial", "worklist"];
    internal static SortedDictionary<string, int> StatusCounts(IEnumerable<Item> items)
    {
        var c = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in Statuses) c[s] = items.Count(i => i.Status == s);
        return c;
    }
    internal static string Coverage(IReadOnlyList<Item> items) =>
        items.Count == 0 ? "screens: none to translate" : "screens: " + string.Join(", ", Statuses.Select(s => $"{items.Count(i => i.Status == s)} {s}")) + $" of {items.Count}";

    internal static JsonArray ScreensPlan(IEnumerable<Item> items) =>
        new(items.Select(i => (JsonNode?)new JsonObject { ["kind"] = i.S.Kind, ["name"] = i.S.Name, ["status"] = i.Status, ["todo"] = i.Todo.Count }).ToArray());

    // 4-screens.md: a summary table, then a section per element that is partial or not built, with its todo as checkboxes.
    internal static string ScreensMd(Result r, IReadOnlyList<Item> items, bool spec)
    {
        var sb = new StringBuilder();
        sb.Append($"# Screens, blocks and emails of {r.OldName} in the new module\n\n");
        sb.Append("Read `oml doc traditional-to-reactive` first. " + (spec ? "`4-screens.json` holds what the translator built; apply it to the new module after `3-new-module.json`. " : "Nothing could be translated by code, so every element is a worklist item. ") + "A `partial` element is built with TODO stubs; a `worklist` element is not built: rebuild it from its description (`oml describe` of the original) and its raw export. Tick each item when it is built and checked.\n\n");
        sb.Append($"The entities stay in {r.OldName} and are read only for the new module. A write goes through a wrapper server action of {r.OldName} (public, with the inputs and outputs of the entity action), never through CreateX, UpdateX or DeleteX directly. Wrappers: {(r.WrapperNames.Count > 0 ? string.Join(", ", r.WrapperNames) : "none")}. If an element needs another one, add it to {r.OldName} the same way and reference it from the new module.\n\n");
        sb.Append("## Summary\n\n| Element | Kind | Status |\n|---|---|---|\n");
        foreach (var i in items) sb.Append($"| {i.S.Name} | {i.S.Kind} | {i.Status} |\n");
        if (items.Count == 0) sb.Append("| (the old module has no screens, blocks or emails) | | |\n");
        sb.Append($"\n{Coverage(items)}\n");
        foreach (var i in items.Where(i => i.Status != "translated"))
        {
            sb.Append($"\n## {i.S.Name} ({i.S.Kind.ToLowerInvariant()}, {i.Status})\n\n");
            if (i.S.Kind != "Email") sb.Append($"Flow {(i.S.Flow.Length > 0 ? i.S.Flow : "?")}; inputs: {(i.S.Inputs.Length > 0 ? string.Join(", ", i.S.Inputs) : "none")}; roles: {(i.S.Roles.Length > 0 ? string.Join(", ", i.S.Roles) : "none")}.\n\n");
            foreach (var t in i.Todo) sb.Append($"- [ ] {t}\n");
            if (i.Todo.Count == 0) sb.Append("- [ ] rebuild it\n");
            sb.Append($"\nDescription: `{i.S.File}`" + (i.Error is null ? $"; raw export: `{i.S.Export}`" : "") + "\n");
        }
        return sb.ToString();
    }

    // ---------- layout ----------

    // The Reactive layout block of a template: a block whose name starts with Layout; LayoutTopMenu, then LayoutSideMenu, then the first.
    internal static string? PickLayout(JsonNode templateInspect)
    {
        var layouts = Names(templateInspect, "blocks").Where(n => n.StartsWith("Layout", StringComparison.Ordinal)).ToArray();
        return layouts.FirstOrDefault(n => n == "LayoutTopMenu") ?? layouts.FirstOrDefault(n => n == "LayoutSideMenu") ?? layouts.FirstOrDefault();
    }

    // The placeholder names in the export of a layout block: its Placeholder widgets (type Placeholder, with a name) anywhere in the tree: in
    // children, an If's true and false, a table's header and row, and the slots of a block instance (Export.cs 590-653).
    internal static List<string> Placeholders(JsonNode? export)
    {
        var names = new List<string>();
        void Walk(JsonNode? n)
        {
            if (n is JsonObject o)
            {
                if (ScreenTranslator.IsPlaceholder(o) && S(o, "name") is { } name && !names.Contains(name)) names.Add(name);
                foreach (var kv in o) Walk(kv.Value);
            }
            else if (n is JsonArray a) foreach (var x in a) Walk(x);
        }
        Walk(export);
        return names;
    }

    // ---------- style sheets ----------

    // css/<Element>.css for each sheet of `oml describe --css --all` (entries {element, kind, styleSheet}) and css/README.md.
    internal static List<(string file, string text)> CssFiles(JsonNode? sheets, string oldName)
    {
        var rows = (sheets is JsonArray a ? a.ToArray() : [sheets]).OfType<JsonObject>().Where(x => S(x, "styleSheet") is { Length: > 0 }).ToArray();
        var files = new List<(string, string)>(); var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var table = new StringBuilder();
        foreach (var x in rows)
        {
            var element = S(x, "element") ?? S(x, "name") ?? "sheet";
            var baseName = Regex.Replace(element, @"[^A-Za-z0-9_-]", "_"); var file = "css/" + baseName + ".css"; var n = 2;
            while (!used.Add(file)) file = $"css/{baseName}-{n++}.css";
            var text = S(x, "styleSheet")!; var kind = S(x, "kind") ?? "?";
            files.Add((file, text));
            table.Append($"| {file[4..]} | {element} | {kind} | {Encoding.UTF8.GetByteCount(text)} bytes | {(kind.Contains("Theme", StringComparison.OrdinalIgnoreCase) ? "theme: port by hand" : "travels with the translated element")} |\n");
        }
        if (files.Count == 0) return [];
        files.Insert(0, ("css/README.md", $"# Style sheets of {oldName}\n\nOne file per style sheet of the old module (`oml describe --css --all`).\n\n- Screen and block sheets travel with their translated element: the translator puts them in `4-screens.json`. These copies are for reference.\n- Theme sheets are NOT added to the new module: they target Traditional markup. A person or agent ports them, selector by selector, into the theme of the new module.\n\n| File | Element | Kind | Size | What happens |\n|---|---|---|---|---|\n{table}"));
        return files;
    }

    // ---------- status: the screens of a plan against the new module ----------

    internal static JsonObject ScreenProgress(JsonNode plan, JsonNode newInspect)
    {
        var have = new Dictionary<string, HashSet<string>>
        {
            ["Screen"] = Set(Names(newInspect, "screens")), ["Block"] = Set(Names(newInspect, "blocks")),
            ["Email"] = Set(Names(newInspect, "emails").Concat(Names(newInspect, "otherContent"))),
        };
        var rows = Rows(plan, "screens");
        var by = new JsonObject(); var parts = new List<string>();
        foreach (var st in Statuses)
        {
            var of = rows.Where(x => S(x, "status") == st).ToArray();
            var missing = of.Where(x => !(have.TryGetValue(S(x, "kind") ?? "", out var h) && h.Contains(S(x, "name") ?? ""))).Select(x => S(x, "name")!).ToArray();
            by[st] = new JsonObject { ["total"] = of.Length, ["exist"] = of.Length - missing.Length, ["missing"] = Arr(missing) };
            parts.Add($"{st} {of.Length - missing.Length} of {of.Length}");
        }
        return new JsonObject { ["byStatus"] = by, ["line"] = "screens in the new module: " + string.Join("; ", parts) };
    }

    // ---------- the whole plan ----------

    internal sealed record Outcome(Result R, List<(string file, string text)> Files, JsonObject Output, ScreenTranslator.Context Context, List<Item> Items);

    static string Json(JsonNode n) => n.ToJsonString(Indented) + "\n";

    // run(args, stdin) starts oml and returns its JSON (a failure with an error message throws a Refusal). Order of the work: the logic
    // (tree) -> which copied actions use Session.X (describe) -> export -> a first translation to learn the entity actions and session variables
    // the screens use -> the final decisions (wrappers, client variables) -> the translation with the full context.
    internal static Outcome Assemble(string old, string name, string outDir, JsonNode inspect, IReadOnlyDictionary<string, string> map, List<string> producerNotes,
        string? template, Func<string[], string?, JsonNode> run, Func<JsonObject, ScreenTranslator.Context, ScreenTranslator.Result>? translate = null, bool copyExposed = false)
    {
        translate ??= ScreenTranslator.Translate;
        var r = Build(inspect, old, name, outDir, map, [], Extra.None with { CopyExposed = copyExposed });
        var notes = new List<string>();
        JsonNode? Try(string[] args, string? stdin, string what)
        { try { return run(args, stdin); } catch (Refusal e) { notes.Add($"{what} could not be read ({e.Message}); {(what.Contains("describe") ? "check by hand" : "plan again once it works")}."); return null; } }

        // 1. entity actions in the copied logic
        var calls = new List<Call>();
        if (r.Logic.Count > 0)
        {
            var steps = new JsonObject { ["steps"] = new JsonArray(r.Logic.Select(l => (JsonNode?)Arr(["tree", old, l.query])).ToArray()) };
            calls = FindCalls(run(["batch", "-", "--continue"], steps.ToJsonString()), r.Logic, r.EntityNames, notes);
        }
        // 2. which copied server and service actions use Session.<Name>
        var serverUse = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var serverLogic = r.Logic.Where(l => l.query.StartsWith("ServerAction:") || l.query.StartsWith("ServiceAction:")).ToList();
        if (r.Sessions.Length > 0 && serverLogic.Count > 0)
            serverUse = SessionUse(Describe(run, old, serverLogic, notes, "--depth", "0"), r.Sessions);
        // 3. export every screen, block and email
        var chunks = ExportChunks(r.Screens);
        JsonNode? exportBatch = null;
        if (chunks.Count > 0)
            exportBatch = run(["batch", "-", "--continue"], new JsonObject { ["steps"] = new JsonArray(chunks.Select(c => (JsonNode?)Arr(new[] { "export", old }.Concat(c.Select(s => s.Query)))).ToArray()) }.ToJsonString());
        var exports = ParseExports(exportBatch, chunks);
        // 4. the layout of a Reactive template
        string? layout = null; var placeholders = new List<string>();
        if (template is not null)
        {
            var ti = run(["inspect", template], null);
            if (IsTraditional(ti)) throw new Refusal($"--template {template} is a {S(ti, "kind")} module: give a Reactive template.");
            layout = PickLayout(ti);
            if (layout is null) notes.Add($"The template has no layout block (a block named Layout...): the screens are built without a layout block.");
            else
            {
                placeholders = Placeholders(Try(["export", template, "Block:" + layout], null, $"oml export of the layout {layout}"));
                if (placeholders.Count == 0) notes.Add($"No placeholder was read in the layout {layout}: check the placeholder names of the translated screens.");
            }
        }
        else notes.Add("No --template was given, so the screens are built without a layout block. Give --template <reactive-template.oml> (a template with a Layout block, see oml templates) and plan again to use one.");

        // 5. first pass (all entity actions have a wrapper name, so none is refused), then the final decisions, then the real pass
        var allWrappers = EntityActionMap(r.EntityNames).ToDictionary(kv => kv.Key, kv => kv.Value.entity + "_" + kv.Value.op);
        var first = TranslateAll(r.Screens, exports, new ScreenTranslator.Context(r.OldName, allWrappers, new HashSet<string>(), new HashSet<string>(r.Screens.Where(s => s.Kind == "Block").Select(s => s.Name)), layout, placeholders), translate);
        r = Build(inspect, old, name, outDir, map, calls, ExtraFrom(first, r.EntityNames, serverUse) with { CopyExposed = copyExposed });
        r.Notes.AddRange(producerNotes); r.Notes.AddRange(notes);
        var context = MakeContext(r, layout, placeholders);
        var items = TranslateAll(r.Screens, exports, context, translate);
        foreach (var i in items.Where(i => i.Error is not null && i.Error != "no export")) r.Notes.Add($"{i.S.Kind} {i.S.Name} was not translated: {i.Error}.");
        foreach (var a in items.SelectMany(i => i.EntityActions).Distinct().Where(a => EntityActionMap(r.EntityNames).ContainsKey(a) && !r.WrapperMap.ContainsKey(a)))
            r.Notes.Add($"A translation reported the entity action {a} without a wrapper for it: add the wrapper by hand.");
        r.ScreensSpec = ScreensSpec(r, items);

        // 6. descriptions and style sheets
        var texts = Describe(run, old, r.Screens.Select(s => (s.Query, s.Name)).ToList(), r.Notes);
        var css = CssFiles(Try(["describe", old, "--css", "--all"], null, "oml describe --css --all"), r.OldName);
        if (css.Count > 0) r.Notes.Add($"{css.Count - 1} style sheet(s) are in css/. Screen and block sheets travel with their translated element; the theme sheets target Traditional markup and are not added to the new module: port them by hand (css/README.md).");

        // 7. the files
        var counts = StatusCounts(items);
        r.Plan["screens"] = ScreensPlan(items);
        r.Plan["layout"] = new JsonObject { ["template"] = template, ["block"] = layout, ["placeholders"] = Arr(placeholders) };
        r.Plan["coverage"] = Coverage(items);
        r.Plan["notes"] = Arr(r.Notes);
        var files = new List<(string, string)> { ("plan.json", Json(r.Plan)) };
        if (r.Prepare is not null) files.Add(("1-prepare-old.json", Json(r.Prepare)));
        if (r.Source is not null) files.Add(("2-source.json", Json(r.Source)));
        files.Add(("3-new-module.json", Json(r.NewModule)));
        if (r.ScreensSpec is not null) files.Add(("4-screens.json", Json(r.ScreensSpec)));
        files.Add(("4-screens.md", ScreensMd(r, items, r.ScreensSpec is not null)));
        files.AddRange(css);
        foreach (var s in r.Screens)
        {
            files.Add((s.File, texts.TryGetValue(s.Query, out var t) ? t : "(no description)\n"));
            if (exports.TryGetValue(s.File, out var ex) && ex["error"] is null) files.Add((s.Export, Json(ex)));
        }
        var output = new JsonObject
        {
            ["ok"] = true, ["source"] = old, ["target"] = name, ["out"] = outDir,
            ["counts"] = JsonSerializer.SerializeToNode(r.Counts),
            ["wrappers"] = new JsonObject { ["create"] = r.WrapperCreate, ["exists"] = r.WrapperExists },
            ["references"] = JsonSerializer.SerializeToNode(r.RefCounts),
            ["repointed"] = r.Calls.Count,
            ["screens"] = JsonSerializer.SerializeToNode(counts), ["coverage"] = Coverage(items),
            ["clientVariables"] = Arr(r.SessionDecisions.Where(d => d.Decision == "client").Select(d => d.Name)),
            ["files"] = JsonSerializer.SerializeToNode(files.Select(f => f.Item1)),
            ["next"] = JsonSerializer.SerializeToNode(Next(r, old, name, outDir, template)),
            ["notes"] = JsonSerializer.SerializeToNode(r.Notes),
        };
        return new Outcome(r, files, output, context, items);
    }
}
