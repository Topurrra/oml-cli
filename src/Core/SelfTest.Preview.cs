using System.Text.Json;
using System.Text.RegularExpressions;
using OutSystems.Model;

namespace OmlCli;

partial class SelfTest
{
    // Block PvCard: input defaults, classes, an expression of an input, a Link, a nested same-module block with a Content
    // placeholder, a stylesheet, a required Script element and OnInitialize/OnRender JavaScript nodes (PvInner has its own, to
    // check the lifecycle order). Block PvBad is built to trip the automatic checks, including the original "linkCopy spec"
    // bug (a seeded Link text kept next to the added text). @LIB@ is the path of the script file.
    const string PreviewSpec = """
        {
          "scripts": [ { "name": "PvLib", "file": "@LIB@" }, { "name": "PvSpare", "file": "@LIB@" } ],
          "blocks": [
            { "name": "PvInner", "inputs": [ { "name": "Heading", "type": "Text", "default": "\"Inner heading\"" } ],
              "styleSheet": ".pv-inner { padding: 4px; }",
              "onInitialize": "InnerInit", "onReady": "InnerReady",
              "actions": [
                { "name": "InnerInit", "flow": [ { "kind": "javaScript", "name": "InnerInitJs", "javaScript": "window.pvLog = (window.pvLog || []).concat('inner-init');" } ] },
                { "name": "InnerReady", "flow": [ { "kind": "javaScript", "name": "InnerReadyJs", "javaScript": "setTimeout(function () { $resolve(); }, 1);" } ] } ],
              "widgets": [ { "type": "Container", "name": "InnerRoot", "style": "\"pv-inner\"", "children": [
                { "type": "Expression", "value": "Heading" }, { "type": "Placeholder", "name": "Content" } ] } ] },
            { "name": "PvCard", "inputs": [ { "name": "Title", "type": "Text", "default": "\"Hello preview\"" } ],
              "styleSheet": ".pv-card { min-height: 120px; color: var(--pv-text); }",
              "requiredScripts": [ "PvLib" ],
              "onInitialize": "InitCard", "onRender": "RenderCard",
              "actions": [
                { "name": "InitCard", "flow": [ { "kind": "javaScript", "name": "CardInitJs", "javaScript": "window.pvLog = ['card-init'];" } ] },
                { "name": "RenderCard", "flow": [ { "kind": "javaScript", "name": "Mark", "inputs": [ { "name": "RootId", "type": "Text" }, { "name": "Label", "type": "Text" } ],
                  "args": { "RootId": "PvRoot.Id", "Label": "Title" },
                  "javaScript": "document.getElementById($parameters.RootId).setAttribute('data-pv', $parameters.Label);" } ] },
                { "name": "Tap", "flow": [] } ],
              "widgets": [ { "type": "Container", "name": "PvRoot", "style": "\"pv-card\"", "children": [
                { "type": "Expression", "name": "TitleText", "value": "Title" },
                { "type": "Link", "name": "OpenLink", "onClick": "Tap", "children": [ { "type": "Text", "text": "Open details" } ] },
                { "type": "BlockInstance", "name": "InnerOne", "sourceBlock": "PvInner", "args": { "Heading": "\"Nested heading\"" },
                  "placeholders": { "Content": [ { "type": "Text", "text": "Slot text" } ] } } ] } ] },
            { "name": "PvBad", "styleSheet": "body { margin: 0; }\n.pv-bad { color: #ff0000; border: 1px solid var(--pv-line); }",
              "onRender": "RenderBad",
              "actions": [
                { "name": "RenderBad", "flow": [ { "kind": "javaScript", "name": "Draw", "javaScript": "document.createElement('canvas').getContext('2d'); window.addEventListener('resize', function(){});" } ] },
                { "name": "Noop", "flow": [] } ],
              "widgets": [ { "type": "Container", "name": "BadRoot", "style": "\"pv-bad\"", "children": [
                { "type": "Link", "name": "BadLink", "onClick": "Noop" }, { "type": "Text", "text": "Demo content" } ] } ] },
            { "name": "PvAssign", "inputs": [ { "name": "Count", "type": "Integer", "default": "3" }, { "name": "Label", "type": "Text", "default": "\"n=\"" } ],
              "onReady": "Ready",
              "actions": [
                { "name": "Ready", "flow": [ { "kind": "if", "name": "IsThree", "condition": "Count = 3", "true": "ThreeJs", "false": "OtherJs" },
                  { "kind": "javaScript", "name": "ThreeJs", "javaScript": "window.pvThree = true;", "next": "End" },
                  { "kind": "javaScript", "name": "OtherJs", "javaScript": "window.pvThree = false;" } ] },
                { "name": "Bump", "flow": [ { "kind": "assign", "name": "Up", "assign": { "Count": "Count + 1" } } ] } ],
              "widgets": [ { "type": "Container", "name": "AssignRoot", "children": [
                { "type": "Expression", "name": "Sum", "value": "Count + 1" }, { "type": "Expression", "name": "Joined", "value": "Label + Count" },
                { "type": "Link", "name": "BumpLink", "onClick": "Bump", "children": [ { "type": "Text", "text": "Add one" } ] } ] } ] }
          ],
          "edits": [ { "op": "addWidgets", "screen": "PvBad", "parent": "BadLink", "widgets": [ { "type": "Text", "text": "Copy spec" } ] } ]
        }
        """;

