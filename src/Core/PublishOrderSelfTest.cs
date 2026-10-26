using System.Text.Json.Nodes;

namespace OmlCli;

// Publish order and batch publish: pure functions and a fake publisher, so nothing here talks to Service Studio.
static class PublishOrderSelfTest
{
    public static void Run(List<string> failures)
    {
        void Expect(bool ok, string what) { if (!ok) failures.Add("PublishOrder: " + what); }
        string Join(IEnumerable<(string module, int level)> o) => string.Join(",", o.Select(x => $"{x.module}:{x.level}"));

        // Consumer to producer: App uses Core, Core uses Lib; Shared is used by App and Lib; Alone has no edge.
        string[] all = ["App", "Core", "Lib", "Shared", "Alone"];
        (string, string)[] edges = [("App", "Core"), ("Core", "Lib"), ("App", "Shared"), ("Lib", "Shared"), ("Core", "Missing"), ("App", "App")];
        var chain = PublishOrder.Compute(all, edges);
        Expect(Join(chain.order) == "Alone:0,Shared:0,Lib:1,Core:2,App:3" && chain.cycles.Count == 0, "chain: producers first, same level by name: " + Join(chain.order));
        Expect(Join(PublishOrder.Compute(all.Reverse(), edges.Reverse()).order) == Join(chain.order), "the input order does not change the result");

        // Changed set: only those modules, ordered by the full graph (App and Lib depend through the unchanged Core); levels start at 0.
        var some = PublishOrder.Compute(all, edges, new HashSet<string>(["app", "Lib"], StringComparer.OrdinalIgnoreCase));
        Expect(Join(some.order) == "Lib:0,App:1", "changed set keeps order and renumbers levels: " + Join(some.order));

        // Cycle: B and C use each other; A uses B; D is used by C. Cycle members share a level and sort by name.
        var cyclic = PublishOrder.Compute(["A", "B", "C", "D"], [("A", "B"), ("B", "C"), ("C", "B"), ("C", "D")]);
        Expect(Join(cyclic.order) == "D:0,B:1,C:1,A:2" && cyclic.cycles.Count == 1 && string.Join(",", cyclic.cycles[0]) == "B,C", "cycle: " + Join(cyclic.order));
        var oneInCycle = PublishOrder.Compute(["A", "B", "C", "D"], [("A", "B"), ("B", "C"), ("C", "B"), ("C", "D")], new HashSet<string>(["B", "A"]));
        Expect(oneInCycle.cycles.Count == 0 && Join(oneInCycle.order) == "B:0,A:1", "a cycle with one changed member is not reported");
        var twoCycles = PublishOrder.Compute(["Z1", "Z2", "A1", "A2"], [("Z1", "Z2"), ("Z2", "Z1"), ("A1", "A2"), ("A2", "A1")]);
        Expect(twoCycles.cycles.Count == 2 && twoCycles.cycles[0][0] == "A1" && Join(twoCycles.order) == "A1:0,A2:0,Z1:0,Z2:0", "cycles are sorted by name");

        // Batch publish with a fake publisher.
        var order = PublishBatch.ReadOrder("""{ "order": [ { "module": "Lib", "file": "C:/other/Lib.oml", "level": 0 }, { "module": "Core" }, { "module": "App", "file": "App.oml" } ], "cycles": [] }""");
        Expect(order.Select(i => i.module).SequenceEqual(["Lib", "Core", "App"]), "order file is read");
        foreach (var bad in new[] { "", "[]", """{ "order": [] }""", """{ "order": [ { "name": "A" } ] }""", """{ "order": [ { "module": "A" }, { "module": "a" } ] }""", "{ not json" })
            try { PublishBatch.ReadOrder(bad); failures.Add("PublishOrder: a bad order file was accepted: " + bad); } catch (CliError e) when (e.ExitCode == 2) { }

        string[] folder = [@"C:\w\Lib.oml", @"C:\w\sub\Core.oml", @"C:\w\App.oml"];
        string NameOf(string path) => Path.GetFileNameWithoutExtension(path);
        var plan = PublishBatch.Plan(order, folder, NameOf);
        Expect(plan.Select(i => i.file).SequenceEqual([folder[0], folder[1], folder[2]]) && plan[1].module == "Core", "plan: files found by name, in order, also in a subfolder");
        string Refusal(List<PublishBatch.Item> o, string[] files, Func<string, string>? nameOf = null)
        {
            try { PublishBatch.Plan(o, files, nameOf ?? NameOf); return "none"; }
            catch (CliError e) when (e.ExitCode == 2) { return e.Message; }
        }
        Expect(Refusal(order, [folder[0], folder[2]]) is { } missing && missing.Contains("Core") && missing.Contains("Nothing was published"), "plan: a module missing from the folder gives exit 2");
        Expect(Refusal(order, [.. folder, @"C:\w\x\App.oml"]).Contains("2 files named App.oml"), "plan: two files with one name are refused");
        Expect(Refusal(order, folder, p => NameOf(p) == "Core" ? "Other" : NameOf(p)).Contains("holds module Other"), "plan: the module name in the file must match");

        Expect(Refusal([new("Pkg", @"C:\wpp.oap!Pkg.oml")], folder).Contains("inside a package"), "plan: a module from a package is refused with a clear reason");
        try { PublishBatch.RefuseOpen(plan, ["Other", "Core"]); failures.Add("PublishOrder: an already open module was accepted"); }
        catch (CliError e) when (e.ExitCode == 2) { Expect(e.Message.Contains("Core") && !e.Message.Contains("Other") && e.Message.Contains("Nothing was published"), "open modules named: " + e.Message); }
        PublishBatch.RefuseOpen(plan, ["Other"]);

        var published = new List<string>();
        PublishBatch.PublishResult Fake(string file, string? failOn, bool throwOnFail = false)
        {
            published.Add(NameOf(file));
            if (NameOf(file) == failOn) return throwOnFail ? throw new CliError("Service Studio is not reachable", 2) : new(NameOf(file), false, null, JsonNode.Parse("""{ "published": false }"""));
            return new(NameOf(file), true, NameOf(file) == "Core" ? "version 0" : null, null);
        }
        string Show(object o) => System.Text.Json.JsonSerializer.Serialize(o);

        var (okCode, okReport) = PublishBatch.Run(plan, f => Fake(f, null));
        Expect(okCode == 0 && string.Join(",", published) == "Lib,Core,App" && Show(okReport).Contains("\"published\":3") && Show(okReport).Contains("\"stoppedAt\":null") && Show(okReport).Contains("\"notRun\":[]") && Show(okReport).Contains("version 0"), "batch: all succeed, in order, warnings kept: " + Show(okReport));

        published.Clear();
        var (failCode, failReport) = PublishBatch.Run(plan, f => Fake(f, "Core"));
        var failText = Show(failReport);
        Expect(failCode == 1 && string.Join(",", published) == "Lib,Core" && failText.Contains("\"stoppedAt\":\"Core\"") && failText.Contains("\"notRun\":[\"App\"]") && failText.Contains("\"published\":1") && failText.Contains("1-Click Publish"), "batch: the second fails, the third is never called: " + failText);

        published.Clear();
        var (throwCode, throwReport) = PublishBatch.Run(plan, f => Fake(f, "Lib", throwOnFail: true));
        Expect(throwCode == 1 && string.Join(",", published) == "Lib" && Show(throwReport).Contains("not reachable") && Show(throwReport).Contains("\"notRun\":[\"Core\",\"App\"]"), "batch: a publisher error stops the batch: " + Show(throwReport));

        published.Clear();
        var (crashCode, crashReport) = PublishBatch.Run(plan, f => NameOf(f) == "Core" ? throw new InvalidOperationException("boom") : Fake(f, null));
        Expect(crashCode == 1 && Show(crashReport).Contains("\"stoppedAt\":\"Core\"") && Show(crashReport).Contains("boom") && Show(crashReport).Contains("\"published\":1"), "batch: any exception stops the batch and keeps the report: " + Show(crashReport));
    }
}
