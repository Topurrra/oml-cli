using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Enumerations;

namespace OmlCli;

static partial class SelfTest
{
    // A stand-in BDDFramework (the documented block names; inputs, placeholder, event and assert action assumed),
    // as in tests/bdd-standin.json, and a producer with one public server action that raises a user exception.
    const string BddStandIn = """
        { "exceptions": ["AssertionFailed"],
          "clientActions": [{ "name": "AssertTrue", "public": true,
            "inputs": [{ "name": "Condition", "type": "Boolean", "mandatory": true }, { "name": "FailureMessage", "type": "Text" }],
            "flow": [{ "kind": "if", "name": "Passed", "condition": "Condition", "true": "End", "false": "Fail" },
                     { "kind": "raiseException", "name": "Fail", "exception": "AssertionFailed", "message": "FailureMessage" }] }],
          "blocks": [
            { "name": "BDDScenario", "public": true, "inputs": [{ "name": "Description", "type": "Text", "mandatory": true }],
              "widgets": [{ "type": "Expression", "value": "Description" }, { "type": "Placeholder", "name": "Steps" }] },
            { "name": "BDDStep", "public": true, "inputs": [{ "name": "Description", "type": "Text", "mandatory": true }],
              "events": [{ "name": "StepAction", "mandatory": true }],
              "actions": [{ "name": "RunStep", "flow": [{ "kind": "triggerEvent", "name": "Run", "event": "StepAction" }] }],
              "onReady": "RunStep", "widgets": [{ "type": "Expression", "value": "Description" }] },
            { "name": "FinalResult", "public": true, "widgets": [{ "type": "Text", "text": "Final result" }] }] }
        """;
    const string BddProducer = """
        { "exceptions": ["InvalidQuantity"],
          "serverActions": [{ "name": "PlaceOrder", "public": true,
            "inputs": [{ "name": "CustomerName", "type": "Text", "mandatory": true }, { "name": "Quantity", "type": "Integer", "mandatory": true }],
            "outputs": [{ "name": "OrderNumber", "type": "Text" }],
            "flow": [{ "kind": "if", "name": "Valid", "condition": "Quantity > 0", "true": "Compute", "false": "Invalid" },
                     { "kind": "assign", "name": "Compute", "assign": { "OrderNumber": "CustomerName" }, "next": "End" },
                     { "kind": "raiseException", "name": "Invalid", "exception": "InvalidQuantity", "message": "\"Quantity must be positive\"" }] }] }
        """;

    // oml test scaffold: the spec it writes from the stand-in applies to a new test module with no errors and no new
    // warnings, each step block is bound to its screen action through the step event, and TODO comments in expressions validate.
    static void TestScaffoldTest(string dir, List<string> failures)
    {
        try
        {
            string Module(string name, string? spec)
            {
                var blank = Path.Combine(dir, $"bdd-{name}-blank.oml");
                var services = Model.Services();
                using (var module = services.CreateESpace(SegmentationKind.CrossDevice, name, "", false, services.NewKey())) Model.Save(module, blank);
                if (spec is null) return blank;
                var file = Path.Combine(dir, $"bdd-{name}.json");
                File.WriteAllText(file, spec);
                var output = Path.Combine(dir, $"bdd-{name}.oml");
                var (code, report) = Apply.Execute(blank, file, output);
                if (code != 0) throw new ModelError($"building {name}: {JsonSerializer.Serialize(report)}");
                return output;
            }
            var bdd = Module("BDDFramework", BddStandIn);
            var producer = Module("BddProducer", BddProducer);
            var specFile = Path.Combine(dir, "bdd-tests.json");
            TestScaffold.Write(producer, ["PlaceOrder"], bdd, specFile, "BddTests", null);
            var built = Path.Combine(dir, "bdd-tests.oml");
            var (applyCode, applyReport) = Apply.Execute(Module("BddTests", null), specFile, built);
            var json = JsonSerializer.SerializeToElement(applyReport);
            if (applyCode != 0 || json.TryGetProperty("newWarnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array && warnings.GetArrayLength() > 0) { failures.Add("test scaffold: the spec did not apply cleanly: " + json); return; }
            using (var tests = Model.Load(built))
            {
                if (Model.Errors(tests).Length > 0) failures.Add("test scaffold: errors after reopen");
                var screen = ElementPaths.Resolve(tests, "TestFlow/PlaceOrder_Tests");
                var bound = Descendants(screen).OfType<OutSystems.Model.UI.Mobile.Widgets.IMobileBlockInstanceWidget>()
                    .Where(w => Model.NameOf(w.SourceBlock) == "BDDStep")
                    .Select(w => Model.NameOf(w.EventHandlers.SingleOrDefault(h => h.Event.Name == "StepAction")?.Handler ?? (object)"")).ToList();
                var expected = new[] { "HappyPath", "RaisesInvalidQuantity", "EmptyCustomerName" }.SelectMany(c => new[] { c + "_Given", c + "_When", c + "_Then" });
                if (!bound.SequenceEqual(expected)) failures.Add("test scaffold: step blocks bound to " + string.Join(", ", bound));
                var expressions = Descendants(screen).OfType<IExpression>().Select(e => e.Text).ToList();
                foreach (var text in new[] { "0 /* TODO */", "RaisesInvalidQuantity_Message = \"Quantity must be positive\"", "OnError.ExceptionMessage" })
                    if (!expressions.Contains(text)) failures.Add("test scaffold: expression missing after reopen: " + text);
            }
            try { TestScaffold.Write(producer, ["PlaceOrder"], producer, Path.Combine(dir, "bdd-refused.json"), null, null); failures.Add("test scaffold: a module without BDDScenario was not refused"); }
            catch (CliError e) when (e.Message.Contains("no public block 'BDDScenario'")) { }
        }
        catch (Exception e) { failures.Add("test scaffold: " + (e.InnerException ?? e).Message); }
    }
}
