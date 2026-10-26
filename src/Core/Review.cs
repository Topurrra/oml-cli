using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OutSystems.Model;
using Item = OmlCli.ReviewModel.Item;
using Snapshot = OmlCli.ReviewModel.Snapshot;

namespace OmlCli;

// `oml review <before.oml> <after.oml>`: what changed between two versions of one module, in plain words, with the
// production-rule risks of the change and the `oml check` findings that are new. It only reads: the inputs are never
// changed, and every output is a new file.
static class Review
{
    public const string NotCovered = "Logic bugs are not covered; 0 findings does not mean the code is correct.";

    // Texts: changed lines of long text and code, for --md and --out only.
    internal sealed record Change(string Area, string Kind, string Name, string What, List<string> Details, List<Text>? Texts = null);

    // The areas a report groups changes by, in report order.
    internal static readonly (string id, string title)[] Areas =
    {
        ("data", "Data model"), ("logic", "Logic"), ("ui", "Screens and UI"), ("integrations", "Integrations"),
        ("security", "Roles and access"), ("settings", "Settings, timers and processes"), ("references", "References"), ("other", "Other"),
    };

    // Rule numbers of skill/rules.md with a short name for reports.
    internal static readonly Dictionary<int, string> RuleNames = new()
    {
        [1] = "production data", [2] = "existing entities stay", [3] = "no renames or deletes", [4] = "public signatures stay", [5] = "attribute and entity settings stay",
        [6] = "no data-changing SQL", [7] = "security stays",
    };

    // JSON on stdout stays small for agents: the full lists go to --md, --out and --notes.
    const int MaxChanges = 30, MaxDetails = 5, MaxRisks = 25, MaxFindings = 15;

    sealed record Request(string Before, string After, string? Md, string? Html, string? Notes, string? Rules, bool MdOverwritten = false);

    public static int Run(List<string> args)
    {
        var request = Parse(args);
        var document = Build(request.Before, request.After, request.Rules);
        try
        {
            if (request.Md is { } md) Check.Write(md, ReviewReport.Markdown(document), request.MdOverwritten);
            if (request.Html is { } html) Check.Write(html, ReviewReport.Html(document));
            if (request.Notes is { } notes) Check.Write(notes, ReviewReport.Notes(document));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new CliError(e.Message, 2); }
        var output = Compact(document);
        foreach (var (key, path) in new[] { ("md", request.Md), ("html", request.Html), ("notes", request.Notes) })
            if (path is not null) output[key] = path;
        if (request.MdOverwritten) output["overwritten"] = request.Md;
        if (Timings.Enabled) output["timings"] = Timings.Report();
        TerminalOutput.PrintNode(output);
        return 0;
    }

