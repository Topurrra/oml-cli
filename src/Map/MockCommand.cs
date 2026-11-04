using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// `oml mock <Api.Method> | --api <Api> | --all [--personas <file.json>] [--out <folder>] [--project <name>]`: mock
// responses for consumed REST APIs, to paste into a mock server such as Castlemock. Built from the application map
// (oml map build), so the mocks agree with each other: values the map links (an API output saved in an entity and read
// into the next API's input, through wrappers and processes) form one group, and every persona has one value per group.
// CustomerId C-1003 returned by GetClient is then the CustomerId that selects GetCreditLines' answer for the same
// persona. The shape follows the method's sample response when the module holds one, else its structures. Same input,
// same output: values come from a hash, not from a random generator. No network, no model.
static class MockCommand
{
    sealed class MockException(string message, int code = 2, string? hint = null) : Exception(message)
    {
        public int Code { get; } = code;
        public string? Hint { get; } = hint;
    }

    sealed record Persona(string Name, int Items, int Status, Dictionary<string, string> Values, string Purpose);

    static readonly Persona[] Defaults =
    [
        new("normal", 2, 200, [], "a usual answer, two items in every list"),
        new("empty", 0, 200, [], "empty lists: nothing found for this customer"),
        new("many", 5, 200, [], "five items in every list"),
        new("not-found", 0, 404, [], "the API answers 404"),
        new("error", 0, 500, [], "the API fails with 500"),
    ];

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h" or "help") { Console.WriteLine(CliUsage.For("mock")); return args.Length == 0 ? 2 : 0; }
            string? method = null, api = null, project = null, personasFile = null, outDir = null;
            var all = false;
            for (var i = 0; i < args.Length; i++)
            {
                string Value() => i + 1 < args.Length ? args[++i] : throw new MockException($"{args[i]} needs a value.");
                switch (args[i])
                {
                    case "--all": all = true; break;
                    case "--api": api = Value(); break;
                    case "--project": project = Value().ToLowerInvariant(); break;
                    case "--personas": personasFile = Path.GetFullPath(Value()); break;
                    case "--out": outDir = Path.GetFullPath(Value()); break;
                    default:
                        if (args[i].StartsWith("--")) throw new MockException($"Unknown option {args[i]}.", 2, "Run 'oml mock --help'.");
                        if (method is not null) throw new MockException("Give one Api.Method, or --api <Api>, or --all.");
                        method = args[i]; break;
                }
            }
            if ((method is null ? 0 : 1) + (api is null ? 0 : 1) + (all ? 1 : 0) != 1) throw new MockException("Give one Api.Method, or --api <Api>, or --all.", 2, "Usage: oml mock <Api.Method> | --api <Api> | --all [--personas <file.json>] [--out <folder>]");
            var map = MapCommand.LoadFacts(project);
            var personas = personasFile is null ? Defaults.ToList() : ReadPersonas(personasFile);
            return Generate(map, method, api, personas, outDir ?? Path.GetFullPath("mocks"), personasFile);
        }
        catch (MockException e)
        {
            TerminalOutput.PrintObject(new { error = e.Message, hint = e.Hint });
            return e.Code;
        }
        catch (MapCommand.MapLoadException e)
        {
            TerminalOutput.PrintObject(new { error = e.Message, hint = e.Hint });
            return e.Code;
        }
    }

    // personas.json: [{ "name": "normal", "items": 2, "status": 200, "purpose": "...", "values": { "Customer.ExternalId": "C-1001" } }]
    // A value key is a group name as oml mock prints it (an entity attribute such as Customer.ExternalId, or Api.Method.in:Param)
    // or a plain field name (FullName) that sets that field in every response of the persona.
    static List<Persona> ReadPersonas(string file)
    {
        if (!File.Exists(file)) throw new MockException($"Personas file not found: {file}");
        JsonArray list;
        try { list = JsonNode.Parse(File.ReadAllText(file)) as JsonArray ?? throw new MockException("The personas file must be a JSON array of personas."); }
        catch (JsonException e) { throw new MockException("The personas file is not valid JSON: " + e.Message); }
        var personas = new List<Persona>();
        foreach (var p in list.OfType<JsonObject>())
        {
            var name = p["name"]?.GetValue<string>() ?? throw new MockException("Every persona needs a name.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]{0,39}$")) throw new MockException($"Persona name '{name}': use lower-case letters, digits and hyphens.");
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in p["values"] as JsonObject ?? []) values[k] = v?.ToString() ?? "";
            if (MemorySecrets.Find(string.Join(" ", values.Values)) is { } secret) throw new MockException($"Persona '{name}' holds what looks like a {secret}. Mock data is plain text: use test values.");
            personas.Add(new Persona(name, p["items"]?.GetValue<int>() ?? 2, p["status"]?.GetValue<int>() ?? 200, values, p["purpose"]?.GetValue<string>() ?? ""));
        }
        if (personas.Count == 0) throw new MockException("The personas file has no persona.");
        if (personas.Select(p => p.Name).Distinct().Count() != personas.Count) throw new MockException("Two personas have the same name.");
        return personas;
    }

    // ---- generation ----

    sealed record Method(string Module, string Api, JsonObject Def, JsonObject ApiDef, Dictionary<string, JsonObject> Structures)
    {
        public string Name => Def["name"]!.GetValue<string>();
        public string Key => $"api:{Api}.{Name}";
    }

    static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    static int Generate(MapCommand.Facts map, string? one, string? apiName, List<Persona> personas, string outDir, string? personasFile)
    {
        var methods = new List<Method>();
        foreach (var f in map.Modules)
        {
            var own = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in (f["structures"] as JsonArray)?.OfType<JsonObject>() ?? []) own[S(s["name"])] = s;
            foreach (var a in (f["restConsumed"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                var structures = new Dictionary<string, JsonObject>(own, StringComparer.OrdinalIgnoreCase);
                foreach (var s in (a["structures"] as JsonArray)?.OfType<JsonObject>() ?? []) structures[S(s["name"])] = s;
                foreach (var m in (a["methods"] as JsonArray)?.OfType<JsonObject>() ?? [])
                    methods.Add(new Method(S(f["name"]), S(a["name"]), m, a, structures));
            }
        }
        if (one is not null)
        {
            var dot = one.IndexOf('.');
            var (a, m) = dot > 0 ? (one[..dot], one[(dot + 1)..]) : ((string?)null, one);
            methods = methods.Where(x => string.Equals(x.Name, m, StringComparison.OrdinalIgnoreCase) && (a is null || string.Equals(x.Api, a, StringComparison.OrdinalIgnoreCase))).ToList();
            if (methods.Select(x => x.Api).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1) throw new MockException($"'{one}' is a method of several APIs. Write it as Api.Method.");
        }
        else if (apiName is not null) methods = methods.Where(x => string.Equals(x.Api, apiName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (methods.Count == 0) throw new MockException(one is not null ? $"No consumed REST method '{one}' in the map." : apiName is not null ? $"No consumed REST API '{apiName}' in the map." : "The map has no consumed REST API.", 1, "List them: oml map overview");

        // Groups of linked values: an API output, the entity attribute it fills and the API input it feeds share one value.
        var links = map.Links;
        var group = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Find(string x) { while (group.TryGetValue(x, out var p) && !string.Equals(p, x, StringComparison.OrdinalIgnoreCase)) x = p; return x; }
        void Union(string a, string b) { var (ra, rb) = (Find(a), Find(b)); group[ra] = ra; group[rb] = rb; if (!string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase)) group[rb] = ra; }
        foreach (var l in links) Union(l.From, l.To);
        // A group's name: its entity attribute when it has one (Customer.ExternalId), else its first API input.
        var members = group.Keys.GroupBy(Find, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        string GroupName(string root) => members[root].Where(x => x.StartsWith("entity:")).Select(x => x["entity:".Length..]).Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            ?? members[root].Where(x => x.Contains(".in:")).Select(x => x["api:".Length..]).Order(StringComparer.OrdinalIgnoreCase).First();
        string? GroupOf(string endpoint) => group.ContainsKey(endpoint) ? GroupName(Find(endpoint)) : null;

        // One value per group and persona: the persona's own value when it gives one, else a synthetic id shared by the chain.
        var personaIndex = personas.Select((p, i) => (p.Name, i)).ToDictionary(x => x.Name, x => x.i);
        // An id-like field gets a code (EI-1001); any other field reads like "Name 1001".
        string Linked(string groupName, Persona p)
        {
            if (p.Values.TryGetValue(groupName, out var v)) return v;
            var leaf = groupName.Split('.', ':')[^1];
            var n = 1001 + personaIndex[p.Name];
            return System.Text.RegularExpressions.Regex.IsMatch(leaf, "(Id|ID|Code|Number|Key|Ref)$") ? $"{Prefix(groupName)}-{n}" : $"{leaf} {n}";
        }

        var written = new List<object>();
        var groupsUsed = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(outDir);
        foreach (var api in methods.GroupBy(m => (m.Module, m.Api)))
        {
            var checklist = new StringBuilder();
            checklist.Append($"# {api.Key.Api} mocks\n\nFrom module {api.Key.Module}, base URL in the module: {S(api.First().ApiDef["baseUrl"])}\n");
            checklist.Append("Point the consumed API at the mock in Service Center (the API's effective URL); the module does not change.\n");
            foreach (var m in api.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var folder = Path.Combine(outDir, Safe(m.Api), Safe(m.Name));
                Directory.CreateDirectory(folder);
                var inputs = (m.Def["inputs"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
                var verb = S(m.Def["verb"]).ToUpperInvariant();
                checklist.Append($"\n## {verb} {S(m.Def["url"])}  ({m.Name})\n\n| Persona | Status | Match the request on | Response file |\n|---|---|---|---|\n");
                foreach (var p in personas)
                {
                    // The request values that select this persona's answer: inputs linked to a group.
                    var match = new List<string>();
                    // An input the map links to nothing still gets one value per persona, so each persona has its own request.
                    foreach (var input in inputs.Where(i => S(i["in"]) is "path" or "query"))
                        if ((GroupOf($"{m.Key}.in:{S(input["name"])}") ?? $"{m.Api}.{m.Name}.{S(input["name"])}") is { } g)
                        {
                            var value = Linked(g, p);
                            var wireName = S(input["wire"]) is { Length: > 0 } w ? w : S(input["name"]);
                            match.Add($"{wireName}={value} ({S(input["in"])})");
                            (groupsUsed.TryGetValue(g, out var l) ? l : groupsUsed[g] = []).Add($"{m.Api}.{m.Name} in:{S(input["name"])}");
                        }
                    JsonNode? body = p.Status >= 400 ? new JsonObject { ["error"] = p.Status == 404 ? "Not found" : "Internal server error", ["status"] = p.Status }
                        : Body(m, p, path =>
                        {
                            if (GroupOf($"{m.Key}.out:{path}") is not { } g) return null;
                            (groupsUsed.TryGetValue(g, out var l) ? l : groupsUsed[g] = []).Add($"{m.Api}.{m.Name} out:{path}");
                            return Linked(g, p);
                        }, groupsUsed);
                    var file = Path.Combine(folder, p.Name + ".json");
                    var text = body?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "";
                    if (MemorySecrets.Find(text) is { } secret) throw new MockException($"The mock for {m.Api}.{m.Name} ({p.Name}) holds what looks like a {secret}; it comes from the module's sample response. Nothing was written for it.", 1);
                    File.WriteAllText(file, text + "\n");
                    checklist.Append($"| {p.Name} | {p.Status} | {(match.Count == 0 ? "(any request)" : string.Join(", ", match))} | `{Path.GetRelativePath(outDir, file).Replace('\\', '/')}` |\n");
                    written.Add(new { api = m.Api, method = m.Name, persona = p.Name, status = p.Status, file, match = match.Count == 0 ? null : match });
                }
                var sample = m.Def["sampleRequest"] is JsonValue sr && sr.TryGetValue<string>(out var req) ? req : null;
                if (sample is not null) checklist.Append($"\nExample request body (from the module): `{Clip(sample)}`\n");
            }
            File.WriteAllText(Path.Combine(outDir, Safe(api.Key.Api), "checklist.md"), checklist.ToString());
        }
        var personasOut = personas.Select(p => new { name = p.Name, items = p.Items, status = p.Status, purpose = p.Purpose.Length > 0 ? p.Purpose : null }).ToArray();
        File.WriteAllText(Path.Combine(outDir, "personas.json"), JsonSerializer.Serialize(personas.Select(p => new
        {
            name = p.Name, items = p.Items, status = p.Status, purpose = p.Purpose,
            values = groupsUsed.Keys.ToDictionary(g => g, g => Linked(g, p)),
        }), new JsonSerializerOptions { WriteIndented = true }) + "\n");
        TerminalOutput.PrintObject(new
        {
            project = map.Project, folder = outDir, methods = methods.Count, personas = personasOut, files = written.Count,
            linked = groupsUsed.Select(g => new { group = g.Key, usedBy = g.Value.Distinct().ToArray() }).ToArray(),
            personasFile = personasFile ?? Path.Combine(outDir, "personas.json"),
            hint = personasFile is null ? $"Linked values are synthetic. To use real test data, copy {Path.Combine(outDir, "personas.json")}, put the dev test values in it (ask the user), and run again with --personas <file>." : null,
        });
        return 0;
    }

    static string Clip(string s) { s = s.Replace("\r", "").Replace("\n", " "); return s.Length <= 300 ? s : s[..297] + "..."; }
    static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
    static string Prefix(string group)
    {
        var leaf = group.Split('.', ':')[^1];
        var letters = new string(leaf.Where(char.IsUpper).ToArray());
        return letters.Length > 0 ? letters : leaf[..Math.Min(3, leaf.Length)].ToUpperInvariant();
    }

    // The response body: the output in the body. With a sample response, its shape (wire names, fields the module does
    // not model); else the output's type from the module's structures.
    static JsonNode? Body(Method m, Persona p, Func<string, string?> linked, SortedDictionary<string, List<string>> used)
    {
        var output = (m.Def["outputs"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(o => S(o["in"]) == "body");
        if (output is null) return null;
        var outName = S(output["name"]);
        var type = S(output["type"]);
        // Model path -> wire path, so a linked value lands on the right sample field.
        var wire = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // salt: the item number in a list, so two items differ (the path, which links use, stays the same).
        JsonNode? Typed(string t, string path, int depth, string salt = "")
        {
            if (depth > 6) return null;
            if (t.StartsWith("List of ", StringComparison.OrdinalIgnoreCase))
                return new JsonArray(Enumerable.Range(0, p.Items).Select(i => Typed(t[8..], path, depth + 1, salt + "#" + i)).ToArray());
            if (m.Structures.TryGetValue(t, out var s))
            {
                var o = new JsonObject();
                foreach (var a in (s["attributes"] as JsonArray)?.OfType<JsonObject>() ?? [])
                {
                    var name = S(a["name"]);
                    var json = S(a["json"]) is { Length: > 0 } j ? j : name;
                    var sub = path + "." + name;
                    wire[sub] = json;
                    o[json] = linked(sub) is { } value ? value : Typed(S(a["type"]), sub, depth + 1, salt);
                }
                return o;
            }
            return Leaf(t, path, salt, p, m);
        }
        if (m.Def["sampleResponse"] is JsonValue sv && sv.TryGetValue<string>(out var sampleText))
        {
            try
            {
                var sample = JsonNode.Parse(sampleText);
                _ = Typed(type, outName, 0); // fills the wire names of the linked paths
                return Reshape(sample, outName, "", p, m, linked, wire);
            }
            catch (JsonException) { /* a sample that is not JSON: use the structures */ }
        }
        return Typed(type, outName, 0);
    }

    // The sample's shape with new leaf values: a linked field gets the group's value, lists get the persona's item count.
    static JsonNode? Reshape(JsonNode? node, string modelRoot, string wirePath, Persona p, Method m, Func<string, string?> linked, Dictionary<string, string> wire)
    {
        string? ModelPath(string w) => w.Length == 0 ? modelRoot : wire.FirstOrDefault(kv => string.Equals(WirePathOf(kv.Key), w, StringComparison.OrdinalIgnoreCase)).Key;
        string WirePathOf(string model)
        {
            var parts = model.Split('.').Skip(1).ToArray();
            var acc = modelRoot;
            var result = new List<string>();
            foreach (var part in parts) { acc += "." + part; result.Add(wire.GetValueOrDefault(acc, part)); }
            return string.Join(".", result);
        }
        switch (node)
        {
            case JsonObject o:
            {
                var copy = new JsonObject();
                foreach (var (k, v) in o)
                {
                    var w = wirePath.Length == 0 ? k : wirePath + "." + k;
                    var model = ModelPath(w);
                    copy[k] = model is not null && linked(model) is { } value ? value : Reshape(v, modelRoot, w, p, m, linked, wire);
                }
                return copy;
            }
            case JsonArray a:
                return a.Count == 0 ? new JsonArray() : new JsonArray(Enumerable.Range(0, p.Items).Select(i => Reshape(a[0], modelRoot, wirePath, p with { Name = p.Name + "#" + i }, m, linked, wire)).ToArray());
            case JsonValue v:
                return v.GetValueKind() switch
                {
                    JsonValueKind.Number => JsonValue.Create(Math.Round(Number(p, m, wirePath) * 100) / 100),
                    JsonValueKind.True or JsonValueKind.False => JsonValue.Create(Hash(p, m, wirePath) % 2 == 0),
                    JsonValueKind.String => JsonValue.Create(p.Values.TryGetValue(Field(wirePath), out var given) ? given : Text(v.GetValue<string>(), wirePath, p, m)),
                    _ => v.DeepClone(),
                };
            default: return null;
        }
    }

    static long Hash(Persona p, Method m, string path) => BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes($"{m.Key}|{p.Name}|{path}")), 0) & long.MaxValue;
    static double Number(Persona p, Method m, string path) => 1 + Hash(p, m, path) % 99_999 / 10.0;

    // A string like the sample's: a date stays a date, a number in text stays a number, else words from the field name.
    static string Text(string sample, string path, Persona p, Method m)
    {
        var h = Hash(p, m, path);
        if (DateTime.TryParse(sample, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
            return new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(h % 300).AddMinutes(h % 1440).ToString(sample.Length <= 10 ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        if (sample.Length > 0 && sample.All(char.IsDigit)) return (h % (long)Math.Pow(10, Math.Min(sample.Length, 12))).ToString(CultureInfo.InvariantCulture).PadLeft(sample.Length, '0');
        return Field(path) + " " + (1 + h % 999);
    }

    static string Field(string path) => path.Split('.', ':')[^1] is { Length: > 0 } leaf ? leaf : "Value";

    static JsonNode? Leaf(string type, string path, string salt, Persona p, Method m)
    {
        if (p.Values.TryGetValue(Field(path), out var given)) return given;
        var h = Hash(p, m, path + salt);
        return type.ToLowerInvariant() switch
        {
            "integer" or "long integer" => JsonValue.Create(1 + h % 9_999),
            "decimal" or "currency" => JsonValue.Create(Math.Round(1 + h % 99_999 / 10.0, 2)),
            "boolean" => JsonValue.Create(h % 2 == 0),
            "date" => JsonValue.Create(new DateTime(2026, 1, 1).AddDays(h % 300).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            "date time" => JsonValue.Create(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(h % 300).AddMinutes(h % 1440).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
            "time" => JsonValue.Create(TimeSpan.FromMinutes(h % 1440).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)),
            "email" => JsonValue.Create($"user{1 + h % 999}@example.com"),
            "phone number" => JsonValue.Create($"+1555{h % 10_000_000:D7}"),
            _ when type.EndsWith(" Identifier", StringComparison.OrdinalIgnoreCase) => JsonValue.Create(1 + h % 9_999),
            _ => JsonValue.Create($"{Field(path)} {1 + h % 999}"),
        };
    }
}
