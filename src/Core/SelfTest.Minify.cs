namespace OmlCli;

static partial class SelfTest
{
    // Comments are stripped from styleSheet/javaScript when a spec is read; license notices ("/*!") stay.
    static void MinifyTest(string blank, string dir, List<string> failures)
    {
        var spec = Path.Combine(dir, "minify.json");
        File.WriteAllText(spec, """
            { "screens": [ { "name": "Styled",
                "styleSheet": "/*! notice */\n/* long\n note */\n.card { color: red; /* inline */ content: \"/* text */\"; }",
                "actions": [ { "name": "Init", "flow": [ { "kind": "javaScript", "name": "Js",
                  "javaScript": "// setup\nvar url = 'http://x.y'; // after code stays\n/* b */ var re = /\\/\\//;" } ] } ] } ] }
            """);
        var output = Path.Combine(dir, "minify.oml");
        if (Apply.Execute(blank, spec, output).code != 0) { failures.Add("minify: apply failed"); return; }
        using var m = Model.Load(output);
        var css = Reflect.Get(ElementPaths.Resolve(m, "Styled"), "StyleSheet") as string;
        const string expectedCss = "/*! notice */\n.card { color: red;  content: \"/* text */\"; }";
        if (css != expectedCss) failures.Add($"minify: stylesheet is {System.Text.Json.JsonSerializer.Serialize(css)}");
        var js = Reflect.Get(ElementPaths.Resolve(m, "Styled/Init/Js"), "JavaScript") as string;
        const string expectedJs = "var url = 'http://x.y'; // after code stays\n var re = /\\/\\//;";
        if (js != expectedJs) failures.Add($"minify: javascript is {System.Text.Json.JsonSerializer.Serialize(js)}");
    }

    // {"file": ...} values: the file's text is stored as written (BOM removed, comments kept), read relative to the spec.
    // The model itself stores line ends as LF, so the sample uses LF.
    static void FileValuesTest(string blank, string dir, List<string> failures)
    {
        var folder = Path.Combine(dir, "file-values");
        Directory.CreateDirectory(Path.Combine(folder, "code"));
        // Quotes, backslashes, a template literal with a blank line and trailing spaces, unicode outside the BMP, a comment.
        const string js = "// React-style\nconst Card = ({ title }) => {\n  const label = `T\u00e9st \"${title}\" 'q'\n\n  \\n \u2014 \U0001F680  `;\n"
            + "  return React.createElement(\"div\", { className: \"card\" }, label.replace(/\\s+/g, \" \"));\n};\n";
        const string css = "/* kept */\n.card { content: \"\\201C\"; color: red; }\n";
        File.WriteAllBytes(Path.Combine(folder, "code", "card.js"), [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(js)]);
        File.WriteAllText(Path.Combine(folder, "code", "card.css"), css);
        var spec = Path.Combine(folder, "spec.json");
        File.WriteAllText(spec, """
            { "screens": [ { "name": "FileValues", "styleSheet": { "file": "code/card.css" },
                "actions": [ { "name": "Init", "flow": [ { "kind": "javaScript", "name": "Js", "javaScript": { "file": "code/../code/card.js" } } ] } ] } ] }
            """);
        var output = Path.Combine(dir, "file-values.oml");
        var (code, report) = Apply.Execute(blank, spec, output);
        if (code != 0) { failures.Add("file values: apply failed: " + System.Text.Json.JsonSerializer.Serialize(report)); return; }
        using var m = Model.Load(output);
        if (Reflect.Get(ElementPaths.Resolve(m, "FileValues/Init/Js"), "JavaScript") as string != js)
            failures.Add("file values: the stored JavaScript differs from the file");
        if (Reflect.Get(ElementPaths.Resolve(m, "FileValues"), "StyleSheet") as string != css)
            failures.Add("file values: the stored styleSheet differs from the file");
    }
}
