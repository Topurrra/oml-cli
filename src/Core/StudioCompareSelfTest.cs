using System.Text.Json.Nodes;

namespace OmlCli;

static class StudioCompareSelfTest
{
    public static void Run(string blank, List<string> failures)
    {
        StudioCompare.Message M(string id, string text) => new(id, text);
        List<StudioCompare.Message> Parse(string json) => StudioCompare.ParseErrors(JsonNode.Parse(json));
        void Expect(bool ok, string what) { if (!ok) failures.Add("StudioCompare: " + what); }

        var reply = Parse("""
            { "validationMessages": [
              { "id": "A", "type": "Error", "message": "one", "ownerPath": "Screen/Widget" },
              { "id": "B", "type": "warning", "message": "three" },
              { "id": "D", "type": "Info", "message": "five" },
              { "id": "C", "message": "four" },
              { "id": "E", "type": "ValidationMessageType.Error", "message": "six" },
              { "id": "F", "type": 2, "message": "seven" } ] }
            """);
        Expect(reply.Select(m => m.Id).SequenceEqual(new[] { "A", "C", "E", "F" }), "ParseErrors skips only warning and info types");
        Expect(Parse("""[{ "id": "A", "type": "error", "message": "x" }]""").Count == 1, "a bare array is read");
        Expect(Parse("""{ "validationMessages": [] }""").Count == 0, "an empty list parses to empty");
        try { Parse("{}"); failures.Add("StudioCompare: a reply with no list was accepted"); } catch (CliError) { }

        var copy = new[] { M("A", "one"), M("A", "two"), M("B", "three"), M("C", "four") };
        var studio = new[] { M("A", "  one. "), M("C", "four  \n"), M("B", "other words") };
        // A matches by text (spacing and a trailing dot ignored), B by id in the second pass, C by text; the second A has no partner.
        Expect(StudioCompare.InStudio(copy, studio).SequenceEqual(new[] { true, false, true, true }), "InStudio: normalized text, then id count");
        Expect(StudioCompare.InStudio(new[] { M("A", "x"), M("A", "y") }, new[] { M("A", "z") }).Count(f => f) == 1, "InStudio: one Studio error matches one copy error only");

        var none = StudioCompare.Attention("Mod", 4, 0, 0, null);
        Expect(none.Contains("Service Studio shows no such errors. Save the module by hand (File > Save As) and use that file.") && none.Contains("has 4") && none.Contains("lists 0"), "Attention: Studio list empty");
        var mismatch = StudioCompare.Attention("Mod", 4, 3, 0, null);
        Expect(mismatch.Contains("Service Studio lists 3 error(s), the copy has 4, and they do not match. Compare them by hand") && !mismatch.Contains("shows no such errors"), "Attention: Studio has errors, none match");
        var all = StudioCompare.Attention("Mod", 4, 4, 4, null);
        Expect(all.Contains("real errors") && all.Contains("has 4") && all.Contains("lists 4"), "Attention: all match");
        var some = StudioCompare.Attention("Mod", 4, 5, 2, null);
        Expect(some.Contains("2 of the copy's") && some.Contains("has 4") && some.Contains("lists 5") && !some.Contains("no such errors"), "Attention: some match");
        var failed = StudioCompare.Attention("Mod", 4, null, 0, "boom");
        Expect(failed.Contains("comparison with Service Studio was not possible (boom)") && failed.Contains("has 4") && !failed.Contains("no such errors"), "Attention: failed comparison");

        // Samples name their module: from the owner chain, or from the fallback when the message has no owner.
        using (var m = Model.Load(blank))
        {
            var name = ((OutSystems.Model.Applications.IModule)m).Name;
            m.CreateServerAction("BadAction", Model.Services().NewKey()).CreateNode<OutSystems.Model.Logic.Nodes.IIfNode>("NoCondition", Model.Services().NewKey());
            var errors = Model.Errors(m);
            var json = JsonOutput.Serialize(Model.Grouped(errors));
            Expect(errors.Length > 0 && json.Contains($"\"module\":\"{name}\"") && json.Contains("\"owner\":\"BadAction\""), "a sample names its module and owner path: ");
            var ownerless = JsonOutput.Serialize(Model.Grouped(new[] { new NoOwner() }));
            Expect(!ownerless.Contains("module") && !ownerless.Contains("inStudio"), "module and inStudio are omitted when unknown");
            Expect(JsonOutput.Serialize(Model.Grouped(new[] { new NoOwner() }, module: "Mod", inStudio: _ => false)).Contains("\"module\":\"Mod\",") , "module fallback and inStudio false are written");
        }
    }

    sealed class NoOwner : OutSystems.Model.IValidationMessage
    {
        public string Id => "X"; public OutSystems.Model.ValidationMessageType Type => OutSystems.Model.ValidationMessageType.Error;
        public string Message => "m"; public string Detail => "d"; public bool Hidden { get; set; } public OutSystems.Model.IObjectSignature Owner => null!;
    }
}