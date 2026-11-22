using System.Text.Json;

namespace OmlCli;

static partial class SelfTest
{
    // JavaScript syntax check: a broken JavaScript node or Script refuses the apply with JavaScriptSyntax newErrors (line and
    // column in the snippet); return, await and $parameters in a node parse. Skipped when Service Studio has no node.exe.
    static void JavaScriptSyntaxTest(string blank, string dir, List<string> failures)
    {
        if (JavaScriptSyntax.NodePath() is null) { Console.Error.WriteLine("self-test: " + JavaScriptSyntax.NotFound); return; }
        var folder = Path.Combine(dir, "javascript");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "bad.js"), "window.f = function (a) {\n  return a +;\n};\n");
        const string good = """{ "kind": "javaScript", "name": "Ok", "javaScript": "var x = await Promise.resolve(1);\nif (!x) { return; }\n$parameters.Out = x;\n$resolve();", "outputs": [ { "name": "Out", "type": "Integer" } ] }""";
        var spec = Path.Combine(folder, "bad.json");
        File.WriteAllText(spec, $$"""
            { "scripts": [ { "name": "BadLib", "file": "bad.js" } ],
              "clientActions": [ { "name": "JsGood", "flow": [ {{good}} ] },
                                 { "name": "JsBad", "flow": [ { "kind": "javaScript", "name": "Broken", "javaScript": "var a = 1;\nconsole.log(a;" } ] } ] }
            """);
        var (code, report) = Apply.Execute(blank, spec, Path.Combine(folder, "bad.oml"));
        var text = JsonSerializer.Serialize(report);
        if (code == 0 || File.Exists(Path.Combine(folder, "bad.oml"))) failures.Add("javascript: a broken JavaScript node was saved");
        foreach (var expected in new[] { "JsBad/Broken", "BadLib", "JavaScriptSyntax", "line 2, column 13" })
            if (!text.Contains(expected)) failures.Add($"javascript: the report lacks '{expected}': {text}");
        if (text.Contains("JsGood")) failures.Add("javascript: a valid node with await and return was reported: " + text);

        File.WriteAllText(spec, $$"""{ "clientActions": [ { "name": "JsGood", "flow": [ {{good}} ] } ] }""");
        var output = Path.Combine(folder, "good.oml");
        var (goodCode, goodReport) = Apply.Execute(blank, spec, output);
        if (goodCode != 0) { failures.Add("javascript: a valid JavaScript node was refused: " + JsonSerializer.Serialize(goodReport)); return; }
        using var m = Model.Load(output);
        var snippets = JavaScriptSyntax.Collect(m);
        if (snippets.Count != 1 || !snippets[0].Body) failures.Add($"javascript: validate found {snippets.Count} snippets, expected the one node");
        if (JavaScriptSyntax.Check(snippets) is { errors.Length: > 0 } or { note: not null }) failures.Add("javascript: validate reports the valid node");
    }
}
