using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OutSystems.Model;
using OutSystems.Model.Data;
using OutSystems.Model.Logic.Nodes;

namespace OmlCli;

// `oml test scenarios <source.oml> <staged.oml> [--out <folder>]`: the scenarios a change needs, from the model, so a
// person knows what to test and an agent knows what to automate. Two kinds:
//   - BDD (Given/When/Then) for every public server or service action the change created or changed: the main path, each
//     If branch, each exception it raises or handles, each mandatory input left empty, each entity it writes. These become
//     BDDFramework tests with `oml test scaffold` (server logic is what BDD tests; screens are not).
//   - QA for every screen the change created or changed: open it, fill its named fields, click each button or link with an
//     action, and expect the messages and screens that action leads to; an empty-list scenario per screen aggregate. Each
//     screen also gets a runnable qa.json skeleton (baseUrl to fill in) for `oml qa run`.
// The list is what the flows show; an agent writes the values and the expected results the model cannot know.
static class Scenarios
{
    sealed record Scenario(string Title, List<string> Given, List<string> When, List<string> Then);

    public static int Run(List<string> args)
    {
        if (args.Count < 4 || args[1] != "scenarios") throw new CliError("Usage: oml test scenarios <source.oml> <staged.oml> [--out <folder>]", 2);
        var (source, staged) = (Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
        string? outDir = null;
        for (var i = 4; i < args.Count; i++)
            if (args[i] == "--out" && i + 1 < args.Count) outDir = Path.GetFullPath(args[++i]);
            else throw new CliError($"test scenarios accepts --out <folder>; got {args[i]}.", 2);
        outDir ??= Path.Combine(Path.GetDirectoryName(staged)!, Path.GetFileNameWithoutExtension(staged) + ".scenarios");
        if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
            throw new CliError($"The folder {outDir} is not empty. Give a new --out folder.", 2);

        Dictionary<string, string> before;
        using (var open = Model.Load(source)) before = Signatures(open);
        using var after = Model.Load(staged);
        var now = Signatures(after);
        bool Touched(string key) => !before.TryGetValue(key, out var sig) || sig != now[key];

        var actions = new List<(string name, List<Scenario> scenarios, string status)>();
        foreach (var a in after.ServerActions.Cast<IObjectSignature>().Concat(after.ServiceActions).Where(a => Reflect.Get(a, "Public") is true))
        {
            var key = "action:" + Model.NameOf(a);
            if (!Touched(key)) continue;
            actions.Add((Model.NameOf(a)!, ActionScenarios(after, a), before.ContainsKey(key) ? "changed" : "new"));
        }
        var screens = new List<(string name, List<Scenario> scenarios, JsonObject qa, string status)>();
        foreach (var s in ScreensOf(after))
        {
            var key = "screen:" + CheckRules.PathOf(s);
            if (!Touched(key)) continue;
            var (scenarios, qa) = ScreenScenarios(after, s);
            screens.Add((Model.NameOf(s)!, scenarios, qa, before.ContainsKey(key) ? "changed" : "new"));
        }

        Directory.CreateDirectory(outDir);
        var md = new StringBuilder();
        md.Append($"# Scenarios for {Model.NameOf(after)}\n\nFrom `oml test scenarios`: {actions.Count} public action(s) and {screens.Count} screen(s) the change created or changed. ");
        md.Append("The list comes from the flows; fill in the values and the results the model cannot know.\n");
        if (actions.Count > 0)
        {
            md.Append("\n## Server logic (BDD)\n\nBuild the tests with: `oml test scaffold <staged.oml> <Action>... --bdd <BDDFramework.oml>`\n");
            foreach (var (name, scenarios, status) in actions) Section(md, $"{name} ({status})", scenarios);
        }
        var qaFiles = new List<string>();
        if (screens.Count > 0)
        {
            md.Append("\n## Screens (QA)\n\nEach screen has a qa.json skeleton in `qa/`. Set `baseUrl`, check the steps, then run: `oml qa run qa/<Screen>.qa.json --save <Screen>.replay.json`\n");
            Directory.CreateDirectory(Path.Combine(outDir, "qa"));
            foreach (var (name, scenarios, qa, status) in screens)
            {
                Section(md, $"{name} ({status})", scenarios);
                var file = Path.Combine(outDir, "qa", name + ".qa.json");
                File.WriteAllText(file, qa.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
                qaFiles.Add(file);
            }
        }
        if (actions.Count + screens.Count == 0) md.Append("\nNo public action and no screen was created or changed: nothing to test here with BDD or QA.\n");
        var mdFile = Path.Combine(outDir, "scenarios.md");
        File.WriteAllText(mdFile, md.ToString());
        return Cli.Emit(new
        {
            scenarios = mdFile, folder = outDir,
            actions = actions.Select(a => new { name = a.name, status = a.status, scenarios = a.scenarios.Count }).ToArray(),
            screens = screens.Select(s => new { name = s.name, status = s.status, scenarios = s.scenarios.Count }).ToArray(),
            qa = qaFiles,
            bdd = actions.Count == 0 ? null : $"oml test scaffold {staged} {string.Join(" ", actions.Select(a => a.name))} --bdd <BDDFramework.oml>",
            next = "Show scenarios.md to the user: it is what to test. Fill in values and expected results with them, then automate: BDD for the actions, the qa files for the screens.",
        });
    }

    static void Section(StringBuilder md, string title, List<Scenario> scenarios)
    {
        md.Append($"\n### {title}\n");
        foreach (var s in scenarios)
        {
            md.Append($"\n**Scenario: {s.Title}**\n\n");
            void Lines(string word, List<string> lines) { for (var i = 0; i < lines.Count; i++) md.Append($"- {(i == 0 ? word : "And")} {lines[i]}\n"); }
            Lines("Given", s.Given); Lines("When", s.When); Lines("Then", s.Then);
        }
    }

    // A fingerprint per public action and screen: the kinds, names and expressions of everything under it. A change in
    // any of them marks the element as changed.
    static Dictionary<string, string> Signatures(IESpace m)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in m.ServerActions.Cast<IObjectSignature>().Concat(m.ServiceActions).Where(a => Reflect.Get(a, "Public") is true))
            result["action:" + Model.NameOf(a)] = Fingerprint(a);
        foreach (var s in ScreensOf(m)) result["screen:" + CheckRules.PathOf(s)] = Fingerprint(s);
        return result;
    }

