using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Logic.Nodes;

namespace OmlCli;

static partial class SelfTest
{
    static void FlowLayoutTest(string blank, string dir, List<string> failures)
    {
        var path = Path.Combine(dir, "layout.json");
        string? Build(string source, string spec, string name)
        {
            File.WriteAllText(path, spec);
            var output = Path.Combine(dir, "layout-" + name + ".oml");
            var (code, report) = Apply.Execute(source, path, output);
            if (code == 0) return output;
            failures.Add("flow layout " + name + ": " + JsonSerializer.Serialize(report));
            return null;
        }
        void Require(bool condition, string message) { if (!condition) failures.Add("flow layout: " + message); }
        Dictionary<string, (int x, int y)> Positions(IObjectSignature root) => ProductionRules.Walk(root)
            .OfType<IFlowNode>().ToDictionary(n => n.GlobalKey.ToString()!, n => (n.HorizontalPosition, n.VerticalPosition));
        void Same(IObjectSignature root, Dictionary<string, (int x, int y)> expected, string message)
        {
            var actual = Positions(root);
            Require(actual.OrderBy(p => p.Key).SequenceEqual(expected.OrderBy(p => p.Key)), message + ": "
                + string.Join("; ", ProductionRules.Walk(root).OfType<IFlowNode>()
                    .Where(n => !expected.TryGetValue(n.GlobalKey.ToString()!, out var old)
                        || old != (n.HorizontalPosition, n.VerticalPosition))
                    .Select(n => $"{Model.NameOf(n)} {expected.GetValueOrDefault(n.GlobalKey.ToString()!)} -> ({n.HorizontalPosition}, {n.VerticalPosition})")));
        }
        void Check(IObjectSignature owner)
        {
            var nodes = FlowLayout.Nodes(owner).Where(n => !FlowLayout.IsComment(n)).ToList();
            for (var i = 0; i < nodes.Count; i++)
            for (var j = i + 1; j < nodes.Count; j++)
                // Actual Studio default node bounds, independently measured with oml api NodeSizes.
                Require(Math.Abs(nodes[i].HorizontalPosition - nodes[j].HorizontalPosition) >= 914
                    || Math.Abs(nodes[i].VerticalPosition - nodes[j].VerticalPosition) >= 928,
                    $"overlap in {Model.NameOf(owner)}: {Model.NameOf(nodes[i])}, {Model.NameOf(nodes[j])}");
            foreach (var start in nodes.OfType<IStartNode>())
            foreach (var (target, _) in FlowLayout.Edges(start))
                Require(start.VerticalPosition < target.VerticalPosition, "Start is not above its successor");
            foreach (var from in nodes)
            foreach (var (target, _) in FlowLayout.Edges(from).Where(e => e.target is IEndNode))
                Require(from.VerticalPosition < target.VerticalPosition, "End is not below its predecessor");
            foreach (var note in FlowLayout.Nodes(owner).Where(FlowLayout.IsComment))
            foreach (var node in nodes)
                Require(Math.Abs(note.HorizontalPosition - node.HorizontalPosition) >= 914
                    || Math.Abs(note.VerticalPosition - node.VerticalPosition) >= 928, "node overlaps fixed comment");
        }

        var chain = Enumerable.Range(0, 64).Select(i => new { kind = "assign", name = "Step" + i, assign = new { N = "N + 1" } });
        var spec = """
            {"clientActions":[
              {"name":"Keep","flow":[{"kind":"javaScript","name":"Work","javaScript":"void 0;"}]},
              {"name":"FxComplex","locals":[{"name":"Rows","type":"List of Integer"},{"name":"N","type":"Integer"}],"flow":[
                {"kind":"if","name":"Gate","condition":"True","true":"Pick","false":"Loop"},
                {"kind":"switch","name":"Pick","cases":[{"condition":"N = 0","next":"A"},{"condition":"N = 1","next":"B"}],"otherwise":"Other"},
                {"kind":"assign","name":"A","assign":{"N":"1"},"next":"Join"},
                {"kind":"assign","name":"B","assign":{"N":"2"},"next":"Join"},
                {"kind":"end","name":"Other"},
                {"kind":"assign","name":"Join","assign":{"N":"3"},"next":"Done"},
                {"kind":"end","name":"Done"},
                {"kind":"forEach","name":"Loop","recordList":"Rows","cycle":"Body","next":"End"},
                {"kind":"assign","name":"Body","assign":{"N":"N + 1"},"next":"Loop"},
                {"kind":"comment","name":"Note","text":"Keep my position"},
                {"kind":"exceptionHandler","name":"OnError","exception":"AllExceptions","next":"Recover"},
                {"kind":"assign","name":"Recover","assign":{"N":"0"}}
              ]}],
              "screens":[{"name":"CopyText","flow":"Showcase","actions":[{"name":"Copy","flow":[{"kind":"javaScript","javaScript":"void 0;"}]}]}]
            }
            """;
        var json = System.Text.Json.Nodes.JsonNode.Parse(spec)!;
        json["clientActions"]!.AsArray().Add(JsonSerializer.SerializeToNode(new
        { name = "FxLong", locals = new[] { new { name = "N", type = "Integer" } }, flow = chain }));
        var built = Build(blank, json.ToJsonString(), "built");
        if (built is null) return;
        Dictionary<string, (int x, int y)> keep, complex, copy, comments;
        var seeded = Path.Combine(dir, "layout-seeded.oml");
        using (var module = Model.Load(built))
        {
            foreach (var flow in FlowLayout.Flows(module)) Check(flow);
            var action = ElementPaths.Resolve(module, "FxComplex");
            var nodes = FlowLayout.Nodes(action).Where(n => n is not IEndNode && !FlowLayout.IsComment(n))
                .ToDictionary(n => Model.NameOf(n)!);
            Require(nodes["Loop"].HorizontalPosition == nodes["Gate"].HorizontalPosition, "If false branch must run down");
            Require(nodes["Pick"].HorizontalPosition > nodes["Gate"].HorizontalPosition, "If true branch must run right");
            Require(nodes["Body"].HorizontalPosition > nodes["Loop"].HorizontalPosition, "loop body must run right");
            var afterLoop = FlowLayout.Edges(nodes["Loop"]).Single(e => e.kind == "Sequence").target;
            Require(afterLoop.VerticalPosition > nodes["Body"].VerticalPosition, "loop continuation must follow its body");
            Require(FlowLayout.Edges(nodes["Body"]).Any(e => ReferenceEquals(e.target, nodes["Loop"])), "loop return connector was lost");
            Require(nodes["A"].HorizontalPosition != nodes["B"].HorizontalPosition
                && nodes["A"].VerticalPosition == nodes["B"].VerticalPosition, "switch cases must be side by side");
            Require(nodes["Join"].VerticalPosition > Math.Max(nodes["A"].VerticalPosition, nodes["B"].VerticalPosition), "join must follow both branches");
            Require(nodes["OnError"].HorizontalPosition > nodes["Pick"].HorizontalPosition, "handler must have a separate column");
            var longNodes = FlowLayout.Nodes(ElementPaths.Resolve(module, "FxLong")).OrderBy(n => n.VerticalPosition).ToList();
            Require(longNodes.Count >= 66, "large flow missing nodes");
            Require(longNodes.All(n => n.HorizontalPosition == longNodes[0].HorizontalPosition)
                && longNodes.Zip(longNodes.Skip(1)).All(p => p.Second.VerticalPosition - p.First.VerticalPosition == 2660),
                "large chain must have a straight column and even Studio spacing");
            foreach (var flow in FlowLayout.Flows(module))
            {
                var i = 0;
                foreach (var node in FlowLayout.Nodes(flow))
                {
                    node.HorizontalPosition = 17000;
                    node.VerticalPosition = 9000 + i++ * 1900;
                }
            }
            keep = Positions(ElementPaths.Resolve(module, "Keep"));
            complex = Positions(action);
            copy = Positions(ElementPaths.Resolve(module, "Showcase/CopyText/Copy"));
            comments = FlowLayout.Nodes(action).Where(FlowLayout.IsComment).ToDictionary(n => n.GlobalKey.ToString()!, n => (n.HorizontalPosition, n.VerticalPosition));
            Model.Save(module, seeded);
        }
        var noOp = Build(seeded, "{}", "noop");
        if (noOp is null) return;
        using (var module = Model.Load(noOp))
        {
            Same(ElementPaths.Resolve(module, "Keep"), keep, "empty spec moved untouched action");
            Same(ElementPaths.Resolve(module, "FxComplex"), complex, "empty spec moved complex action");
        }
        var edited = Build(seeded, """
            {"edits":[{"op":"insertNodes","action":"FxLong","after":"Start","flow":[{"kind":"assign","name":"Inserted","assign":{"N":"0"}}]},
                       {"op":"delete","element":"FxLong/Step0"}]}
            """, "edited");
        if (edited is null) return;
        using (var module = Model.Load(edited))
        {
            Check(ElementPaths.Resolve(module, "FxLong"));
            Require(FlowLayout.Nodes(ElementPaths.Resolve(module, "FxLong")).Single(n => Model.NameOf(n) == "Start").HorizontalPosition == 3196, "edited existing flow was not laid out");
            Same(ElementPaths.Resolve(module, "Keep"), keep, "editing another flow moved Keep");
            Same(ElementPaths.Resolve(module, "FxComplex"), complex, "editing another flow moved FxComplex");
        }
        var tidy = Build(seeded, """{"tidyFlows":["Fx*","Showcase/CopyText/Copy"]}""", "tidy");
        if (tidy is null) return;
        Dictionary<string, (int x, int y)> stable;
        using (var module = Model.Load(tidy))
        {
            Same(ElementPaths.Resolve(module, "Keep"), keep, "tidy pattern moved unmatched flow");
            foreach (var name in new[] { "FxLong", "FxComplex", "Showcase/CopyText/Copy" }) Check(ElementPaths.Resolve(module, name));
            Require(!Positions(ElementPaths.Resolve(module, "FxComplex")).OrderBy(p => p.Key).SequenceEqual(complex.OrderBy(p => p.Key)), "tidy pattern had no effect");
            Require(!Positions(ElementPaths.Resolve(module, "Showcase/CopyText/Copy")).OrderBy(p => p.Key).SequenceEqual(copy.OrderBy(p => p.Key)), "qualified tidy path had no effect");
            foreach (var note in ProductionRules.Walk(module).OfType<ICommentNode>())
                Require(comments[note.GlobalKey.ToString()!] == (note.HorizontalPosition, note.VerticalPosition), "comment moved");
            stable = Positions(ElementPaths.Resolve(module, "FxComplex"));
        }
        var again = Build(tidy, """{"tidyFlows":["*"]}""", "again");
        if (again is null) return;
        using (var module = Model.Load(again))
        {
            foreach (var flow in FlowLayout.Flows(module)) Check(flow);
            Same(ElementPaths.Resolve(module, "FxComplex"), stable, "layout changed on a second run");
            Require(FlowLayout.Nodes(ElementPaths.Resolve(module, "Keep")).OfType<IStartNode>().Single().HorizontalPosition == 3196, "tidy * did not select Keep");
        }
    }
}
