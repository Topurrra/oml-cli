using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

static partial class SelfTest
{
    // Fewer agent turns: apply --describe, describe with several names, spec errors that teach, multi-spec apply.
    static void TurnsTest(string blank, string dir, List<string> failures)
    {
        string Write(string name, string json) { var path = Path.Combine(dir, name); File.WriteAllText(path, json); return path; }
        JsonElement Json(object report) => JsonSerializer.SerializeToElement(report);
        string Problem(object report) => Json(report).TryGetProperty("problems", out var p) && p.GetArrayLength() > 0 ? p[0].GetString()! : "";

        // apply --describe and --describe-created
        var one = Write("turns-one.json", """
            { "entities": [{ "name": "Gadget", "attributes": [{ "name": "Label" }] }],
              "serverActions": [{ "name": "CountGadgets", "outputs": [{ "name": "N", "type": "Integer" }], "flow": [{ "kind": "assign", "assign": { "N": "0" } }] }] }
            """);
        var oneOut = Path.Combine(dir, "turns-one.oml");
        var (code, report) = Apply.Execute(blank, [one], oneOut, new ApplyOptions(["CountGadgets", "NoSuchThing"], true));
        var r = Json(report);
        if (code != 0) failures.Add("turns: apply --describe failed: " + r);
        else
        {
            r.TryGetProperty("describe", out var described);
            if (described.ValueKind != JsonValueKind.Object || !described.TryGetProperty("CountGadgets", out _)) failures.Add("turns: --describe lost a named element");
            else if (!described.TryGetProperty("Gadget", out var gadget) || !gadget.GetProperty("text").GetString()!.Contains("Label: Text"))
                failures.Add("turns: --describe-created did not describe the new entity");
            if (!r.TryGetProperty("describeErrors", out var errors) || errors.GetArrayLength() != 1 || errors[0].GetProperty("element").GetString() != "NoSuchThing")
                failures.Add("turns: an unknown --describe name was not listed in describeErrors");
        }

        // --describe-created lists at most 10 elements and counts the rest
        var many = Write("turns-many.json", "{ \"structures\": [" + string.Join(",", Enumerable.Range(1, 12).Select(i => $"{{ \"name\": \"Part{i:00}\", \"attributes\": [{{ \"name\": \"X\" }}] }}")) + "] }");
        var (manyCode, manyReport) = Apply.Execute(blank, [many], Path.Combine(dir, "turns-many.oml"), new ApplyOptions([], true));
        var m = Json(manyReport);
        if (manyCode != 0 || m.GetProperty("describe").EnumerateObject().Count() != 10 || m.GetProperty("describeCreatedMore").GetInt32() != 2)
            failures.Add("turns: --describe-created cap: " + m);

        // describe with several elements: one load, one entry each, a bad name becomes an error entry
        using (var built = Model.Load(oneOut))
        {
            var results = Describe.Many(built, ["Gadget", "CountGadgets", "Missing"], 0, [], collectErrors: true);
            if (results.Count != 3 || results[2] is not Describe.DescribeError) failures.Add("turns: describe with several names did not return one entry per name");
            try { Describe.Run(built, "Missing", 0, []); failures.Add("turns: a single unknown describe name did not fail"); }
            catch (ModelError) { }
        }

        // spec errors that teach
        var typo = Write("turns-typo.json", """{ "entites": [{ "name": "Order" }] }""");
        var (typoCode, typoReport) = Apply.Execute(blank, [typo], Path.Combine(dir, "turns-typo.oml"), null);
        var typoText = Problem(typoReport);
        if (typoCode == 0 || !typoText.Contains("did you mean `entities`?") || !typoText.Contains("Example:") || !typoText.Contains("\"attributes\""))
            failures.Add("turns: a misspelled key got no suggestion and example: " + typoText);
        var nested = Write("turns-nested.json", """{ "entities": [{ "name": "Order", "atributes": [] }] }""");
        if (!Problem(Apply.Execute(blank, [nested], Path.Combine(dir, "turns-nested.oml"), null).report).Contains("$.entities[0].atributes"))
            failures.Add("turns: a misspelled nested key was not located");
        var wrong = Write("turns-wrong.json", """{ "entities": "Order" }""");
        var wrongText = Problem(Apply.Execute(blank, [wrong], Path.Combine(dir, "turns-wrong.oml"), null).report);
        if (!wrongText.Contains("expected an array") || !wrongText.Contains("Example:") || !wrongText.Contains("\"entities\": [{"))
            failures.Add("turns: a wrong value type got no example: " + wrongText);
        var wrongInner = Write("turns-wrong-inner.json", """{ "entities": [{ "name": "A", "attributes": [{ "name": "X", "length": "ten" }] }] }""");
        var innerText = Problem(Apply.Execute(blank, [wrongInner], Path.Combine(dir, "turns-wrong-inner.oml"), null).report);
        if (!innerText.Contains("attributes[0].length") || !innerText.Contains("whole number") || !innerText.Contains("Example:"))
            failures.Add("turns: a wrong inner value type got no example: " + innerText);
        var note = Write("turns-note.json", """{ "_note": "free", "$comment": "free", "entities": [{ "name": "Noted", "attributes": [{ "name": "X" }] }] }""");
        if (Apply.Execute(blank, [note], Path.Combine(dir, "turns-note.oml"), null).code != 0) failures.Add("turns: a _note or $comment key was refused");

        // every example is valid JSON, spec-key examples use known keys, and the self-contained ones apply
        var examples = SpecHelp.Examples;
        foreach (var (key, example) in examples)
            try
            {
                var node = JsonNode.Parse(example.TrimStart().StartsWith('"') ? "{" + example + "}" : example);
                if (example.TrimStart().StartsWith('"') && key != "attributes" && key != "flow" && key != "inputs" && key != "outputs" && key != "locals" && key != "widgets" && key != "access"
                    && SpecHelp.UnknownKeys(node).Count > 0) failures.Add("turns: example for " + key + " uses unknown keys");
            }
            catch (JsonException e) { failures.Add($"turns: example for {key} is not valid JSON: {e.Message}"); }
        var applied = string.Join(",", new[] { "entities", "structures", "staticEntities", "exceptions", "roles", "siteProperties", "serverActions", "clientActions", "blocks", "screens", "restClients", "timers" }
            .Select(k => examples[k]));
        // The timer example needs a server action without inputs; a second part supplies it (and exercises multi-spec apply).
        var exampleSpecs = new[] { Write("turns-examples.json", "{" + applied + "}"), Write("turns-examples-extra.json", """{ "serverActions": [{ "name": "CleanupOrders" }] }""") };
        var exampleOut = Apply.Execute(blank, exampleSpecs, Path.Combine(dir, "turns-examples.oml"), null);
        if (exampleOut.code != 0) failures.Add("turns: the spec examples do not apply: " + JsonSerializer.Serialize(exampleOut.report));

        // multi-spec apply: parts merge into one module, and conflicts name both files
        var a = Write("turns-a.json", """{ "entities": [{ "name": "Alpha", "attributes": [{ "name": "X" }] }], "exceptions": ["AlphaFault"] }""");
        var b = Write("turns-b.json", """{ "entities": [{ "name": "Beta", "attributes": [{ "name": "X" }] }], "serverActions": [{ "name": "UseBeta", "inputs": [{ "name": "Rec", "type": "Beta" }], "flow": [{ "kind": "execute", "action": "CreateOrUpdateBeta", "args": { "Source": "Rec" } }] }] }""");
        var (mergeCode, mergeReport) = Apply.Execute(blank, [a, b], Path.Combine(dir, "turns-merged.oml"), null);
        if (mergeCode != 0) failures.Add("turns: multi-spec apply failed: " + JsonSerializer.Serialize(mergeReport));
        else
        {
            using var merged = Model.Load(Path.Combine(dir, "turns-merged.oml"));
            if (!merged.Entities.Any(e => e.Name == "Alpha") || !merged.Entities.Any(e => e.Name == "Beta") || !merged.ServerActions.Any(s => s.Name == "UseBeta")
                || Json(mergeReport).GetProperty("specs").GetArrayLength() != 2)
                failures.Add("turns: multi-spec apply lost an element or the specs list");
        }
        var dup = Write("turns-dup.json", """{ "entities": [{ "name": "alpha", "attributes": [{ "name": "X" }] }] }""");
        var (dupCode, dupReport) = Apply.Execute(blank, [a, dup], Path.Combine(dir, "turns-dup.oml"), null);
        if (dupCode == 0 || !Problem(dupReport).Contains("turns-a.json") || !Problem(dupReport).Contains("turns-dup.json") || !Problem(dupReport).Contains("alpha"))
            failures.Add("turns: a duplicate element across specs was not refused with both files: " + Problem(dupReport));
        var keepTrue = Write("turns-keep-true.json", """{ "keepComments": true }""");
        var keepFalse = Write("turns-keep-false.json", """{ "keepComments": false }""");
        var (scalarCode, scalarReport) = Apply.Execute(blank, [keepTrue, keepFalse], Path.Combine(dir, "turns-scalar.oml"), null);
        if (scalarCode == 0 || !Problem(scalarReport).Contains("turns-keep-true.json") || !Problem(scalarReport).Contains("turns-keep-false.json") || !Problem(scalarReport).Contains("keepComments"))
            failures.Add("turns: a scalar conflict across specs was not refused with both files: " + Problem(scalarReport));
    }
}