    static void PreviewTest(string blank, string dir, List<string> failures)
    {
        void Check(bool condition, string detail) { if (!condition) failures.Add("preview: " + detail); }
        var spec = Path.Combine(dir, "preview-spec.json");
        var staged = Path.Combine(dir, "preview.oml");
        var lib = Path.Combine(dir, "preview-lib.js");
        File.WriteAllText(lib, "var PvLib = { ready: true, note: '<!-- x' };");
        File.WriteAllText(spec, PreviewSpec.Replace("@LIB@", lib.Replace("\\", "\\\\")));
        var (code, report) = Apply.Execute(blank, spec, staged);
        if (code != 0) { failures.Add("preview fixture: " + JsonSerializer.Serialize(report)); return; }
        using var module = Model.Load(staged);
        var none = new Dictionary<string, string>();

        var card = Preview.Render(module, "PvCard", none, "dark", 390);
        var html = card.Html;
        Check(html.Contains("data-theme=\"dark\"") && html.Contains("width:390px"), "theme or width missing");
        Check(html.Contains("id=\"PvRoot\"") && html.Contains("class=\"pv-card\""), "container id and class missing");
        Check(html.Contains("Hello preview"), "input default not rendered");
        Check(html.Contains("id=\"InnerRoot\"") && html.Contains("Nested heading") && html.Contains("Slot text"), "nested block or its Content placeholder missing");
        Check(card.BlocksRendered.SequenceEqual(["PvInner"]), "blocksRendered");
        Check(html.Contains(".pv-card {") && html.Contains(".pv-inner {"), "stylesheets of the element and the nested block missing");
        Check(html.Contains("function($parameters, $actions, $roles, $public, $resolve, $reject){") && html.Contains("setAttribute('data-pv'"), "JavaScript not wrapped");
        Check(html.Contains("p:{\"RootId\":\"PvRoot\",\"Label\":\"Hello preview\"}"), "JavaScript call has the wrong parameters");
        Check(card.Kind == "Block" && card.Widgets >= 7 && card.JavaScriptNodes == 4 && card.AsyncNodes == 1 && card.Unsupported.Count == 0, "report fields");
        // Scripts: the required one loads before the widgets, the spare one does not load and a note names it. No network.
        Check(card.Scripts.SequenceEqual(["PvLib"]) && html.IndexOf("data-oml-script=\"PvLib\"", StringComparison.Ordinal) is var at and > 0
            && at < html.IndexOf("id=\"oml-frame\"", StringComparison.Ordinal) && html.Contains("var PvLib = { ready: true, note: '\\x3C!-- x' };"), "required script not loaded before the widgets, or its <!-- not escaped");
        Check(card.Notes.Any(n => n.Contains("not loaded: PvSpare")), "unused script not noted");
        Check(html.Contains("Content-Security-Policy") && html.Contains("connect-src 'none'") && html.Contains("default-src 'none'"), "no-network policy missing");
        // Lifecycle order: OnInitialize parent first, then OnReady and OnRender children first; async nodes are marked.
        var run = html[html.LastIndexOf("__omlRun(", StringComparison.Ordinal)..];
        var order = new[] { "PvCard.OnInitialize.CardInitJs", "PvInner.OnInitialize.InnerInitJs", "PvInner.OnReady.InnerReadyJs", "PvCard.OnRender.Mark" }
            .Select(n => run.IndexOf(n, StringComparison.Ordinal)).ToList();
        Check(order.All(i => i > 0) && order.SequenceEqual(order.Order()), "lifecycle order: " + string.Join(",", order));
        Check(Regex.IsMatch(run, "InnerReadyJs\"[^\\n]*async:true") && Regex.IsMatch(run, "Mark\"[^\\n]*async:false"), "async flag");
        // Flow If conditions the preview can evaluate: literals and inputs compared with = or <>, not, brackets. Others are null.
        var scope = new Dictionary<string, string?> { ["Mode"] = "\"bar\"", ["Count"] = "3", ["On"] = "True", ["Empty"] = null, ["Typed"] = "\"7\"" };
        var kinds = new Dictionary<string, string> { ["Mode"] = "text", ["Count"] = "number", ["On"] = "bool", ["Typed"] = "number" };
        // Unknown, so both branches run: texts equal except for case; a value whose kind is not its declared type (an --arg
        // used as text for an Integer input); a comparison of different kinds; an input without a declared kind.
        Check(Preview.Condition("Mode = \"Bar\"", scope, kinds) is null && Preview.Condition("Mode <> \"BAR\"", scope, kinds) is null
            && Preview.Condition("Typed = 7", scope, kinds) is null && Preview.Condition("Count = \"3\"", scope, kinds) is null
            && Preview.Condition("Mode = \"bar\"", scope, new Dictionary<string, string>()) is null
            && Preview.Condition("Mode = \"bar\"", scope, kinds) == true && Preview.Condition("Mode = \"baz\"", scope, kinds) == false, "flow If: case, kinds");
        Check(Preview.Condition("True", scope) == true && Preview.Condition("On", scope) == true && Preview.Condition("Mode = \"bar\"", scope) == true
            && Preview.Condition("Mode <> \"bar\"", scope) == false && Preview.Condition("not (Count = 3)", scope) == false && Preview.Condition("(Count = 3.0)", scope) == true
            && Preview.Condition("\"a=b\" = \"a=b\"", scope) == true && Preview.Condition("Mode", scope) is null && Preview.Condition("Length(Mode) > 2", scope) is null
            && Preview.Condition("Mode = \"bar\" and Count = 3", scope) is null && Preview.Condition("Local = 1", scope) is null && Preview.Condition("Empty = 1", scope) is null
            && Preview.Condition("Count >= 3", scope) is null, "flow If condition evaluation");
        // --type splits at the first '=' outside [...] and quotes.
        Check(Preview.TypeEquals("input[name=q]=Ann") == 13 && Preview.TypeEquals("#a=b=c") == 2 && Preview.TypeEquals("=x") == -1
            && Preview.TypeEquals("[a=b]") == -1 && Preview.TypeEquals("[title='a=b']=c") == 13, "--type selector split");
        Check(card.Checklist.Count == 6 && card.Checklist.All(c => c.ok), "a clean block failed the checklist: " + string.Join(", ", card.Checklist.Where(c => !c.ok).Select(c => c.id + " " + c.detail)));

        var custom = Preview.Render(module, "PvCard", new Dictionary<string, string> { ["Title"] = "\"Custom\"" }, "light", 1200);
        // Text that reaches a <script> as JSON cannot end the element or open an HTML comment there.
        var tricky = Preview.Render(module, "PvCard", new Dictionary<string, string> { ["Title"] = "\"<!--x</script>\"" }, "light", 1200).Html;
        Check(!tricky.Contains("<!--x") && !tricky.Contains("x</script>") && tricky.Contains("\\u003c!--x\\u003c/script>"), "JSON in a script not escaped");
        Check(custom.Html.Contains("Custom") && custom.Html.Contains("\"Label\":\"Custom\""), "--arg ignored");
        try { Preview.Render(module, "PvCard", new Dictionary<string, string> { ["Nope"] = "1" }, "light", 1200); Check(false, "unknown --arg accepted"); }
        catch (CliError e) { Check(e.Message.Contains("Title"), "unknown --arg error should list the inputs"); }

        // An input that an Assign of the block changes is not used to pick an If branch; a + with no text is a sum, not a join.
        var assign = Preview.Render(module, "PvAssign", none, "light", 1200);
        Check(assign.Html.Contains("ThreeJs") && assign.Html.Contains("OtherJs") && assign.Notes.Any(n => n.StartsWith("If IsThree in PvAssign.OnReady cannot be evaluated")), "an assigned input picked an If branch");
        Check(assign.Html.Contains(">{Count + 1}<") && !assign.Html.Contains(">31<") && assign.Html.Contains(">n=3<"), "Count + 1 shown as a text join");
        var bad = Preview.Render(module, "PvBad", none, "light", 1200).Checklist.ToDictionary(c => c.id, c => c.ok);
        Check(bad.Values.All(ok => !ok), "PvBad should fail every check: " + string.Join(",", bad.Where(p => p.Value).Select(p => p.Key)));

        Check(PreviewChecks.DefaultLabel("linkCopy spec") && PreviewChecks.DefaultLabel("link") && PreviewChecks.DefaultLabel("ButtonSave")
            && !PreviewChecks.DefaultLabel("Copy spec") && !PreviewChecks.DefaultLabel("linked") && !PreviewChecks.DefaultLabel("Button Save"), "default label detector");
        Check(PreviewChecks.HasHeightRule(".a{min-height:1px}", ["a"]) && !PreviewChecks.HasHeightRule(".a{width:1px}.b{height:1px}", ["a"]), "height rule detector");
        Check(PreviewChecks.GlobalSelectors("html,body{margin:0}.a button{x:y}button{x:y}*{x:y}").SequenceEqual(["html", "body", "button", "*"]), "global selector detector");
        Check(PreviewChecks.HardCodedColours(".a{color:#fff;background:var(--x)}.b{--c:#000;border:1px solid rgb(0,0,0)}", ".a{background:var(--x)}") == 2
            && PreviewChecks.HardCodedColours(".a{color:#fff}", ".a{color:#fff}") == 0, "hard-coded colour detector");
        Check(PreviewChecks.LeakedListeners("window.addEventListener('resize',f);document.addEventListener('keydown',g);document.removeEventListener('keydown',g)").SequenceEqual(["window.addEventListener('resize') has no removeEventListener"]), "listener detector");

        // The command itself: the file is written and the JSON report carries every field.
        var written = Path.Combine(dir, "preview-written.html");
        var console = Console.Out;
        var captured = new StringWriter();
        try { Console.SetOut(captured); Preview.Run(["preview", staged, "PvCard", "--out", written, "--theme", "dark", "--width", "390"]); }
        finally { Console.SetOut(console); }
        using (var reportJson = JsonDocument.Parse(captured.ToString()))
        {
            var root = reportJson.RootElement;
            Check(File.Exists(written) && File.ReadAllText(written) == html, "HTML file not written or differs from Render");
            Check(root.GetProperty("html").GetString() == written && root.GetProperty("element").GetString() == "PvCard" && root.GetProperty("kind").GetString() == "Block"
                && root.GetProperty("widgets").GetInt32() == card.Widgets && root.GetProperty("javaScriptNodes").GetInt32() == 4
                && root.GetProperty("scripts")[0].GetString() == "PvLib"
                && root.GetProperty("blocksRendered").GetArrayLength() == 1 && root.GetProperty("notes").GetArrayLength() > 1 && root.GetProperty("checklist").GetArrayLength() == 6, "report fields");
        }

        var existing = Path.Combine(dir, "preview-existing.html");
        File.WriteAllText(existing, "keep");
        try { Preview.Run(["preview", staged, "PvCard", "--out", existing]); Check(false, "overwrite accepted"); }
        catch (CliError e) { Check(e.Message.Contains("already exists") && File.ReadAllText(existing) == "keep", "overwrite error"); }
    }
}
