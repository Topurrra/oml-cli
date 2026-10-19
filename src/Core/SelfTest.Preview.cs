using System.Text.Json;
using OutSystems.Model;

namespace OmlCli;

partial class SelfTest
{
    // Block PvCard: input defaults, classes, an expression of an input, a Link, a nested same-module block with a Content
    // placeholder, a stylesheet and an OnRender JavaScript node. Block PvBad is built to trip the automatic checks, including
    // the original "linkCopy spec" bug (a seeded Link text kept next to the added text).
    const string PreviewSpec = """
        {
          "blocks": [
            { "name": "PvInner", "inputs": [ { "name": "Heading", "type": "Text", "default": "\"Inner heading\"" } ],
              "styleSheet": ".pv-inner { padding: 4px; }",
              "widgets": [ { "type": "Container", "name": "InnerRoot", "style": "\"pv-inner\"", "children": [
                { "type": "Expression", "value": "Heading" }, { "type": "Placeholder", "name": "Content" } ] } ] },
            { "name": "PvCard", "inputs": [ { "name": "Title", "type": "Text", "default": "\"Hello preview\"" } ],
              "styleSheet": ".pv-card { min-height: 120px; color: var(--pv-text); }",
              "onRender": "RenderCard",
              "actions": [
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
                { "type": "Link", "name": "BadLink", "onClick": "Noop" }, { "type": "Text", "text": "Demo content" } ] } ] }
          ],
          "edits": [ { "op": "addWidgets", "screen": "PvBad", "parent": "BadLink", "widgets": [ { "type": "Text", "text": "Copy spec" } ] } ]
        }
        """;

    static void PreviewTest(string blank, string dir, List<string> failures)
    {
        void Check(bool condition, string detail) { if (!condition) failures.Add("preview: " + detail); }
        var spec = Path.Combine(dir, "preview-spec.json");
        var staged = Path.Combine(dir, "preview.oml");
        File.WriteAllText(spec, PreviewSpec);
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
        Check(html.Contains("function($parameters, $actions, $roles, $public){") && html.Contains("setAttribute('data-pv'"), "JavaScript not wrapped");
        Check(html.Contains("p:{\"RootId\":\"PvRoot\",\"Label\":\"Hello preview\"}"), "JavaScript call has the wrong parameters");
        Check(card.Kind == "Block" && card.Widgets >= 7 && card.JavaScriptNodes == 1 && card.Unsupported.Count == 0, "report fields");
        Check(card.Checklist.Count == 6 && card.Checklist.All(c => c.ok), "a clean block failed the checklist: " + string.Join(", ", card.Checklist.Where(c => !c.ok).Select(c => c.id + " " + c.detail)));

        var custom = Preview.Render(module, "PvCard", new Dictionary<string, string> { ["Title"] = "\"Custom\"" }, "light", 1200);
        Check(custom.Html.Contains("Custom") && custom.Html.Contains("\"Label\":\"Custom\""), "--arg ignored");
        try { Preview.Render(module, "PvCard", new Dictionary<string, string> { ["Nope"] = "1" }, "light", 1200); Check(false, "unknown --arg accepted"); }
        catch (CliError e) { Check(e.Message.Contains("Title"), "unknown --arg error should list the inputs"); }

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
                && root.GetProperty("widgets").GetInt32() == card.Widgets && root.GetProperty("javaScriptNodes").GetInt32() == 1
                && root.GetProperty("blocksRendered").GetArrayLength() == 1 && root.GetProperty("notes").GetArrayLength() > 1 && root.GetProperty("checklist").GetArrayLength() == 6, "report fields");
        }

        var existing = Path.Combine(dir, "preview-existing.html");
        File.WriteAllText(existing, "keep");
        try { Preview.Run(["preview", staged, "PvCard", "--out", existing]); Check(false, "overwrite accepted"); }
        catch (CliError e) { Check(e.Message.Contains("already exists") && File.ReadAllText(existing) == "keep", "overwrite error"); }
    }
}