    static string Fingerprint(IObjectSignature root)
    {
        var sb = new StringBuilder();
        foreach (var o in Walk(root))
        {
            sb.Append(ElementPaths.Kind(o)).Append(':').Append(Model.NameOf(o)).Append(';');
            foreach (var key in new[] { "Condition", "Value", "Variable" })
                if (Reflect.Property(o, key) is not null && Reflect.Get(o, key) is IExpression e) sb.Append(e.Text).Append(';');
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    static IEnumerable<IObjectSignature> Walk(IObjectSignature root)
    {
        var seen = new HashSet<IObjectSignature>();
        var pending = new Stack<IObjectSignature>(ElementPaths.Children(root));
        while (pending.TryPop(out var o))
        {
            if (!seen.Add(o)) continue;
            yield return o;
            foreach (var c in ElementPaths.Children(o)) pending.Push(c);
        }
    }

    static IEnumerable<IObjectSignature> ScreensOf(IESpace m) =>
        m.MobileFlows.SelectMany(f => f.Nodes).Cast<IObjectSignature>().Concat(m.WebFlows.SelectMany(f => f.Nodes))
            .Where(s => s is OutSystems.Model.UI.IScreenSignature && !Inspect.IsBlock(s)).Distinct();

    static string Text(object? expression) => expression is IExpression e ? (e.Text ?? "").Trim() : "";
    static string Kind(object o) => o.GetType().Name;

    // ---- server logic ----

    static List<Scenario> ActionScenarios(IESpace m, IObjectSignature action)
    {
        var name = Model.NameOf(action)!;
        var inputs = ((Reflect.Get(action, "InputParameters") as System.Collections.IEnumerable)?.Cast<object>() ?? [])
            .Select(p => (name: Model.NameOf(p)!, type: Model.Describe(m, Reflect.Get(p, "DataType") as OutSystems.Model.Types.ITypeSignature), mandatory: Reflect.Get(p, "IsMandatory") is true)).ToList();
        var outputs = ((Reflect.Get(action, "OutputParameters") as System.Collections.IEnumerable)?.Cast<object>() ?? []).Select(p => Model.NameOf(p)!).ToList();
        var nodes = FlowLayout.Nodes(action).Cast<IObjectSignature>().ToList();
        var writes = new List<string>();
        foreach (var n in nodes.Where(n => n is IExecuteServerActionNode))
            if (Reflect.Get(n, "Action") is IObjectSignature { Parent: IEntitySignature entity } a && Model.NameOf(a) is { } an && !an.StartsWith("Get"))
                writes.Add($"{(an.StartsWith("Delete") ? "deletes" : "saves")} a {Model.NameOf(entity)} record");
        var scenarios = new List<Scenario>();
        var given = inputs.Count == 0 ? ["nothing special"] : inputs.Select(i => $"{i.name} is a valid {i.type} (<FILL: value>)").ToList();
        scenarios.Add(new Scenario("Main path", given, [$"{name} runs"],
            [.. (outputs.Count == 0 ? ["it ends without an error"] : outputs.Select(o => $"{o} is <FILL: expected value>")), .. writes.Distinct().Select(w => $"it {w} (check it with oml test data check)")]));
        foreach (var n in nodes)
        {
            var kind = Kind(n);
            if (n is IIfNode)
            {
                var condition = Text(Reflect.Get(n, "Condition"));
                scenarios.Add(new Scenario($"{condition} is true", [$"the inputs make {condition} true (<FILL: values>)"], [$"{name} runs"], ["it follows the True branch: <FILL: expected result>"]));
                scenarios.Add(new Scenario($"{condition} is false", [$"the inputs make {condition} false (<FILL: values>)"], [$"{name} runs"], ["it follows the False branch: <FILL: expected result>"]));
            }
            else if (kind.Contains("Switch") && Reflect.Get(n, "Conditions") is System.Collections.IEnumerable conditions)
            {
                foreach (var c in conditions.Cast<object>())
                {
                    var condition = Text(Reflect.Get(c, "Condition"));
                    scenarios.Add(new Scenario($"Switch case {condition}", [$"the inputs make {condition} true (<FILL: values>)"], [$"{name} runs"], ["it follows that case: <FILL: expected result>"]));
                }
                scenarios.Add(new Scenario("Switch otherwise", ["no case of the Switch holds (<FILL: values>)"], [$"{name} runs"], ["it follows Otherwise: <FILL: expected result>"]));
            }
            else if (kind.Contains("RaiseException") || ElementPaths.Kind(n).Contains("Raise", StringComparison.Ordinal))
            {
                var exception = Reflect.Get(n, "Exception") is { } raised ? Model.NameOf(raised) ?? "an exception" : "an exception";
                scenarios.Add(new Scenario($"Raises {exception}", [$"the inputs reach the Raise {exception} node (<FILL: values>)"], [$"{name} runs"], [$"it raises {exception} with the message <FILL: text>", "nothing is saved"]));
            }
            else if (kind.Contains("ExceptionHandler") || ElementPaths.Kind(n).Contains("ExceptionHandler", StringComparison.Ordinal))
            {
                var exception = Reflect.Get(n, "Exception") is { } e ? Model.NameOf(e) : "an error";
                scenarios.Add(new Scenario($"Handles {exception}", [$"a step inside fails with {exception} (<FILL: how>)"], [$"{name} runs"], ["the handler runs: <FILL: expected result>"]));
            }
        }
        foreach (var i in inputs.Where(i => i.mandatory && (i.type == "Text" || i.type.EndsWith(" Identifier"))))
            scenarios.Add(new Scenario($"{i.name} is empty", [i.type == "Text" ? $"{i.name} is \"\"" : $"{i.name} is NullIdentifier()"], [$"{name} runs"], ["<FILL: it refuses with a clear message, or what the user expects>"]));
        return scenarios;
    }

    // ---- screens ----

    static (List<Scenario>, JsonObject) ScreenScenarios(IESpace m, IObjectSignature screen)
    {
        var name = Model.NameOf(screen)!;
        var widgets = Walk(screen).ToList();
        var fields = widgets.Where(w => ElementPaths.Kind(w) is "Input" or "TextArea" or "Dropdown" or "Checkbox" or "Switch" && Model.NameOf(w) is { Length: > 0 }).ToList();
        var clickables = widgets.Where(w => ElementPaths.Kind(w) is "Button" or "Link" or "ButtonGroup" && Reflect.Property(w, "OnClick") is not null && Reflect.Get(w, "OnClick") is { } evt && Reflect.Get(evt, "Destination") is not null).ToList();
        var scenarios = new List<Scenario>();
        var steps = new JsonArray(new JsonObject { ["open"] = name });
        scenarios.Add(new Scenario("Opens", ["a user with access to the screen"], [$"they open {name}"], ["it shows without an error message"]));
        var fill = new JsonObject();
        foreach (var f in fields) fill[Model.NameOf(f)!] = "<FILL: value>";
        foreach (var w in clickables)
        {
            var target = Model.NameOf(w) is { Length: > 0 } wn ? wn : null;
            var destination = Reflect.Get(Reflect.Get(w, "OnClick")!, "Destination") as IObjectSignature;
            var dName = destination is null ? "" : Model.NameOf(destination) ?? "";
            var label = target ?? $"the {ElementPaths.Kind(w)} that runs {dName}";
            var given = fields.Count == 0 ? new List<string> { $"the user is on {name}" } : [$"the user is on {name}", $"they filled {string.Join(", ", fields.Select(f => Model.NameOf(f)))} (<FILL: values>)"];
            // One scenario per path through the action: the If conditions on the way, and what the user then sees.
            var paths = destination is OutSystems.Model.UI.IScreenSignature ? [([], new List<Outcome> { new("screen", dName, null) })]
                : destination is not null ? Paths(destination) : [];
            if (paths.Count == 0) paths.Add(([], []));
            foreach (var (conditions, outcomes) in paths)
            {
                var then = outcomes.Select(o => o.Kind == "screen" ? $"the screen {o.Text} opens" : $"a {o.Type ?? "feedback"} message shows{(o.Text is null ? "" : $": \"{o.Text}\"")}").ToList();
                if (then.Count == 0) then.Add("<FILL: what the user sees>");
                var title = $"Click {label}" + (conditions.Count == 0 ? "" : ": " + string.Join(", ", conditions));
                scenarios.Add(new Scenario(title, [.. given, .. conditions.Select(c => $"{c} (<FILL: values that make it so>)")], [$"they click {label}"], then));
            }
            if (target is null) continue; // an unnamed widget cannot be a qa target: the scenario tells the agent to name it
            // The qa skeleton follows the path that ends in a success message, else the first one.
            var main = paths.FirstOrDefault(p => p.outcomes.Any(o => o.Type == "success")) is { outcomes: not null } ok ? ok : paths[0];
            var expect = new JsonObject();
            if (main.outcomes.FirstOrDefault(o => o.Kind == "feedback") is { } msg) expect["feedback"] = msg.Text is null ? (JsonNode)(msg.Type ?? "success") : new JsonObject { ["type"] = msg.Type ?? "success", ["text"] = msg.Text };
            if (main.outcomes.FirstOrDefault(o => o.Kind == "screen") is { } scr) expect["screen"] = scr.Text;
            if (fill.Count > 0) steps.Add(new JsonObject { ["fill"] = fill.DeepClone() });
            var step = new JsonObject { ["click"] = target };
            if (expect.Count > 0) step["expect"] = expect;
            steps.Add(step);
        }        foreach (var w in clickables.Where(w => Model.NameOf(w) is not { Length: > 0 }))
            scenarios.Add(new Scenario($"Name the {ElementPaths.Kind(w)} so tests can find it", [$"a {ElementPaths.Kind(w)} on {name} has no Name"], ["an agent writes the qa file"], ["it gives the widget a Name (spec setProperty), so a qa step can click it"]));
        foreach (var agg in widgets.Where(w => w.GetType().Name.Contains("Aggregate") || ElementPaths.Kind(w).Contains("Aggregate")))
            scenarios.Add(new Scenario($"{Model.NameOf(agg)} returns nothing", [$"no record matches {Model.NameOf(agg)} (<FILL: how>)"], [$"they open {name}"], ["the screen shows its empty state: <FILL: text>"]));
        var qa = new JsonObject { ["baseUrl"] = "<FILL: https://<environment>/<App>/>", ["steps"] = steps };
        return (scenarios, qa);
    }
    sealed record Outcome(string Kind, string? Text, string? Type);

    // Every path from the Start of a screen action to an End (at most 16), with the If conditions taken on the way and
    // the messages and screens on it, in order.
    static List<(List<string> conditions, List<Outcome> outcomes)> Paths(IObjectSignature flow)
    {
        var result = new List<(List<string>, List<Outcome>)>();
        var nodes = FlowLayout.Nodes(flow);
        var start = nodes.FirstOrDefault(n => n is IStartNode);
        if (start is null) return result;
        void Walk(IFlowNode node, List<string> conditions, List<Outcome> outcomes, HashSet<IFlowNode> seen)
        {
            if (result.Count >= 16 || !seen.Add(node)) { if (result.Count < 16) result.Add((conditions, outcomes)); return; }
            if (Message(node) is { } m) outcomes = [.. outcomes, m];
            else if (node.GetType().Name.Contains("Destination") && Reflect.Get(node, "Destination") is IObjectSignature to) outcomes = [.. outcomes, new Outcome("screen", Model.NameOf(to), null)];
            var edges = FlowLayout.Edges(node).ToList();
            if (edges.Count == 0) { result.Add((conditions, outcomes)); return; }
            if (node is IIfNode)
            {
                var condition = Text(Reflect.Get(node, "Condition"));
                foreach (var (target, kind) in edges)
                    Walk(target, [.. conditions, $"{condition} is {(kind == "True" ? "true" : "false")}"], outcomes, [.. seen]);
                return;
            }
            foreach (var (target, _) in edges) Walk(target, conditions, outcomes, [.. seen]);
        }
        Walk(start, [], [], []);
        return result;
    }

    // A Message node (the feedback message), or a call to the Message client action.
    static Outcome? Message(object n)
    {
        var node = n.GetType().Name.Contains("Message", StringComparison.Ordinal) && Reflect.Property(n, "Message") is not null;
        if (!node && !(Reflect.Get(n, "Action") is { } a && Model.NameOf(a) is "Message" or "FeedbackMessage")) return null;
        var args = node ? [Text(Reflect.Get(n, "Message")), Reflect.Get(n, "MessageType")?.ToString() ?? Reflect.Get(n, "Type")?.ToString() ?? ""]
            : ((Reflect.Get(n, "Arguments") as System.Collections.IEnumerable)?.Cast<object>() ?? []).Select(x => Text(Reflect.Get(x, "Value"))).ToList();
        var text = args.FirstOrDefault(x => x.StartsWith('"'))?.Trim('"');
        var type = args.Skip(node ? 1 : 0).FirstOrDefault(x => x.Length > 0 && !x.StartsWith('"') && (node || x.Contains("MessageType", StringComparison.Ordinal)))?.Split('.')[^1].ToLowerInvariant();
        return new Outcome("feedback", text, type);
    }
}