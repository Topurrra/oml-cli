using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// `oml check`: AI Mentor-like findings from module files, read with the same loader as `oml architecture`.
// It only reads: inputs are never changed, and outputs are new files.
static class Check
{
    static int Rank(string severity) => System.Array.IndexOf(CheckInput.Severities, severity);

    public static int Run(List<string> args)
    {
        var watch = Stopwatch.StartNew();
        if (args.Contains("--diff")) return Diff(args, watch);
        CheckInput.Request request;
        try { request = CheckInput.Parse(args); }
        catch (CheckInputError e) { throw new CliError(e.Message, 2); }
        if (request.InitRules is { } rulesOut) return InitRules(rulesOut);
        var scan = Scan(request, watch);
        var (findings, ignored, skipped, timings, architecture) = (scan.Findings, scan.Ignored, scan.Skipped, scan.Timings, scan.Architecture);
        var baseline = request.Baseline;
        if (baseline is not null)
            foreach (var f in findings)
            {
                var key = CheckInput.Key(Text(f, "rule"), Text(f, "module"), Text(f, "element"));
                var known = baseline.GetValueOrDefault(key) > 0;
                if (known) baseline[key]--;
                f["status"] = known ? "existing" : "new";
            }

        var summary = new JsonObject { ["findings"] = findings.Count };
        foreach (var s in CheckInput.Severities) summary[s] = findings.Count(f => Text(f, "severity") == s);
        summary["byCategory"] = new JsonObject(CheckCatalog.All.Select(x => x.Category).Distinct()
            .Select(c => KeyValuePair.Create(c, (JsonNode?)findings.Count(f => Text(f, "category") == c))));
        if (baseline is not null)
        {
            summary["new"] = findings.Count(f => Text(f, "status") == "new");
            summary["existing"] = findings.Count(f => Text(f, "status") == "existing");
        }
        summary["ignored"] = ignored;
        summary["skipped"] = skipped;
        var document = new JsonObject {
            ["schema"] = 1, ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"), ["omlVersion"] = typeof(Cli).Assembly.GetName().Version?.ToString(3),
            ["input"] = new JsonObject { ["sources"] = Array(request.Sources), ["apps"] = Array(request.Apps), ["team"] = request.TeamLabel,
                ["rules"] = request.RulesPath, ["baseline"] = request.BaselinePath, ["layers"] = request.Layers },
            ["modules"] = timings, ["summary"] = summary, ["findings"] = new JsonArray(findings.ToArray<JsonNode?>()),
            ["warnings"] = Array(architecture.warnings), ["elapsedSeconds"] = Math.Round(watch.Elapsed.TotalSeconds, 3),
        };
        try
        {
            if (request.Json is { } jsonPath) Write(jsonPath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (request.Html is { } html) Write(html, CheckPage.Render(document.ToJsonString()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new CliError(e.Message, 2); }
        document["html"] = request.Html;
        document["json"] = request.Json;
        if (Timings.Enabled) document["timings"] = Timings.Report();
        TerminalOutput.PrintNode(document);
        if (request.FailOn is not { } failOn) return 0;
        // A gate must not pass on a partial check: anything skipped is an input error.
        if (skipped.Count > 0) return 2;
        // With a baseline, only new findings fail the run: teams can adopt the check without fixing old debt first.
        return findings.Any(f => Rank(Text(f, "severity")) <= Rank(failOn) && (baseline is null || Text(f, "status") == "new")) ? 1 : 0;
    }

    sealed record Scanned(List<JsonObject> Findings, int Ignored, JsonArray Skipped, JsonArray Timings, Architecture.Result Architecture);

    // Reads the request's modules once and runs the rules: the findings of `check` and of `check --diff`.
    static Scanned Scan(CheckInput.Request request, Stopwatch watch)
    {
        var settings = request.Settings;
        bool Enabled(string rule) => settings.Rules[rule].Enabled;
        int Option(string rule, string key) => settings.Rules[rule].Options[key];

        var hits = new List<CheckRules.Hit>();
        var prints = new List<CheckRules.Fingerprint>();
        var timings = new JsonArray();
        var skipped = new JsonArray();
        Architecture.Result architecture;
        try
        {
            var overrides = Architecture.ReadOverrides(request.Layers);
            architecture = Architecture.Read(request.Sources, request.Apps, request.TeamLabel, overrides, (module, application) =>
            {
                var moduleWatch = Stopwatch.StartNew();
                var result = CheckRules.Run(module, Enabled, Option);
                hits.AddRange(result.Hits);
                prints.AddRange(result.Flows);
                var name = Model.NameOf(module);
                // A rule that fails is reported, not hidden: its findings for this module are missing.
                foreach (var (rule, reason) in result.Failures)
                    skipped.Add(new JsonObject { ["module"] = name, ["rule"] = rule, ["reason"] = $"Rule {rule} failed on module {name}: {reason}" });
                timings.Add(new JsonObject { ["name"] = name, ["application"] = application, ["seconds"] = Math.Round(moduleWatch.Elapsed.TotalSeconds, 3) });
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { throw new CliError(e.Message, 2); }
        foreach (var (input, reason) in architecture.skipped)
            skipped.Add(new JsonObject { ["input"] = input, ["reason"] = $"Not checked: {input}: {reason}" });
        if (timings.Count == 0)
            throw new CliError("No module was checked. " + string.Join(" ", architecture.warnings.DefaultIfEmpty(
                "No .oml module matched the input" + (request.Apps.Count > 0 ? " and the --app filter" : "") + ". Extensions (.xif) are not checked.")), 2);
        hits.AddRange(CheckRules.Duplicates(prints));
        // Module graph rules come from `oml architecture` and need at least two modules.
        if (timings.Count >= 2)
            foreach (var f in architecture.document["findings"]!.AsArray())
            {
                var id = f!["id"]!.GetValue<string>();
                if (CheckCatalog.All.FirstOrDefault(x => x.Id == id) is not { SeveralModules: true } || !Enabled(id)) continue;
                var modules = f["modules"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                hits.Add(new(id, modules[0], string.Join(", ", modules.Skip(1)), f["detail"]!.GetValue<string>()));
            }

        var findings = Findings(hits, settings, out var ignored);
        return new(findings, ignored, skipped, timings, architecture);
    }

    // `oml check --diff <before.oml> <after.oml>`: the findings that one change fixed and introduced, per rule.
    // A finding is the same in both when rule and element are equal (the detail text when the element is empty);
    // the same key twice is counted twice. The module name is not compared: each file holds one module, and a renamed copy must still match. Exit 0 when nothing is introduced, 1 when something is, 2 on an input error
    // or when a rule or module was skipped (a partial check cannot prove anything).
    static int Diff(List<string> args, Stopwatch watch)
    {
        const string usage = "check --diff needs <before.oml> <after.oml> and accepts --rules <oml-rules.json>.";
        var files = new List<string>();
        string? rulesFile = null;
        for (var i = 1; i < args.Count; i++)
            if (args[i] == "--diff") continue;
            else if (args[i] == "--rules" && rulesFile is null && i + 1 < args.Count && !args[i + 1].StartsWith("--")) rulesFile = Path.GetFullPath(args[++i]);
            else if (!args[i].StartsWith("--")) files.Add(args[i]);
            else throw new CliError(usage, 2);
        if (files.Count != 2) throw new CliError(usage, 2);
        foreach (var f in files)
        {
            if (!Path.GetExtension(f).Equals(".oml", StringComparison.OrdinalIgnoreCase)) throw new CliError($"Not an .oml module file: {f}", 2);
            if (!File.Exists(f)) throw new CliError($"File not found: {Path.GetFullPath(f)}", 2);
        }
        CheckInput.Settings settings;
        try { settings = CheckInput.ReadSettings(rulesFile); }
        catch (CheckInputError e) { throw new CliError(e.Message, 2); }
        Scanned Read(string file) => Scan(new([Path.GetFullPath(file)], [], null, null, null, null, rulesFile, null, null, null, settings, null), watch);
        var (before, after) = (Read(files[0]), Read(files[1]));

        // An empty element is told apart by its detail.
        string Key(JsonObject f) => CheckInput.Key(Text(f, "rule"), "", Text(f, "element").Length > 0 ? Text(f, "element") : Text(f, "detail"));
        // Findings of `from` that `other` does not have, each key as often as it is missing.
        List<JsonObject> Missing(List<JsonObject> from, List<JsonObject> other, out int common)
        {
            var left = other.GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
            var missing = new List<JsonObject>();
            common = 0;
            foreach (var f in from)
                if (left.GetValueOrDefault(Key(f)) > 0) { left[Key(f)]--; common++; }
                else missing.Add(f);
            return missing;
        }
        var fixedFindings = Missing(before.Findings, after.Findings, out var unchanged);
        var introduced = Missing(after.Findings, before.Findings, out _);
        JsonArray Shape(IEnumerable<JsonObject> list) => new(list.Select(f => (JsonNode?)new JsonObject {
            ["rule"] = f["rule"]!.DeepClone(), ["severity"] = f["severity"]!.DeepClone(), ["module"] = f["module"]!.DeepClone(), ["element"] = f["element"]!.DeepClone(),
            ["title"] = f["title"]!.DeepClone(), ["detail"] = f["detail"]?.DeepClone() }).ToArray());
        var rules = new JsonArray();
        foreach (var rule in CheckCatalog.All.Select(x => x.Id))
        {
            var (gone, added) = (fixedFindings.Where(f => Text(f, "rule") == rule).ToList(), introduced.Where(f => Text(f, "rule") == rule).ToList());
            if (gone.Count + added.Count == 0) continue;
            rules.Add(new JsonObject { ["rule"] = rule, ["fixed"] = gone.Count, ["introduced"] = added.Count,
                ["fixedFindings"] = Shape(gone), ["introducedFindings"] = Shape(added) });
        }
        var skipped = new JsonArray(before.Skipped.Concat(after.Skipped).Select(n => n!.DeepClone()).ToArray<JsonNode?>());
        var document = new JsonObject {
            ["rules"] = rules,
            ["summary"] = new JsonObject { ["fixed"] = fixedFindings.Count, ["introduced"] = introduced.Count, ["unchanged"] = unchanged },
            ["before"] = Path.GetFullPath(files[0]), ["after"] = Path.GetFullPath(files[1]),
        };
        if (skipped.Count > 0) document["skipped"] = skipped;
        document["elapsedSeconds"] = Math.Round(watch.Elapsed.TotalSeconds, 3);
        TerminalOutput.PrintNode(document);
        return skipped.Count > 0 ? 2 : introduced.Count > 0 ? 1 : 0;
    }

    // Hits as report findings, sorted: the team's severities, without the ignored ones (counted). `oml review` uses it too.
    internal static List<JsonObject> Findings(IEnumerable<CheckRules.Hit> hits, CheckInput.Settings settings, out int ignored)
    {
        var findings = new List<JsonObject>();
        ignored = 0;
        foreach (var hit in hits)
        {
            var rule = CheckCatalog.All.Single(x => x.Id == hit.Rule);
            if (settings.Ignores.Any(i => (i.Rule == "*" || i.Rule == hit.Rule) && (i.Match.IsMatch(hit.Element) || i.Match.IsMatch(hit.Module + "/" + hit.Element)))) { ignored++; continue; }
            findings.Add(new JsonObject {
                ["rule"] = rule.Id, ["severity"] = settings.Rules[rule.Id].Severity, ["category"] = rule.Category, ["title"] = rule.Title,
                ["module"] = hit.Module, ["element"] = hit.Element,
                ["detail"] = rule.SeveralModules ? hit.Note : hit.Note is null ? rule.Detail : hit.Note + " " + rule.Detail,
                ["fix"] = rule.Fix, ["aiMentorName"] = hit.AiMentorName ?? rule.AiMentorName,
            });
        }
        return findings.OrderBy(f => Rank(Text(f, "severity"))).ThenBy(f => Text(f, "rule"), StringComparer.Ordinal)
            .ThenBy(f => Text(f, "module"), StringComparer.OrdinalIgnoreCase).ThenBy(f => Text(f, "element"), StringComparer.Ordinal).ToList();
    }

    internal static string Text(JsonObject o, string key) => o[key]?.GetValue<string>() ?? "";
    static JsonArray Array(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)v).ToArray());

    internal static void Write(string path, string text, bool overwrite = false)
    {
        using var stream = new FileStream(path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.Write(text);
    }

    static int InitRules(string path)
    {
        var rules = CheckCatalog.All.Select(x => new JsonObject {
            ["id"] = x.Id, ["title"] = x.Title, ["category"] = x.Category, ["aiMentorName"] = x.AiMentorName,
            ["enabled"] = x.Enabled, ["severity"] = x.Severity,
            ["options"] = new JsonObject((x.Options ?? new()).Select(o => KeyValuePair.Create(o.Key, (JsonNode?)o.Value))),
        });
        var document = new JsonObject { ["schema"] = 1, ["rules"] = new JsonArray(rules.ToArray<JsonNode?>()), ["ignore"] = new JsonArray() };
        try { Write(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new CliError(e.Message, 2); }
        return Cli.Emit(new { written = path, rules = CheckCatalog.All.Length });
    }
}
