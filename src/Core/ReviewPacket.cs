using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OutSystems.Model;
using Item = OmlCli.ReviewModel.Item;
using Snapshot = OmlCli.ReviewModel.Snapshot;

namespace OmlCli;

// `oml review <before.oml> <after.oml> --prepare <folder>` and `--import <findings.json>`: a blind logic review. No
// model runs inside oml. --prepare writes a packet for a reviewer agent: the changed and added flows as describe text,
// their steps with element paths, the changed expressions before and after, and instructions with the answer format.
// "Blind": the packet holds none of oml's own findings, so the reviewer is not anchored on them. --import checks the
// reviewer's answer against the new module (the path must resolve, the evidence must be in that element) and prints the
// accepted findings in the shape of oml review's own findings, merged with them. Same inputs, same packet.
static class ReviewPacket
{
    internal const string InstructionsFile = "instructions.md", ChangesFile = "changes.md", Rule = "logic-review";
    const int MaxDiff = 40, MaxText = 400;

    internal sealed record Flow(string Path, string Kind, string Change, string Text, List<string> Diff, List<(string path, string what)> Steps, List<Expr> Expressions);
    internal sealed record Expr(string Path, string Property, string Before, string After);
    // Keys: the elements the packet shows (the flows with all they hold, and the elements of the other expressions).
    internal sealed record Packet(string Module, List<Flow> Flows, List<Expr> Other, HashSet<string> Keys);

    // Properties that hold an expression, outside flows: a variable's default, a widget's condition, a SQL statement...
    static readonly HashSet<string> ExpressionKeys = new(StringComparer.Ordinal) { "Condition", "Value", "DefaultValue", "Visible", "Enabled", "Statement", "RecordList", "Source" };
    static bool IsExpression(string key) => ExpressionKeys.Contains(key) || key.StartsWith("set ", StringComparison.Ordinal)
        || key.StartsWith("argument ", StringComparison.Ordinal) || key.StartsWith("attribute ", StringComparison.Ordinal);

    // ---- The packet ----

