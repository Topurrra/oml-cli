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
    // threads: the plan can pass the value to the copied server logic as an input (ThreadSession); without it, server use is a reason for a person.
    internal static (string decision, string why) DecideSession(string name, string type, string[] serverActions, bool usedByScreens, bool hasDefault = false, bool threads = false)
    {
        var why = new List<string>();
        if (serverActions.Length > 0 && !threads) why.Add($"used by copied server logic ({string.Join(", ", serverActions)}): client variables do not exist on the server, so the value must come in as an input parameter or be read on the server");
        if (!IsBasicType(type)) why.Add($"its type ({(type.Length > 0 ? type : "unknown")}) is not a basic type");
        if (SensitiveName(name)) why.Add("its name looks sensitive and a client variable can be read by the user: never put it in the browser");
        if (!usedByScreens && serverActions.Length == 0) why.Add("no screen or block was found using it, so check by hand whether it is still needed");
        if (why.Count > 0) return ("manual", string.Join("; ", why));
        var head = serverActions.Length > 0
            ? $"a basic type: becomes a client variable of the same name in 4-screens.json; the copied server actions that read it ({string.Join(", ", serverActions)}) get it as an input (2-source.json), and screens pass Client.{name}"
            : "used only by screens and blocks and a basic type: becomes a client variable of the same name in 4-screens.json";
        return ("client", head + (hasDefault ? ", with its default value" : "; its default value is not read (this inspect does not give it), so set it by hand if it had one"));
    }

    // Reactive has no session variables. A session variable that copied server logic reads (and does not write) stays in the browser as a
    // client variable and reaches the server as an input: every copied action that reads Session.<Name> gets an input (the variable's
    // name, or Session<Name> when the action has a name like it already) with the variable's default, its reads become that input
    // (rewrite), and every copied action that calls one of them gets the input too and passes it on (setArgs), up to the screens, which
    // pass Client.<Name> (SessionInputs). A caller with no session (a timer, a process) passes nothing, so the input's default is used, as
    // the session variable's default was. Returns the edits for 2-source.json and the input of each action, or why it cannot be done.
    internal sealed record SessionThread(List<JsonObject> Edits, Dictionary<string, string> InputOf, string? Why);

    internal static SessionThread ThreadSession(string variable, string type, string? def, IReadOnlyList<string> readers,
        IReadOnlyDictionary<string, string> texts, IReadOnlyList<ActionCall> calls, IReadOnlyList<(string query, string name)> logic)
    {
        var queryOf = logic.ToDictionary(l => l.name, l => l.query, StringComparer.OrdinalIgnoreCase);
        var writes = new Regex(@"(?m)^\s*set Session\." + Regex.Escape(variable) + @"(?![\w])", RegexOptions.IgnoreCase);
        if (readers.FirstOrDefault(a => texts.TryGetValue(a, out var t) && writes.IsMatch(t)) is { } writer)
            return new([], [], $"copied server logic writes it ({writer}), and a value written on the server cannot reach the browser's copy");
        var needs = new HashSet<string>(readers, StringComparer.OrdinalIgnoreCase);
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var c in calls) if (needs.Contains(c.Callee) && !needs.Contains(c.Caller) && queryOf.ContainsKey(c.Caller)) grew |= needs.Add(c.Caller);
        }
        if (needs.FirstOrDefault(a => !queryOf.ContainsKey(a)) is { } unknown) return new([], [], $"{unknown} is not one of the copied actions");
        string Param(string action)
        {
            var text = texts.GetValueOrDefault(action) ?? "";
            var own = new Regex(@"(?<![\w.])" + Regex.Escape(variable) + @"(?!\w)", RegexOptions.IgnoreCase);
            return own.IsMatch(Regex.Replace(text, @"Session\." + Regex.Escape(variable), "", RegexOptions.IgnoreCase)) ? "Session" + variable : variable;
        }
        var inputOf = needs.ToDictionary(a => a, Param, StringComparer.OrdinalIgnoreCase);
        var edits = new List<JsonObject>();
        foreach (var a in needs.Order(StringComparer.Ordinal))
        {
            var input = new JsonObject { ["name"] = inputOf[a], ["type"] = type, ["mandatory"] = false };
            if (!string.IsNullOrEmpty(def)) input["default"] = def;
            edits.Add(new JsonObject { ["op"] = "addVariables", ["action"] = queryOf[a], ["inputs"] = new JsonArray(input) });
        }
        foreach (var a in readers.Order(StringComparer.Ordinal))
            edits.Add(new JsonObject { ["op"] = "rewrite", ["element"] = queryOf[a], ["from"] = "Session." + variable, ["to"] = inputOf[a] });
        foreach (var c in calls.Where(c => needs.Contains(c.Callee) && needs.Contains(c.Caller)))
            edits.Add(new JsonObject { ["op"] = "setArgs", ["element"] = c.Path, ["args"] = new JsonObject { [inputOf[c.Callee]] = inputOf[c.Caller] } });
        return new(edits, inputOf, null);
    }

    // The server store for the session variables that cannot live in the browser (written by server logic, a sensitive name, a record or
    // list) but that copied server logic uses. 2-source.json creates, in the working copy (so the copy brings them along):
    //   - the entity SessionValue (SessionKey, UserId, Name, Value as JSON, ChangedOn), one row per variable, session key and user;
    //   - per variable a structure SessionBox_<X> { Value } (any type serializes the same way) and the server actions
    //     GetSession_<X>(SessionKey) (a function: the stored value, else the variable's default) and SetSession_<X>(SessionKey, Value).
    // Edits: every copied action that uses one of them, and every copied caller of those, gets the input SessionKey and passes it on; a
    // reader's Session.<X> becomes GetSession_<X>(SessionKey); a writer gets a local Session<X>, loaded after Start and saved by
    // SetSession_<X> after each Assign that writes it. Screens pass Client.SessionKey, a key the browser makes once (4-screens.json).
    // A row is read only for the signed-in user (UserId = GetUserId()), so a key alone does not reach another user's values.
    internal sealed record StoreVariable(string Name, string Type, string? Default, string[] Users);
    internal sealed record SessionStorePlan(JsonObject Creation, List<JsonObject> Edits, List<string> Actions, List<string> CopyPaths);

    internal const string SessionKey = "SessionKey";

    internal static SessionStorePlan StoreSessions(IReadOnlyList<StoreVariable> vars, IReadOnlyList<ActionCall> calls, IReadOnlyList<SessionWrite> writes,
        IReadOnlyList<(string query, string name)> logic)
    {
        var queryOf = logic.ToDictionary(l => l.name, l => l.query, StringComparer.OrdinalIgnoreCase);
        var structures = new JsonArray();
        var actions = new JsonArray();
        var copy = new List<string> { "SessionValue" };
        foreach (var v in vars)
        {
            var box = $"SessionBox_{v.Name}";
            structures.Add(new JsonObject { ["name"] = box, ["description"] = $"The value of the former session variable {v.Name}, as stored in SessionValue (oml migrate).",
                ["attributes"] = new JsonArray(new JsonObject { ["name"] = "Value", ["type"] = v.Type }) });
            var filters = new JsonArray($"SessionValue.SessionKey = {SessionKey}", $"SessionValue.Name = \"{v.Name}\"", "SessionValue.UserId = GetUserId()");
            var get = new JsonArray(
                new JsonObject { ["kind"] = "aggregate", ["name"] = "Stored", ["sources"] = new JsonArray("SessionValue"), ["filters"] = filters.DeepClone(), ["maxRecords"] = "1", ["next"] = "Found" },
                new JsonObject { ["kind"] = "if", ["name"] = "Found", ["condition"] = "Stored.List.Empty", ["true"] = string.IsNullOrEmpty(v.Default) ? "End" : "UseDefault", ["false"] = "Parse" },
                new JsonObject { ["kind"] = "jsonDeserialize", ["name"] = "Parse", ["jsonString"] = "Stored.List.Current.SessionValue.Value", ["dataType"] = box, ["next"] = "Take" },
                new JsonObject { ["kind"] = "assign", ["name"] = "Take", ["assign"] = new JsonObject { ["Value"] = "Parse.Data.Value" }, ["next"] = "End" });
            if (!string.IsNullOrEmpty(v.Default)) get.Add(new JsonObject { ["kind"] = "assign", ["name"] = "UseDefault", ["assign"] = new JsonObject { ["Value"] = v.Default }, ["next"] = "End" });
            actions.Add(new JsonObject { ["name"] = $"GetSession_{v.Name}", ["function"] = true,
                ["description"] = $"The value of the former session variable {v.Name} for this session key and user, else its default (oml migrate).",
                ["inputs"] = new JsonArray(new JsonObject { ["name"] = SessionKey, ["type"] = "Text", ["mandatory"] = false }),
                ["outputs"] = new JsonArray(new JsonObject { ["name"] = "Value", ["type"] = v.Type }), ["flow"] = get });
            actions.Add(new JsonObject { ["name"] = $"SetSession_{v.Name}",
                ["description"] = $"Stores the former session variable {v.Name} for this session key and user (oml migrate).",
                ["inputs"] = new JsonArray(new JsonObject { ["name"] = SessionKey, ["type"] = "Text", ["mandatory"] = false }, new JsonObject { ["name"] = "Value", ["type"] = v.Type, ["mandatory"] = false }),
                ["locals"] = new JsonArray(new JsonObject { ["name"] = "Box", ["type"] = box }, new JsonObject { ["name"] = "Row", ["type"] = "SessionValue" }),
                ["flow"] = new JsonArray(
                    new JsonObject { ["kind"] = "assign", ["name"] = "Fill", ["assign"] = new JsonObject { ["Box.Value"] = "Value" }, ["next"] = "ToJson" },
                    new JsonObject { ["kind"] = "jsonSerialize", ["name"] = "ToJson", ["data"] = "Box", ["next"] = "Stored" },
                    new JsonObject { ["kind"] = "aggregate", ["name"] = "Stored", ["sources"] = new JsonArray("SessionValue"), ["filters"] = filters.DeepClone(), ["maxRecords"] = "1", ["next"] = "Prepare" },
                    new JsonObject { ["kind"] = "assign", ["name"] = "Prepare", ["assign"] = new JsonObject { ["Row"] = "Stored.List.Current.SessionValue", ["Row.SessionKey"] = SessionKey,
                        ["Row.Name"] = $"\"{v.Name}\"", ["Row.UserId"] = "GetUserId()", ["Row.Value"] = "ToJson.JSON", ["Row.ChangedOn"] = "CurrDateTime()" }, ["next"] = "Save" },
                    new JsonObject { ["kind"] = "execute", ["name"] = "Save", ["action"] = "CreateOrUpdateSessionValue", ["args"] = new JsonObject { ["Source"] = "Row" }, ["next"] = "End" }) });
            copy.AddRange([box, $"GetSession_{v.Name}", $"SetSession_{v.Name}"]);
        }
        var creation = new JsonObject
        {
            ["entities"] = new JsonArray(new JsonObject
            {
                ["name"] = "SessionValue", ["description"] = "The values of the former session variables, by session key and user (oml migrate). Delete old rows with a timer.",
                ["attributes"] = new JsonArray(
                    new JsonObject { ["name"] = "SessionKey", ["length"] = 50, ["mandatory"] = true },
                    new JsonObject { ["name"] = "UserId", ["type"] = "User Identifier" },
                    new JsonObject { ["name"] = "Name", ["length"] = 100, ["mandatory"] = true },
                    new JsonObject { ["name"] = "Value", ["length"] = 5000 },
                    new JsonObject { ["name"] = "ChangedOn", ["type"] = "Date Time" }),
                ["indexes"] = new JsonArray(new JsonObject { ["attributes"] = new JsonArray("SessionKey", "Name") }),
            }),
            ["structures"] = structures,
            ["serverActions"] = actions,
        };
        // Who needs the key: every copied action that uses a stored variable, and its copied callers, up to the screens.
        var needs = new HashSet<string>(vars.SelectMany(v => v.Users).Where(queryOf.ContainsKey), StringComparer.OrdinalIgnoreCase);
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var c in calls) if (needs.Contains(c.Callee) && !needs.Contains(c.Caller) && queryOf.ContainsKey(c.Caller)) grew |= needs.Add(c.Caller);
        }
        var edits = new List<JsonObject>();
        var writers = writes.Where(w => vars.Any(v => v.Name.Equals(w.Variable, StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var a in needs.Order(StringComparer.Ordinal))
        {
            var edit = new JsonObject { ["op"] = "addVariables", ["action"] = queryOf[a], ["inputs"] = new JsonArray(new JsonObject { ["name"] = SessionKey, ["type"] = "Text", ["mandatory"] = false }) };
            var locals = vars.Where(v => v.Users.Contains(a, StringComparer.OrdinalIgnoreCase))
                .Select(v => (JsonNode?)new JsonObject { ["name"] = "Session" + v.Name, ["type"] = v.Type }).ToArray();
            if (locals.Length > 0) edit["locals"] = new JsonArray(locals);
            edits.Add(edit);
        }
        foreach (var v in vars)
            foreach (var a in v.Users.Where(queryOf.ContainsKey).Order(StringComparer.Ordinal))
            {
                // A local holds the value in the action (an expression cannot read a member of a function's result): loaded after Start, saved
                // after each Assign that writes it.
                var writes1 = writers.Where(w => w.Action.Equals(a, StringComparison.OrdinalIgnoreCase) && w.Variable.Equals(v.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                var local = "Session" + v.Name;
                edits.Add(new JsonObject { ["op"] = "rewrite", ["element"] = queryOf[a], ["from"] = "Session." + v.Name, ["to"] = local });
                edits.Add(new JsonObject { ["op"] = "insertNodes", ["action"] = queryOf[a], ["after"] = "Start", ["connector"] = "next",
                    ["flow"] = new JsonArray(new JsonObject { ["kind"] = "assign", ["name"] = "Load" + local, ["assign"] = new JsonObject { [local] = $"GetSession_{v.Name}({SessionKey})" } }) });
                for (var i = 0; i < writes1.Count; i++)
                    edits.Add(new JsonObject { ["op"] = "insertNodes", ["action"] = queryOf[a], ["after"] = writes1[i].Path[(writes1[i].Path.LastIndexOf('/') + 1)..], ["connector"] = "next",
                        ["flow"] = new JsonArray(new JsonObject { ["kind"] = "execute", ["name"] = "Save" + local + (i > 0 ? (i + 1).ToString() : ""), ["action"] = $"SetSession_{v.Name}",
                            ["args"] = new JsonObject { [SessionKey] = SessionKey, ["Value"] = local } }) });
            }
        foreach (var c in calls.Where(c => needs.Contains(c.Callee) && needs.Contains(c.Caller)))
            edits.Add(new JsonObject { ["op"] = "setArgs", ["element"] = c.Path, ["args"] = new JsonObject { [SessionKey] = SessionKey } });
        return new(creation, edits, needs.Order(StringComparer.Ordinal).ToList(), copy);
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
        if (Has("Email")) return ("translate", "email: exported and translated like a screen when its Preparation loads no data (it lands in 4-screens.md)");
        if (Has("Entry")) return ("manual", "entry point: Reactive has no entry points; the URL of a screen or a redirect screen replaces it");
        if (Has("OnException")) return ("translate", "a UI flow's exception handler: translated into onException of 4-screens.json, unless the template's flow of that name has one (a note then says what the old one did)");
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
        if (ctx.NotifyingBlocks is null) ctx = ctx with { NotifyingBlocks = ScreenTranslator.NotifyingBlocks(exports.Values) };
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

    // The blocks the old module uses from a Traditional producer that was migrated earlier: "Old.Block" -> the public block of the same name in
    // the Reactive module it became (its inputs and events), and the references the new module needs for them. The Reactive module is the one
    // --migrated names, else the one Reactive module under --in whose public blocks include every block used (several: a note asks for
    // --migrated). blocksOf(file) is `oml inspect <file> --only blocks`.
    internal static (Dictionary<string, ScreenTranslator.ProducerBlock> map, List<JsonObject> refs) MigratedProducerBlocks(JsonNode inspect, IReadOnlyDictionary<string, string> reactive,
        IReadOnlyDictionary<string, string> migrated, Func<string, JsonNode?> blocksOf, List<string> notes)
    {
        var map = new Dictionary<string, ScreenTranslator.ProducerBlock>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<JsonObject>();
        var cache = new Dictionary<string, Dictionary<string, JsonObject>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, JsonObject> Public(string module)
        {
            if (!cache.TryGetValue(module, out var b))
                cache[module] = b = Rows(blocksOf(reactive[module]), "blocks").Where(x => B(x, "public") == true && S(x, "name") is not null)
                    .GroupBy(x => S(x, "name")!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            return b;
        }
        static HashSet<string> Names(JsonNode? n) => (n as JsonArray ?? []).Select(x => x?.ToString()).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rf in Rows(inspect, "references"))
        {
            if (S(rf, "module") is not { } module || reactive.ContainsKey(module) || UiLibraryMap.IsLibrary(module + ".x")) continue;   // a UI library keeps its map
            var used = Rows(rf, "elements").Where(e => (S(e, "kind") ?? "").Contains("Block", StringComparison.OrdinalIgnoreCase)).Select(e => S(e, "name")).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
            if (used.Count == 0) continue;
            var to = migrated.GetValueOrDefault(module);
            var how = "--migrated";
            if (to is null)
            {
                var fits = reactive.Keys.Where(k => used.All(Public(k).ContainsKey)).Order(StringComparer.Ordinal).ToList();
                if (fits.Count > 1) notes.Add($"The blocks of {module} ({string.Join(", ", used)}) are public blocks of several Reactive modules under --in ({string.Join(", ", fits)}): plan again with --migrated {module}=<the module it was migrated to>.");
                if (fits.Count != 1) continue;
                to = fits[0]; how = "found by its block names";
            }
            var have = Public(to);
            var found = used.Where(have.ContainsKey).ToList();
            var missing = used.Except(found, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var b in found) map[$"{module}.{b}"] = new ScreenTranslator.ProducerBlock($"{to}.{S(have[b], "name")}", Names(have[b]["inputs"]), Names(have[b]["events"]));
            if (found.Count > 0) refs.Add(new JsonObject { ["producer"] = Path.GetFullPath(reactive[to]), ["elements"] = Arr(found.Select(b => S(have[b], "name")!)) });
            notes.Add((found.Count > 0 ? $"The blocks {string.Join(", ", found)} of {module} come from {to}, the Reactive module it was migrated to ({how}): 3-new-module.json references them, so {to} must be published before the new module." : "")
                + (missing.Count > 0 ? $"{(found.Count > 0 ? " " : "")}{to} has no public block {string.Join(", ", missing)} (used from {module}): those instances stay Todos." : ""));
        }
        return (map, refs);
    }

    // The old module's layout blocks and the blocks only they hold (Layout_Normal, its Menu, Footer, LoginInfo): the template's layout replaces
    // them in every translated screen, so a block that the old screens reached and that no translated screen or kept block reaches any more is
    // left out (with a note), instead of being built and failing on Traditional-only parts. A block no old screen used (perhaps public for
    // other modules) is kept.
    internal static List<Item> DropReplacedLayouts(Result r, List<Item> items, IReadOnlyDictionary<string, JsonObject> exports, string? layout = null)
    {
        var blocks = r.Screens.Where(s => s.Kind == "Block").Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        static IEnumerable<string> Uses(JsonNode? n)
        {
            if (n is JsonObject o)
            {
                if (o["sourceBlock"] is JsonValue v && v.ToString() is { } sb) yield return sb[(sb.LastIndexOf('.') + 1)..];
                foreach (var (_, c) in o) foreach (var u in Uses(c)) yield return u;
            }
            else if (n is JsonArray a) foreach (var c in a) foreach (var u in Uses(c)) yield return u;
        }
        HashSet<string> Reach(IEnumerable<JsonNode?> roots, Func<string, JsonNode?> body)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var todo = new Stack<string>(roots.SelectMany(Uses).Where(blocks.Contains));
            while (todo.Count > 0) { var b = todo.Pop(); if (!seen.Add(b)) continue; foreach (var u in Uses(body(b)).Where(blocks.Contains)) todo.Push(u); }
            return seen;
        }
        JsonNode? Export(Screen s) => exports.TryGetValue(s.File, out var e) ? e : null;
        var oldReached = Reach(r.Screens.Where(s => s.Kind == "Screen").Select(Export), b => r.Screens.FirstOrDefault(s => s.Kind == "Block" && s.Name.Equals(b, StringComparison.OrdinalIgnoreCase)) is { } bs ? Export(bs) : null);
        var newReached = Reach(items.Where(i => i.S.Kind == "Screen").Select(i => (JsonNode?)i.Spec), b => items.FirstOrDefault(i => i.S.Kind == "Block" && i.S.Name.Equals(b, StringComparison.OrdinalIgnoreCase))?.Spec);
        var dropped = items.Where(i => i.S.Kind == "Block" && (oldReached.Contains(i.S.Name) && !newReached.Contains(i.S.Name) || i.S.Name.Equals(layout, StringComparison.OrdinalIgnoreCase))).ToList();   // the template's own layout name too: building it would replace the template's layout
        if (dropped.Count == 0) return items;
        r.Notes.Add($"Blocks that the translated screens no longer use are not built (the template's layout or a native replacement such as OutSystems UI Pagination took their place): {string.Join(", ", dropped.Select(d => d.S.Name).Order(StringComparer.Ordinal))}. Rebuild one in the new module only if you still need it.");
        return items.Except(dropped).ToList();
    }

    // Names the copy brings that the Reactive template has already: the paste would rename them and refuse the step. A site property of the
    // same name (TenantId, TenantName of a template) is the template's own: it is left out of the copy. Any other clash is a note that comes
    // first in `next`, because only a person can say which one stays.
    internal static void TemplateClashes(Result r, JsonNode template)
    {
        if (r.NewModule["copy"] is not JsonArray { Count: > 0 } copies || copies[0] is not JsonObject copy || copy["elements"] is not JsonArray elements) return;
        static HashSet<string> Own(JsonNode t, params string[] keys) =>
            keys.SelectMany(k => (t[k] as JsonArray ?? []).OfType<JsonObject>().Select(e => S(e, "name")).Where(n => n is not null)).ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        var siteProperties = Own(template, "siteProperties");
        var others = Own(template, "entities", "structures", "serverActions", "serviceActions", "clientActions", "exceptions", "timers", "processes", "staticEntities");
        var dropped = new List<string>(); var clashes = new List<string>();
        foreach (var e in elements.ToList())
        {
            var path = e?.ToString() ?? "";
            var leaf = path[(path.LastIndexOf(':') + 1)..];
            if (siteProperties.Contains(leaf)) { elements.Remove(e); dropped.Add(leaf); }
            else if (others.Contains(leaf)) clashes.Add(leaf);
        }
        if (dropped.Count > 0) r.Notes.Add($"The template has the site properties {string.Join(", ", dropped)} already, so they are not copied: the new module uses the template's (set their values in Service Center).");
        if (clashes.Count > 0) r.Notes.Insert(0, $"The template already has {string.Join(", ", clashes)}: the copy in 3-new-module.json would rename them and refuse. Before that step, rename them in the old module or delete them from the new module, as the person decides.");
    }

    // The old module's static entities with their records, as inspect gives them (identifier, attribute -> value expression).
    internal static Dictionary<string, IReadOnlyList<(string Id, IReadOnlyDictionary<string, string> Values)>> StaticRecords(JsonNode? inspect) =>
        (inspect?["staticEntities"] as JsonArray ?? []).OfType<JsonObject>().Where(e => S(e, "name") is not null)
            .ToDictionary(e => S(e, "name")!, e => (IReadOnlyList<(string, IReadOnlyDictionary<string, string>)>)(e["records"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(rec => S(rec, "identifier") is not null)
                .Select(rec => (S(rec, "identifier")!, (IReadOnlyDictionary<string, string>)(rec["values"] as JsonObject ?? []).Where(kv => kv.Value is JsonValue)
                    .ToDictionary(kv => kv.Key, kv => kv.Value!.ToString(), StringComparer.OrdinalIgnoreCase))).ToList(), StringComparer.OrdinalIgnoreCase);

    // The server actions the translated screens call and their outputs: the old module's server and service actions (copied, same name) and the
    // entity wrappers (in the old module: Create and CreateOrUpdate return the new Id).
    internal static Dictionary<string, IReadOnlyDictionary<string, string>> ServerOutputs(JsonNode? inspect, Result r)
    {
        var map = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "serverActions", "serviceActions" })
            foreach (var a in (inspect?[key] as JsonArray ?? []).OfType<JsonObject>())
                if (S(a, "name") is { } n && !r.WrapperMap.ContainsValue(n))
                    map[n] = (a["outputs"] as JsonArray ?? []).OfType<JsonObject>().Where(o => S(o, "name") is not null)
                        .ToDictionary(o => S(o, "name")!, o => S(o, "type") ?? "Text", StringComparer.OrdinalIgnoreCase);
        foreach (var (entityAction, wrapper) in r.WrapperMap)
        {
            var entity = r.EntityNames.OrderByDescending(e => e.Length).FirstOrDefault(e => entityAction.EndsWith(e, StringComparison.Ordinal) && entityAction.Length > e.Length);
            var returnsId = entityAction.StartsWith("Create", StringComparison.Ordinal);
            map[$"{r.OldName}.{wrapper}"] = returnsId && entity is not null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Id"] = entity + " Identifier" } : new Dictionary<string, string>();
        }
        return map;
    }

    // What an OnException action did, in a few words per handler ("AllExceptions: navigate to InternalError").
    internal static string HandlerSummary(JsonObject export)
    {
        var nodes = ((export["flow"] as JsonObject)?["nodes"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var byName = nodes.Where(n => S(n, "name") is not null).GroupBy(n => S(n, "name")!).ToDictionary(g => g.Key, g => g.First());
        var parts = new List<string>();
        foreach (var h in nodes.Where(n => S(n, "kind") == "exceptionHandler"))
        {
            var steps = new List<string>();
            for (var (next, guard) = (S(h, "next"), 0); next is not null && next != "End" && byName.TryGetValue(next, out var n) && guard < 12; next = S(n, "next"), guard++)
                steps.Add(S(n, "kind") switch
                {
                    "destination" => "navigate to " + S(n, "screen"),
                    "execute" => "call " + (S(n, "action") ?? S(n, "_ref")),
                    "raiseException" => "raise " + S(n, "exception"),
                    var k => k ?? "node",
                });
            parts.Add($"{S(h, "exception")}: {(steps.Count > 0 ? string.Join(", ", steps) : "nothing")}");
        }
        return parts.Count > 0 ? string.Join("; ", parts) : "no exception handler";
    }

    // The old module's UI flow OnException actions (inspect uiFlows), translated like screen actions into entries of the spec's onException.
    // A flow of the same name that already has one in the template keeps the template's (Reactive templates handle sign-in and errors there,
    // and apply would reuse a same-named one without the old nodes): a note says what the old one did. One that cannot be translated, or
    // that navigates to a screen the new module will not have, is a note too: a wrong handler is worse than the module's global one.
    internal static List<JsonObject> OnExceptionHandlers(List<string> notes, JsonNode? inspect, JsonNode? template, List<Item> items,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, JsonObject>> export, ScreenTranslator.Context context)
    {
        var result = new List<JsonObject>();
        var old = Rows(inspect, "uiFlows").Where(f => S(f, "name") is not null && S(f, "onException") is not null).Select(f => (flow: S(f, "name")!, name: S(f, "onException")!)).ToList();
        if (old.Count == 0) return result;
        var taken = Rows(template, "uiFlows").Where(f => S(f, "name") is not null && S(f, "onException") is not null)
            .GroupBy(f => S(f, "name")!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => S(g.First(), "onException")!, StringComparer.OrdinalIgnoreCase);
        var screens = items.Where(i => i.S.Kind == "Screen" && i.Spec is not null).Select(i => S(i.Spec!, "name")).Concat(Rows(template, "screens").Select(x => S(x, "name")))
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exported = export(old.Select(h => $"{h.flow}/{h.name}").ToList());
        foreach (var (flow, name) in old)
        {
            var e = exported.GetValueOrDefault($"{flow}/{name}") ?? new JsonObject { ["error"] = "no result from oml export" };
            var did = S(e, "error") is { } err ? $"(oml export could not read it: {err})" : HandlerSummary(e);
            if (taken.TryGetValue(flow, out var theirs))
            { notes.Add($"The template's {flow}/{theirs} stays the exception handler of {flow}. The old {flow}/{name} did: {did}. If it did more than the template's (logging, a special screen), add that to {theirs} by hand."); continue; }
            var t = ScreenTranslator.TranslateOnException(e, context);
            if (t.Spec is null)
            { notes.Add($"The old {flow}/{name} was not translated ({string.Join(" ", t.Todo).TrimEnd('.')}); it did: {did}. Build the OnException of {flow} by hand."); continue; }
            var missing = ((t.Spec["nodes"] as JsonArray) ?? []).OfType<JsonObject>().Where(n => S(n, "kind") == "destination").Select(n => S(n, "screen")).OfType<string>()
                .Where(sc => !screens.Contains(sc)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0)
            { notes.Add($"The old {flow}/{name} navigates to {string.Join(", ", missing)}, which the new module will not have, so it is not added: {did}. Add the screen, or point the handler at one the new module has."); continue; }
            notes.AddRange(t.Todo);
            result.Add(t.Spec);
        }
        return result;
    }

    // The old module's server functions (inspect marks them function: true).
    internal static HashSet<string> ServerFunctionNames(JsonNode? inspect) =>
        Rows(inspect, "serverActions").Where(a => a["function"] is JsonValue f && f.GetValueKind() == System.Text.Json.JsonValueKind.True).Select(a => S(a, "name")).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    // The answer of `oml export <old> ServerAction:A ServerAction:B ...` by name (one name gives an object, several an array); a missing one is an error.
    internal static Dictionary<string, JsonObject> ExportActions(JsonNode? output, IReadOnlyList<string> names)
    {
        var entries = output is JsonArray arr && arr.Count == names.Count ? arr.ToArray() : names.Count == 1 && output is JsonObject ? [output] : null;
        var map = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++) map[names[i]] = entries?[i] as JsonObject ?? new JsonObject { ["error"] = "no result from oml export" };
        return map;
    }

    static readonly HashSet<string> ClientFunctionKinds = new(StringComparer.OrdinalIgnoreCase) { "assign", "if", "switch", "foreach", "comment", "execute" };
    static readonly string[] FlowNameKeys = ["kind", "name", "next", "true", "false", "cycle", "otherwise", "action", "_path", "_ref", "type"];

    // Every expression text of an exported node (assignment targets too), not its names and connectors.
    static IEnumerable<string> NodeTexts(JsonNode? n, string? key = null)
    {
        switch (n)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                {
                    if (FlowNameKeys.Contains(k)) continue;
                    if (k == "assign" && v is JsonObject map) foreach (var (target, _) in map) yield return target;
                    foreach (var t in NodeTexts(v, k)) yield return t;
                }
                break;
            case JsonArray a: foreach (var x in a) foreach (var t in NodeTexts(x, key)) yield return t; break;
            case JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String: yield return v.ToString(); break;
        }
    }

    static readonly System.Text.RegularExpressions.Regex FunctionCall = new(@"(?<![\w.])([A-Za-z_]\w*)\s*\(", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    static readonly System.Text.RegularExpressions.Regex ServerOnly = new(@"(?<![\w.])(Site\.\w+|Session\.\w+|Check\w+Role\s*\()", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // Server functions that screens call in the browser. A Reactive screen runs there and cannot call a server function, so each one becomes
    // a client function of the same name, as Service Studio's converter does, when its flow can run in the browser: Assign, If, Switch,
    // For Each, comments and calls of other functions copied the same way, and no Site property, session variable or role check. (The
    // converter puts a mock body in the others; a mock returns wrong values without a word, so here they are a Todo instead.) The functions
    // they call are exported too. Returns the copies, and why for each function that is not copied.
    internal static (List<JsonObject> Copies, Dictionary<string, string> Refused) ClientFunctions(IEnumerable<string> wanted,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, JsonObject>> export, IReadOnlySet<string> functions)
    {
        var exported = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var callees = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var queue = wanted.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        for (var round = 0; queue.Count > 0 && round < 20; round++)
        {
            var got = export(queue);
            queue = [];
            foreach (var (fn, e) in got)
            {
                exported[fn] = e;
                var nodes = (e["flow"] as JsonObject)?["nodes"] as JsonArray ?? [];
                var called = nodes.OfType<JsonObject>().Where(n => S(n, "kind") == "execute").Select(n => S(n, "action")).OfType<string>()
                    .Concat(NodeTexts(nodes).SelectMany(t => FunctionCall.Matches(t).Select(m => m.Groups[1].Value)))
                    .Where(functions.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Where(c => !c.Equals(fn, StringComparison.OrdinalIgnoreCase)).ToList();
                callees[fn] = called;
                foreach (var c in called) if (!exported.ContainsKey(c) && !got.ContainsKey(c) && !queue.Contains(c, StringComparer.OrdinalIgnoreCase)) queue.Add(c);
            }
        }
        var refused = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fn, e) in exported)
        {
            if (S(e, "error") is { } err) { refused[fn] = $"oml export could not read it: {err}"; continue; }
            var flow = e["flow"] as JsonObject;
            if (flow?["complete"] is not JsonValue c || c.GetValueKind() != System.Text.Json.JsonValueKind.True)
            { refused[fn] = "its flow could not be read completely" + ((flow?["unsupported"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() is { } u ? $" ({S(u, "why")})" : ""); continue; }
            var nodes = (flow["nodes"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            if (nodes.FirstOrDefault(n => !ClientFunctionKinds.Contains(S(n, "kind") ?? "")) is { } bad) { refused[fn] = $"its {S(bad, "kind")} node {S(bad, "name")} runs on the server"; continue; }
            if (nodes.FirstOrDefault(n => S(n, "kind") == "execute" && !functions.Contains(S(n, "action") ?? "")) is { } call) { refused[fn] = $"it calls the server action {S(call, "action") ?? S(call, "_ref")}"; continue; }
            if (NodeTexts(new JsonArray(nodes.Select(n => (JsonNode?)n.DeepClone()).ToArray())).Select(t => ServerOnly.Match(t)).FirstOrDefault(m => m.Success) is { } so)
            { refused[fn] = $"it reads {so.Value.TrimEnd('(', ' ')}, which only the server has"; continue; }
        }
        // A function that calls one that is not copied is not copied either.
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (fn, called) in callees)
                if (!refused.ContainsKey(fn) && called.FirstOrDefault(refused.ContainsKey) is { } no) { refused[fn] = $"it calls {no}, which is not copied ({refused[no]})"; changed = true; }
        }
        var copies = new List<JsonObject>();
        foreach (var (fn, e) in exported.Where(kv => !refused.ContainsKey(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var o = new JsonObject { ["name"] = S(e, "name") ?? fn, ["function"] = true,
                ["description"] = $"The server function {S(e, "name") ?? fn}, copied for the browser: a Reactive screen cannot call a server function (oml migrate)." };
            foreach (var key in new[] { "inputs", "outputs", "locals" }) if (e[key] is JsonArray { Count: > 0 } vars) o[key] = vars.DeepClone();
            var flow = new JsonArray();
            foreach (var n in ((e["flow"] as JsonObject)!["nodes"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var copy = (JsonObject)n.DeepClone();
                copy.Remove("_path"); copy.Remove("_ref");
                flow.Add(copy);
            }
            o["flow"] = flow;
            copies.Add(o);
        }
        return (copies, refused);
    }

    // 4-screens.json: client variables, then emails, blocks and screens (the non-null specs), applied to the new module after 3-new-module.json.
    internal static JsonObject? ScreensSpec(Result r, IEnumerable<Item> items)
    {
        var spec = new JsonObject();
        if (r.ClientVariables.Count > 0) spec["clientVariables"] = r.ClientVariables.DeepClone();
        var scripts = new JsonArray();
        var merged = new JsonArray();
        var clientActions = new JsonArray();
        foreach (var (kind, key) in new[] { ("Email", "emails"), ("Block", "blocks"), ("Screen", "screens") })
        {
            var list = items.Where(i => i.S.Kind == kind && i.Spec is not null).Select(i => (JsonObject)i.Spec!.DeepClone()).ToArray();
            // A screen's or block's own JavaScript (the translator leaves it in _javaScript): a script of the new module, required by the element.
            foreach (var e in list)   // server actions the translator made of consecutive server calls: top-level serverActions
                if (e["_serverActions"] is JsonArray sas) { e.Remove("_serverActions"); foreach (var x in sas.ToList()) { sas.Remove(x); merged.Add(x); } }
            foreach (var e in list)   // client functions the translator made (static entity reads): once per name
                if (e["_clientActions"] is JsonArray cas)
                {
                    e.Remove("_clientActions");
                    foreach (var x in cas.ToList()) { cas.Remove(x); if (!clientActions.OfType<JsonObject>().Any(c => S(c, "name") == S(x, "name"))) clientActions.Add(x); }
                }
            foreach (var e in list)
                if (e["_javaScript"] is JsonValue js && S(e, "name") is { } en)
                {
                    e.Remove("_javaScript");
                    var script = $"{en}Script";
                    scripts.Add(new JsonObject { ["name"] = script, ["file"] = $"scripts/{script}.js", ["_text"] = js.ToString() });
                    e["requiredScripts"] = new JsonArray(script);
                }
            if (list.Length > 0) spec[key] = new JsonArray(list.Select(x => (JsonNode?)x).ToArray());
        }
        if (scripts.Count > 0) spec["scripts"] = scripts;
        if (merged.Count > 0) spec["serverActions"] = merged;
        // The session key of the server store: made once per browser when the app starts (a random UUID), kept in the client variable SessionKey.
        if (r.Store is not null)
        {
            clientActions.Add(new JsonObject
            {
                ["name"] = "InitSessionKey", ["description"] = "Makes the key of the server-side session store once per browser (oml migrate).",
                ["flow"] = new JsonArray(
                    new JsonObject { ["kind"] = "if", ["name"] = "HasKey", ["condition"] = $"Client.{SessionKey} <> \"\"", ["true"] = "End", ["false"] = "NewKey" },
                    new JsonObject { ["kind"] = "javaScript", ["name"] = "NewKey", ["outputs"] = new JsonArray(new JsonObject { ["name"] = "Key", ["type"] = "Text" }),
                        ["javaScript"] = "$parameters.Key = (window.crypto && crypto.randomUUID) ? crypto.randomUUID() : Date.now().toString(36) + Math.random().toString(36).slice(2) + Math.random().toString(36).slice(2);",
                        ["next"] = "Keep" },
                    new JsonObject { ["kind"] = "assign", ["name"] = "Keep", ["assign"] = new JsonObject { [$"Client.{SessionKey}"] = "NewKey.Key" }, ["next"] = "End" }),
            });
            spec["systemEvents"] = new JsonObject { ["onApplicationReady"] = "InitSessionKey" };
        }
        if (clientActions.Count > 0) spec["clientActions"] = clientActions;
        if (merged.ToJsonString().Contains($"\"{ScreenTranslator.AccessDenied}\"", StringComparison.Ordinal))   // raised by the role checks of merged server actions
            spec["exceptions"] = new JsonArray(ScreenTranslator.AccessDenied);
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
        string? template, Func<string[], string?, JsonNode> run, Func<JsonObject, ScreenTranslator.Context, ScreenTranslator.Result>? translate = null, bool copyExposed = false,
        bool serverStore = true, bool mergeServerCalls = true, IReadOnlyDictionary<string, string>? reactive = null, IReadOnlyDictionary<string, string>? migrated = null)
    {
        translate ??= ScreenTranslator.Translate;
        var r = Build(inspect, old, name, outDir, map, [], Extra.None with { CopyExposed = copyExposed });
        var notes = new List<string>();
        JsonNode? Try(string[] args, string? stdin, string what)
        { try { return run(args, stdin); } catch (Refusal e) { notes.Add($"{what} could not be read ({e.Message}); {(what.Contains("describe") ? "check by hand" : "plan again once it works")}."); return null; } }

        // 1. entity actions in the copied logic
        var calls = new List<Call>();
        var actionCalls = new List<ActionCall>();
        var sessionWrites = new List<SessionWrite>();
        if (r.Logic.Count > 0)
        {
            var steps = new JsonObject { ["steps"] = new JsonArray(r.Logic.Select(l => (JsonNode?)Arr(["tree", old, l.query])).ToArray()) };
            var trees = run(["batch", "-", "--continue"], steps.ToJsonString());
            calls = FindCalls(trees, r.Logic, r.EntityNames, notes);
            actionCalls = FindActionCalls(trees, r.Logic);
            sessionWrites = FindSessionWrites(trees, r.Logic);
        }
        // 2. which copied server and service actions use Session.<Name>
        var serverUse = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var serverLogic = r.Logic.Where(l => l.query.StartsWith("ServerAction:") || l.query.StartsWith("ServiceAction:")).ToList();
        var serverTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // copied action name -> its describe text
        if (r.Sessions.Length > 0 && serverLogic.Count > 0)
        {
            var described = Describe(run, old, serverLogic, notes, "--depth", "0");
            serverUse = SessionUse(described, r.Sessions);
            foreach (var (query, text) in described) serverTexts[query[(query.IndexOf(':') + 1)..]] = text;
        }
        // 3. export every screen, block and email
        var chunks = ExportChunks(r.Screens);
        JsonNode? exportBatch = null;
        if (chunks.Count > 0)
            exportBatch = run(["batch", "-", "--continue"], new JsonObject { ["steps"] = new JsonArray(chunks.Select(c => (JsonNode?)Arr(new[] { "export", old }.Concat(c.Select(s => s.Query)))).ToArray()) }.ToJsonString());
        var exports = ParseExports(exportBatch, chunks);
        // 4. the layout of a Reactive template
        string? layout = null; var placeholders = new List<string>();
        JsonNode? ti = null;
        if (template is not null)
        {
            ti = run(["inspect", template], null);
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
        r = Build(inspect, old, name, outDir, map, calls, ExtraFrom(first, r.EntityNames, serverUse) with { CopyExposed = copyExposed, ServerTexts = serverTexts, ActionCalls = actionCalls, SessionWrites = sessionWrites, ServerStore = serverStore });
        r.Notes.AddRange(producerNotes); r.Notes.AddRange(notes);
        if (ti is not null) TemplateClashes(r, ti);
        // Blocks of Traditional producers migrated earlier: their instances use the same block of the Reactive module, which the new module references.
        var (producerBlocks, producerRefs) = MigratedProducerBlocks(inspect, reactive ?? new Dictionary<string, string>(), migrated ?? new Dictionary<string, string>(),
            file => Try(["inspect", file, "--only", "blocks"], null, $"oml inspect of {Path.GetFileName(file)}"), r.Notes);
        if (producerRefs.Count > 0)
        {
            var refs = r.NewModule["references"] as JsonArray ?? new JsonArray();
            foreach (var x in producerRefs) refs.Add(x);
            r.NewModule["references"] = refs;
        }
        var context = MakeContext(r, layout, placeholders) with { ProducerBlocks = producerBlocks.Count > 0 ? producerBlocks : null, ServerOutputs = mergeServerCalls ? ServerOutputs(inspect, r) : null, SessionInputs = r.SessionInputs,
            StaticRecords = StaticRecords(inspect),
            SiteProperties = Rows(inspect, "siteProperties").Where(p => S(p, "name") is not null).GroupBy(p => S(p, "name")!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => S(g.First(), "type") ?? "Text", StringComparer.OrdinalIgnoreCase),
            Roles = Rows(inspect, "roles").Select(p => S(p, "name")).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase),
            ServerFunctions = ServerFunctionNames(inspect),
            StoreVariables = r.SessionDecisions.Where(d => d.Decision == "store").ToDictionary(d => d.Name, d => d.Type, StringComparer.OrdinalIgnoreCase),
            ClientTypes = r.ClientVariables.OfType<JsonObject>().Where(c => S(c, "name") is not null).ToDictionary(c => S(c, "name")!, c => S(c, "type") ?? "Text", StringComparer.OrdinalIgnoreCase) };
        var items = TranslateAll(r.Screens, exports, context, translate);
        foreach (var i in items.Where(i => i.Error is not null && i.Error != "no export")) r.Notes.Add($"{i.S.Kind} {i.S.Name} was not translated: {i.Error}.");
        foreach (var a in items.SelectMany(i => i.EntityActions).Distinct().Where(a => EntityActionMap(r.EntityNames).ContainsKey(a) && !r.WrapperMap.ContainsKey(a)))
            r.Notes.Add($"A translation reported the entity action {a} without a wrapper for it: add the wrapper by hand.");
        items = DropReplacedLayouts(r, items, exports, layout);
        // Server functions the screens call in the browser: copied as client functions of the same name, or a Todo on each screen that calls one.
        // The UI flows' OnException actions: the spec's onException, one per flow (OnExceptionHandlers).
        var handlers = OnExceptionHandlers(r.Notes, inspect, ti, items, paths => ExportActions(Try(["export", old, .. paths], null, "oml export of the UI flows' OnException actions"), paths), context);
        var fnCopies = new List<JsonObject>();
        var usedBy = items.Where(i => i.Spec?["_serverFunctions"] is JsonArray).SelectMany(i => ((JsonArray)i.Spec!["_serverFunctions"]!).Select(x => (fn: x!.ToString(), item: (Item?)i, flow: (string?)null)))
            .Concat(handlers.Where(h => h["_serverFunctions"] is JsonArray).SelectMany(h => ((JsonArray)h["_serverFunctions"]!).Select(x => (fn: x!.ToString(), item: (Item?)null, flow: S(h, "flow"))))).ToList();
        foreach (var i in items) i.Spec?.Remove("_serverFunctions");
        foreach (var h in handlers) h.Remove("_serverFunctions");
        if (usedBy.Count > 0)
        {
            var (copies, refused) = ClientFunctions(usedBy.Select(u => u.fn), names => ExportActions(Try(["export", old, .. names.Select(n => "ServerAction:" + n)], null, "oml export of the server functions"), names),
                ServerFunctionNames(inspect));
            fnCopies = copies;
            foreach (var (fn, item, flow) in usedBy.Where(u => refused.ContainsKey(u.fn)))
            {
                var why = $"it calls the server function {fn} in the browser, which Reactive cannot, and {fn} was not copied as a client function ({refused[fn]}); return the value from a data action or server action, or write {fn} as a client function";
                if (item is null) { r.Notes.Add($"The OnException of the UI flow {flow}: {why}."); continue; }
                item.Todo.Add(why);
                var at = items.IndexOf(item);
                if (at >= 0 && items[at].Status == "translated") items[at] = items[at] with { Status = "partial" };
            }
            r.Notes.AddRange(notes.Where(n => !r.Notes.Contains(n)));
        }
        // The images the translated widgets show: copied with the logic (3-new-module.json), so 4-screens.json finds them.
        var shown = items.Where(i => i.Spec?["_images"] is JsonArray).SelectMany(i => ((JsonArray)i.Spec!["_images"]!).Select(x => x!.ToString())).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        foreach (var i in items) i.Spec?.Remove("_images");
        if (shown.Count > 0)
        {
            if (r.NewModule["copy"] is not JsonArray { Count: > 0 } cps)
                r.NewModule["copy"] = cps = new JsonArray(new JsonObject { ["from"] = r.Source is null ? r.Prepared : r.MigrateSource, ["elements"] = new JsonArray() });
            var els = (JsonArray)((JsonObject)cps[0]!)["elements"]!;
            foreach (var img in shown) if (!els.Any(e => e?.ToString() == "Image:" + img)) els.Add("Image:" + img);
        }
        r.ScreensSpec = ScreensSpec(r, items);
        if (handlers.Count > 0)
        {
            r.ScreensSpec ??= new JsonObject();
            var merged = r.ScreensSpec["serverActions"] as JsonArray ?? new JsonArray();
            foreach (var h in handlers)
                if (h["_serverActions"] is JsonArray sas) { h.Remove("_serverActions"); foreach (var x in sas.ToList()) { sas.Remove(x); merged.Add(x); } }
            if (merged.Count > 0) r.ScreensSpec["serverActions"] = merged;
            r.ScreensSpec["onException"] = handlers.Count == 1 ? handlers[0] : new JsonArray(handlers.Select(h => (JsonNode?)h).ToArray());
        }
        if (fnCopies.Count > 0 && r.ScreensSpec is not null)
        {
            var cas = r.ScreensSpec["clientActions"] as JsonArray ?? new JsonArray();
            foreach (var c in fnCopies) if (!cas.OfType<JsonObject>().Any(x => string.Equals(S(x, "name"), S(c, "name"), StringComparison.OrdinalIgnoreCase))) cas.Add(c);
            r.ScreensSpec["clientActions"] = cas;
        }
        // Reactive library blocks the translation uses (from the Traditional UI library map): the new module must reference them.
        if (r.ScreensSpec is not null)
        {
            var libs = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            void Find(JsonNode? n)
            {
                if (n is JsonObject o)
                {
                    if (o["sourceBlock"] is JsonValue v && v.ToString() is var sb && sb.IndexOf('.') is > 0 and var dot && sb[..dot] is "OutSystemsUI" or "OutSystemsCharts")
                        (libs.TryGetValue(sb[..dot], out var set) ? set : libs[sb[..dot]] = new SortedSet<string>(StringComparer.Ordinal)).Add(sb[(dot + 1)..]);
                    foreach (var (_, c) in o) Find(c);
                }
                else if (n is JsonArray a) foreach (var c in a) Find(c);
            }
            Find(r.ScreensSpec);
            foreach (var (lib, blocks) in libs) r.Unresolved.Add($"{lib} ({string.Join(", ", blocks)})");
        }

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
        if (r.ScreensSpec?["scripts"] is JsonArray scriptList)   // the script files next to 4-screens.json, their text out of the spec
            foreach (var sc in scriptList.OfType<JsonObject>())
                if (sc["_text"] is JsonValue t) { files.Add((S(sc, "file")!, t.ToString())); sc.Remove("_text"); }
        if (r.ScreensSpec is not null) files.Add(("4-screens.json", Json(r.ScreensSpec)));
        files.Add(("4-screens.md", ScreensMd(r, items, r.ScreensSpec is not null)));
        files.AddRange(css);
        // The Traditional UI library elements the old module uses, and what each becomes (UiLibraryMap).
        static string KindOf(string k) => k.Replace("Signature", "").TrimStart('I') switch
        { "WebBlock" => "block", "ServerAction" => "serverAction", "StaticEntity" => "staticEntity", "Structure" => "structure", "Image" => "image", "WebTheme" => "theme", var x => x };
        var referenced = Rows(inspect, "references").Where(rf => S(rf, "module") is { } m && UiLibraryMap.Libraries.Contains(m, StringComparer.OrdinalIgnoreCase))
            .SelectMany(rf => Rows(rf, "elements").Select(e => (module: S(rf, "module")!, kind: KindOf(S(e, "kind") ?? ""), name: S(e, "name") ?? ""))).ToList();
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Uses(JsonNode? n)
        {
            if (n is JsonObject o)
            {
                foreach (var key in new[] { "sourceBlock", "action", "_ref" })
                    if (o[key] is JsonValue v && v.ToString() is { } t && UiLibraryMap.IsLibrary(t)) named.Add(t);
                foreach (var (_, c) in o) Uses(c);
            }
            else if (n is JsonArray a) foreach (var c in a) Uses(c);
        }
        foreach (var ex in exports.Values) Uses(ex);
        var libUse = referenced.Where(e => e.kind is "block" or "serverAction" ? named.Contains(e.module + "." + e.name) : false).Select(e => (e.module, e.kind, e.name));
        if (UiLibraryMap.Markdown(libUse) is { Length: > 0 } libMd)
        {
            files.Add(("ui-libraries.md", libMd));
            r.Notes.Add("ui-libraries.md lists the Traditional UI library elements this module uses (OutSystemsUIWeb, RichWidgets, OutSystemsChartsWeb) and what each becomes in Reactive.");
        }
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
