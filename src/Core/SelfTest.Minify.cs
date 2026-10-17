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
}