    internal static Packet Compute(IESpace before, IESpace after, Snapshot a, Snapshot b, bool texts = true)
    {
        var (objectsA, objectsB) = (Objects(before), Objects(after));
        var (kidsA, kidsB) = (a.Items.Values.Where(i => i.Parent is not null).ToLookup(i => i.Parent!), b.Items.Values.Where(i => i.Parent is not null).ToLookup(i => i.Parent!));
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        string PathOf(string key) => Path(after, objectsB, b, key, paths);
        // A flow: the element that holds flow nodes (an action, a screen action, a process...).
        var holders = b.Items.Values.Where(i => i.IsNode && i.Parent is not null).Select(i => i.Parent!).ToHashSet();
        var flows = new List<Flow>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in b.Order.Where(holders.Contains))
        {
            var now = b.Items[key];
            var inB = Descendants(kidsB, key).ToList();
            var old = a.Items.GetValueOrDefault(key);
            var change = old is null ? "added" : "changed";
            var expressions = new List<Expr>();
            if (old is not null)
            {
                var inA = Descendants(kidsA, key).ToDictionary(i => i.Key);
                var same = Same(old, now) && inA.Count == inB.Count && inB.All(i => inA.TryGetValue(i.Key, out var o) && Same(o, i));
                if (same) continue;
                foreach (var i in inB.Prepend(now))
                {
                    var was = i == now ? old : inA.GetValueOrDefault(i.Key);
                    if (was is null) continue;
                    foreach (var prop in i.Props.Keys.Concat(was.Props.Keys).Distinct().Where(Kept).Order(StringComparer.Ordinal))
                        if ((was.Props.GetValueOrDefault(prop) ?? "") is var x && (i.Props.GetValueOrDefault(prop) ?? "") is var y && Squash(x) != Squash(y) && (i.IsNode || IsExpression(prop)))
                            expressions.Add(new(PathOf(i.Key), Review.Word(prop), x, y));
                }
            }
            keys.Add(key);
            keys.UnionWith(inB.Select(i => i.Key));
            var path = PathOf(key);
            var text = texts ? Text(after, path) : "";
            var diff = old is null || !texts ? [] : Review.Diff(objectsA.TryGetValue(key, out var o) ? Text(before, Path(before, objectsA, a, key, new())) : "", text, MaxDiff);
            var steps = InFlowOrder(inB.Where(i => i.IsNode && i.Parent == key).ToList(), PathOf)
                .Where(i => i.Kind is not ("StartNode" or "EndNode")).Select(i => (PathOf(i.Key), Review.What(i))).ToList();
            flows.Add(new(path, Review.Human(now.Kind), change, text, diff, steps, Sorted(expressions)));
        }
        // Expressions that changed outside flows: a screen's variable default, a widget's Visible, an aggregate filter.
        var other = new List<Expr>();
        foreach (var key in b.Order)
        {
            var (i, was) = (b.Items[key], a.Items.GetValueOrDefault(key));
            if (was is null || keys.Contains(key) || InFlow(b, i, holders)) continue;
            foreach (var prop in i.Props.Keys.Concat(was.Props.Keys).Distinct().Where(p => Kept(p) && IsExpression(p)).Order(StringComparer.Ordinal))
                if ((was.Props.GetValueOrDefault(prop) ?? "") is var x && (i.Props.GetValueOrDefault(prop) ?? "") is var y && Squash(x) != Squash(y))
                {
                    other.Add(new(PathOf(key), Review.Word(prop), x, y));
                    keys.Add(key);
                }
        }
        // The model's walk order varies between loads: paths give the same packet for the same modules.
        return new(b.Module, flows.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(), Sorted(other), keys);
    }

    static List<Expr> Sorted(List<Expr> list) => list.OrderBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.Property, StringComparer.Ordinal).ToList();

    // A flow's nodes as they run: from Start, each branch in label order (true before false), then any node that
    // nothing reaches, by path.
    static List<Item> InFlowOrder(List<Item> nodes, Func<string, string> path)
    {
        var byKey = nodes.ToDictionary(n => n.Key);
        var order = new List<Item>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(Item n)
        {
            if (!seen.Add(n.Key)) return;
            order.Add(n);
            foreach (var (_, target) in n.Next.OrderBy(x => x.label == "False" ? 1 : 0).ThenBy(x => x.label, StringComparer.Ordinal))
                if (byKey.TryGetValue(target, out var next)) Walk(next);
        }
        foreach (var start in nodes.Where(n => n.Kind == "StartNode").OrderBy(n => path(n.Key), StringComparer.Ordinal)) Walk(start);
        foreach (var rest in nodes.Where(n => !seen.Contains(n.Key)).OrderBy(n => path(n.Key), StringComparer.Ordinal)) Walk(rest);
        return order;
    }

    // A description or a public flag is not logic; a whitespace-only change is not either.
    static bool Kept(string prop) => prop is not ("Description" or "Public");
    static bool Same(Item x, Item y) => x.Name == y.Name && x.Next.SequenceEqual(y.Next)
        && x.Props.Where(p => Kept(p.Key)).Select(p => (p.Key, Squash(p.Value))).SequenceEqual(y.Props.Where(p => Kept(p.Key)).Select(p => (p.Key, Squash(p.Value))));
    static bool InFlow(Snapshot s, Item i, HashSet<string> holders)
    {
        for (var p = i.Parent; p is not null; p = s.Items.GetValueOrDefault(p)?.Parent) if (holders.Contains(p)) return true;
        return false;
    }

    static IEnumerable<Item> Descendants(ILookup<string, Item> kids, string key)
    {
        foreach (var child in kids[key])
        {
            yield return child;
            foreach (var deeper in Descendants(kids, child.Key)) yield return deeper;
        }
    }

    // Every own element of the module by GlobalKey (references are other modules' elements).
    static Dictionary<string, IObjectSignature> Objects(IESpace m)
    {
        var all = new Dictionary<string, IObjectSignature>(StringComparer.Ordinal);
        var stack = new Stack<IObjectSignature>([(IObjectSignature)m]);
        while (stack.Count > 0)
        {
            IObjectSignature[] children;
            try { children = ElementPaths.Children(stack.Pop()).ToArray(); }
            catch (System.Reflection.TargetInvocationException) { continue; }
            foreach (var c in children)
                if (c is not IReference && all.TryAdd(c.GlobalKey.ToString()!, c)) stack.Push(c);
        }
        return all;
    }

    // An element path that resolves to this element alone: its owner's path and its own part when that works
    // (`Orders/SaveOnClick/Check`), else the shortest path, else its name.
    static string Path(IESpace m, Dictionary<string, IObjectSignature> objects, Snapshot s, string key, Dictionary<string, string> cache)
    {
        if (cache.TryGetValue(key, out var known)) return known;
        var item = s.Items[key];
        string path;
        if (!objects.TryGetValue(key, out var o)) path = item.Label;
        else if (item.Parent is { } parent && objects.TryGetValue(parent, out var owner))
        {
            string part;
            try { part = ElementPaths.Segment(o, owner); } catch (Exception e) when (e is ModelError or System.Reflection.TargetInvocationException) { part = item.Label; }
            path = Safe(() => ElementPaths.Best(m, o, Path(m, objects, s, parent, cache) + "/" + part)) ?? item.Label;
        }
        else path = Safe(() => ElementPaths.PathTo(m, o)) ?? item.Label;
        return cache[key] = path;
    }

    static string? Safe(Func<string?> f)
    {
        try { return f(); }
        catch (Exception e) when (e is ModelError or System.Reflection.TargetInvocationException or InvalidOperationException) { return null; }
    }

    static string Text(IESpace m, string path)
    {
        try { return Describe.Text(m, path); }
        catch (Exception e) when (e is ModelError or System.Reflection.TargetInvocationException or InvalidOperationException or NullReferenceException) { return ""; }
    }

    static string Squash(string text) => Regex.Replace(text, @"\s+", " ").Trim();
    static string Cut(string text) => text.Length <= MaxText ? text : text[..(MaxText - 3)] + "...";

    // ---- --prepare ----

    internal static int Prepare(string beforePath, string afterPath, string folder)
    {
        JsonObject output;
        using (var beforeOpen = Model.Borrow(beforePath, "loadBefore"))
        using (var afterOpen = Model.Borrow(afterPath, "loadAfter"))
        {
            var (a, b) = (ReviewModel.Read(beforeOpen.Module), ReviewModel.Read(afterOpen.Module));
            if (a.Key != b.Key)
                throw new CliError($"The two files are different modules ({a.Module} and {b.Module}): review compares two versions of one module.", 2);
            var packet = Compute(beforeOpen.Module, afterOpen.Module, a, b);
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(System.IO.Path.Combine(folder, InstructionsFile), Instructions(packet.Module), new UTF8Encoding(false));
                File.WriteAllText(System.IO.Path.Combine(folder, ChangesFile), Changes(packet), new UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new CliError(e.Message, 2); }
            var expressions = packet.Flows.Sum(f => f.Expressions.Count) + packet.Other.Count;
            output = new JsonObject
            {
                ["module"] = packet.Module, ["packet"] = folder,
                ["files"] = new JsonArray(InstructionsFile, ChangesFile),
                ["flows"] = new JsonArray(packet.Flows.Select(f => (JsonNode?)new JsonObject { ["path"] = f.Path, ["kind"] = f.Kind, ["change"] = f.Change }).ToArray()),
                ["expressions"] = expressions,
            };
            if (packet.Flows.Count == 0 && expressions == 0) output["warnings"] = new JsonArray("No flow or expression changed: there is nothing for a logic review.");
            output["next"] = $"Give a reviewer agent {InstructionsFile} and {ChangesFile} from {folder}, without oml's findings. Save its JSON answer to a file, then run: oml review <before.oml> <after.oml> --import <findings.json>";
        }
        TerminalOutput.PrintNode(output);
        return 0;
    }

    internal static string Instructions(string module) => $$"""
        # Logic review: {{module}}

        You review one change to the OutSystems module {{module}}. {{ChangesFile}} holds only what changed: each changed or added flow as readable text, its steps with their element paths, and the changed expressions before and after.

        Look for logic bugs in the new version:
        - A wrong condition or comparison: > where >= is meant, a missing Not, And and Or mixed up.
        - Off by one: a loop bound, an index, a count, a date range.
        - Null and empty cases: an empty list, an empty text, NullIdentifier(), a division by zero.
        - Missing error handling: a call that can fail with no exception handler, an error that is swallowed.
        - The wrong variable, input or output: a value set to the wrong place, a copy and paste slip.
        - A branch that can never run, or a path that ends without setting an output.

        Do not report style, names or descriptions. Report only what gives a wrong result.

        ## Your answer

        Answer with a JSON array only, no other text. One object for each bug:

        [{ "path": "<element path>", "severity": "high|medium|low", "title": "...", "evidence": "...", "fix": "..." }]

        - path: an element path exactly as {{ChangesFile}} prints it in backticks: a flow, a step or a parameter.
        - severity: high = a wrong result or wrong data; medium = wrong in some cases; low = works now but is fragile.
        - title: what is wrong, in one short sentence.
        - evidence: the expression or line that shows the bug, copied exactly from that element in {{ChangesFile}}.
        - fix: what to change.

        No bugs found: answer [].
        A finding is rejected when its path is not in the module, or when its evidence is not in the element it names.

        """.Replace("\r\n", "\n");

    internal static string Changes(Packet packet)
    {
        var md = new StringBuilder();
        var expressions = packet.Flows.Sum(f => f.Expressions.Count) + packet.Other.Count;
        md.Append("# Changes: ").Append(packet.Module).Append("\n\n");
        md.Append($"{Count(packet.Flows.Count, "flow")} to review ({packet.Flows.Count(f => f.Change == "changed")} changed, {packet.Flows.Count(f => f.Change == "added")} added), {Count(expressions, "changed expression")}. ")
          .Append("Text in `backticks` before a colon is an element path: use it as the path of a finding.\n");
        if (packet.Flows.Count == 0 && expressions == 0) md.Append("\nNo flow or expression changed.\n");
        foreach (var f in packet.Flows)
        {
            md.Append($"\n## `{f.Path}` ({f.Kind}, {f.Change})\n\n");
            md.Append("```text\n").Append(f.Text.Length > 0 ? f.Text : "(no readable text)").Append("\n```\n");
            if (f.Steps.Count > 0)
            {
                md.Append("\nSteps:\n");
                foreach (var (path, what) in f.Steps) md.Append($"- `{path}`: {Cut(what)}\n");
            }
            if (f.Diff.Count > 0)
            {
                md.Append("\nWhat changed in the text (- before, + after):\n\n```diff\n");
                foreach (var line in f.Diff) md.Append(line).Append('\n');
                md.Append("```\n");
            }
            WriteExpressions(md, "Changed expressions:", f.Expressions);
        }
        if (packet.Other.Count > 0) { md.Append("\n## Other changed expressions\n"); WriteExpressions(md, "", packet.Other); }
        return md.ToString();
    }

    static void WriteExpressions(StringBuilder md, string title, List<Expr> list)
    {
        if (list.Count == 0) return;
        if (title.Length > 0) md.Append('\n').Append(title).Append('\n');
        else md.Append('\n');
        foreach (var e in list)
            md.Append($"- `{e.Path}`: {e.Property}: {Code(e.Before)} -> {Code(e.After)}\n");
    }

    static string Code(string value) => value.Length == 0 ? "(none)" : "`" + Cut(Squash(value)).Replace("`", "'") + "`";
    static string Count(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    // ---- --import ----

    // The reviewer's answer: a JSON array of findings, or an object with a "findings" array.
    internal static JsonArray ReadFindings(string path)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException e) { throw new CliError($"Findings file {path} is not valid JSON: {e.Message} Save the reviewer's answer as a JSON array.", 2); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new CliError(e.Message, 2); }
        return node switch
        {
            JsonArray array => array,
            JsonObject { } o when o["findings"] is JsonArray array => array,
            _ => throw new CliError($"Findings file {path} must hold a JSON array of findings: [{{\"path\", \"severity\", \"title\", \"evidence\", \"fix\"}}].", 2),
        };
    }

    internal sealed record Imported(List<JsonObject> Accepted, List<JsonObject> Rejected);

    // Each finding is accepted only when its path resolves to one element of the new module and its evidence is text of
    // that element (whitespace and backticks aside). Accepted findings take the shape of oml review's own findings.
    internal static Imported Validate(IESpace before, IESpace after, Snapshot a, Snapshot b, JsonArray findings)
    {
        var packet = Compute(before, after, a, b, texts: false); // only which elements it shows
        var kids = b.Items.Values.Where(i => i.Parent is not null).ToLookup(i => i.Parent!);
        var (accepted, rejected) = (new List<JsonObject>(), new List<JsonObject>());
        for (var index = 0; index < findings.Count; index++)
        {
            var f = findings[index] as JsonObject;
            string Field(string name) => f?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
            var (path, severity, title, evidence, fix) = (Field("path"), Field("severity").ToLowerInvariant(), Field("title"), Field("evidence"), Field("fix"));
            void Reject(string reason) => rejected.Add(new JsonObject { ["index"] = index, ["path"] = path, ["title"] = title, ["reason"] = reason });
            if (f is null) { Reject("not a JSON object"); continue; }
            if (path.Length == 0) { Reject("no path: give the element path from " + ChangesFile); continue; }
            IObjectSignature element;
            try { element = ElementPaths.Resolve(after, path); }
            catch (AmbiguousPathError e) { Reject("the path matches several elements: " + e.Message); continue; }
            catch (ModelError e) { Reject("the path is not in the new module: " + e.Message); continue; }
            var quote = Squash(evidence.Trim('`'));
            if (quote.Length == 0) { Reject("no evidence: quote the expression or line that shows the bug"); continue; }
            var text = Squash(ElementText(after, element, path, b, kids));
            if (!text.Contains(quote, StringComparison.Ordinal) && !(Expressions.Canonical(quote) is { } canonical && text.Contains(Squash(canonical), StringComparison.Ordinal)))
            { Reject($"the evidence is not in {path}: copy it exactly from that element in {ChangesFile}"); continue; }
            if (severity is not ("high" or "medium" or "low")) { Reject("severity must be high, medium or low"); continue; }
            if (title.Length == 0) { Reject("no title"); continue; }
            var finding = new JsonObject
            {
                ["rule"] = Rule, ["severity"] = severity, ["category"] = "logic", ["title"] = title, ["element"] = path, ["detail"] = evidence, ["fix"] = fix.Length > 0 ? fix : null,
                ["source"] = "reviewer",
            };
            // A finding on code the change did not touch is kept, and marked: the packet did not show that code.
            if (!InPacket(element, packet.Keys)) finding["outsideChange"] = true;
            accepted.Add(finding);
        }
        return new(accepted, rejected);
    }

    static bool InPacket(IObjectSignature element, HashSet<string> keys)
    {
        for (var o = element; o is not null; o = ParentOf(o))
            if (keys.Contains(o.GlobalKey.ToString()!)) return true;
        return false;
    }

    // Some platform objects throw for Parent: they belong to no element.
    static IObjectSignature? ParentOf(IObjectSignature o)
    {
        try { return o.Parent; }
        catch (InvalidOperationException) { return null; }
    }

    // The text a finding's evidence must come from: the element's describe text, and the readable properties of the
    // element and all it holds (an expression or argument is folded into the element that owns it).
    static string ElementText(IESpace m, IObjectSignature element, string path, Snapshot b, ILookup<string, Item> kids)
    {
        var text = new StringBuilder(Text(m, path)).Append('\n');
        IObjectSignature? o = element;
        while (o is not null && !b.Items.ContainsKey(o.GlobalKey.ToString()!)) o = ParentOf(o);
        if (o is null || o is IESpace) return text.ToString();
        var item = b.Items[o.GlobalKey.ToString()!];
        foreach (var i in Descendants(kids, item.Key).Prepend(item))
        {
            text.Append(i.Label).Append('\n');
            if (i.IsNode) text.Append(Review.What(i)).Append('\n');
            foreach (var (key, value) in i.Props)
            {
                text.Append(key).Append(": ").Append(value).Append('\n').Append(value).Append('\n');
                if (key.StartsWith("set ", StringComparison.Ordinal)) text.Append(key).Append(" = ").Append(value).Append('\n').Append(key[4..]).Append(" = ").Append(value).Append('\n');
            }
        }
        return text.ToString();
    }

    // The --import result: the reviewer's accepted findings merged with oml review's own new findings, most severe first.
    internal static JsonObject Output(JsonObject review, Imported imported, string findingsPath)
    {
        var own = review["newFindings"]!.AsArray().Select(n => n!.DeepClone().AsObject()).ToList();
        foreach (var f in own) f["source"] = "oml";
        static int Rank(JsonObject f) => Array.IndexOf(CheckInput.Severities, f["severity"]?.GetValue<string>() ?? "");
        var all = imported.Accepted.Concat(own).OrderBy(Rank).ThenBy(f => f["source"]!.GetValue<string>() == "reviewer" ? 0 : 1)
            .ThenBy(f => f["element"]?.GetValue<string>(), StringComparer.Ordinal).ThenBy(f => f["title"]?.GetValue<string>(), StringComparer.Ordinal).ToList();
        var submitted = imported.Accepted.Count + imported.Rejected.Count;
        var output = new JsonObject
        {
            ["module"] = review["module"]!.DeepClone(), ["findingsFile"] = findingsPath,
            ["summary"] = new JsonObject
            {
                ["submitted"] = submitted, ["accepted"] = imported.Accepted.Count, ["rejected"] = imported.Rejected.Count,
                ["score"] = submitted == 0 ? "no findings submitted" : $"{imported.Accepted.Count} of {submitted} accepted",
                ["outsideChange"] = imported.Accepted.Count(f => f["outsideChange"] is not null),
                ["omlFindings"] = own.Count, ["changes"] = review["summary"]!["changes"]!.DeepClone(), ["risks"] = review["summary"]!["risks"]!.DeepClone(),
            },
            ["findings"] = new JsonArray(all.Select(f => (JsonNode?)f).ToArray()),
            ["rejected"] = new JsonArray(imported.Rejected.Select(f => (JsonNode?)f).ToArray()),
        };
        if (imported.Rejected.Count > 0) output["next"] = "Send the rejected findings and their reasons back to the reviewer, or drop them. Fix the accepted ones with oml apply.";
        output["note"] = "Accepted means the path and the evidence check out, not that the bug is real: read each finding before you fix it.";
        return output;
    }
}
