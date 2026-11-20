using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// `oml migrate plan <old.oml> --name <New> [--in <folder|file.oml>]... [--out <folder>]` and `oml migrate status <old.oml> <new.oml>`:
// the preparation of a Traditional Web module's move to a new Reactive module. It runs in the launcher and starts oml as a child
// process for the Core commands it needs (inspect, header, describe), so the decisions below are pure functions of their JSON
// (Build, ProducerMap, EntityWrites, Compare), tested by the hidden `oml migrate selftest`. Nothing outside --out is written;
// the old module is never touched. The generated specs are applied by the existing `oml apply`.
static partial class Migrate
{
    sealed class Refusal(string message, string? hint = null, int code = 2) : Exception(message) { public string? Hint = hint; public int Code = code; }

    // ---------- the thin shell: arguments, child processes, files ----------

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h") { Console.WriteLine(CliUsage.For("migrate")); return args.Length == 0 ? 2 : 0; }
        try
        {
            return args[0] switch
            {
                "plan" => Plan(args[1..]),
                "status" => Status(args[1..]),
                "selftest" => SelfTest(args.Contains("--show")),
                _ => throw new Refusal($"Unknown migrate command '{args[0]}'.", "Usage: " + CliUsage.For("migrate").Replace("\r", "").Replace("\n", " | ")),
            };
        }
        catch (Refusal r) { TerminalOutput.PrintObject(new { error = r.Message, hint = r.Hint }); return r.Code; }
    }

    static readonly Regex ModuleName = new("^[A-Za-z][A-Za-z0-9_]{0,49}$", RegexOptions.CultureInvariant);
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static int Plan(string[] a)
    {
        string? old = null, name = null, outDir = null, template = null;
        var copyExposed = false; // exposed REST and SOAP stay unless the person asks for a copy
        var inputs = new List<string>();
        for (var i = 0; i < a.Length; i++)
            switch (a[i])
            {
                case "--name" when i + 1 < a.Length: name = a[++i]; break;
                case "--in" when i + 1 < a.Length: inputs.Add(Path.GetFullPath(a[++i])); break;
                case "--out" when i + 1 < a.Length: outDir = Path.GetFullPath(a[++i]); break;
                case "--template" when i + 1 < a.Length: template = Path.GetFullPath(a[++i]); break;
                case "--copy-exposed": copyExposed = true; break;
                case var o when o.StartsWith("--"): throw new Refusal($"Unknown or incomplete option {o}.", "Usage: " + CliUsage.For("migrate").Split('\n')[0].Trim());
                default: if (old is not null) throw new Refusal($"Unexpected argument {a[i]}: migrate plan takes one old module."); old = a[i]; break;
            }
        // Every refusal that needs no module comes before the first child process.
        if (old is null) throw new Refusal("migrate plan needs the old module: oml migrate plan <old.oml> --name <NewModule>.");
        old = Path.GetFullPath(old);
        if (!File.Exists(old)) throw new Refusal($"{old} does not exist.");
        if (name is null) throw new Refusal("migrate plan needs --name <NewModule>, the name of the new Reactive module.");
        if (!ModuleName.IsMatch(name)) throw new Refusal($"--name {name}: use letters, digits and underscores, starting with a letter (up to 50).");
        foreach (var input in inputs)
            if (!Directory.Exists(input) && !(File.Exists(input) && Path.GetExtension(input).Equals(".oml", StringComparison.OrdinalIgnoreCase)))
                throw new Refusal($"--in {input}: not a folder or an .oml file.");
        if (template is not null && !(File.Exists(template) && Path.GetExtension(template).Equals(".oml", StringComparison.OrdinalIgnoreCase))) throw new Refusal($"--template {template}: not an .oml file.", "Give a Reactive template module file (see oml templates).");
        outDir ??= Path.Combine(Environment.CurrentDirectory, "migrate", name);
        if (File.Exists(outDir) || Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
            throw new Refusal($"{outDir} exists and is not empty.", "migrate plan never writes into a folder that has files: choose another --out.");

        var inspect = Child("inspect", old);
        if (!IsTraditional(inspect)) throw new Refusal($"{Path.GetFileName(old)} is a {S(inspect, "kind")} module: oml migrate takes a Traditional Web module (kind WebResponsive or Web...).");
        if (string.Equals(S(inspect, "name"), name, StringComparison.OrdinalIgnoreCase)) throw new Refusal($"--name {name} is the name of the old module: the new module needs another name.");

        // Producer files: one header call for every .oml found under --in.
        var files = inputs.SelectMany(i => Directory.Exists(i) ? Directory.EnumerateFiles(i, "*.oml", SearchOption.AllDirectories) : [i])
            .Select(Path.GetFullPath).Where(f => !f.Equals(old, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        var header = new JsonArray();
        for (var at = 0; at < files.Count;)
        {
            var chunk = new List<string>(); var size = 0; // a command line has a length limit
            while (at < files.Count && (chunk.Count == 0 || size + files[at].Length < 24000)) { size += files[at].Length + 3; chunk.Add(files[at++]); }
            var h = Child(["header", .. chunk], tolerateFailure: true);
            if (h is JsonArray list) foreach (var e in list.ToArray()) { list.Remove(e); header.Add(e); } else header.Add(h);
        }
        var (map, producerNotes) = ProducerMap(header, S(inspect, "name") ?? "");

        // The whole plan is decided by pure code over what the child processes print (Assemble); only the files are written here.
        var plan = Assemble(old, name, outDir, inspect, map, producerNotes, template, (args, stdin) => Child(args, tolerateFailure: true, stdin: stdin), copyExposed: copyExposed);
        Directory.CreateDirectory(outDir);
        foreach (var (file, text) in plan.Files)
        {
            var path = Path.Combine(outDir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.CreateNew); // never overwrites
            stream.Write(new UTF8Encoding(false).GetBytes(text));
        }
        TerminalOutput.PrintNode(plan.Output);
        return 0;
    }

    static int Status(string[] a)
    {
        var files = new List<string>(); string? plan = null;
        for (var i = 0; i < a.Length; i++)
            if (a[i] == "--plan" && i + 1 < a.Length) plan = a[++i];
            else if (a[i].StartsWith("--")) throw new Refusal($"Unknown or incomplete option {a[i]}.", "Usage: " + CliUsage.For("migrate").Split('\n')[1].Trim());
            else files.Add(a[i]);
        if (files.Count != 2) throw new Refusal("migrate status takes two modules: oml migrate status <old.oml> <new.oml> [--plan <folder>].");
        foreach (var f in files) if (!File.Exists(f)) throw new Refusal($"{Path.GetFullPath(f)} does not exist.");
        JsonNode? planJson = null;
        if (plan is not null)
        {
            var planFile = Path.Combine(Path.GetFullPath(plan), "plan.json");
            if (!File.Exists(planFile)) throw new Refusal($"--plan {plan}: no plan.json in that folder.", "Give the --out folder of oml migrate plan.");
            try { planJson = JsonNode.Parse(File.ReadAllText(planFile)); } catch (JsonException e) { throw new Refusal($"{planFile} is not valid JSON: {e.Message}"); }
        }
        var newInspect = Child("inspect", Path.GetFullPath(files[1]));
        var result = Compare(Child("inspect", Path.GetFullPath(files[0])), newInspect);
        if (planJson is not null) { var p = ScreenProgress(planJson, newInspect); result["screensProgress"] = p; result["progress"] = result["progress"] + "; " + p["line"]; }
        TerminalOutput.PrintNode(result);
        return 0;
    }

    // Starts oml with these arguments and returns its JSON output. A failed command with JSON output is returned as it is when
    // tolerated (header and describe print per-file or per-element errors); anything else is one refusal with what oml said.
    static JsonNode Child(string command, string file) => Child([command, file]);
    static JsonNode Child(string[] args, bool tolerateFailure = false, string? stdin = null)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true, RedirectStandardInput = stdin is not null, StandardInputEncoding = stdin is null ? null : new UTF8Encoding(false) };
        foreach (var x in args) info.ArgumentList.Add(x);
        info.Environment["OML_OUTPUT"] = "json";
        info.Environment["OML_LOG"] = "0"; // the parent command is logged once
        using var p = Process.Start(info) ?? throw new Refusal("Could not start oml.", code: 1);
        var err = p.StandardError.ReadToEndAsync();
        if (stdin is not null) { p.StandardInput.Write(stdin); p.StandardInput.Close(); }
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        JsonNode? node = null;
        try { node = JsonNode.Parse(text); } catch (JsonException) { }
        var message = node is JsonObject o && o["error"] is JsonNode e ? e.ToString() : null;
        if (node is null || p.ExitCode != 0 && (!tolerateFailure || message is not null))
        {
            var said = message ?? (text.Trim().Length > 0 ? text.Trim() : err.Result.Trim());
            throw new Refusal($"oml {string.Join(' ', args.Take(2).Select(x => Path.GetFileName(x)))} failed: {(said.Length > 300 ? said[..300] + "..." : said)}", "Fix this and run the command again; a migrate plan writes nothing until every call has worked.", 1);
        }
        return node;
    }

    // oml describe with several names in one process (a long list in a few); returns name -> text, and says in notes what did not describe.
    static Dictionary<string, string> Describe(Func<string[], string?, JsonNode> run, string file, List<(string query, string name)> names, List<string> notes, params string[] extra)
    {
        var texts = new Dictionary<string, string>();
        for (var at = 0; at < names.Count; at += 150)
        {
            var chunk = names.Skip(at).Take(150).ToList();
            var args = new List<string> { "describe", file };
            args.AddRange(chunk.Select(n => n.query));
            args.AddRange(extra);
            foreach (var (n, entry) in Pair(run([.. args], null), chunk.Select(c => c.query).ToList()))
                if (S(entry, "text") is { } text) texts[n] = text.TrimEnd() + "\n";
                else { texts[n] = $"(describe failed: {S(entry, "error") ?? "no text"})\n"; notes.Add($"oml describe could not read {n}: {S(entry, "error") ?? "no text"}"); }
        }
        return texts;
    }

    // ---------- the pure parts ----------

    // The entry of a describe result for each name asked: an object for one name, an array in the order asked for several.
    internal static List<(string name, JsonNode? entry)> Pair(JsonNode result, List<string> names)
    {
        var entries = result is JsonArray arr ? arr.ToArray() : [result];
        return names.Select((n, i) => (n, entries.Length == names.Count ? entries[i] : null)).ToList();
    }

    static JsonNode? P(JsonNode? o, string name) => o is JsonObject obj ? obj.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value : null;
    static string? S(JsonNode? o, string key) => P(o, key) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static bool? B(JsonNode? o, string key) => P(o, key) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    static JsonObject[] Rows(JsonNode? n, string key) => (P(n, key) as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
    static string[] Strings(JsonNode? n, string key) => (P(n, key) as JsonArray)?.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToArray() ?? [];
    static string[] Names(JsonNode? n, string key) => Rows(n, key).Select(r => S(r, "name")).OfType<string>().ToArray();
    static HashSet<string> Set(IEnumerable<string> x) => new(x, StringComparer.OrdinalIgnoreCase);
    static JsonArray Arr(IEnumerable<string> x) => new(x.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());

    internal static bool IsTraditional(JsonNode inspect) => (S(inspect, "kind") ?? "").StartsWith("Web", StringComparison.OrdinalIgnoreCase);

    // Module name -> producer file, from the output of `oml header` (an object for one file, an array for several; a file that is
    // not a module has `error`). The property names are the header record's own (Name, Key, ESpaceType); matched without case.
    internal static (Dictionary<string, string> map, List<string> notes) ProducerMap(JsonNode header, string oldName)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();
        foreach (var e in header is JsonArray a ? a.ToArray() : [header])
        {
            var path = S(e, "path");
            var name = S(P(e, "info"), "Name");
            if (path is null || name is null) { if (path is not null) notes.Add($"{path} is not a readable module and was skipped."); continue; }
            if (name.Equals(oldName, StringComparison.OrdinalIgnoreCase)) continue;
            if (map.TryGetValue(name, out var first)) notes.Add($"Module {name} is in two files: {first} is used and {path} is ignored.");
            else map[name] = path;
        }
        return (map, notes);
    }

    // A Traditional-only UI kind: the new module cannot reference a web screen, block or theme. `kind` is the interface name
    // Inspect prints for a referenced element (e.GetInterface().Name); matched by the words it must contain.
    static readonly Regex UiKind = new("Screen|Block|Theme|Flow", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Kind is Screen, Block or Email (the emails come from otherContent); File is the describe text, screens/<Name>.json is its raw export.
    // QueryKind: the kind word of the path `Kind:Name` when it is not Kind; an email takes the kind inspect printed for it (otherContent).
    internal sealed record Screen(string Name, string Kind, string Flow, string[] Inputs, string[] Roles, string File, string? QueryKind = null)
    { public string Query => (QueryKind ?? Kind) + ":" + Name; public string Export => File[..^4] + ".json"; }

    // One session variable of the old module and what becomes of it (item 3 of the plan): client or manual.
    internal sealed record SessionDecision(string Name, string Type, string? Default, string Decision, string Why);
    // A module element that no inspect section lists (otherContent) and what is done with it.
    internal sealed record Other(string Kind, string Name, string Decision, string Why);
    // What the screens and blocks add to the picture before the plan is final: entity actions their logic calls, the session variables
    // they use, and the session variables each copied server or service action uses (variable -> actions).
    // CopyExposed: the person chose to copy the exposed REST and SOAP services too (--copy-exposed).
    internal sealed record Extra(IReadOnlyList<Call> ScreenWrites, IReadOnlyDictionary<string, string[]> ServerSession, IReadOnlySet<string> ScreenSession, bool CopyExposed = false,
        IReadOnlyDictionary<string, string>? ServerTexts = null, IReadOnlyList<ActionCall>? ActionCalls = null)
    { public static readonly Extra None = new([], new Dictionary<string, string[]>(), new HashSet<string>()); }

    internal sealed class Result
    {
        public JsonObject Plan = new(), NewModule = new();
        public JsonObject? Prepare, Source; // Source: the repoint edits for the working copy of the prepared module
        public string Prepared = "", MigrateSource = "", OldName = "";
        public List<string> Notes = [];
        public SortedDictionary<string, int> Counts = new() { ["stay"] = 0, ["copy"] = 0, ["client"] = 0, ["manual"] = 0 };
        public SortedDictionary<string, int> RefCounts = new() { ["add"] = 0, ["skip-ui"] = 0, ["unresolved"] = 0, ["builtin"] = 0 };
        public int WrapperCreate, WrapperExists;
        public List<string> WrapperNames = [];
        public List<string> ExposedKept = []; // exposed REST and SOAP services left in the old module: the person may choose to copy them
        public List<string> Unresolved = []; // "Module (A, B)": references the new module needs before the copy, added by hand
        public List<Call> Calls = [];
        public List<Screen> Screens = [];
        public List<(string query, string name)> Logic = []; // copied server and service actions and processes: described to find entity writes
        public string[] EntityNames = [];
        public string[] Sessions = [];
        public List<SessionDecision> SessionDecisions = [];
        public List<Other> Others = [];
        public JsonArray ClientVariables = [];
        public JsonObject? ScreensSpec; // 4-screens.json, set when the screens are translated
        public Dictionary<string, string> WrapperMap = new(StringComparer.Ordinal); // entity action -> wrapper name
        public Dictionary<string, List<(string input, string variable)>> SessionInputs = new(StringComparer.OrdinalIgnoreCase); // copied action -> inputs that carry a client variable
        public bool NoOtherContent; // the inspect of an older Core has no otherContent key
    }

    static readonly string[] EntityOps = ["Create", "CreateOrUpdate", "Update", "Delete"];
    // Platform actions that act on a Traditional Web page from the server. Copied as they are, they do nothing useful in Reactive.
    static readonly HashSet<string> PageOnly = new(StringComparer.Ordinal) { "RunJavaScript", "AddJavaScriptTag", "AddStyleSheetTag", "AddMetaTag", "AddFavicon", "AddPostProcessingFilter", "IsAjaxRequest" };

    // One call of an entity action in the copied logic: `element` is the copied element, `path` the path of the Execute node
    // as `oml tree` prints it (the same in the new module), `op` is Create, CreateOrUpdate, Update or Delete.
    // A call between copied actions, from `oml tree`: the caller's name, the call node's exact path, the called action.
    internal sealed record ActionCall(string Caller, string Path, string Callee);

    internal sealed record Call(string Element, string Path, string Node, string Entity, string Op)
    { public string Wrapper => Entity + "_" + Op; }

    // The plan and the specs. inspect is the JSON of `oml inspect <old.oml>`; producers maps a module name to its file;
    // calls are the entity actions the copied logic calls (FindCalls), which decide the wrappers and the repoint edits.
    internal static Result Build(JsonNode inspect, string oldFile, string newName, string outDir, IReadOnlyDictionary<string, string> producers, IReadOnlyList<Call> calls, Extra? extra = null)
    {
        extra ??= Extra.None;
        var r = new Result { OldName = S(inspect, "name") ?? Path.GetFileNameWithoutExtension(oldFile) };
        var elements = new JsonArray();
        void El(string kind, string name, string decision, string why)
        { elements.Add(new JsonObject { ["kind"] = kind, ["name"] = name, ["decision"] = decision, ["why"] = why }); r.Counts[decision] = r.Counts.GetValueOrDefault(decision) + 1; }

        // A static entity is listed under entities (kind StaticEntity) and under staticEntities.
        var staticNames = Set(Names(inspect, "staticEntities").Concat(Rows(inspect, "entities").Where(e => S(e, "kind") == "StaticEntity").Select(e => S(e, "name")!)));
        var entityRows = Rows(inspect, "entities").Where(e => S(e, "name") is { } n && !staticNames.Contains(n)).ToArray();
        r.EntityNames = entityRows.Select(e => S(e, "name")!).ToArray();
        var serverActions = Rows(inspect, "serverActions");
        var actionNames = Set(serverActions.Select(a => S(a, "name")!));

        // A wrapper action in the old module for each entity action the copied logic calls, since the entities stay read only outside it.
        // An action that is itself a wrapper (Order_Create calling CreateOrder) is not copied, so its call is not one to re-point.
        calls = calls.Where(c => !string.Equals(c.Element, c.Wrapper, StringComparison.OrdinalIgnoreCase)).ToList();
        r.Calls = calls.ToList();
        var wrappers = new JsonArray();
        var wrapperSpecs = new JsonArray();
        var existingWrappers = Set([]);
        // The entity actions that screens and blocks call need a wrapper too (their Path is empty: nothing to re-point, the translator calls it).
        foreach (var g in calls.Concat(extra.ScreenWrites.Where(c => !string.Equals(c.Element, c.Wrapper, StringComparison.OrdinalIgnoreCase))).GroupBy(c => c.Wrapper, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var c = g.First();
            r.WrapperMap.TryAdd(c.Op + c.Entity, c.Wrapper);
            var exists = actionNames.Contains(c.Wrapper);
            r.WrapperNames.Add(c.Wrapper);
            wrappers.Add(new JsonObject { ["kind"] = "wrapper", ["entity"] = c.Entity, ["name"] = c.Wrapper, ["entityAction"] = c.Op + c.Entity, ["decision"] = exists ? "exists" : "create", ["usedBy"] = Arr(g.Select(x => x.Element).Distinct().Order(StringComparer.Ordinal)) });
            if (exists)
            {
                r.WrapperExists++; existingWrappers.Add(c.Wrapper);
                if (serverActions.First(a => string.Equals(S(a, "name"), c.Wrapper, StringComparison.OrdinalIgnoreCase)) is var row && B(row, "public") != true)
                    wrapperSpecs.Add(new JsonObject { ["name"] = S(row, "name"), ["public"] = true });
            }
            else { r.WrapperCreate++; wrapperSpecs.Add(Wrapper(c.Entity, c.Op)); }
        }
        if (r.WrapperExists > 0) r.Notes.Add($"{r.WrapperExists} wrapper action(s) already exist in the old module ({string.Join(", ", r.WrapperNames.Where(existingWrappers.Contains))}); they are not created again and not copied. Check that each has the signature of the entity action it wraps (input Source or Id, output Id); a private one is made public.");

        // Names used by more than one element of the old module: a copy path then needs Kind:Name.
        var all = r.EntityNames.Concat(staticNames).Concat(new[] { "structures", "serverActions", "serviceActions", "clientActions", "screens", "blocks", "timers", "processes", "roles", "siteProperties", "exceptions", "restClients", "restServices" }.SelectMany(k => Names(inspect, k)));
        var dup = Set(all.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key));
        var copyPaths = new List<string>();
        void Copy(string section, string kind, string label, string name, string why, bool describeToo = false)
        {
            El(kind, name, "copy", why);
            copyPaths.Add(dup.Contains(name) && label.Length > 0 ? label + ":" + name : name);
            if (describeToo) r.Logic.Add((label + ":" + name, name));
        }

        foreach (var e in entityRows)
            El("entity", S(e, "name")!, "stay", B(e, "public") == true ? "stays in the old module, already public; the new module references it (read only there: use the wrapper actions to write)" : "stays in the old module and is made public; the new module references it (read only there: use the wrapper actions to write)");
        foreach (var e in staticNames.Order(StringComparer.Ordinal)) El("staticEntity", e, "stay", "stays in the old module and is made public; the new module references it");
        var roles = Rows(inspect, "roles");
        foreach (var x in roles) El("role", S(x, "name")!, "stay", "stays in the old module; a copied role would be a new role with no users granted");
        foreach (var x in Rows(inspect, "structures")) Copy("structures", "structure", "Structure", S(x, "name")!, "data definition: copied into the new module");
        foreach (var x in Rows(inspect, "exceptions")) Copy("exceptions", "exception", "UserException", S(x, "name")!, "user exception: copied");
        foreach (var x in Rows(inspect, "siteProperties")) Copy("siteProperties", "siteProperty", "SiteProperty", S(x, "name")!, "site property: copied (its value in Service Center does not follow)");
        foreach (var x in serverActions)
        {
            var n = S(x, "name")!;
            if (existingWrappers.Contains(n)) El("serverAction", n, "stay", "wrapper of the old module: the new module references it");
            else Copy("serverActions", "serverAction", "ServerAction", n, "logic: copied; a call that writes an entity directly is re-pointed to a wrapper in 2-source.json", true);
        }
        foreach (var x in Rows(inspect, "serviceActions")) Copy("serviceActions", "serviceAction", "ServiceAction", S(x, "name")!, "service action: copied", true);
        foreach (var x in Rows(inspect, "clientActions")) El("clientAction", S(x, "name")!, "manual", "a Traditional Web module has no client actions in this version; check it by hand");
        foreach (var x in Rows(inspect, "restClients")) Copy("restClients", "restClient", "", S(x, "name")!, "consumed REST API: copied (its integration settings in Service Center do not follow)");
        foreach (var x in Rows(inspect, "restServices"))
            if (extra.CopyExposed) Copy("restServices", "restService", "", S(x, "name")!, "exposed REST API: copied because the person chose --copy-exposed; they move its consumers to the new module's URL");
            else { El("restService", S(x, "name")!, "stay", "exposed REST API: stays in the old module, so its consumers keep working; the person decides whether to copy it (--copy-exposed)"); r.ExposedKept.Add(S(x, "name")!); }
        foreach (var x in Rows(inspect, "timers")) Copy("timers", "timer", "Timer", S(x, "name")!, "timer: copied; it now exists in both modules");
        foreach (var x in Rows(inspect, "processes")) Copy("processes", "process", "Process", S(x, "name")!, "process (BPT): copied; it now exists in both modules", true);
        // Session variables: client when only screens and blocks use them (DecideSession), else a decision for a person.
        r.Sessions = Names(inspect, "sessionVariables");
        var clientVars = new JsonArray();
        var sessionEdits = new List<JsonObject>();
        foreach (var x in Rows(inspect, "sessionVariables").Where(x => S(x, "name") is not null))
        {
            var n = S(x, "name")!; var type = S(x, "type") ?? "";
            extra.ServerSession.TryGetValue(n, out var serverUse);
            var def = S(x, "default");   // a newer inspect gives the default of a session variable; an older one gives name and type only, so this is null then
            var (decision, why) = DecideSession(n, type, serverUse ?? [], extra.ScreenSession.Contains(n), !string.IsNullOrEmpty(def), threads: extra.ServerTexts is not null);
            // Read by copied server logic: the client variable reaches it as an input (ThreadSession), or the reason it cannot.
            if (decision == "client" && serverUse is { Length: > 0 })
            {
                var th = ThreadSession(n, type, def, serverUse, extra.ServerTexts ?? new Dictionary<string, string>(), extra.ActionCalls ?? [], r.Logic);
                if (th.Why is not null) (decision, why) = ("manual", $"read by copied server logic ({string.Join(", ", serverUse)}), and it cannot be passed as an input: {th.Why}");
                else
                {
                    sessionEdits.AddRange(th.Edits);
                    foreach (var (action, input) in th.InputOf)
                    {
                        if (!r.SessionInputs.TryGetValue(action, out var list)) r.SessionInputs[action] = list = [];
                        list.Add((input, n));
                    }
                }
            }
            r.SessionDecisions.Add(new SessionDecision(n, type, def, decision, why));
            El("sessionVariable", n, decision, why);
            if (decision == "client")
            {
                var cv = new JsonObject { ["name"] = n, ["type"] = type };
                if (!string.IsNullOrEmpty(def)) cv["default"] = def;
                clientVars.Add(cv);
            }
        }
        r.ClientVariables = clientVars;
        var emails = Rows(inspect, "otherContent").Where(o => (S(o, "kind") ?? "").Contains("Email", StringComparison.OrdinalIgnoreCase)).ToArray();
        var screenRows = Rows(inspect, "screens");
        string FileFor(string kind, string n, int taken) => "screens/" + Regex.Replace(n, @"[^A-Za-z0-9_-]", "_") + (taken > 0 ? "-" + kind.ToLowerInvariant() : "") + ".txt";
        foreach (var x in screenRows) r.Screens.Add(new Screen(S(x, "name")!, "Screen", S(x, "flow") ?? "", Strings(x, "inputs"), Strings(x, "roles"), FileFor("Screen", S(x, "name")!, 0)));
        foreach (var x in Rows(inspect, "blocks")) r.Screens.Add(new Screen(S(x, "name")!, "Block", S(x, "flow") ?? "", Strings(x, "inputs"), Strings(x, "roles"), FileFor("Block", S(x, "name")!, r.Screens.Count(q => q.Name == S(x, "name")))));
        foreach (var x in emails) r.Screens.Add(new Screen(S(x, "name")!, "Email", "", [], [], FileFor("Email", S(x, "name")!, r.Screens.Count(q => q.Name == S(x, "name"))), S(x, "kind")));

        // Everything else the inspect lists under otherContent: a decision by kind name, nothing dropped silently.
        r.NoOtherContent = P(inspect, "otherContent") is not JsonArray;
        foreach (var x in Rows(inspect, "otherContent").Where(o => S(o, "name") is not null))
        {
            var (kind, n) = (S(x, "kind") ?? "?", S(x, "name")!);
            var (decision, why) = DecideOther(kind);
            if (decision == "stay" && extra.CopyExposed) (decision, why) = ("copy", "exposed SOAP web service: copied because the person chose --copy-exposed; they move its consumers to the new module's URL");
            else if (decision == "stay") r.ExposedKept.Add(n);
            r.Others.Add(new Other(kind, n, decision, why));
            if (decision == "copy") { El(kind, n, "copy", why); copyPaths.Add(n); }
            else if (decision is "manual" or "stay") El(kind, n, decision, why);
        }
        if (dup.Count > 0) r.Notes.Add($"These names are used by more than one element of the old module: {string.Join(", ", dup.Order(StringComparer.Ordinal))}. Their copy paths carry the kind (Kind:Name); check them if apply says a path is ambiguous.");

        // References of the old module: producers found in --in get added to the new module.
        var references = new JsonArray();
        var refSpecs = new List<JsonObject>();
        foreach (var rf in Rows(inspect, "references"))
        {
            var module = S(rf, "module") ?? "";
            var all2 = Rows(rf, "elements").Select(e => (name: S(e, "name") ?? "", kind: S(e, "kind") ?? "")).ToArray();
            var keep = all2.Where(e => !UiKind.IsMatch(e.kind)).Select(e => e.name).ToArray();
            var skipped = all2.Where(e => UiKind.IsMatch(e.kind)).Select(e => e.name).ToArray();
            producers.TryGetValue(module, out var file);
            string decision, why;
            if (module.Equals("System", StringComparison.OrdinalIgnoreCase)) { decision = "builtin"; why = "System is part of every module"; }
            else if (keep.Length == 0 && skipped.Length > 0) { decision = "skip-ui"; why = "only web screens, blocks or themes are used: Traditional UI is rebuilt, not referenced"; }
            else if (keep.Length == 0) { decision = "unresolved"; why = "no element is listed (an extension or an unused reference): add it in Manage Dependencies if the new module needs it"; }
            else if (file is null) { decision = "unresolved"; why = "no producer file found: give its folder with --in, or add the reference in Manage Dependencies"; }
            else { decision = "add"; why = "added to the new module from the producer file"; refSpecs.Add(new JsonObject { ["producer"] = Path.GetFullPath(file), ["elements"] = Arr(keep) }); }
            r.RefCounts[decision]++;
            var entry = new JsonObject { ["module"] = module, ["elements"] = Arr(keep), ["producerFile"] = file is null ? null : Path.GetFullPath(file), ["decision"] = decision, ["why"] = why };
            if (skipped.Length > 0) entry["skippedUi"] = Arr(skipped);
            references.Add(entry);
            if (decision == "unresolved" && keep.Length > 0) r.Unresolved.Add($"{module} ({string.Join(", ", keep)})");
            if (decision == "unresolved") r.Notes.Add($"Reference {module} is unresolved ({(keep.Length > 0 ? string.Join(", ", keep.Take(8)) + (keep.Length > 8 ? ", ..." : "") : "no elements")}): give its producer file with --in and plan again, or add it in Manage Dependencies of the new module.");
        }

        // 1-prepare-old.json: public entities, static entities and roles, and the wrappers. Nothing else changes; exposeReadOnly is never set.
        var prepare = new JsonObject();
        JsonArray Publics(IEnumerable<string> names) => new(names.Select(n => (JsonNode?)new JsonObject { ["name"] = n, ["public"] = true }).ToArray());
        var privateEntities = entityRows.Where(e => B(e, "public") != true).Select(e => S(e, "name")!).ToArray();
        var staticRows = Rows(inspect, "staticEntities");
        var privateStatics = staticNames.Where(n => staticRows.FirstOrDefault(s => string.Equals(S(s, "name"), n, StringComparison.OrdinalIgnoreCase)) is not { } s || B(s, "public") != true).Order(StringComparer.Ordinal).ToArray();
        var privateRoles = roles.Where(x => B(x, "public") == false).Select(x => S(x, "name")!).ToArray();
        if (roles.Any(x => B(x, "public") is null)) r.Notes.Add("The public flag of a role was not readable; check in the old module that every role the new module references is public.");
        if (privateEntities.Length > 0) prepare["entities"] = Publics(privateEntities);
        if (privateStatics.Length > 0) prepare["staticEntities"] = Publics(privateStatics);
        if (privateRoles.Length > 0) prepare["roles"] = Publics(privateRoles);
        if (wrapperSpecs.Count > 0) prepare["serverActions"] = wrapperSpecs;
        r.Prepare = prepare.Count > 0 ? prepare : null;
        r.Prepared = r.Prepare is null ? oldFile : Path.Combine(outDir, r.OldName + ".prepared.oml");

        // 2-source.json: the repoint edits, applied to the prepared module to make a working copy that is only copied from. Re-pointing
        // there, before the copy, works: the entity actions and the wrappers are both local, so every node resolves; in the new module
        // the entity is a read-only reference with no Create/Update/Delete, so a pasted node would arrive with a dangling action.
        // The action is the plain wrapper name: a name resolves to the module's own element first, so it cannot be ambiguous.
        // The path is the one `oml tree` prints for the old module; the prepared module changes no path of an existing element.
        r.MigrateSource = Path.Combine(outDir, r.OldName + ".migrate-source.oml");
        // Then the session inputs (ThreadSession): new inputs, reads rewritten, the input passed on by each copied caller.
        if (r.Calls.Count > 0 || sessionEdits.Count > 0)
            r.Source = new JsonObject { ["edits"] = new JsonArray(r.Calls.Select(c => (JsonNode?)new JsonObject { ["op"] = "repoint", ["element"] = c.Path, ["action"] = c.Wrapper })
                .Concat(sessionEdits.Select(e => (JsonNode?)e)).ToArray()) };

        // 3-new-module.json: references first (the prepared old module, then the producers), then the copy from the working copy.
        var refs = new JsonArray();
        var fromOld = r.EntityNames.Concat(staticNames.Order(StringComparer.Ordinal)).Concat(roles.Select(x => S(x, "name")!)).Concat(r.WrapperNames).ToArray();
        if (fromOld.Length > 0) refs.Add(new JsonObject { ["producer"] = r.Prepared, ["elements"] = Arr(fromOld) });
        foreach (var s in refSpecs) refs.Add(s);
        if (refs.Count > 0) r.NewModule["references"] = refs;
        if (copyPaths.Count > 0) r.NewModule["copy"] = new JsonArray(new JsonObject { ["from"] = r.Source is null ? r.Prepared : r.MigrateSource, ["elements"] = Arr(copyPaths) });

        r.Plan = new JsonObject
        {
            ["source"] = oldFile, ["sourceModule"] = r.OldName, ["target"] = newName, ["preparedModule"] = r.Prepare is null ? null : r.Prepared, ["migrateSource"] = r.Source is null ? null : r.MigrateSource,
            ["elements"] = elements, ["wrappers"] = wrappers, ["references"] = references,
            ["otherContent"] = new JsonArray(r.Others.Select(o => (JsonNode?)new JsonObject { ["kind"] = o.Kind, ["name"] = o.Name, ["decision"] = o.Decision, ["why"] = o.Why }).ToArray()),
        };
        Notes(r, inspect);
        r.Plan["notes"] = Arr(r.Notes);
        return r;
    }

    // One public wrapper server action with the signature of the entity action it wraps, so the arguments of a call line up by
    // name: Create and CreateOrUpdate and Update take `Source` (the record), Delete takes `Id`; Create and CreateOrUpdate answer
    // with `Id` (the keys of skill/recipes/entity-with-crud.jsonc).
    internal static JsonObject Wrapper(string entity, string op)
    {
        var spec = new JsonObject { ["name"] = entity + "_" + op, ["public"] = true, ["description"] = $"Calls {op}{entity} for other modules: {entity} is read only outside its own module." };
        JsonObject Arg(string name, string type) => new() { ["name"] = name, ["type"] = type, ["mandatory"] = true };
        if (op == "Delete")
        {
            spec["inputs"] = new JsonArray(Arg("Id", entity + " Identifier"));
            spec["flow"] = new JsonArray(new JsonObject { ["kind"] = "execute", ["name"] = "Remove", ["action"] = "Delete" + entity, ["args"] = new JsonObject { ["Id"] = "Id" } });
            return spec;
        }
        spec["inputs"] = new JsonArray(Arg("Source", entity));
        var save = new JsonObject { ["kind"] = "execute", ["name"] = "Save", ["action"] = op + entity, ["args"] = new JsonObject { ["Source"] = "Source" } };
        if (op == "Update") { spec["flow"] = new JsonArray(save); return spec; }
        spec["outputs"] = new JsonArray(new JsonObject { ["name"] = "Id", ["type"] = entity + " Identifier" });
        spec["flow"] = new JsonArray(save, new JsonObject { ["kind"] = "assign", ["name"] = "ReturnId", ["assign"] = new JsonObject { ["Id"] = "Save.Id" }, ["next"] = "End" });
        return spec;
    }

    // The entity-action calls in the output of `oml batch` over `oml tree` steps (one per element of `logic`, in order).
    // Each result is {step, args, exitCode, output}; the output is the tree: nodes with kind, name, path, `ref` (the called action
    // of an Execute node, by name) and `children`. An entity action is called by its own name (CreateOrder), so a node whose ref is
    // Create/CreateOrUpdate/Update/Delete plus an entity name of this module is the call. A tree is cut at 250 parts (`truncated`).
    // The calls of copied server and service actions inside the copied logic (the same tree batch as FindCalls).
    internal static List<ActionCall> FindActionCalls(JsonNode? batch, List<(string query, string name)> logic)
    {
        var copied = logic.Where(l => l.query.StartsWith("ServerAction:") || l.query.StartsWith("ServiceAction:")).Select(l => l.name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var calls = new List<ActionCall>();
        var results = (P(batch, "results") as JsonArray)?.ToArray() ?? [];
        for (var i = 0; i < logic.Count && i < results.Length; i++)
        {
            if (P(results[i], "output") is not JsonObject tree || P(tree, "error") is not null) continue;
            var caller = logic[i].name;
            void Walk(JsonNode? n)
            {
                if (n is not JsonObject node) return;
                if ((S(node, "kind") ?? S(node, "type"))?.StartsWith("Execute", StringComparison.Ordinal) == true && S(node, "ref") is { } called
                    && copied.Contains(called) && S(node, "path") is { } path)
                    calls.Add(new ActionCall(caller, path, copied.First(c => c.Equals(called, StringComparison.OrdinalIgnoreCase))));
                foreach (var c in (P(node, "children") as JsonArray)?.ToArray() ?? []) Walk(c);
            }
            Walk(tree);
        }
        return calls;
    }

    internal static List<Call> FindCalls(JsonNode batch, List<(string query, string name)> logic, string[] entities, List<string> notes)
    {
        var actions = new Dictionary<string, (string entity, string op)>(StringComparer.Ordinal);
        foreach (var e in entities) foreach (var op in EntityOps) actions.TryAdd(op + e, (e, op));
        var calls = new List<Call>();
        var pageOnly = new List<string>();
        var results = (P(batch, "results") as JsonArray)?.ToArray() ?? [];
        for (var i = 0; i < logic.Count; i++)
        {
            var output = i < results.Length ? P(results[i], "output") : null;
            if (output is not JsonObject tree || P(tree, "error") is not null)
            { notes.Add($"oml tree could not read {logic[i].name}: {(output is JsonObject o ? S(o, "error") : "no result")}; look for entity writes in it by hand."); continue; }
            if (P(tree, "truncated") is not null) notes.Add($"The tree of {logic[i].name} was cut: an entity write deep in it may be missing from the repoint edits; apply will report a direct write as an error.");
            var name = logic[i].name;
            void Walk(JsonNode? n)
            {
                if (n is not JsonObject node) return;
                // Only the call node itself: its Sequence child (the connector to the next node) carries the same ref.
                if ((S(node, "kind") ?? S(node, "type"))?.StartsWith("Execute", StringComparison.Ordinal) == true
                    && S(node, "ref") is { } called && actions.TryGetValue(called, out var hit) && S(node, "path") is { } path)
                    calls.Add(new Call(name, path, S(node, "name") ?? "", hit.entity, hit.op));
                if ((S(node, "kind") ?? S(node, "type"))?.StartsWith("Execute", StringComparison.Ordinal) == true && S(node, "ref") is { } r2 && PageOnly.Contains(r2))
                    pageOnly.Add($"{name} calls {r2}");
                foreach (var c in (P(node, "children") as JsonArray)?.ToArray() ?? []) Walk(c);
            }
            Walk(tree);
        }
        if (pageOnly.Count > 0) notes.Add($"Copied server logic calls platform actions that work only on a Traditional Web page: {string.Join("; ", pageOnly.Distinct())}. "
            + "Rewrite those parts for Reactive (JavaScript runs in a client action; tags go in the theme or a script) after the copy.");
        return calls;
    }

    // The plain-words notes: what the command cannot do for the developer.
    static void Notes(Result r, JsonNode inspect)
    {
        var timers = Names(inspect, "timers"); var processes = Names(inspect, "processes");
        var head = new List<string>();
        if (timers.Length + processes.Length > 0)
            head.Add($"Timers and processes exist in both modules after the copy ({string.Join(", ", timers.Concat(processes))}). Remove or deactivate each in one of the two modules, or both will run.");
        head.Add("The old module keeps the entities (public now, but read only for other modules) and is best turned into a service module once its screens and blocks are gone.");
        if (r.EntityNames.Length > 0)
            head.Add(r.WrapperNames.Count > 0
                ? $"The entities stay read only for other modules, so the old module gains {r.WrapperCreate} public wrapper server action(s) for the entity actions its copied logic and its translated screens call ({string.Join(", ", r.WrapperNames)}; apply warns PublicServerAction for each, as intended), and the copied logic calls the wrappers: 2-source.json re-points {r.Calls.Count} Execute node(s) in a working copy of the old module."
                : "The entities stay read only for other modules. The copied logic and the screens write no entity directly, so the old module gains no wrapper.");
        if (r.Calls.Count > 0) head.Add("The logic is re-pointed to the wrappers in a working copy of the old module before it is copied, so the copied calls bind to the wrapper references in the new module.");
        head.Add("Wrappers are made for the entity actions the copied logic and the exported screens and blocks call. A rebuilt screen that writes an entity in a way the translator did not see needs one more: add a wrapper to the old module the same way (a public server action with the entity action's inputs and outputs), add it to the references of the new module and call it.");
        head.Add("Site property values, timer schedules and integration settings (REST, e-mail) set in Service Center do not follow a copy: set them again for the new module.");
        var clients = r.SessionDecisions.Where(d => d.Decision == "client").Select(d => d.Name).ToArray();
        var noDefault = r.SessionDecisions.Where(d => d.Decision == "client" && string.IsNullOrEmpty(d.Default)).Select(d => d.Name).ToArray();
        if (clients.Length > 0) head.Add($"Session variable(s) of a basic type with a harmless name become client variables of the same name in 4-screens.json ({string.Join(", ", clients)}); a client variable is readable by the user, so nothing sensitive goes there."
            + (r.SessionInputs.Count > 0 ? $" The copied server actions that read one get it as an input instead ({string.Join(", ", r.SessionInputs.Keys.Order(StringComparer.Ordinal))}; 2-source.json), and the screens pass the client variable." : "") + (noDefault.Length > 0 ? $" The default value of a session variable is not read by this oml inspect ({string.Join(", ", noDefault)}): set the default of each of these client variables by hand if the session variable had one." : " Each keeps the default value inspect gave."));
        foreach (var d in r.SessionDecisions.Where(d => d.Decision == "manual")) head.Add($"Session variable {d.Name} is not migrated, it needs a decision by hand: {d.Why}.");
        if (r.Others.Count > 0)
            foreach (var g in r.Others.GroupBy(o => (o.Decision, o.Kind)).OrderBy(g => g.Key.Decision, StringComparer.Ordinal).ThenBy(g => g.Key.Kind, StringComparer.Ordinal))
                head.Add($"{g.Key.Kind} ({string.Join(", ", g.Select(o => o.Name))}): {g.First().Why}");
        if (r.NoOtherContent) head.Add("This oml did not report otherContent in inspect: emails, SOAP references, entry points and other elements outside the usual sections were not looked at. Use a newer oml, or check them by hand.");
        foreach (var p in Rows(inspect, "processes").Where(p => S(p, "launchOn") is not null))
            head.Add($"Process {S(p, "name")} launches on entity {S(p, "launchOn")}, which stays in the old module: check that the copy still launches, or start it from a server action.");
        r.Notes.InsertRange(0, head);
    }

    internal static List<string> Next(Result r, string old, string name, string outDir, string? template = null)
    {
        string Q(string s) => s.Contains(' ') ? "\"" + s + "\"" : s;
        string F(string f) => Q(Path.Combine(outDir, f));
        var next = new List<string>();
        if (r.ExposedKept.Count > 0)
            next.Add($"Ask the person: the old module exposes {string.Join(", ", r.ExposedKept)}. They stay there, so their consumers keep working. Copy them into the new module too? If yes, plan again with --copy-exposed: the person then owns moving every consumer to the new URL.");
        if (r.Prepare is not null)
        {
            next.Add($"oml apply {Q(old)} {F("1-prepare-old.json")} {Q(r.Prepared)}   (a staged copy of the old module: public entities, static entities, roles{(r.WrapperCreate > 0 ? " and the wrapper actions" : "")})");
            next.Add($"Ask the person to review it, then install and publish it: oml install {Q(r.Prepared)} {Q(old)} ; oml publish {Q(old)}. The new module cannot reference what is not published.");
        }
        else next.Add("The old module needs no preparation: its entities and roles are public and no wrapper is missing.");
        if (r.Source is not null)
            next.Add($"oml apply {Q(r.Prepared)} {F("2-source.json")} {Q(r.MigrateSource)}   ({r.Calls.Count} repoint edit(s) so the logic calls the wrappers: a working copy to copy from; never install it)");
        var created = Q(Path.Combine(outDir, name + ".oml"));
        var staged = Q(Path.Combine(outDir, name + ".staged.oml"));
        var last = staged;
        next.Add($"oml new {created} --template {(template is null ? "<a Reactive template module: see oml templates>" : Q(template))} --name {name}");
        if (r.Unresolved.Count > 0)
            next.Add($"Before the next step, ask the person to open {name} in Service Studio and add in Manage Dependencies: {string.Join("; ", r.Unresolved)}. The copy refuses without them. Then: oml pull {name} --out {created}");
        next.Add($"oml apply {created} {F("3-new-module.json")} {staged}   (references, then the copy from {(r.Source is null ? "the prepared module" : "the working copy")})");
        if (r.ScreensSpec is not null)
        {
            last = Q(Path.Combine(outDir, name + ".screens.oml"));
            next.Add($"oml apply {staged} {F("4-screens.json")} {last}   (the translated screens, blocks and client variables; the TODO stubs and untranslated ones are in 4-screens.md)");
        }
        next.Add("Check the apply report, then ask the person to install and publish the new module.");
        next.Add($"Give {F("4-screens.md")} to an AI agent with: oml doc traditional-to-reactive (it lists what is built, what is left per element, and css/README.md for the theme)");
        next.Add($"oml migrate status {Q(old)} {last} --plan {Q(outDir)}   (what is done, what is still to do, what exists twice, the screens by status)");
        return next;
    }

    // Names like <Entity>_Create that are server actions of the old module: the wrappers it holds.
    static string[] WrappersIn(JsonNode inspect)
    {
        var statics = Set(Names(inspect, "staticEntities"));
        var names = Set(Names(inspect, "serverActions"));
        return Rows(inspect, "entities").Select(e => S(e, "name")!).Where(n => !statics.Contains(n)).SelectMany(e => EntityOps.Select(o => e + "_" + o)).Where(names.Contains).ToArray();
    }

    // The name-based comparison of oml migrate status.
    internal static JsonObject Compare(JsonNode oldI, JsonNode newI)
    {
        var present = WrappersIn(oldI);
        var wrapperSet = Set(present);
        // Roles stay in the old module and the new one references them: a referenced role counts as present.
        var newRoles = Names(newI, "roles").Concat(Rows(newI, "references").SelectMany(x => Rows(x, "elements")).Where(e => (S(e, "kind") ?? "").Contains("Role")).Select(e => S(e, "name")!));
        var kinds = new (string label, string[] old, string[] now)[]
        {
            ("screens", Names(oldI, "screens"), Names(newI, "screens")),
            ("blocks", Names(oldI, "blocks"), Names(newI, "blocks")),
            ("serverActions", Names(oldI, "serverActions").Where(n => !wrapperSet.Contains(n)).ToArray(), Names(newI, "serverActions")), // wrappers stay in the old module
            ("serviceActions", Names(oldI, "serviceActions"), Names(newI, "serviceActions")),
            ("structures", Names(oldI, "structures"), Names(newI, "structures")),
            ("exceptions", Names(oldI, "exceptions"), Names(newI, "exceptions")),
            ("siteProperties", Names(oldI, "siteProperties"), Names(newI, "siteProperties")),
            ("timers", Names(oldI, "timers"), Names(newI, "timers")),
            ("processes", Names(oldI, "processes"), Names(newI, "processes")),
            ("roles", Names(oldI, "roles"), newRoles.ToArray()),
            ("rest", Names(oldI, "restClients").Concat(Names(oldI, "restServices")).ToArray(), Names(newI, "restClients").Concat(Names(newI, "restServices")).ToArray()),
        };
        var result = new JsonObject { ["old"] = S(oldI, "name"), ["new"] = S(newI, "name") };
        var done = new Dictionary<string, (int have, int of)>();
        var duplicates = new JsonObject();
        foreach (var (label, old, now) in kinds)
        {
            var n = Set(now); var o = Set(old);
            var both = old.Where(n.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
            result[label] = new JsonObject { ["both"] = Arr(both), ["onlyOld"] = Arr(old.Where(x => !n.Contains(x)).Order(StringComparer.Ordinal)), ["onlyNew"] = Arr(now.Where(x => !o.Contains(x)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)) };
            done[label] = (both.Length, o.Count);
            if (label is "timers" or "processes" && both.Length > 0) duplicates[label] = Arr(both);
        }
        result["duplicates"] = duplicates;
        result["wrappers"] = new JsonObject { ["presentInOld"] = Arr(present) };
        string P1(string label, string text) => $"{text} {done[label].have} of {done[label].of}";
        result["progress"] = string.Join("; ", P1("screens", "screens"), P1("blocks", "blocks"), P1("serverActions", "server actions"), P1("structures", "structures")) + $"; wrappers in the old module: {present.Length}";
        return result;
    }
}