    // An earlier review report: an existing .md file whose first line is the report header ("# Review: <module>").
    static bool IsReviewFile(string path)
    {
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)) return false;
        try { using var reader = new StreamReader(path); return reader.ReadLine()?.StartsWith("# Review: ", StringComparison.Ordinal) == true; }
        catch (IOException) { return false; }
    }

    static Request Parse(List<string> args)
    {
        var files = new List<string>();
        var options = new Dictionary<string, string>();
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] is "--md" or "--out" or "--notes" or "--rules" && i + 1 < args.Count && !args[i + 1].StartsWith("--"))
            {
                if (!options.TryAdd(args[i], args[++i])) throw new CliError($"Duplicate option {args[i - 1]}.", 2);
            }
            else if (!args[i].StartsWith("--")) files.Add(args[i]);
            else throw new CliError("review accepts <before.oml> <after.oml>, --md <file.md>, --out <file.html>, --notes <file.md> and --rules <oml-rules.json>.", 2);
        }
        if (files.Count != 2) throw new CliError("review needs two module files: <before.oml> <after.oml>.", 2);
        foreach (var f in files)
        {
            if (!Path.GetExtension(f).Equals(".oml", StringComparison.OrdinalIgnoreCase)) throw new CliError($"Not an .oml module file: {f}", 2);
            if (!File.Exists(f)) throw new CliError($"File not found: {Path.GetFullPath(f)}", 2);
        }
        var overwritten = false;
        string? Output(string key)
        {
            if (!options.TryGetValue(key, out var value)) return null;
            var full = Path.GetFullPath(value);
            // --md may replace an earlier review file: an existing .md file only, never a staged .oml or another output.
            if (key == "--md" && IsReviewFile(full)) { overwritten = true; return full; }
            try { CheckInput.NewOutput(full); } catch (CheckInputError e) { throw new CliError(e.Message, 2); }
            return full;
        }
        var outputs = new[] { Output("--md"), Output("--out"), Output("--notes") };
        if (outputs.OfType<string>().GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new CliError("--md, --out and --notes need different paths.", 2);
        var rules = options.TryGetValue("--rules", out var r) ? Path.GetFullPath(r) : null;
        if (rules is not null && !File.Exists(rules)) throw new CliError($"Rules file not found: {rules}", 2);
        return new(Path.GetFullPath(files[0]), Path.GetFullPath(files[1]), outputs[0], outputs[1], outputs[2], rules, overwritten);
    }

    // The whole report. The self-test and the CLI both use it.
    internal static JsonObject Build(string beforePath, string afterPath, string? rulesPath)
    {
        var watch = Stopwatch.StartNew();
        CheckInput.Settings settings;
        try { settings = CheckInput.ReadSettings(rulesPath); }
        catch (CheckInputError e) { throw new CliError(e.Message, 2); }
        using var beforeOpen = Model.Borrow(beforePath, "loadBefore");
        using var afterOpen = Model.Borrow(afterPath, "loadAfter");
        var (before, after) = (beforeOpen.Module, afterOpen.Module);
        var a = Timings.Measure("readBefore", () => ReviewModel.Read(before));
        var b = Timings.Measure("readAfter", () => ReviewModel.Read(after));
        if (a.Key != b.Key)
            throw new CliError($"The two files are different modules ({a.Module} and {b.Module}): review compares two versions of one module.", 2);
        var kinds = new Dictionary<string, int>(); // Compare counts each changed property by name, for the summary.
        var changes = Timings.Measure("compare", () => Compare(a, b, kinds));
        var risks = Timings.Measure("risks", () => ProductionRules.ForReview(before).Risks(after)
            .OrderBy(r => r.Info).ThenBy(r => r.Rule).ThenBy(r => r.Element, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Text, StringComparer.Ordinal).ToList());
        var skipped = new List<string>();
        List<JsonObject> Findings(IESpace module, string label)
        {
            var result = CheckRules.Run(module, rule => settings.Rules[rule].Enabled, (rule, key) => settings.Rules[rule].Options[key]);
            foreach (var (rule, reason) in result.Failures) skipped.Add($"Rule {rule} failed on the {label} module: {reason}");
            return Check.Findings(result.Hits.Concat(CheckRules.Duplicates(result.Flows)), settings, out _);
        }
        var (old, now) = Timings.Measure("check", () => (Findings(before, "before"), Findings(after, "after")));
        // Matched by rule and element, as `oml check --baseline` does; the module name is the same module.
        string Key(JsonObject f) => CheckInput.Key(Check.Text(f, "rule"), "", Check.Text(f, "element"));
        var known = old.GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
        var fresh = new List<JsonObject>();
        foreach (var f in now)
            if (known.GetValueOrDefault(Key(f)) > 0) known[Key(f)]--;
            else fresh.Add(f);
        var warnings = new List<string>();
        var unreadable = Unreadable.Cap(a.Unreadable.Concat(b.Unreadable).ToList());
        if (a.Unreadable.Count + b.Unreadable.Count > 0)
            warnings.Add($"{a.Unreadable.Count + b.Unreadable.Count} parts of the modules could not be read; they are not compared. They are listed in \"unreadable\".");
        return new JsonObject
        {
            ["schema"] = 1, ["module"] = b.Module, ["before"] = beforePath, ["after"] = afterPath,
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"), ["omlVersion"] = typeof(Cli).Assembly.GetName().Version?.ToString(3),
            ["summary"] = new JsonObject
            {
                ["added"] = changes.Count(c => c.What == "added"), ["removed"] = changes.Count(c => c.What == "removed"),
                ["recreated"] = changes.Count(c => c.What == "recreated"), ["renamed"] = changes.Count(c => c.What == "renamed"), ["changed"] = changes.Count(c => c.What == "changed"),
                ["risks"] = risks.Count(r => !r.Info), ["info"] = risks.Count(r => r.Info), ["newFindings"] = fresh.Count, ["resolvedFindings"] = known.Values.Sum(),
                ["changes"] = changes.Count,
                ["byArea"] = new JsonObject(Areas.Where(x => changes.Any(c => c.Area == x.id)).Select(x => KeyValuePair.Create(x.id, (JsonNode?)changes.Count(c => c.Area == x.id)))),
                ["changeKinds"] = new JsonObject(kinds.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Select(k => KeyValuePair.Create(k.Key, (JsonNode?)k.Value))),
            },
            ["risks"] = new JsonArray(risks.Select(r => (JsonNode?)Risk(r)).ToArray()),
            ["changes"] = new JsonArray(changes.Select(c => (JsonNode?)new JsonObject
            {
                ["area"] = c.Area, ["kind"] = c.Kind, ["name"] = c.Name, ["change"] = c.What,
                ["details"] = new JsonArray(c.Details.Select(d => (JsonNode?)d).ToArray()),
                ["texts"] = new JsonArray((c.Texts ?? []).Select(t => (JsonNode?)new JsonObject { ["subject"] = t.Subject, ["lines"] = new JsonArray(t.Lines.Select(l => (JsonNode?)l).ToArray()) }).ToArray()),
            }).ToArray()),
            ["newFindings"] = new JsonArray(fresh.Select(f => (JsonNode?)new JsonObject
            {
                ["rule"] = f["rule"]!.DeepClone(), ["severity"] = f["severity"]!.DeepClone(), ["category"] = f["category"]!.DeepClone(), ["title"] = f["title"]!.DeepClone(),
                ["element"] = f["element"]!.DeepClone(), ["detail"] = f["detail"]?.DeepClone(), ["fix"] = f["fix"]?.DeepClone(),
            }).ToArray()),
            ["skipped"] = new JsonArray(skipped.Select(s => (JsonNode?)s).ToArray()),
            ["unreadable"] = new JsonArray((unreadable ?? []).Select(u => (JsonNode?)new JsonObject { ["part"] = u.part, ["reason"] = u.reason }).ToArray()),
            ["warnings"] = new JsonArray(warnings.Select(s => (JsonNode?)s).ToArray()),
            ["note"] = NotCovered,
            ["elapsedSeconds"] = Math.Round(watch.Elapsed.TotalSeconds, 3),
        };
    }

    // A risk in the report. `info` only when it is one: worth a look, not a broken rule.
    static JsonObject Risk(ProductionRules.Risk r)
    {
        var risk = new JsonObject { ["rule"] = r.Rule, ["ruleName"] = RuleNames.GetValueOrDefault(r.Rule), ["element"] = r.Element, ["text"] = r.Text };
        if (r.Info) risk["info"] = true;
        return risk;
    }

    // The stdout copy: counts first, then capped lists with their totals, and no long fields.
    static JsonObject Compact(JsonObject full)
    {
        JsonArray Cap(string key, int max, Func<JsonObject, JsonObject> shape) =>
            new(full[key]!.AsArray().Take(max).Select(n => (JsonNode?)shape(n!.AsObject())).ToArray());
        var output = new JsonObject
        {
            ["module"] = full["module"]!.DeepClone(), ["summary"] = full["summary"]!.DeepClone(),
            ["risks"] = Cap("risks", MaxRisks, r => { var risk = r.DeepClone().AsObject(); risk.Remove("ruleName"); return risk; }),
            ["changes"] = Cap("changes", MaxChanges, c =>
            {
                var details = c["details"]!.AsArray();
                var shaped = new JsonObject { ["area"] = c["area"]!.DeepClone(), ["kind"] = c["kind"]!.DeepClone(), ["name"] = c["name"]!.DeepClone(), ["change"] = c["change"]!.DeepClone(),
                    ["details"] = new JsonArray(details.Take(MaxDetails).Select(d => (JsonNode?)Short(d!.GetValue<string>())).ToArray()) };
                if (details.Count > MaxDetails) shaped["moreDetails"] = details.Count - MaxDetails;
                return shaped;
            }),
            ["newFindings"] = Cap("newFindings", MaxFindings, f => new JsonObject { ["rule"] = f["rule"]!.DeepClone(), ["severity"] = f["severity"]!.DeepClone(), ["element"] = f["element"]!.DeepClone(), ["title"] = f["title"]!.DeepClone() }),
        };
        // What was cut: the full length of each cut list, and how many changes lost details.
        var truncated = new JsonObject();
        foreach (var (key, max) in new[] { ("risks", MaxRisks), ("changes", MaxChanges), ("newFindings", MaxFindings) })
            if (full[key]!.AsArray().Count > max) truncated[key] = full[key]!.AsArray().Count;
        if (full["changes"]!.AsArray().Count(c => c!["details"]!.AsArray().Count > MaxDetails) is > 0 and var cut) truncated["changesWithMoreDetails"] = cut;
        if (truncated.Count > 0)
        {
            truncated["hint"] = "Lists are cut here. Write the full report with --md <file.md>.";
            output["truncated"] = truncated;
        }
        foreach (var key in new[] { "skipped", "warnings" })
            if (full[key]!.AsArray().Count > 0) output[key] = full[key]!.DeepClone();
        // The parts that could not be read, as many as the page and the Markdown list.
        if (full["unreadable"]!.AsArray().Count > 0) output["unreadable"] = full["unreadable"]!.DeepClone();
        output["note"] = NotCovered;
        output["elapsedSeconds"] = full["elapsedSeconds"]!.DeepClone();
        return output;
    }

    static string Short(string text) => text.Length <= 160 ? text : text[..157] + "...";

    // ---- Comparison ----

    internal static List<Change> Compare(Snapshot a, Snapshot b, Dictionary<string, int> kinds)
    {
        Dictionary<string, List<Item>> Members(Snapshot s) => s.Order.Select(k => s.Items[k]).GroupBy(i => i.Unit).ToDictionary(g => g.Key, g => g.ToList());
        var (membersA, membersB) = (Members(a), Members(b));
        var units = b.Order.Where(k => b.Items[k].Unit == k).Concat(a.Order.Where(k => a.Items[k].Unit == k && !b.Items.ContainsKey(k))).ToList();
        var changes = new List<Change>();
        foreach (var unit in units)
        {
            var (ua, ub) = (a.Items.GetValueOrDefault(unit), b.Items.GetValueOrDefault(unit));
            // A unit in both snapshots must be a unit in both: an element never moves between top elements.
            if (ua is not null && ua.Unit != unit) ua = null;
            if (ub is not null && ub.Unit != unit) ub = null;
            var top = ub ?? ua!;
            var kind = Human(top.Kind);
            if (ua is null) { changes.Add(new(Area(top.Kind), kind, top.Label, "added", Contents(membersB.GetValueOrDefault(unit) ?? new(), top))); continue; }
            if (ub is null) { changes.Add(new(Area(top.Kind), kind, top.Label, "removed", Contents(membersA.GetValueOrDefault(unit) ?? new(), top))); continue; }
            var details = new List<string>();
            var renamed = ua.Name != ub.Name;
            if (renamed) details.Add($"renamed from {ua.Label} to {ub.Label}");
            var texts = new List<Text>();
            details.AddRange(Props(ua, ub, $"{kind} {ub.Label}", texts, kinds));
            details.AddRange(Inside(a, b, membersA.GetValueOrDefault(unit) ?? new(), membersB.GetValueOrDefault(unit) ?? new(), unit, top.Kind == "Reference", texts, kinds));
            if (details.Count > 0) changes.Add(new(Area(top.Kind), kind, ub.Label, renamed ? "renamed" : "changed", details, texts));
        }
        // Deleted and made again with the same name (in Service Studio, or a module built again): one change. It is
        // still a new element with a new key, and references to the old one break.
        foreach (var removed in changes.Where(c => c.What == "removed").ToList())
            if (changes.FirstOrDefault(c => c.What == "added" && c.Kind == removed.Kind && c.Name == removed.Name) is { } added)
            {
                changes.Remove(removed);
                changes[changes.IndexOf(added)] = added with { What = "recreated",
                    Details = [$"deleted and created again with the same name: a new element, so references to the old one break", .. added.Details] };
            }
        var order = Areas.Select(x => x.id).ToList();
        return changes.OrderBy(c => order.IndexOf(c.Area)).ThenBy(c => c.What switch { "removed" => 0, "recreated" => 1, "renamed" => 2, "changed" => 3, _ => 4 })
            .ThenBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // What an added or removed top element holds, in one or two lines.
    static List<string> Contents(List<Item> members, Item top)
    {
        var lines = new List<string>();
        if (top.Kind == "Reference")
        {
            var used = members.Where(m => m.Key != top.Key).Select(m => m.Label).ToList();
            if (used.Count > 0) lines.Add("uses " + Names(used));
            return lines;
        }
        foreach (var group in members.Where(m => m.Parent == top.Key && !m.IsNode && !m.IsWidget).GroupBy(m => Human(m.Kind)))
            lines.Add($"{Plural(group.Count(), group.Key)}: {Names(group.Select(Describe).ToList())}");
        var steps = members.Count(m => m.IsNode && m.Kind is not ("StartNode" or "EndNode"));
        var widgets = members.Count(m => m.IsWidget);
        if (steps > 0) lines.Add(Plural(steps, "step") + " in its flows");
        if (widgets > 0) lines.Add(Plural(widgets, "widget"));
        if (top.Props.GetValueOrDefault("Public") == "True") lines.Add("public");
        foreach (var key in new[] { "StyleSheet", "JavaScript" })
            if (top.Props.TryGetValue(key, out var text)) lines.Add($"{Word(key)}: {text.Length} characters");
        return lines;
    }

    // An attribute or parameter with its type: "Email (Email, mandatory)".
    static string Describe(Item i)
    {
        var parts = new List<string>();
        if (i.Props.GetValueOrDefault("DataType") is { Length: > 0 } type) parts.Add(type);
        if (i.Props.GetValueOrDefault("IsMandatory") == "True") parts.Add("mandatory");
        return i.Label + (parts.Count > 0 ? " (" + string.Join(", ", parts) + ")" : "");
    }

    // Changes inside one top element that is in both modules.
    static IEnumerable<string> Inside(Snapshot a, Snapshot b, List<Item> inA, List<Item> inB, string unit, bool reference, List<Text> texts, Dictionary<string, int> kinds)
    {
        var keysA = inA.Select(i => i.Key).ToHashSet();
        var keysB = inB.Select(i => i.Key).ToHashSet();
        var lines = new List<string>();
        if (reference)
        {
            var added = inB.Where(i => i.Key != unit && !keysA.Contains(i.Key)).Select(i => $"{Human(i.Kind)} {i.Label}").ToList();
            var removed = inA.Where(i => i.Key != unit && !keysB.Contains(i.Key)).Select(i => $"{Human(i.Kind)} {i.Label}").ToList();
            if (added.Count > 0) lines.Add("now uses " + Names(added));
            if (removed.Count > 0) lines.Add("no longer uses " + Names(removed));
            return lines;
        }
        // Only the outermost added or removed element is listed: its insides come with it.
        bool Listed(Item i, HashSet<string> other, Snapshot s) => !other.Contains(i.Key) && (i.Parent is null || i.Parent == unit || other.Contains(i.Parent) || !s.Items.ContainsKey(i.Parent));
        var widgetsAdded = new List<string>();
        var widgetsRemoved = new List<string>();
        var widgetsChanged = new List<(string name, string line)>();
        foreach (var i in inB.Where(i => i.Key != unit && !i.IsNode && Listed(i, keysA, b)))
            if (i.IsWidget) widgetsAdded.Add(Display(i));
            else lines.Add($"{Human(i.Kind)} {Describe(i)} added{Where(b, i, unit)}");
        foreach (var i in inA.Where(i => i.Key != unit && !i.IsNode && Listed(i, keysB, a)))
            if (i.IsWidget) widgetsRemoved.Add(Display(i));
            else lines.Add($"{Human(i.Kind)} {i.Label} removed{Where(a, i, unit)}");
        foreach (var i in inB.Where(i => i.Key != unit && !i.IsNode && keysA.Contains(i.Key)))
        {
            var old = a.Items[i.Key];
            var moved = old.Parent != i.Parent ? new[] { $"moved from {ParentName(a, old, unit)} to {ParentName(b, i, unit)}" } : [];
            var subject = $"{Human(i.Kind)} {(old.Name != i.Name ? old.Label : Display(i, kind: false))}{Where(b, i, unit)}";
            if (Changed(subject, old, i, moved.Concat(Props(old, i, subject, texts, kinds))) is not { } line) continue;
            if (i.IsWidget) widgetsChanged.Add((Display(i), line));
            else lines.Add(line);
        }
        if (widgetsAdded.Count > 0) lines.Add(Plural(widgetsAdded.Count, "widget") + " added: " + Names(widgetsAdded));
        if (widgetsRemoved.Count > 0) lines.Add(Plural(widgetsRemoved.Count, "widget") + " removed: " + Names(widgetsRemoved));
        // Widgets at a summary level: each change while there are few, then one line with the widgets' names.
        if (widgetsChanged.Count <= 5) lines.AddRange(widgetsChanged.Select(w => "widget " + w.line));
        else lines.Add(Plural(widgetsChanged.Count, "widget") + " changed: " + Names(widgetsChanged.Select(w => w.name).ToList()));
        lines.AddRange(Flows(a, b, inA, inB, unit, texts, kinds));
        return lines;
    }

    // "input Total renamed to Amount: type Decimal -> Integer", or null when nothing changed.
    static string? Changed(string subject, Item old, Item now, IEnumerable<string> what)
    {
        var list = what.ToList();
        if (old.Name == now.Name && list.Count == 0) return null;
        return subject + (old.Name != now.Name ? " renamed to " + now.Label : "") + (list.Count > 0 ? ": " + string.Join("; ", list) : "");
    }

    // " in GetOrders": the element that holds a nested element, when that is not the top element.
    static string Where(Snapshot s, Item i, string unit) =>
        i.Parent is { } p && p != unit && s.Items.TryGetValue(p, out var parent) && !i.IsWidget ? " in " + parent.Label : "";
    static string ParentName(Snapshot s, Item i, string unit) =>
        i.Parent is { } p && s.Items.TryGetValue(p, out var parent) ? (p == unit ? "the top level" : Display(parent)) : "the top level";

    static string Display(Item i, bool kind = true) =>
        i.Name is { } name ? (kind ? Human(i.Kind) + " " : "") + name
        : i.Main is { } main ? (kind ? Human(i.Kind) + " " : "") + Quote(main) : Human(i.Kind);
    static string Quote(string text) => text.Length > 40 ? "'" + text[..37] + "...'" : "'" + text + "'";

    // ---- Flows ----

    // Node changes in each flow (an action, a screen action, a process activity...), in plain words.
    static IEnumerable<string> Flows(Snapshot a, Snapshot b, List<Item> inA, List<Item> inB, string unit, List<Text> texts, Dictionary<string, int> kinds)
    {
        var flows = inB.Concat(inA).Where(i => i.IsNode && i.Parent is not null).Select(i => i.Parent!).Distinct().ToList();
        foreach (var flow in flows)
        {
            // A flow that is new or gone as a whole is reported by its owner.
            if (!a.Items.ContainsKey(flow) || !b.Items.ContainsKey(flow)) continue;
            var nodesA = inA.Where(i => i.IsNode && i.Parent == flow).ToDictionary(i => i.Key);
            var nodesB = inB.Where(i => i.IsNode && i.Parent == flow).ToDictionary(i => i.Key);
            static Dictionary<string, HashSet<string>> Preds(Dictionary<string, Item> nodes)
            {
                var preds = nodes.Keys.ToDictionary(k => k, _ => new HashSet<string>());
                foreach (var n in nodes.Values)
                    foreach (var (_, target) in n.Next)
                        if (preds.TryGetValue(target, out var set)) set.Add(n.Key);
                return preds;
            }
            var (predsA, predsB) = (Preds(nodesA), Preds(nodesB));
            var added = nodesB.Keys.Where(k => !nodesA.ContainsKey(k)).ToHashSet();
            var removed = nodesA.Keys.Where(k => !nodesB.ContainsKey(k)).ToHashSet();
            // Moved: cut out and put back elsewhere, so both what comes before it and what comes after it changed,
            // among steps that were already there.
            var moved = nodesB.Keys.Where(k => nodesA.ContainsKey(k) && !predsA[k].SetEquals(predsB[k])
                && predsB[k].All(nodesA.ContainsKey) && !Targets(nodesA[k]).SetEquals(Targets(nodesB[k]))).ToHashSet();
            var owner = b.Items[flow].Key == unit ? "" : " in " + b.Items[flow].Label;
            string Names(IEnumerable<string> keys, Dictionary<string, Item> nodes) =>
                string.Join(", ", keys.Where(nodes.ContainsKey).Select(k => nodes[k].Label).Order(StringComparer.Ordinal));
            foreach (var k in b.Order.Where(added.Contains))
            {
                var n = nodesB[k];
                var place = new List<string>();
                if (Names(predsB[k], nodesB) is { Length: > 0 } after) place.Add("after " + after);
                if (Names(Targets(n), nodesB) is { Length: > 0 } before) place.Add("before " + before);
                yield return $"step {n.Label} added{owner} ({What(n)}){(place.Count > 0 ? ", " + string.Join(", ", place) : "")}";
            }
            foreach (var k in a.Order.Where(removed.Contains))
                yield return $"step {nodesA[k].Label} removed{owner} ({What(nodesA[k])})";
            foreach (var k in b.Order.Where(moved.Contains))
                yield return $"step {nodesB[k].Label} moved{owner}: now after {Names(predsB[k], nodesB)} (was after {Names(predsA[k], nodesA)})";
            foreach (var k in b.Order.Where(k => nodesB.ContainsKey(k) && nodesA.ContainsKey(k)))
            {
                var (o, n) = (nodesA[k], nodesB[k]);
                var what = new List<string>();
                what.AddRange(Props(o, n, $"step {o.Label}{owner}", texts, kinds));
                if (!moved.Contains(k))
                    foreach (var label in o.Next.Select(x => x.label).Concat(n.Next.Select(x => x.label)).Distinct())
                    {
                        var was = o.Next.Where(x => x.label == label).Select(x => x.target).ToHashSet();
                        var now = n.Next.Where(x => x.label == label).Select(x => x.target).ToHashSet();
                        if (was.SetEquals(now)) continue;
                        // Explained by a step that was added, removed or moved: that line says it.
                        if (was.Concat(now).Any(t => added.Contains(t) || removed.Contains(t) || moved.Contains(t))) continue;
                        what.Add($"{Branch(label)} now goes to {Or(Names(now, nodesB))} (was {Or(Names(was, nodesA))})");
                    }
                if (Changed($"step {o.Label}{owner}", o, n, what) is { } line) yield return line;
            }
        }
    }

    static HashSet<string> Targets(Item n) => n.Next.Select(x => x.target).ToHashSet();
    static string Or(string names) => names.Length > 0 ? names : "nothing";
    static string Branch(string label) => label switch
    {
        "Sequence" => "next step", "True" => "true branch", "False" => "false branch", "Cycle" => "loop body", "Otherwise" => "otherwise branch",
        _ => label.StartsWith("case ", StringComparison.Ordinal) ? label : Word(label) + " branch",
    };

    // What a step does, in a few words.
    static string What(Item n)
    {
        string P(string key) => n.Props.GetValueOrDefault(key) ?? "";
        return n.Kind switch
        {
            "AssignNode" => "set " + string.Join(", ", n.Props.Where(p => p.Key.StartsWith("set ", StringComparison.Ordinal)).Select(p => p.Key[4..] + " = " + p.Value)),
            "IfNode" => "if " + P("Condition"),
            "ForEachNode" => "for each " + P("RecordList"),
            "SQLNode" => "SQL",
            "RaiseExceptionNode" => "raise " + P("Exception"),
            "DestinationNode" => "go to " + P("Destination"),
            "RefreshDataNode" => "refresh " + P("DataSource"),
            "ExceptionHandlerNode" => "on exception " + P("Exception"),
            "StartNode" => "start", "EndNode" => "end",
            _ when n.Props.ContainsKey("Action") => "call " + P("Action"),
            _ => Human(n.Kind),
        } is var text && text.Length > 80 ? text[..77] + "..." : text;
    }

    // ---- Properties ----

    // Code: always shown as a size in the details, and as changed lines in --md and --out.
    static readonly HashSet<string> Code = new(StringComparer.Ordinal) { "StyleSheet", "JavaScript", "Statement" };

    // The property changes between two versions of one element. Long text and code also go to `texts` (with the
    // changed lines) for the Markdown and HTML reports; the details stay one short line each.
    static IEnumerable<string> Props(Item o, Item n, string subject, List<Text> texts, Dictionary<string, int> kinds)
    {
        // A secret site property or a sensitive REST input: its value is never printed.
        var secret = o.Props.GetValueOrDefault("IsSecret") == "True" || n.Props.GetValueOrDefault("IsSecret") == "True"
            || o.Props.GetValueOrDefault("IsSensitive") == "Yes" || n.Props.GetValueOrDefault("IsSensitive") == "Yes";
        foreach (var key in o.Props.Keys.Concat(n.Props.Keys).Distinct().Order(StringComparer.Ordinal))
        {
            var (was, now) = (o.Props.GetValueOrDefault(key) ?? "", n.Props.GetValueOrDefault(key) ?? "");
            if (was == now) continue;
            var word = key == "OriginalName" ? (n.Props.Any(p => p.Key.EndsWith("Placement", StringComparison.Ordinal) && p.Value.Contains("Header")) ? "header name" : "external name") : Word(key);
            var kind = key.Contains(' ') && key != "ui flow" ? key[..key.IndexOf(' ')] : word; // "argument X" -> "argument"
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            var longText = Code.Contains(key) || was.Length > 80 || now.Length > 80 || was.Contains('\n') || now.Contains('\n');
            if (key == "Description") yield return "description changed";
            else if (secret && key == "DefaultValue") yield return "default value changed (secret: values not shown)";
            else if (key == "ui flow") yield return $"moved from UI flow {was} to {now}: its URL changes";
            else if (Squash(was) == Squash(now)) yield return $"{word} changed (whitespace only)";
            // A named part (an index, an argument, an assignment, a widget attribute) comes and goes.
            else if (key.Contains(' ') && (was.Length == 0 || now.Length == 0) && !longText)
                yield return was.Length == 0 ? $"{word} added: {Value(now)}" : $"{word} removed (was {Value(was)})";
            else if (longText)
            {
                texts.Add(new($"{subject}: {word}", Diff(was, now)));
                // Short SQL stays readable in the line itself; style sheets and scripts are given as a size.
                yield return key == "Statement" && was.Length <= 80 && now.Length <= 80 && !was.Contains('\n') && !now.Contains('\n') ? $"{word} {Value(was)} -> {Value(now)}"
                    : was.Length == 0 ? $"{word} added ({now.Length} characters)" : now.Length == 0 ? $"{word} removed (was {was.Length} characters)"
                    : $"{word} changed ({was.Length} -> {now.Length} characters, {now.Length - was.Length:+#;-#;0})";
            }
            else yield return $"{word} {Value(was)} -> {Value(now)}";
        }
    }

    static string Squash(string text) => Regex.Replace(text, @"\s+", " ").Trim();
    static string Value(string v) => v switch { "" => "(none)", "True" => "yes", "False" => "no", _ => v };

    // A text change for the Markdown and HTML reports: what it is, and its changed lines.
    internal sealed record Text(string Subject, List<string> Lines);

    // Changed lines, "- " before and "+ " after, with up to 2 unchanged lines around them ("  "). At most `cap` lines.
    internal static List<string> Diff(string before, string after, int cap = 60)
    {
        static string[] Split(string s) => s.Length == 0 ? [] : s.Replace("\r\n", "\n").Split('\n');
        static string Cut(string line) => line.Length <= 500 ? line : line[..497] + "...";
        var (a, b) = (Split(before), Split(after));
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
        var end = 0;
        while (end < a.Length - start && end < b.Length - start && a[a.Length - 1 - end] == b[b.Length - 1 - end]) end++;
        var lines = new List<string>();
        for (var i = Math.Max(0, start - 2); i < start; i++) lines.Add("  " + a[i]);
        var (x, y) = (a[start..(a.Length - end)], b[start..(b.Length - end)]);
        if ((long)x.Length * y.Length <= 250_000)
        {
            // Longest common subsequence of the changed middle, so a moved block reads as lines out and in.
            var lcs = new int[x.Length + 1, y.Length + 1];
            for (var i = x.Length - 1; i >= 0; i--)
                for (var j = y.Length - 1; j >= 0; j--)
                    lcs[i, j] = x[i] == y[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int p = 0, q = 0;
            while (p < x.Length || q < y.Length)
                if (p < x.Length && q < y.Length && x[p] == y[q]) { lines.Add("  " + x[p]); p++; q++; }
                else if (p < x.Length && (q == y.Length || lcs[p + 1, q] >= lcs[p, q + 1])) lines.Add("- " + x[p++]);
                else lines.Add("+ " + y[q++]);
        }
        else { lines.AddRange(x.Select(l => "- " + l)); lines.AddRange(y.Select(l => "+ " + l)); }
        for (var i = a.Length - end; i < Math.Min(a.Length, a.Length - end + 2); i++) lines.Add("  " + a[i]);
        lines = lines.Select(Cut).ToList();
        if (lines.Count > cap) lines = [.. lines.Take(cap), $"... {lines.Count - cap} more lines"];
        return lines;
    }

    // "IsMandatory" -> "mandatory", "MaxRecords" -> "max records", "HTTPMethod" -> "HTTP method"; keys that are
    // already words ("set X") stay.
    internal static string Word(string key)
    {
        if (key.Contains(' ')) return key;
        if (key == "JavaScript") return key;
        var core = key == "DataType" ? "Type" : key.StartsWith("Is", StringComparison.Ordinal) && key.Length > 2 && char.IsUpper(key[2]) ? key[2..] : key;
        return Words(core);
    }

    // Words of a name, lowercase except acronyms: "URLPath" -> "URL path".
    static string Words(string name) => string.Join(" ", Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ")
        .Split(' ').Select(w => w.Length > 1 && w.All(char.IsUpper) ? w : w.ToLowerInvariant()));

    // ---- Names ----

    static readonly Dictionary<string, string> Kinds = new(StringComparer.Ordinal)
    {
        ["ServerEntity"] = "entity", ["Entity"] = "entity", ["StaticEntity"] = "static entity", ["ClientEntity"] = "local entity",
        ["EntityAttribute"] = "attribute", ["StructureAttribute"] = "attribute", ["Attribute"] = "attribute", ["RestStructureAttribute"] = "attribute",
        ["InputParameter"] = "input", ["OutputParameter"] = "output", ["LocalVariable"] = "local variable",
        ["ScreenAggregate"] = "aggregate", ["AggregateNode"] = "aggregate", ["AliasedSource"] = "source", ["SQLNode"] = "SQL",
        ["RestClient"] = "consumed REST API", ["RestService"] = "exposed REST API", ["RESTMethod"] = "REST method", ["RestStructure"] = "REST structure",
        ["UserException"] = "exception", ["TextWidget"] = "text", ["MobileTheme"] = "theme", ["WebTheme"] = "theme",
        ["BlockEvent"] = "event", ["MobileBlockInstanceWidget"] = "block instance", ["WebBlockInstanceWidget"] = "block instance",
    };
    internal static string Human(string kind) => Kinds.TryGetValue(kind, out var name) ? name
        : Regex.Replace(Words(Regex.Replace(kind, "(Node|Widget|Signature)$", "")), "^rest ", "REST ");

    static string Area(string kind) => Human(kind) switch
    {
        "entity" or "static entity" or "local entity" or "structure" => "data",
        "server action" or "client action" or "service action" or "exception" => "logic",
        "screen" or "block" or "theme" or "script" or "image" or "resource" or "email" => "ui",
        var h when h.Contains("REST") || h.Contains("SOAP") || h.Contains("rest ") || h.Contains("soap") => "integrations",
        "role" => "security",
        "site property" or "timer" or "process" or "client variable" or "session variable" => "settings",
        "reference" => "references",
        _ => "other",
    };

    static string Plural(int count, string word) => $"{count} {word}{(count == 1 ? "" : word.EndsWith('s') ? "es" : "s")}";
    static string Names(List<string> names) => names.Count <= 8 ? string.Join(", ", names) : string.Join(", ", names.Take(8)) + $" and {names.Count - 8} more";
}
