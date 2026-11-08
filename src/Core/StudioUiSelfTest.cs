using System.Text.Json.Nodes;

namespace OmlCli;

// The decisions behind oml studio close and oml publish, on fake data: no Service Studio window or call.
static class StudioUiSelfTest
{
    public static void Run(List<string> failures)
    {
        void Check(bool ok, string what) { if (!ok) failures.Add("studio decisions: " + what); }
        var parsed = StudioTabs.ParseLabel("* MCP_Core (OUIAI)");
        Check(parsed == ("MCP_Core", "OUIAI", true), "label with mark and application");
        Check(StudioTabs.ParseLabel("OUI") == ("OUI", null, false), "plain label");
        StudioTabs.Tab T(int i, string? label) => new(1, i, label);
        var tabs = new[] { T(0, "OUI"), T(1, "OUIAI"), T(2, "MCP_API (OUIAI)"), T(3, "* MCP_Core"), T(4, null) };
        Check(StudioTabs.Pick(tabs, "OUIAI").tab?.Index == 1, "exact label, not the longer name");
        Check(StudioTabs.Pick(tabs, "MCP_API").tab?.Index == 2, "label with application");
        Check(StudioTabs.Pick(tabs, "MCP_Core").refusal?.Contains("unsaved") == true, "an unsaved tab is refused");
        Check(StudioTabs.Pick(tabs, "OU").tab is null, "a near match is no match");
        Check(StudioTabs.Pick([T(0, "OUI"), T(1, "OUI")], "OUI").refusal is not null, "two tabs with the label are refused");
        Check(StudioTabs.CheckAfter("A", ["A", "B"], ["B"]) is null, "the module closed");
        Check(StudioTabs.CheckAfter("A", ["A", "B"], ["A", "B"])?.Contains("still open") == true, "the module stayed open");
        Check(StudioTabs.CheckAfter("A", ["A", "B"], [])?.Contains("B") == true, "another module closed");
        var scan = JsonNode.Parse("""{"windows":[{"window":9,"tabs":[{"index":0,"label":"OUI"},{"index":1,"label":null}]}]}""");
        Check(StudioTabs.Read(scan) is [{ Window: 9, Label: "OUI" }, { Index: 1, Label: null }], "scan result");

        Check(StudioPush.StaleBase("M", "own", ["own", "pub", "old"], "f", ["pub", "old"]) is null, "the file holds the last saved version");
        Check(StudioPush.StaleBase("M", "own", ["own", "pub", "old"], "f", ["old"])?.Contains("concurrent publication") == true, "an older file is refused");
        Check(StudioPush.StaleBase("M", "own", ["own"], "f", ["old"]) is null, "only the session's own save: unknown");
        Check(StudioPush.StaleBase("M", "own", ["own", "pub"], "f", []) is null, "a file without history: unknown");
        Check(StudioPush.StaleBase("M", "own", ["own", "pub"], "pub", ["x"]) is null, "the file is the last saved version");

        var dir = Path.Combine(Path.GetTempPath(), "oml-cli", "writeback-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var (target, copy) = (Path.Combine(dir, "m.oml"), Path.Combine(dir, "c.oml"));
            File.WriteAllText(target, "old"); File.WriteAllText(copy, "new");
            var backup = StudioPush.ReplaceWithBackup(target, copy);
            Check(File.ReadAllText(target) == "new" && File.ReadAllText(backup) == "old" && backup.EndsWith(".bak", StringComparison.Ordinal), "write back with a backup");
        }
        finally { Directory.Delete(dir, recursive: true); }

        var outdated = JsonNode.Parse("""{"messages":["Outdated Consumer: 'OUIAI' uses an older version of 'OUI'"],"details":[{"message":"Outdated Consumer","extraInfo":"MCP_API;key"}]}""");
        Check(StudioGuards.OutdatedConsumers(outdated, "OUI") is ["MCP_API", "OUIAI"], "consumers from the publish result");
        Check(StudioGuards.OutdatedConsumers(JsonNode.Parse("""{"messages":["Published"]}"""), "OUI").Count == 0, "no consumers named");
        Check(StudioGuards.ConcurrentPublication(JsonNode.Parse("""{"messages":["A concurrent publication was detected"]}""")), "concurrent publication answer");
    }
}
