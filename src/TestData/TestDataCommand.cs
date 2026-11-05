using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// `oml test data allow|create|check|cleanup|status`: test data in a DEV database, through the test-data module that
// `oml test data module` builds, the way BDD tests work: create the data, check what must be checked, clean up exactly
// what was created. An agent calls the API only through these commands (the hook refuses curl and the like to an allowed
// host), so every value it reports comes from oml's output. The API's URL and key come from OML_TESTDATA_URL and
// OML_TESTDATA_KEY; the host must be one the user allowed for this project (.oml/testdata.json). Each run keeps a local
// copy of what it created (.oml/testdata/runs/<run>.json), so a cleanup after a crash still knows what to remove.
static class TestDataCommand
{
    sealed class TestDataException(string message, int code = 2, string? hint = null) : Exception(message)
    {
        public int Code { get; } = code;
        public string? Hint { get; } = hint;
    }

    static string Folder => Path.Combine(Environment.CurrentDirectory, ".oml");
    static string ConfigFile => Path.Combine(Folder, "testdata.json");
    static string RunsFolder => Path.Combine(Folder, "testdata", "runs");
    static string CurrentFile => Path.Combine(Folder, "testdata", "current");
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h") { Console.WriteLine(CliUsage.For("test")); return args.Length == 0 ? 2 : 0; }
            var rest = args[1..];
            return args[0] switch
            {
                "allow" => Allow(rest),
                "create" => Create(rest),
                "check" => Check(rest),
                "cleanup" => Cleanup(rest),
                "status" => Status(),
                _ => throw new TestDataException($"Unknown test data command '{args[0]}'.", 2, "Use module, allow, create, check, cleanup or status."),
            };
        }
        catch (TestDataException e)
        {
            TerminalOutput.PrintObject(new { error = e.Message, hint = e.Hint });
            return e.Code;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            TerminalOutput.PrintObject(new { error = "The test-data API did not answer: " + e.Message, hint = "Check OML_TESTDATA_URL, the network, and that the module is published in that environment." });
            return 1;
        }
    }

    // ---- configuration ----

    internal static List<string> AllowedHosts(string folder)
    {
        var file = Path.Combine(folder, ".oml", "testdata.json");
        try { return File.Exists(file) ? (JsonNode.Parse(File.ReadAllText(file))?["allowedHosts"] as JsonArray)?.Select(h => h?.GetValue<string>() ?? "").Where(h => h.Length > 0).ToList() ?? [] : []; }
        catch (Exception e) when (e is JsonException or InvalidOperationException or IOException) { return []; }
    }

    static int Allow(string[] args)
    {
        if (args.Length != 1 || args[0].StartsWith("--")) throw new TestDataException("Usage: oml test data allow <dev host>", 2);
        var host = args[0].Trim().ToLowerInvariant();
        if (Uri.TryCreate(host, UriKind.Absolute, out var u)) host = u.Host.ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(host, @"^[a-z0-9.-]+$")) throw new TestDataException($"'{args[0]}' is not a host name.", 2);
        var hosts = AllowedHosts(Environment.CurrentDirectory);
        if (!hosts.Contains(host)) hosts.Add(host);
        Directory.CreateDirectory(Folder);
        File.WriteAllText(ConfigFile, new JsonObject { ["allowedHosts"] = new JsonArray(hosts.Select(h => (JsonNode)JsonValue.Create(h)!).ToArray()) }.ToJsonString(Indented) + "\n");
        TerminalOutput.PrintObject(new { allowed = hosts, file = ConfigFile, notice = "Only DEV environments. Test data is created and deleted there." });
        return 0;
    }

    static (Uri Base, string Key) Api()
    {
        var url = Environment.GetEnvironmentVariable("OML_TESTDATA_URL");
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
            throw new TestDataException("Set OML_TESTDATA_URL to the test-data API, such as https://<dev>/<Module>TestData/rest/TestDataApi.", 2, "Build the module first: oml test data module <producer.oml> --entity ...");
        var local = baseUri.Host is "localhost" or "127.0.0.1" or "[::1]";
        if (baseUri.Scheme != "https" && !local) throw new TestDataException("OML_TESTDATA_URL must use https (http only for localhost).", 2);
        if (!AllowedHosts(Environment.CurrentDirectory).Contains(baseUri.Host.ToLowerInvariant()))
            throw new TestDataException($"{baseUri.Host} is not an allowed test-data host for this project.", 2, $"Ask the user whether {baseUri.Host} is a DEV environment. If yes: oml test data allow {baseUri.Host}");
        var key = Environment.GetEnvironmentVariable("OML_TESTDATA_KEY");
        if (string.IsNullOrEmpty(key)) throw new TestDataException("Set OML_TESTDATA_KEY to the module's TestDataKey site property value.", 2);
        return (baseUri, key);
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    static async Task<(int Status, JsonNode? Body, string Text)> Call(HttpMethod method, Uri uri, string key, JsonNode? body = null)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-Test-Data-Key", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonNode? json = null;
        try { json = text.Trim().Length == 0 ? null : JsonNode.Parse(text); } catch (JsonException) { }
        return ((int)response.StatusCode, json, text);
    }

    // An OutSystems REST error: {"Errors": ["..."], "StatusCode": 500}.
    static string Problem(int status, JsonNode? body, string text) =>
        $"HTTP {status}: " + ((body?["Errors"] as JsonArray)?.FirstOrDefault()?.ToString() ?? (text.Length > 300 ? text[..300] : text));

    // ---- runs ----

    sealed class RunFile
    {
        public string Run = "";
        public string Started = "";
        public string? Cleaned;
        public List<(string Entity, long Id)> Records = [];

        public JsonObject Json() => new()
        {
            ["run"] = Run, ["started"] = Started, ["cleaned"] = Cleaned,
            ["records"] = new JsonArray(Records.Select(r => (JsonNode)new JsonObject { ["entity"] = r.Entity, ["id"] = r.Id }).ToArray()),
        };

        public static RunFile Read(string path)
        {
            var o = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            return new RunFile
            {
                Run = o["run"]!.GetValue<string>(), Started = o["started"]?.GetValue<string>() ?? "", Cleaned = o["cleaned"]?.GetValue<string>(),
                Records = (o["records"] as JsonArray ?? []).OfType<JsonObject>().Select(r => (r["entity"]!.GetValue<string>(), r["id"]!.GetValue<long>())).ToList(),
            };
        }
    }

    static string RunPath(string run) => Path.Combine(RunsFolder, run + ".json");
    static void Save(RunFile r) { Directory.CreateDirectory(RunsFolder); File.WriteAllText(RunPath(r.Run), r.Json().ToJsonString(Indented) + "\n"); }

    static string? Option(ref string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        if (i < 0) return null;
        if (i + 1 >= args.Length) throw new TestDataException($"{name} needs a value.", 2);
        var value = args[i + 1];
        args = [.. args[..i], .. args[(i + 2)..]];
        return value;
    }

    // The run to use: --run, else the current open run, else a new one.
    static RunFile CurrentRun(string? asked, bool create)
    {
        if (asked is not null)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(asked, "^[A-Za-z0-9-]{1,50}$")) throw new TestDataException("A run id is letters, digits and hyphens (at most 50).", 2);
            return File.Exists(RunPath(asked)) ? RunFile.Read(RunPath(asked)) : create ? new RunFile { Run = asked, Started = DateTime.UtcNow.ToString("O") } : throw new TestDataException($"No run {asked} in this project.", 1, "List them: oml test data status");
        }
        if (File.Exists(CurrentFile) && File.ReadAllText(CurrentFile).Trim() is { Length: > 0 } current && File.Exists(RunPath(current)) && RunFile.Read(RunPath(current)) is { Cleaned: null } open)
            return open;
        if (!create) throw new TestDataException("No open run.", 1, "oml test data status lists the runs.");
        var run = new RunFile { Run = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..4], Started = DateTime.UtcNow.ToString("O") };
        Directory.CreateDirectory(Path.GetDirectoryName(CurrentFile)!);
        File.WriteAllText(CurrentFile, run.Run);
        return run;
    }

    static JsonObject Values(ref string[] args)
    {
        var text = Option(ref args, "--values");
        var file = Option(ref args, "--file");
        if ((text is null) == (file is null)) throw new TestDataException("Give the record with --values '<json>' or --file <record.json>.", 2);
        text ??= File.Exists(file!) ? File.ReadAllText(file!) : throw new TestDataException($"File not found: {file}", 2);
        try { return JsonNode.Parse(text) as JsonObject ?? throw new TestDataException("The record must be a JSON object such as {\"Name\": \"Ana\"}.", 2); }
        catch (JsonException e) { throw new TestDataException("The record is not valid JSON: " + e.Message, 2); }
    }

    static string Entity(string[] args, string usage) =>
        args.Length >= 1 && System.Text.RegularExpressions.Regex.IsMatch(args[0], "^[A-Za-z_][A-Za-z0-9_]*$") ? args[0] : throw new TestDataException("Usage: " + usage, 2);

    // ---- create, check, cleanup, status ----

    static int Create(string[] args)
    {
        const string usage = "oml test data create <Entity> --values '<json>' | --file <record.json> [--run <id>]";
        var run = Option(ref args, "--run");
        var values = Values(ref args);
        var entity = Entity(args, usage);
        if (args.Length != 1) throw new TestDataException("Usage: " + usage, 2);
        if (MemorySecrets.Find(values.ToJsonString()) is { } secret) throw new TestDataException($"The record holds what looks like a {secret}. Test data is plain data: use made-up values.", 2);
        var (api, key) = Api();
        var r = CurrentRun(run, create: true);
        if (r.Cleaned is not null) throw new TestDataException($"Run {r.Run} is cleaned up already. Start a new one: leave out --run.", 1);
        var (status, body, text) = Call(HttpMethod.Post, new Uri(api, $"runs/{Uri.EscapeDataString(r.Run)}/{entity}"), key, values).GetAwaiter().GetResult();
        if (status >= 300) throw new TestDataException($"create {entity} failed: " + Problem(status, body, text), 1);
        long id = body is JsonValue v && v.TryGetValue<long>(out var n) ? n : body?["Id"] is JsonValue iv && iv.TryGetValue<long>(out var m) ? m : 0;
        if (id <= 0) throw new TestDataException($"create {entity}: the API answered without an id ({text}).", 1);
        r.Records.Add((entity, id));
        Save(r);
        TerminalOutput.PrintObject(new { run = r.Run, entity, id, created = r.Records.Count, cleanup = $"oml test data cleanup --run {r.Run}" });
        return 0;
    }

    static int Check(string[] args)
    {
        const string usage = "oml test data check <Entity> <Id> [--expect '<json>'] [--absent]";
        var expectText = Option(ref args, "--expect");
        var absent = args.Contains("--absent");
        args = args.Where(a => a != "--absent").ToArray();
        var entity = Entity(args, usage);
        if (args.Length != 2 || !long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id)) throw new TestDataException("Usage: " + usage, 2);
        JsonObject? expect = null;
        if (expectText is not null)
            try { expect = JsonNode.Parse(expectText) as JsonObject ?? throw new TestDataException("--expect takes a JSON object.", 2); }
            catch (JsonException e) { throw new TestDataException("--expect is not valid JSON: " + e.Message, 2); }
        var (api, key) = Api();
        var (found, record) = Read(api, key, entity, id);
        var mismatches = new List<object>();
        if (found && expect is not null)
            foreach (var (k, want) in expect)
            {
                var have = record!.FirstOrDefault(p => string.Equals(p.Key, k, StringComparison.OrdinalIgnoreCase)).Value;
                if (Normal(have) != Normal(want)) mismatches.Add(new { field = k, expected = want?.ToJsonString(), actual = have?.ToJsonString() });
            }
        var ok = absent ? !found : found && mismatches.Count == 0;
        TerminalOutput.PrintObject(new
        {
            ok, entity, id, found,
            values = found ? (expect is null ? record : new JsonObject(expect.Select(e => KeyValuePair.Create(e.Key, record!.FirstOrDefault(p => string.Equals(p.Key, e.Key, StringComparison.OrdinalIgnoreCase)).Value?.DeepClone())))) : null,
            mismatches = mismatches.Count == 0 ? null : mismatches,
            hint = ok ? null : absent ? "The record is still there." : !found ? "No record with that id." : "Values differ: see mismatches.",
        });
        return ok ? 0 : 1;
    }

    static string Normal(JsonNode? n) => n switch
    {
        null => "null",
        JsonValue v when v.TryGetValue<decimal>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => n.ToJsonString(),
    };

    // A record whose Id is 0 (or no record) means not found: the API returns the empty record then.
    static (bool Found, JsonObject? Record) Read(Uri api, string key, string entity, long id)
    {
        var (status, body, text) = Call(HttpMethod.Get, new Uri(api, $"{entity}/{id}"), key).GetAwaiter().GetResult();
        if (status == 404) return (false, null);
        if (status >= 300) throw new TestDataException($"read {entity} {id} failed: " + Problem(status, body, text), 1);
        var record = body as JsonObject;
        var found = record is not null && record.FirstOrDefault(p => string.Equals(p.Key, "Id", StringComparison.OrdinalIgnoreCase)).Value is JsonValue v && v.TryGetValue<long>(out var n) && n != 0;
        return (found, found ? record : null);
    }

    static int Cleanup(string[] args)
    {
        var run = Option(ref args, "--run");
        if (args.Length > 0) throw new TestDataException("Usage: oml test data cleanup [--run <id>]", 2);
        var r = CurrentRun(run, create: false);
        if (r.Cleaned is not null) { TerminalOutput.PrintObject(new { run = r.Run, cleaned = r.Cleaned, hint = "Cleaned up already." }); return 0; }
        var (api, key) = Api();
        var results = new List<object>();
        var ok = true;
        foreach (var group in r.Records.GroupBy(x => x.Entity, StringComparer.Ordinal))
        {
            var (status, body, text) = Call(HttpMethod.Delete, new Uri(api, $"runs/{Uri.EscapeDataString(r.Run)}/{group.Key}"), key).GetAwaiter().GetResult();
            if (status >= 300) throw new TestDataException($"cleanup {group.Key} failed: " + Problem(status, body, text), 1, "Nothing is lost: run the cleanup again; the run file still lists the records.");
            long deleted = body is JsonValue v && v.TryGetValue<long>(out var n) ? n : body?["Deleted"] is JsonValue dv && dv.TryGetValue<long>(out var m) ? m : -1;
            var still = group.Where(x => Read(api, key, x.Entity, x.Id).Found).Select(x => x.Id).ToArray();
            ok &= still.Length == 0 && deleted == group.Count();
            results.Add(new { entity = group.Key, created = group.Count(), deleted, stillThere = still.Length == 0 ? null : still });
        }
        if (ok)
        {
            r.Cleaned = DateTime.UtcNow.ToString("O");
            Save(r);
            if (File.Exists(CurrentFile) && File.ReadAllText(CurrentFile).Trim() == r.Run) File.Delete(CurrentFile);
        }
        TerminalOutput.PrintObject(new { ok, run = r.Run, entities = results, hint = ok ? null : "The counts do not match: some records were not deleted or were deleted elsewhere. Tell the user; run the cleanup again after checking." });
        return ok ? 0 : 1;
    }

    static int Status()
    {
        var runs = Directory.Exists(RunsFolder) ? Directory.EnumerateFiles(RunsFolder, "*.json").Select(RunFile.Read).OrderBy(r => r.Started, StringComparer.Ordinal).ToList() : [];
        var open = runs.Where(r => r.Cleaned is null).ToList();
        TerminalOutput.PrintObject(new
        {
            open = open.Select(r => new { run = r.Run, started = r.Started, records = r.Records.Count, entities = r.Records.Select(x => x.Entity).Distinct().ToArray(), cleanup = $"oml test data cleanup --run {r.Run}" }).ToArray(),
            cleaned = runs.Count - open.Count,
            allowedHosts = AllowedHosts(Environment.CurrentDirectory),
            hint = open.Count > 0 ? "Open runs still have test data in the database. Clean them up when the test is done." : null,
        });
        return 0;
    }
}
