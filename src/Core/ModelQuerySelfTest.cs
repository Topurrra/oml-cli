using System.Text.Json.Nodes;

namespace OmlCli;

// oml query's decisions without oml-tool: which @filter conditions run, how a big result is cut, the hint for an error.
// tests/query-check.ps1 runs real queries through oml-tool (and skips when it is absent).
static class ModelQuerySelfTest
{
    public static void Run(List<string> failures)
    {
        void Check(bool ok, string what) { if (!ok) failures.Add("query: " + what); }
        string Q(string condition) => "Root { Entities @filter(condition: \"" + condition.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\") { Name } }";

        Check(ModelQuery.Refusal("Root { Entities { Name Attributes { Name } } }") is null, "a plain query passes");
        Check(ModelQuery.Refusal("Root { AllDescendants(ofType: \"IMobileScreen\", limit: 5) { Name } }") is null, "ofType and limit pass");
        Check(ModelQuery.Refusal("__type(name: \"ESpace\") { fields { name } }") is null, "introspection passes");
        foreach (var ok in new[] { "_.Name.StartsWith(\"Get\")", "_.Name == \"Order\" || _.Name.Length > 5 && !_.IsPublic", "_.Description != null && _.Description.ToLower().Contains(\"x\")", "_?.Name?.EndsWith('s') == true" })
            Check(ModelQuery.Refusal(Q(ok)) is null, "condition passes: " + ok);
        foreach (var bad in new[] { "System.IO.File.Exists(\"C:/x\")", "_.Name.GetType().Assembly != null", "new System.Object() != null", "typeof(string) != null",
                     "true)); System.Console.Beep(); ((true", "_.Name = \"x\"", "$\"{_.Name}\" == \"a\"", "@\"a\" == _.Name", "(string)_.Name == \"a\"",
                     "_.Nodes.Any(n => true)", "_(1)", "_.Name[0] == 'a'", "\\u0053ystem.Environment.Exit(0) == null", "_.Save() == null" })
            Check(ModelQuery.Refusal(Q(bad)) is not null, "condition refused: " + bad);
        // The engine reads GraphQL escapes before it compiles: \u0022 is a quote, so the check reads the same text.
        Check(ModelQuery.Refusal("Root { Entities @filter(condition: \"_.Name == \\u0022a\\u0022 || System.GC.GetTotalMemory(false) > 0\") { Name } }") is not null, "escaped text is read as the engine reads it");
        Check(ModelQuery.Refusal("Root { Entities @filter(condition: [\"System.Environment.Exit(1) == null\"]) { Name } }") is not null, "a list value is checked too");
        Check(ModelQuery.Refusal("Root { Entities @filter(condition: \"\"\"System.Environment.Exit(1) == null\"\"\") { Name } }") is not null, "block strings are refused");
        Check(ModelQuery.Refusal("Root { AllDescendants(ofType: \"IEntity) || true\") { Name } }") is not null, "a type name with code is refused");
        Check(ModelQuery.Refusal("Root { Entities(name: \"System.Exit\") { Name } }") is null, "other string arguments are names, not code");

        var data = JsonNode.Parse("""{"Root":{"Entities":[{"Name":"A","Attributes":[{"Name":"1"},{"Name":"2"},{"Name":"3"}]},{"Name":"B"},{"Name":"C"}]}}""")!;
        var shaped = ModelQuery.Shape(data, 2, 100000);
        Check(shaped.Fits && shaped.Cuts.Select(c => $"{c.Path}:{c.Total}").SequenceEqual(["Root.Entities:3", "Root.Entities[0].Attributes:3"]), "lists cut to the limit, paths and totals");
        Check(data["Root"]!["Entities"]!.AsArray().Count == 3, "the input is not changed");
        var small = ModelQuery.Shape(data, 50, 80);
        Check(small.Fits && small.Limit < 3 && small.Chars <= 80, $"over the size cap the limit halves (limit {small.Limit}, {small.Chars} chars)");
        Check(!ModelQuery.Shape(data, 50, 10).Fits, "too big even with one item per list");

        Check(ModelQuery.Hint("The query is malformed: Schema is not configured correctly to fetch field 'Screens' from type 'MobileFlow'.", "m.oml", false)
            .Contains("--fields MobileFlow"), "a missing field points to --fields of its type");
        Check(ModelQuery.Hint("The @filter condition could not be compiled", "m.oml", false).Contains("_.Name"), "a condition error shows the form");
    }
}
