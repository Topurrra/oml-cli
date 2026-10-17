using System.Text.Json.Nodes;

namespace OmlCli;

static partial class SelfTest
{
    static void ArchitectureTest(string builtPath, List<string> failures)
    {
        try
        {
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Shop*"] = "Core", ["Shop_CW"] = "Foundation" };
            void Check(bool condition, string message) { if (!condition) failures.Add("architecture: " + message); }
            Check(Architecture.Layer("Shop_CW", "Reactive", 0, 0, 0, overrides) == ("Foundation", "override"), "exact override must win");
            Check(Architecture.Layer("Shop_Th", "Reactive", 0, 0, 0, overrides) == ("Core", "override"), "wildcard override");
            overrides.Clear();
            Check(Architecture.Layer("shop_cs", "Reactive", 1, 0, 0, overrides) == ("Core", "suffix _CS"), "case insensitive suffix precedes content");
            Check(Architecture.Layer("Neutral", "Reactive", 1, 0, 0, overrides).layer == "EndUser", "screen content");
            Check(Architecture.Layer("Neutral", "Reactive", 0, 1, 0, overrides).layer == "Core", "entity content");
            Check(Architecture.Layer("Neutral", "Reactive", 0, 0, 0, overrides).layer == "Foundation", "utility content");
            Check(Architecture.Layer("Thing_CS", "Library", 0, 0, 0, overrides).layer == "Foundation", "library kind");
            Check(Architecture.Layer("Thing_CS", "Extension", 0, 0, 0, overrides).layer == "Foundation", "extension kind");
            using var built = Model.Load(builtPath);
            var snapshot = Architecture.ReadModule(built, "Test", overrides);
            Check(snapshot.data["counts"]!["screens"]!.GetValue<int>() == 2, "screen count");
            Check(snapshot.data["entities"]!.AsArray().Any(e => e!["name"]!.GetValue<string>() == "Order" && e["references"]!.AsArray().Any(r => r!.GetValue<string>() == "Customer")), "entity references");
            Check(snapshot.data["counts"]!["blocks"]!.GetValue<int>() == 1, "block count");
            var customer = built.Entities.Single(e => e.Name == "Customer");
            var parent = customer.CreateAttribute("ParentId", Model.Services().NewKey());
            parent.DataType = customer.IdentifierType;
            var recursive = Architecture.ReadModule(built, "Test", overrides);
            Check(recursive.data["entities"]!.AsArray().Single(e => e!["name"]!.GetValue<string>() == "Customer")!["references"]!.AsArray()
                .Any(r => r!.GetValue<string>() == "Customer"), "self-referencing foreign key");
            var a = (JsonObject)snapshot.data.DeepClone(); a["name"] = "A"; a["layer"] = "Foundation";
            var b = (JsonObject)snapshot.data.DeepClone(); b["name"] = "B"; b["layer"] = "Core";
            var c = (JsonObject)snapshot.data.DeepClone(); c["name"] = "C"; c["layer"] = "EndUser";
            var d = (JsonObject)snapshot.data.DeepClone(); d["name"] = "D"; d["layer"] = "EndUser";
            a["counts"]!["serverActions"] = 151;
            var snapshots = new Dictionary<string, Architecture.Snapshot> { ["A"] = new(a, [new("ServerAction", "Unused")], 1), ["B"] = new(b, [], 0), ["C"] = new(c, [], 0), ["D"] = new(d, [], 0) };
            var edges = new List<Architecture.Edge> { new("A", "B", true, [new("Entity", "Order")]), new("B", "A", true, []), new("C", "D", true, [new("Block", "Card")]) };
            var findings = Architecture.Analyze(snapshots, snapshots.ToDictionary(p => p.Key, p => p.Value.data), edges);
            foreach (var id in new[] { "upward-reference", "side-reference-enduser", "cycle", "core-ui", "foundation-business", "unused-public", "large-module", "missing-description" })
                Check(findings.Any(f => f.id == id), "missing rule " + id);
            Check(findings.Single(f => f.id == "cycle").detail.Contains("A → B → A"), "closed cycle path");
            edges.RemoveAt(2); edges.Add(new("C", "D", false, [new("Theme", "Base")]));
            Check(!Architecture.Analyze(snapshots, snapshots.ToDictionary(p => p.Key, p => p.Value.data), edges).Any(f => f.id == "side-reference-enduser"), "weak EndUser reference");
        }
        catch (Exception e) { failures.Add("architecture: " + e.Message); }
    }
}
