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
static class Migrate
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
        string? old = null, name = null, outDir = null;
        var inputs = new List<string>();
        for (var i = 0; i < a.Length; i++)
            switch (a[i])
            {
                case "--name" when i + 1 < a.Length: name = a[++i]; break;
                case "--in" when i + 1 < a.Length: inputs.Add(Path.GetFullPath(a[++i])); break;
                case "--out" when i + 1 < a.Length: outDir = Path.GetFullPath(a[++i]); break;
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

        var r = Build(inspect, old, name, outDir, map, []);
        var notes = new List<string>();

        // Which entity actions the copied logic calls: the node trees of every copied element, in one `oml batch` process.
        var calls = new List<Call>();
        if (r.Logic.Count > 0)
        {
            var steps = new JsonObject { ["steps"] = new JsonArray(r.Logic.Select(l => (JsonNode?)new JsonArray(JsonValue.Create("tree"), JsonValue.Create(old), JsonValue.Create(l.query))).ToArray()) };
            var batch = Child(["batch", "-", "--continue"], tolerateFailure: true, stdin: steps.ToJsonString());
            calls = FindCalls(batch, r.Logic, r.EntityNames, notes);
        }
        r = Build(inspect, old, name, outDir, map, calls);
        r.Notes.AddRange(producerNotes);
        r.Notes.AddRange(notes);

        // Descriptions of the screens and blocks, all in one call.
        var screenTexts = Describe(old, r.Screens.Select(s => ((s.Block ? "Block:" : "Screen:") + s.Name, s.Name)).ToList(), r.Notes);

        Directory.CreateDirectory(outDir);
        var written = new List<string>();
        void Write(string file, string text)
        {
            var path = Path.Combine(outDir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.CreateNew); // never overwrites
            stream.Write(new UTF8Encoding(false).GetBytes(text));
            written.Add(file);
        }
        string Json(JsonNode n) => n.ToJsonString(Indented) + "\n";
        Write("plan.json", Json(r.Plan));
        if (r.Prepare is not null) Write("1-prepare-old.json", Json(r.Prepare));
        if (r.Source is not null) Write("2-source.json", Json(r.Source));
        Write("3-new-module.json", Json(r.NewModule));
        Write("4-screens.md", ScreensMd(r));
        foreach (var s in r.Screens) Write(s.File, screenTexts.TryGetValue((s.Block ? "Block:" : "Screen:") + s.Name, out var t) ? t : "(no description)\n");

        var result = new JsonObject
        {
            ["ok"] = true, ["source"] = old, ["target"] = name, ["out"] = outDir,
            ["counts"] = JsonSerializer.SerializeToNode(r.Counts),
            ["wrappers"] = new JsonObject { ["create"] = r.WrapperCreate, ["exists"] = r.WrapperExists },
            ["references"] = JsonSerializer.SerializeToNode(r.RefCounts),
            ["repointed"] = r.Calls.Count,
            ["files"] = JsonSerializer.SerializeToNode(written),
            ["next"] = JsonSerializer.SerializeToNode(Next(r, old, name, outDir)),
            ["notes"] = JsonSerializer.SerializeToNode(r.Notes),
        };
        TerminalOutput.PrintNode(result);
        return 0;
    }

    static int Status(string[] a)
    {
        var files = a.Where(x => !x.StartsWith("--")).ToArray();
        if (files.Length != 2 || a.Length != 2) throw new Refusal("migrate status takes two modules: oml migrate status <old.oml> <new.oml>.");
        foreach (var f in files) if (!File.Exists(f)) throw new Refusal($"{Path.GetFullPath(f)} does not exist.");
        TerminalOutput.PrintNode(Compare(Child("inspect", Path.GetFullPath(files[0])), Child("inspect", Path.GetFullPath(files[1]))));
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
    static Dictionary<string, string> Describe(string file, List<(string query, string name)> names, List<string> notes)
    {
        var texts = new Dictionary<string, string>();
        for (var at = 0; at < names.Count; at += 150)
        {
            var chunk = names.Skip(at).Take(150).ToList();
            var args = new List<string> { "describe", file };
            args.AddRange(chunk.Select(n => n.query));
            foreach (var (n, entry) in Pair(Child([.. args], tolerateFailure: true), chunk.Select(c => c.query).ToList()))
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

    internal sealed record Screen(string Name, bool Block, string Flow, string[] Inputs, string[] Roles, string File);

    internal sealed class Result
    {
        public JsonObject Plan = new(), NewModule = new();
        public JsonObject? Prepare, Source; // Source: the repoint edits for the working copy of the prepared module
        public string Prepared = "", MigrateSource = "", OldName = "";
        public List<string> Notes = [];
        public SortedDictionary<string, int> Counts = new() { ["stay"] = 0, ["copy"] = 0, ["worklist"] = 0, ["manual"] = 0 };
        public SortedDictionary<string, int> RefCounts = new() { ["add"] = 0, ["skip-ui"] = 0, ["unresolved"] = 0, ["builtin"] = 0 };
        public int WrapperCreate, WrapperExists;
        public List<string> WrapperNames = [];
        public List<Call> Calls = [];
        public List<Screen> Screens = [];
        public List<(string query, string name)> Logic = []; // copied server and service actions and processes: described to find entity writes
        public string[] EntityNames = [];
        public string[] Sessions = [];
    }

    static readonly string[] EntityOps = ["Create", "CreateOrUpdate", "Update", "Delete"];

    // One call of an entity action in the copied logic: `element` is the copied element, `path` the path of the Execute node
    // as `oml tree` prints it (the same in the new module), `op` is Create, CreateOrUpdate, Update or Delete.
    internal sealed record Call(string Element, string Path, string Node, string Entity, string Op)
    { public string Wrapper => Entity + "_" + Op; }

    // The plan and the specs. inspect is the JSON of `oml inspect <old.oml>`; producers maps a module name to its file;
    // calls are the entity actions the copied logic calls (FindCalls), which decide the wrappers and the repoint edits.
    internal static Result Build(JsonNode inspect, string oldFile, string newName, string outDir, IReadOnlyDictionary<string, string> producers, IReadOnlyList<Call> calls)
    {
        var r = new Result { OldName = S(inspect, "name") ?? Path.GetFileNameWithoutExtension(oldFile) };
        var elements = new JsonArray();
        void El(string kind, string name, string decision, string why)
        { elements.Add(new JsonObject { ["kind"] = kind, ["name"] = name, ["decision"] = decision, ["why"] = why }); r.Counts[decision]++; }

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
        foreach (var g in calls.GroupBy(c => c.Wrapper, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var c = g.First();
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
        foreach (var x in Rows(inspect, "restServices")) Copy("restServices", "restService", "", S(x, "name")!, "exposed REST API: copied (the URL of the new module differs)");
        foreach (var x in Rows(inspect, "timers")) Copy("timers", "timer", "Timer", S(x, "name")!, "timer: copied; it now exists in both modules");
        foreach (var x in Rows(inspect, "processes")) Copy("processes", "process", "Process", S(x, "name")!, "process (BPT): copied; it now exists in both modules", true);
        r.Sessions = Names(inspect, "sessionVariables");
        foreach (var n in r.Sessions) El("sessionVariable", n, "manual", "not copied: decide a client variable, data read on the server, or nothing (nothing sensitive in a client variable)");
        foreach (var x in Rows(inspect, "screens").Concat(Rows(inspect, "blocks")).Select((row, i) => (row, block: i >= Rows(inspect, "screens").Length)))
        {
            var n = S(x.row, "name")!;
            var file = "screens/" + Regex.Replace(n, @"[^A-Za-z0-9_-]", "_") + (x.block && Rows(inspect, "screens").Any(s => S(s, "name") == n) ? "-block" : "") + ".txt";
            r.Screens.Add(new Screen(n, x.block, S(x.row, "flow") ?? "", Strings(x.row, "inputs"), Strings(x.row, "roles"), file));
            El(x.block ? "block" : "screen", n, "worklist", "Traditional UI: not translated by code; an agent rebuilds it from its description");
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
        if (r.Calls.Count > 0)
            r.Source = new JsonObject { ["edits"] = new JsonArray(r.Calls.Select(c => (JsonNode?)new JsonObject { ["op"] = "repoint", ["element"] = c.Path, ["action"] = c.Wrapper }).ToArray()) };

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
    internal static List<Call> FindCalls(JsonNode batch, List<(string query, string name)> logic, string[] entities, List<string> notes)
    {
        var actions = new Dictionary<string, (string entity, string op)>(StringComparer.Ordinal);
        foreach (var e in entities) foreach (var op in EntityOps) actions.TryAdd(op + e, (e, op));
        var calls = new List<Call>();
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
                if (S(node, "ref") is { } called && actions.TryGetValue(called, out var hit) && S(node, "path") is { } path)
                    calls.Add(new Call(name, path, S(node, "name") ?? "", hit.entity, hit.op));
                foreach (var c in (P(node, "children") as JsonArray)?.ToArray() ?? []) Walk(c);
            }
            Walk(tree);
        }
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
                ? $"The entities stay read only for other modules, so the old module gains {r.WrapperCreate} public wrapper server action(s) for the entity actions its copied logic calls ({string.Join(", ", r.WrapperNames)}; apply warns PublicServerAction for each, as intended), and the copied logic calls the wrappers: 2-source.json re-points {r.Calls.Count} Execute node(s) in a working copy of the old module."
                : "The entities stay read only for other modules. The copied logic writes no entity directly, so the old module gains no wrapper.");
        if (r.Calls.Count > 0) head.Add("The logic is re-pointed to the wrappers in a working copy of the old module before it is copied, so the copied calls bind to the wrapper references in the new module.");
        head.Add("oml migrate plan creates wrappers for the copied logic only. A migrated screen that writes an entity may need more: the agent adds a wrapper to the old module the same way (a public server action with the entity action's inputs and outputs), adds it to the references of the new module and calls it.");
        head.Add("Site property values, timer schedules and integration settings (REST, e-mail) set in Service Center do not follow a copy: set them again for the new module.");
        if (r.Sessions.Length > 0) head.Add($"Session variables are not copied; each needs a decision ({string.Join(", ", r.Sessions)}): a client variable only when it holds nothing sensitive, else data read on the server.");
        foreach (var p in Rows(inspect, "processes").Where(p => S(p, "launchOn") is not null))
            head.Add($"Process {S(p, "name")} launches on entity {S(p, "launchOn")}, which stays in the old module: check that the copy still launches, or start it from a server action.");
        r.Notes.InsertRange(0, head);
    }

    static List<string> Next(Result r, string old, string name, string outDir)
    {
        string Q(string s) => s.Contains(' ') ? "\"" + s + "\"" : s;
        string F(string f) => Q(Path.Combine(outDir, f));
        var next = new List<string>();
        if (r.Prepare is not null)
        {
            next.Add($"oml apply {Q(old)} {F("1-prepare-old.json")} {Q(r.Prepared)}   (a staged copy of the old module: public entities, static entities, roles{(r.WrapperCreate > 0 ? " and the wrapper actions" : "")})");
            next.Add($"Ask the person to review it, then install and publish it: oml install {Q(r.Prepared)} {Q(old)} ; oml publish {Q(old)}. The new module cannot reference what is not published.");
        }
        else next.Add("The old module needs no preparation: its entities and roles are public and no wrapper is missing.");
        if (r.Source is not null)
            next.Add($"oml apply {Q(r.Prepared)} {F("2-source.json")} {Q(r.MigrateSource)}   ({r.Calls.Count} repoint edit(s) so the logic calls the wrappers: a working copy to copy from; never install it)");
        var created = Q(Path.Combine(outDir, name + ".oml"));
        next.Add($"oml new {created} --template <a Reactive template module: see oml templates> --name {name}");
        next.Add($"oml apply {created} {F("3-new-module.json")} {Q(Path.Combine(outDir, name + ".staged.oml"))}   (references, then the copy from {(r.Source is null ? "the prepared module" : "the working copy")})");
        next.Add("Check the apply report, then ask the person to install and publish the new module.");
        next.Add($"Give {F("4-screens.md")} to an AI agent with: oml doc traditional-to-reactive");
        next.Add($"oml migrate status {Q(old)} {Q(Path.Combine(outDir, name + ".staged.oml"))}   (what is done, what is still to do, what exists twice)");
        return next;
    }

    static string ScreensMd(Result r)
    {
        var sb = new StringBuilder();
        sb.Append($"# Screens and blocks of {r.OldName} to rebuild in the new module\n\n");
        sb.Append("Read `oml doc traditional-to-reactive` first. Each item is rebuilt by hand from its description file (`oml describe` of the original); tick it when it is built and checked.\n\n");
        sb.Append($"The entities stay in {r.OldName} and are read only for the new module. A write from a migrated screen goes through a wrapper server action of {r.OldName} (public, with the inputs and outputs of the entity action), never through CreateX, UpdateX or DeleteX directly. Wrappers made for the copied logic:{(r.WrapperNames.Count > 0 ? " " + string.Join(", ", r.WrapperNames) : " none")}. If a screen needs another one, add it to {r.OldName} the same way and reference it from the new module.\n\n");
        foreach (var s in r.Screens)
            sb.Append($"- [ ] {s.Name} ({(s.Block ? "block" : "screen")}; flow {(s.Flow.Length > 0 ? s.Flow : "?")}; inputs: {(s.Inputs.Length > 0 ? string.Join(", ", s.Inputs) : "none")}; roles: {(s.Roles.Length > 0 ? string.Join(", ", s.Roles) : "none")}) - description: {s.File}\n");
        if (r.Screens.Count == 0) sb.Append("(the old module has no screens or blocks)\n");
        return sb.ToString();
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

    // ---------- selftest ----------

    const string SampleInspect = """
    { "name": "OrdersWeb", "kind": "WebResponsive",
      "entities": [
        { "name": "Customer", "public": false, "attributes": [] },
        { "name": "Order", "public": true, "attributes": [] },
        { "name": "Note", "public": false, "attributes": [] },
        { "name": "OrderStatus", "public": false, "kind": "StaticEntity", "attributes": [] } ],
      "staticEntities": [ { "name": "OrderStatus", "public": false, "records": [] } ],
      "structures": [ { "name": "OrderDto", "public": false, "attributes": [] } ],
      "serverActions": [
        { "name": "SaveOrder", "public": false, "inputs": [], "outputs": [] },
        { "name": "CleanupOrders", "public": false, "inputs": [], "outputs": [] },
        { "name": "ReadOnly", "public": false, "inputs": [], "outputs": [] },
        { "name": "Customer_Update", "public": false, "inputs": [], "outputs": [] } ],
      "serviceActions": [], "clientActions": [], "restClients": [], "restServices": [],
      "screens": [
        { "flow": "MainFlow", "name": "Orders", "public": false, "inputs": [], "roles": ["Registered"], "anonymousAccess": false },
        { "flow": "MainFlow", "name": "OrderDetail", "public": false, "inputs": ["OrderId"], "roles": ["OrderManager"], "anonymousAccess": false } ],
      "blocks": [ { "flow": "Common", "name": "OrderCard", "public": false, "inputs": ["Order"], "roles": [] } ],
      "timers": [ { "name": "NightlyCleanup", "action": "CleanupOrders", "schedule": "WhenPublished", "timeout": 20, "priority": "Normal" } ],
      "processes": [ { "name": "ReviewOrder", "launchOn": "Order", "inputs": [], "nodes": [] } ],
      "sessionVariables": [ { "name": "CurrentCustomerId", "type": "Customer Identifier" } ],
      "roles": [ { "name": "OrderManager", "public": false, "persistent": true, "actions": [] } ],
      "siteProperties": [ { "name": "MaxItems", "type": "Integer", "default": "50", "multiTenant": "No" } ],
      "exceptions": [ { "name": "InvalidOrder", "public": false } ],
      "references": [
        { "module": "Producer", "entities": [], "structures": [], "serverActions": ["GetCustomerById"], "clientActions": [], "serviceActions": [],
          "elements": [ { "name": "GetCustomerById", "kind": "IServerAction" }, { "name": "CustomerCard", "kind": "IWebBlock" } ] },
        { "module": "Missing", "elements": [ { "name": "Tax", "kind": "IServerAction" } ] },
        { "module": "System", "elements": [] } ] }
    """;

    const string SampleHeader = """
    [ { "path": "/p/Producer.oml", "info": { "Name": "Producer", "Key": "k1", "ESpaceType": "WebResponsive" } },
      { "path": "/p/Other.oml", "info": { "Name": "Other", "Key": "k2" } },
      { "path": "/p/Broken.oml", "error": "not a readable module" } ]
    """;

    // The output of `oml batch` over the tree steps of the copied logic, in the order the steps are written (SampleOrder). SaveOrder
    // calls CreateOrUpdateOrder twice; CleanupOrders calls DeleteOrder inside a loop; Customer_Update is an existing wrapper that calls
    // UpdateCustomer; ReadOnly calls a user action, UpdateCustomer and reads with an aggregate; the process calls nothing. Shapes after Inspect.Tree.
    const string SampleBatch = """
    { "results": [
      { "step": 1, "exitCode": 0, "output": { "kind": "ServerAction", "name": "CleanupOrders", "path": "CleanupOrders", "children": [
          { "kind": "ForEach", "name": "Old", "path": "CleanupOrders/Old", "children": [
            { "kind": "ExecuteServerAction", "name": "Remove", "path": "CleanupOrders/Remove", "ref": "DeleteOrder", "children": [ { "kind": "Argument", "ref": "Id" } ] } ] } ] } },
      { "step": 2, "exitCode": 0, "output": { "kind": "ServerAction", "name": "ReadOnly", "path": "ReadOnly", "children": [
          { "kind": "ExecuteServerAction", "name": "Other", "path": "ReadOnly/Other", "ref": "SaveOrder" },
          { "kind": "ExecuteServerAction", "name": "Touch", "path": "ReadOnly/Touch", "ref": "UpdateCustomer" },
          { "kind": "Aggregate", "name": "GetOrder", "path": "ReadOnly/GetOrder", "ref": "Order" } ] } },
      { "step": 3, "exitCode": 0, "output": { "kind": "Process", "name": "ReviewOrder", "path": "ReviewOrder", "children": [] } },
      { "step": 4, "exitCode": 0, "output": { "kind": "ServerAction", "name": "SaveOrder", "path": "SaveOrder", "children": [
          { "kind": "ExecuteServerAction", "name": "Save", "path": "SaveOrder/Save", "ref": "CreateOrUpdateOrder" },
          { "kind": "ExecuteServerAction", "name": "SaveAgain", "path": "SaveOrder/SaveAgain", "ref": "CreateOrUpdateOrder" } ] } },
      { "step": 5, "exitCode": 0, "output": { "kind": "ServerAction", "name": "Customer_Update", "path": "Customer_Update", "children": [
          { "kind": "ExecuteServerAction", "name": "Save", "path": "Customer_Update/Save", "ref": "UpdateCustomer" } ] } } ] }
    """;
    static readonly List<(string, string)> SampleOrder = [("ServerAction:CleanupOrders", "CleanupOrders"), ("ServerAction:ReadOnly", "ReadOnly"), ("Process:ReviewOrder", "ReviewOrder"), ("ServerAction:SaveOrder", "SaveOrder"), ("ServerAction:Customer_Update", "Customer_Update")];

    static int SelfTest(bool show)
    {
        var fails = new List<string>();
        void Check(bool ok, string what) { if (!ok) fails.Add(what); }
        var inspect = JsonNode.Parse(SampleInspect)!;
        var (map, headerNotes) = ProducerMap(JsonNode.Parse(SampleHeader)!, "OrdersWeb");
        Check(map.Count == 2 && map["Producer"] == "/p/Producer.oml" && headerNotes.Count == 1, "producer map: two modules, one note for the broken file");
        Check(ProducerMap(JsonNode.Parse("""{ "path": "/p/A.oml", "info": { "Name": "A" } }""")!, "X").map.ContainsKey("A"), "producer map: one file prints an object");
        Check(IsTraditional(inspect) && !IsTraditional(JsonNode.Parse("""{ "kind": "CrossDevice" }""")!), "traditional kind");

        var first = Build(inspect, "/p/OrdersWeb.oml", "OrdersNew", "/out", map, []);
        Check(first.Logic.Select(l => l.name).Order().SequenceEqual(["CleanupOrders", "Customer_Update", "ReadOnly", "ReviewOrder", "SaveOrder"]), "logic to read: copied actions and processes");
        var notes = new List<string>();
        var calls = FindCalls(JsonNode.Parse(SampleBatch)!, SampleOrder, first.EntityNames, notes);
        Check(calls.Count == 5 && notes.Count == 0, "calls found: Delete, two CreateOrUpdate, two Update");
        Check(calls.Any(c => c.Path == "CleanupOrders/Remove" && c.Entity == "Order" && c.Op == "Delete") && calls.Count(c => c.Op == "CreateOrUpdate" && c.Element == "SaveOrder") == 2 && calls.Count(c => c.Path.StartsWith("ReadOnly")) == 1, "calls: paths and operations; a read and a user action are not entity writes");
        Check(FindCalls(JsonNode.Parse("""{ "results": [ { "step": 1, "exitCode": 2, "output": { "error": "x" } } ] }""")!, [("A", "A")], ["Order"], notes).Count == 0 && notes.Count == 1, "a tree that failed is noted");

        var r = Build(inspect, "/p/OrdersWeb.oml", "OrdersNew", "/out", map, calls);
        string D(string kind, string name) => Rows(r.Plan, "elements").FirstOrDefault(e => S(e, "kind") == kind && S(e, "name") == name) is { } e ? S(e, "decision")! : "(absent)";
        Check(D("entity", "Customer") == "stay" && D("entity", "Order") == "stay" && D("entity", "Note") == "stay" && D("staticEntity", "OrderStatus") == "stay" && D("role", "OrderManager") == "stay", "entities, static entity and role stay");
        Check(Rows(r.Plan, "elements").Count(e => S(e, "kind") == "entity") == 3, "a static entity is not also an entity");
        Check(D("serverAction", "SaveOrder") == "copy" && D("structure", "OrderDto") == "copy" && D("exception", "InvalidOrder") == "copy" && D("siteProperty", "MaxItems") == "copy" && D("timer", "NightlyCleanup") == "copy" && D("process", "ReviewOrder") == "copy", "logic and data copy");
        Check(D("serverAction", "Customer_Update") == "stay", "an existing wrapper that is needed is not copied");
        Check(D("screen", "Orders") == "worklist" && D("block", "OrderCard") == "worklist" && D("sessionVariable", "CurrentCustomerId") == "manual", "screens and blocks go to the worklist, a session variable is manual");
        Check(r.Counts["worklist"] == 3 && r.Counts["manual"] == 1, "counts");
        var w = Rows(r.Plan, "wrappers");
        Check(w.Length == 3 && r.WrapperCreate == 2 && r.WrapperExists == 1, "wrappers: Order_CreateOrUpdate and Order_Delete created, Customer_Update exists; Note has none");
        Check(w.Any(x => S(x, "name") == "Customer_Update" && S(x, "decision") == "exists") && w.Any(x => S(x, "name") == "Order_Delete" && S(x, "decision") == "create" && S(x, "entityAction") == "DeleteOrder") && w.All(x => S(x, "kind") == "wrapper"), "plan wrappers");
        var rf = Rows(r.Plan, "references");
        string RD(string m) => rf.First(x => S(x, "module") == m) is { } x ? S(x, "decision")! : "";
        Check(RD("Producer") == "add" && RD("Missing") == "unresolved" && RD("System") == "builtin", "reference decisions");
        Check(Strings(rf.First(x => S(x, "module") == "Producer"), "elements").SequenceEqual(["GetCustomerById"]) && Strings(rf.First(x => S(x, "module") == "Producer"), "skippedUi").SequenceEqual(["CustomerCard"]), "the web block is left out of the producer's elements");
        Check(r.Notes.Any(n => n.Contains("Missing") && n.Contains("--in")) && r.Notes.Any(n => n.Contains("CurrentCustomerId")) && r.Notes.Any(n => n.Contains("NightlyCleanup") && n.Contains("both modules")) && r.Notes.Any(n => n.Contains("service module")) && r.Notes.Any(n => n.Contains("Customer_Update") && n.Contains("already exist")) && r.Notes.Any(n => n.Contains("wrappers for the copied logic only")) && r.Notes.Any(n => n.Contains("read only") && n.Contains("gains 2 public wrapper")), "notes");

        var prep = r.Prepare;
        Check(prep is not null && Names(prep, "entities").SequenceEqual(["Customer", "Note"]) && Names(prep, "staticEntities").SequenceEqual(["OrderStatus"]) && Names(prep, "roles").SequenceEqual(["OrderManager"]), "prepare: the private entities, static entity and role are made public");
        Check(prep is not null && !prep.ToJsonString().Contains("exposeReadOnly"), "prepare never sets exposeReadOnly");
        var acts = Rows(prep, "serverActions");
        Check(acts.Length == 3 && acts.Count(a => S(a, "name") == "Customer_Update" && Rows(a, "flow").Length == 0 && B(a, "public") == true) == 1 && Names(prep, "serverActions").Contains("Order_CreateOrUpdate") && Names(prep, "serverActions").Contains("Order_Delete"), "prepare: two new wrappers and the private existing one made public");
        var cu = acts.First(a => S(a, "name") == "Order_CreateOrUpdate");
        Check(B(cu, "public") == true && Rows(cu, "inputs")[0]["name"]!.ToString() == "Source" && Rows(cu, "inputs")[0]["type"]!.ToString() == "Order" && Rows(cu, "outputs")[0]["name"]!.ToString() == "Id" && Rows(cu, "outputs")[0]["type"]!.ToString() == "Order Identifier" && Rows(cu, "flow")[0]["action"]!.ToString() == "CreateOrUpdateOrder" && Rows(cu, "flow")[0]["args"]!["Source"]!.ToString() == "Source", "wrapper Order_CreateOrUpdate has the signature of the entity action");
        var del = acts.First(a => S(a, "name") == "Order_Delete");
        Check(Rows(del, "inputs")[0]["name"]!.ToString() == "Id" && Rows(del, "inputs")[0]["type"]!.ToString() == "Order Identifier" && Rows(del, "outputs").Length == 0 && Rows(del, "flow")[0]["action"]!.ToString() == "DeleteOrder", "wrapper Order_Delete");
        Check(r.Prepared == "/out/OrdersWeb.prepared.oml", "prepared file");

        var refs = Rows(r.NewModule, "references");
        Check(refs.Length == 2 && S(refs[0], "producer") == r.Prepared && S(refs[1], "producer") == Path.GetFullPath("/p/Producer.oml"), "new module: the prepared old module first, then the producer");
        var fromOld = Strings(refs[0], "elements");
        Check(fromOld.Order().SequenceEqual(new[] { "Customer", "Order", "Note", "OrderStatus", "OrderManager", "Customer_Update", "Order_CreateOrUpdate", "Order_Delete" }.Order()), "new module: entities, static entity, role and the three wrappers from the old module");
        var copied = Strings(Rows(r.NewModule, "copy")[0], "elements");
        Check(copied.Order().SequenceEqual(new[] { "OrderDto", "InvalidOrder", "MaxItems", "SaveOrder", "CleanupOrders", "ReadOnly", "NightlyCleanup", "ReviewOrder" }.Order()), "new module: copy lists the copied elements only");
        var edits = Rows(r.Source, "edits");
        Check(edits.Length == 4 && edits.All(e => S(e, "op") == "repoint") && r.Source!.Select(kv => kv.Key).SequenceEqual(["edits"]), "2-source.json: only edits, one repoint per call (the wrapper's own call is left alone)");
        Check(edits.Any(e => S(e, "element") == "CleanupOrders/Remove" && S(e, "action") == "Order_Delete") && edits.Any(e => S(e, "element") == "SaveOrder/Save" && S(e, "action") == "Order_CreateOrUpdate") && edits.Any(e => S(e, "element") == "SaveOrder/SaveAgain") && edits.All(e => !S(e, "action")!.Contains('.')), "repoint edits: node path and the plain wrapper name");
        Check(r.MigrateSource == "/out/OrdersWeb.migrate-source.oml" && S(Rows(r.NewModule, "copy")[0], "from") == r.MigrateSource && S(r.Plan, "migrateSource") == r.MigrateSource, "the copy is from the working copy, named in the plan");
        Check(r.NewModule.Select(kv => kv.Key).SequenceEqual(["references", "copy"]), "3-new-module.json: references, then copy, no edits");
        Check(r.Screens.Count == 3 && r.Screens.Any(s => s.Name == "OrderDetail" && s.Inputs.SequenceEqual(["OrderId"]) && s.File == "screens/OrderDetail.txt"), "screen worklist rows");
        var md = ScreensMd(r);
        Check(md.Contains("- [ ] OrderDetail (screen; flow MainFlow; inputs: OrderId; roles: OrderManager) - description: screens/OrderDetail.txt") && md.Contains("- [ ] OrderCard (block;") && md.Contains("Order_CreateOrUpdate") && md.Contains("wrapper"), "4-screens.md lists the wrappers and the screens");
        var next = Next(r, "/p/OrdersWeb.oml", "OrdersNew", "/out");
        Check(next.Count == 8 && next[2].Contains("/out/OrdersWeb.prepared.oml") && next[2].Contains("2-source.json") && next[2].Contains("never install it") && next[4].Contains("3-new-module.json") && next[6].Contains("4-screens.md"), "next lists eight steps, the working copy third");

        // No write at all: no wrapper, no edits.
        var none = Build(inspect, "/p/OrdersWeb.oml", "OrdersNew", "/out", map, []);
        Check(none.WrapperNames.Count == 0 && none.Source is null && S(Rows(none.NewModule, "copy")[0], "from") == none.Prepared && Rows(none.Prepare, "serverActions").Length == 0 && none.Notes.Any(n => n.Contains("gains no wrapper")), "no entity write: no wrapper, no 2-source.json, the copy is from the old module");

        var newer = JsonNode.Parse("""
        { "name": "OrdersNew", "kind": "CrossDevice", "screens": [ { "name": "Orders" } ], "blocks": [], "structures": [ { "name": "OrderDto" } ],
          "serverActions": [ { "name": "SaveOrder" } ], "timers": [ { "name": "NightlyCleanup" } ], "processes": [], "roles": [],
          "references": [ { "module": "OrdersWeb", "elements": [ { "name": "OrderManager", "kind": "IRole" } ] } ] }
        """)!;
        var s = Compare(inspect, newer);
        Check(Strings(s["screens"], "both").SequenceEqual(["Orders"]) && Strings(s["screens"], "onlyOld").SequenceEqual(["OrderDetail"]), "status: screens");
        Check(Strings(s["serverActions"], "onlyOld").Order().SequenceEqual(["CleanupOrders", "ReadOnly"]) && Strings(s["serverActions"], "both").SequenceEqual(["SaveOrder"]), "status: server actions leave out the wrappers");
        Check(Strings(s["duplicates"], "timers").SequenceEqual(["NightlyCleanup"]) && Strings(s["roles"], "both").SequenceEqual(["OrderManager"]), "status: duplicates and referenced role");
        Check(Strings(s["wrappers"], "presentInOld").SequenceEqual(["Customer_Update"]) && S(s, "progress")!.StartsWith("screens 1 of 2; blocks 0 of 1;"), "status: wrappers and progress");

        if (show)
        {
            Console.WriteLine("--- 1-prepare-old.json\n" + r.Prepare!.ToJsonString(Indented));
            Console.WriteLine("--- 2-source.json\n" + r.Source!.ToJsonString(Indented));
            Console.WriteLine("--- 3-new-module.json\n" + r.NewModule.ToJsonString(Indented));
            Console.WriteLine("--- plan.json\n" + r.Plan.ToJsonString(Indented));
            Console.WriteLine("--- 4-screens.md\n" + md);
            Console.WriteLine("--- next\n" + string.Join("\n", next));
            Console.WriteLine("--- status\n" + s.ToJsonString(Indented));
            Console.WriteLine("--- notes\n" + string.Join("\n", r.Notes));
        }
        if (fails.Count == 0) { Console.WriteLine("ok"); return 0; }
        foreach (var f in fails) Console.WriteLine("FAIL " + f);
        return 1;
    }
}
