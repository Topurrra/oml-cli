using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Enumerations;

namespace OmlCli;

static partial class SelfTest
{
    // Two producers share a name: a plain name must be refused with both candidates, the qualified name must
    // resolve to the right module, and oml find must list both.
    static void QualifiedNamesTest(string blank, string dir, List<string> failures)
    {
        var spec = Path.Combine(dir, "names.json");
        string Producer(string name, string json)
        {
            var source = Path.Combine(dir, $"{name}-blank.oml");
            var output = Path.Combine(dir, $"{name}.oml");
            using (var module = Model.Services().CreateESpace(SegmentationKind.CrossDevice, name, "", false, Model.Services().NewKey()))
                Model.Save(module, source);
            File.WriteAllText(spec, json);
            if (Apply.Execute(source, spec, output).code != 0) failures.Add($"names: producer {name} setup failed");
            return output;
        }
        var core = Producer("NamesCore", """
            { "entities": [{ "name": "Customer", "public": true, "attributes": [{ "name": "Name" }] }],
              "blocks": [{ "name": "Badge", "widgets": [{ "type": "Text", "text": "core" }] }],
              "edits": [{ "op": "set", "element": "Badge", "properties": { "public": true } }] }
            """);
        var legacy = Producer("NamesLegacy", """{ "serverActions": [{ "name": "Badge", "public": true }] }""");

        var consumer = Path.Combine(dir, "names-consumer.oml");
        File.WriteAllText(spec, JsonSerializer.Serialize(new
        {
            references = new object[] { new { producer = core, elements = new[] { "Customer", "Badge" } },
                                        new { producer = legacy, elements = new[] { "Badge" } } },
        }));
        if (Apply.Execute(blank, spec, consumer).code != 0) { failures.Add("names: consumer references failed"); return; }

        File.WriteAllText(spec, """{ "serverActions": [{ "name": "UseBadge", "flow": [{ "kind": "execute", "action": "Badge" }] }] }""");
        var (code, report) = Apply.Execute(consumer, spec, Path.Combine(dir, "names-ambiguous.oml"));
        var text = JsonSerializer.Serialize(report);
        if (code == 0 || !text.Contains("NamesCore.Badge") || !text.Contains("NamesLegacy.Badge"))
            failures.Add("names: an ambiguous name was not refused with its candidates: " + text);

        File.WriteAllText(spec, """
            { "serverActions": [{ "name": "UseBadge", "locals": [{ "name": "C", "type": "NamesCore.Customer Identifier" }],
                "flow": [{ "kind": "execute", "action": "NamesLegacy.Badge" }] }] }
            """);
        var qualified = Path.Combine(dir, "names-qualified.oml");
        if (Apply.Execute(consumer, spec, qualified).code != 0) failures.Add("names: qualified Module.Element did not resolve");

        // Nothing in the consumer uses the producers yet, so "Remove Unused Dependencies" must drop both.
        // Producer elements hold no data in the consumer, so the production rules do not protect them.
        File.WriteAllText(spec, """{ "removeUnusedDependencies": true }""");
        var cleaned = Path.Combine(dir, "names-cleaned.oml");
        var (cleanCode, cleanReport) = Apply.Execute(consumer, spec, cleaned);
        var cleanText = JsonSerializer.Serialize(cleanReport);
        if (cleanCode != 0 || !cleanText.Contains("NamesCore") || !cleanText.Contains("NamesLegacy"))
            failures.Add("names: removeUnusedDependencies did not remove unused producers: " + cleanText);

        var found = JsonSerializer.Serialize(Finder.Find("Badge", null, new List<string> { core, legacy }));
        if (!found.Contains("NamesCore.Badge") || !found.Contains("NamesLegacy.Badge")) failures.Add("names: find did not list both modules: " + found);
    }
}
