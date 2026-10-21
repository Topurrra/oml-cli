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
            Check(Architecture.Layer("Shop_CW", "Reactive", 3, 0, 0, overrides) == ("Core", "suffix _CW"), "_CW (Core Widgets) is a Core module even with screens");
            Check(Architecture.Layer("Shop_UI", "Reactive", 0, 0, 0, overrides) == ("EndUser", "suffix _UI"), "_UI is an End-user module");
            Check(Architecture.Layer("Neutral", "Reactive", 1, 0, 0, overrides).layer == "EndUser", "screen content");
            Check(Architecture.Layer("Neutral", "Reactive", 0, 1, 0, overrides).layer == "Core", "entity content");
            Check(Architecture.Layer("Neutral", "Reactive", 0, 0, 0, overrides).layer == "Foundation", "utility content");
            Check(Architecture.Layer("Thing_CS", "Library", 0, 0, 0, overrides).layer == "Foundation", "library kind");
            Check(Architecture.Layer("Thing_CS", "Extension", 0, 0, 0, overrides).layer == "Foundation", "extension kind");
            // Layout (the page runs the same algorithm in JavaScript; tests/architecture-page-check.ps1 compares the two).
            var ladder = ArchitectureLayout.Run([0, 0, 1, 1], [(0, 3), (1, 2)]);
            Check(ladder.Crossings == 0 && ladder.Ranks.Count == 2, "sweeps must untangle a crossed ladder");
            Check(ArchitectureLayout.Run([0, 0, 1, 1], [(0, 2), (0, 3), (1, 2), (1, 3)]).Crossings == 1, "two nodes fully joined to two nodes cross once");
            var cyclic = ArchitectureLayout.Run([1, 1], [(0, 1), (1, 0)]);
            Check(cyclic.Ranks.Count == 2 && cyclic.Crossings == 0, "a two-module cycle in one layer takes two rows");
            var wide = ArchitectureLayout.Run(Enumerable.Repeat(1, 13).ToArray(), []);
            Check(wide.Ranks.Count == 3 && wide.Ranks.All(r => r.Count is >= 4 and <= ArchitectureLayout.Cols), "13 modules in a layer wrap into three balanced rows");
            // A, B in band 0 and C, D in band 2 with edges A to D and B to C; the long edges A to C and B to D skip the empty Core band without a row.
            var skip = ArchitectureLayout.Run([0, 0, 2, 2], [(0, 3), (1, 2), (0, 2)]);
            Check(skip.Ranks.Count == 2 && skip.Ranks.All(r => r.Count == 2), "an empty layer has no row and adds no dummy items");
            string Order(ArchitectureLayout.Structure x) => string.Join("|", x.Ranks.Select(r => string.Join(",", r)));
            Check(Order(ArchitectureLayout.Run([0, 0, 0, 1, 1, 1, 2, 2], [(0, 3), (1, 4), (2, 5), (3, 6), (5, 7), (0, 7), (2, 6)]))
                == Order(ArchitectureLayout.Run([0, 0, 0, 1, 1, 1, 2, 2], [(0, 3), (1, 4), (2, 5), (3, 6), (5, 7), (0, 7), (2, 6)])), "layout is deterministic");
            var doc = JsonNode.Parse("""
                {"modules":[{"name":"Web","application":"A","external":false,"layer":"EndUser"},{"name":"Lib","application":"A","external":false,"layer":"Foundation"},
                 {"name":"System","application":"(external)","external":true,"layer":"Foundation"}],
                 "edges":[{"from":"Web","to":"Lib"},{"from":"Web","to":"System"},{"from":"Lib","to":"System"},{"from":"Web","to":"Missing"},{"from":"Web","to":"Web"}]}
                """)!.AsObject();
            var layout = ArchitectureLayout.Compute(doc);
            Check(layout["crossings"]!.GetValue<int>() == 0 && layout["ranks"]!.GetValue<int>() == 3, "document layout: external modules sit in a Platform row below Foundation");
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
            // Strong kinds as the OutSystems docs list them (reuse-and-refactor/strong-weak-dependencies).
            foreach (var kind in new[] { "ServerAction", "ClientAction", "Block", "Image", "Resource", "Script", "Theme", "Role", "Process" })
                Check(Architecture.Strong([new("Entity", "Order"), new(kind, "X")]), kind + " must make a dependency strong");
            Architecture.Element[] weakOnly = [new("Entity", "Order"), new("StaticEntity", "Status"), new("Structure", "OrderDto"), new("ServiceAction", "GetOrders"), new("Screen", "OrderScreen")];
            Check(!Architecture.Strong(weakOnly), "entities, structures, service actions and screens alone are a weak dependency");
            bool Side(params Architecture.Element[] elements)
            {
                edges[2] = new("C", "D", Architecture.Strong(elements), elements);
                return Architecture.Analyze(snapshots, snapshots.ToDictionary(p => p.Key, p => p.Value.data), edges).Any(f => f.id == "side-reference-enduser");
            }
            Check(Side(new Architecture.Element("Entity", "Order")), "an entity-only EndUser reference is a side reference");
            Check(!Side(new Architecture.Element("Screen", "Orders")), "a screen-only EndUser reference is not a side reference");
            // A referenced public process is read from the reference and counts as strong.
            var dir = Path.GetDirectoryName(builtPath)!;
            var processSpec = Path.Combine(dir, "architecture-process.json");
            var processModule = Path.Combine(dir, "architecture-process.oml");
            var processBlank = Path.Combine(dir, "architecture-process-blank.oml");
            using (var blankProducer = Model.Services().CreateESpace(OutSystems.Model.Enumerations.SegmentationKind.CrossDevice, "ProcessProducer", "", false, Model.Services().NewKey()))
                Model.Save(blankProducer, processBlank);
            File.WriteAllText(processSpec, """{"processes":[{"name":"ApproveOrder","flow":[{"kind":"start","name":"Start","next":"Done"},{"kind":"end","name":"Done"}]}],"edits":[{"op":"set","element":"ApproveOrder","properties":{"public":true}}]}""");
            var (processCode, processReport) = Apply.Execute(processBlank, processSpec, processModule);
            if (processCode != 0) { failures.Add("architecture: process producer: " + System.Text.Json.JsonSerializer.Serialize(processReport)); return; }
            using var producer = Model.Load(processModule);
            built.AddDependency<OutSystems.Model.Processes.IProcess, OutSystems.Model.Processes.IProcessSignature>(producer.Processes.Single());
            var processEdge = Architecture.ReadEdges(built).SingleOrDefault(e => e.to == "ProcessProducer");
            Check(processEdge is { strong: true } && processEdge.elements.Contains(new("Process", "ApproveOrder")), "a referenced process must be a strong edge element");
        }
        catch (Exception e) { failures.Add("architecture: " + e.Message); }
    }
}
