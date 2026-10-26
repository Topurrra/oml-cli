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

        // install, publish and references decisions (StudioGuards): fake data only.
        StudioGuards.ErrorKey K(string id, string owner) => new(id, owner);
        var (gAdded, gOld) = StudioGuards.SplitErrors(new[] { K("A", "X"), K("A", "X"), K("B", "Y"), K("A", "Z") }, new[] { K("A", "X"), K("B", "Y") });
        Expect(gOld.Count == 2 && gAdded.SequenceEqual(new[] { K("A", "X"), K("A", "Z") }), "SplitErrors: duplicates are counted, owner matters");
        Expect(StudioGuards.InstallRefusal(0, 0, false) is null, "install: no errors passes");
        Expect(StudioGuards.InstallRefusal(2, 0, false)!.Contains("--allow-existing-errors"), "install: old errors refuse and name the flag");
        Expect(StudioGuards.InstallRefusal(2, 0, true) is null, "install: only old errors pass with the flag");
        Expect(StudioGuards.InstallRefusal(2, 1, true) is { } r1 && r1.Contains("refusing"), "install: a new error refuses with the flag");
        Expect(StudioGuards.InstallRefusal(2, 1, false) is { } r2 && !r2.Contains("--allow"), "install: a new error refuses without the flag, no flag hint");
        Expect(StudioGuards.PublishWarning("M", JsonNode.Parse("""{ "name": "M", "version": "0" }""")) is { } p0 && p0.Contains("extracted from a package"), "publish: version 0 warns");
        Expect(StudioGuards.PublishWarning("M", JsonNode.Parse("""{ "name": "M", "version": "12" }""")) is null && StudioGuards.PublishWarning("M", JsonNode.Parse("""{ "name": "M" }""")) is null && StudioGuards.PublishWarning("M", null) is null, "publish: other or unknown version passes");
        Expect(StudioGuards.PublishWarning("M", JsonNode.Parse("""{ "version": 0 }""")) is not null && StudioGuards.PublishWarning("M", JsonNode.Parse("""{ "version": 3 }""")) is null
               && StudioGuards.PublishWarning("M", JsonNode.Parse("""{ "version": { "n": "0" } }""")) is null && StudioGuards.PublishWarning("M", JsonNode.Parse("\"0\"")) is null, "publish: odd version shapes fail open");
        Expect(StudioGuards.FindApp(JsonNode.Parse("""{ "apps": [ 5, { "name": 7 }, { "name": "M", "version": "0" } ] }"""), "M") is { } found && found["version"]!.ToString() == "0"
               && StudioGuards.FindApp(JsonNode.Parse("""{ "apps": "x" }"""), "M") is null && StudioGuards.FindApp(JsonNode.Parse("[1]"), "M") is null && StudioGuards.FindApp(null, "M") is null, "publish: listApps lookup never throws");
        Expect(StudioGuards.RefreshOutcome(JsonNode.Parse("""{ "hasErrors": false, "items": ["Screen is busy"], "detail": { "text": "x is busy" } }""")).refreshed, "references: a deeper text does not count");
        var busy = StudioGuards.RefreshOutcome(JsonNode.Parse("""{ "message": "Service Studio is busy, try again." }"""));
        Expect(!busy.refreshed && busy.message!.Contains("busy"), "references: busy is not a refresh");
        Expect(StudioGuards.RefreshOutcome(JsonNode.Parse("""{ "hasErrors": false, "items": ["done"] }""")).refreshed, "references: a real refresh");

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

        InstallFlow(blank, failures);
    }

    // Real install on temp files: the flag lets old errors pass, a new error refuses, the target stays unchanged on refusal.
    static void InstallFlow(string blank, List<string> failures)
    {
        void Expect(bool ok, string what) { if (!ok) failures.Add("install flag: " + what); }
        var dir = Path.Combine(Path.GetTempPath(), "oml-installflag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var oldOut = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            string Make(string file, params string[] badActions)
            {
                var path = Path.Combine(dir, file);
                using var m = Model.Load(blank);
                foreach (var a in badActions)
                    m.CreateServerAction(a, Model.Services().NewKey()).CreateNode<OutSystems.Model.Logic.Nodes.IIfNode>("NoCondition", Model.Services().NewKey());
                Model.Save(m, path);
                return path;
            }
            string Hash(string p) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)));
            int Try(string staged, string target, bool allow, out string error)
            {
                try { error = ""; return Cli.Install(staged, target, allow); }
                catch (CliError e) { error = e.Message; return e.ExitCode; }
            }
            var oneBad = Make("one.oml", "BadAction");
            var twoBad = Make("two.oml", "BadAction", "OtherBad");
            var clean = Make("clean.oml");

            var target = Path.Combine(dir, "t1.oml"); File.Copy(oneBad, target);
            Expect(Try(oneBad, target, false, out var e1) == 1 && e1.Contains("--allow-existing-errors"), "old errors without the flag refuse and name the flag");
            Expect(Try(oneBad, target, true, out _) == 0, "old errors pass with the flag");
            Expect(Hash(target) == Hash(oneBad), "the installed file is the staged file");

            target = Path.Combine(dir, "t2.oml"); File.Copy(oneBad, target); var before = Hash(target);
            Expect(Try(twoBad, target, true, out var e2) == 1 && e2.Contains("target does not have"), "a new error refuses with the flag");
            Expect(Try(twoBad, target, false, out var e3) == 1 && !e3.Contains("--allow"), "a new error refuses without the flag");
            Expect(Hash(target) == before, "the target is unchanged after a refusal");

            target = Path.Combine(dir, "t3.oml"); File.Copy(clean, target);
            Expect(Try(oneBad, target, true, out _) == 1, "errors that the target lacks refuse with the flag");
        }
        finally { Console.SetOut(oldOut); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    sealed class NoOwner : OutSystems.Model.IValidationMessage
    {
        public string Id => "X"; public OutSystems.Model.ValidationMessageType Type => OutSystems.Model.ValidationMessageType.Error;
        public string Message => "m"; public string Detail => "d"; public bool Hidden { get; set; } public OutSystems.Model.IObjectSignature Owner => null!;
    }
}