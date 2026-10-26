using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Logic.Nodes;

namespace OmlCli;

// Self-test step for per-field record mappings (FieldMappings) and for deleting a flow node: a mapping shows in the tree
// and the description, `set` changes it, a node that a mapping reads cannot be deleted, every usage check sees the
// mapping, and a comment arrow is a note: it does not stop a delete and goes with the node it points at.
static class FieldMappingSelfTest
{
    const string Spec = """
        {
          "entities": [{ "name": "Applicant", "attributes": [{ "name": "Code", "type": "Long Integer" }, { "name": "Name", "type": "Text" }] }],
          "structures": [{ "name": "Summary", "attributes": [{ "name": "LastCode", "type": "Long Integer" }, { "name": "Name", "type": "Text" }] }],
          "serverActions": [
            { "name": "Take", "inputs": [{ "name": "Item", "type": "Summary" }], "flow": [] },
            { "name": "Collect", "locals": [{ "name": "Rec", "type": "Applicant" }, { "name": "Out", "type": "Summary" }], "flow": [
              { "kind": "aggregate", "name": "GetApplicants", "sources": ["Applicant"], "maxRecords": 1 },
              { "kind": "comment", "text": "Passes the record on." },
              { "kind": "execute", "action": "Take", "name": "Add", "args": { "Item": "Rec" } },
              { "kind": "assign", "name": "Fill", "assign": { "Out": "Rec" } } ] }
          ]
        }
        """;

    static string Edits(string json) => "{ \"edits\": [" + json + "] }";

    public static void Run(string blank, string dir, List<string> failures)
    {
        void Check(bool condition, string message) { if (!condition) failures.Add("field mappings: " + message); }
        try
        {
            string Apply(string source, string output, string json, out string report)
            {
                var spec = Path.Combine(dir, "mapping-spec.json");
                File.WriteAllText(spec, json);
                var (code, result) = OmlCli.Apply.Execute(source, spec, output);
                report = JsonSerializer.Serialize(result);
                return code == 0 ? output : "";
            }
            var built = Apply(blank, Path.Combine(dir, "mapping-built.oml"), Spec, out var buildReport);
            if (built == "") { failures.Add("field mappings: build: " + buildReport); return; }

            // The unmapped field is listed; the field the two records share is mapped by name and stays out.
            using (var m = Model.Load(built))
            {
                var argument = ElementPaths.Resolve(m, "Collect/Add/Argument[0]");
                var fields = ElementPaths.Children(argument).Where(FieldMappings.Is).Select(f => Model.NameOf(f)).ToList();
                Check(fields.SequenceEqual(["LastCode"]), "an unmapped field must be a child named after the field; got " + string.Join(",", fields));
                Check(ElementPaths.Kind(ElementPaths.Resolve(m, "Collect/Add/Argument[0]/LastCode")) == "FieldMapping", "the child must be a FieldMapping");
            }

            // set fills the field; tree and describe show it; the node it reads cannot go.
            var set = Apply(built, Path.Combine(dir, "mapping-set.oml"), Edits("""{ "op": "set", "element": "ServerAction:Collect/Add/Argument[0]/LastCode", "properties": { "value": "GetApplicants.List.Current.Applicant.Code" } }"""), out var setReport);
            if (set == "") { failures.Add("field mappings: set: " + setReport); return; }
            using (var m = Model.Load(set))
            {
                var tree = JsonSerializer.Serialize(Inspect.Tree(m, "ServerAction:Collect/Add", 20));
                Check(tree.Contains("\"kind\":\"FieldMapping\",\"name\":\"LastCode\"") && tree.Contains("LastCode/ParsedExpression[0]") && tree.Contains("GetApplicants.List.Current.Applicant.Code"),
                    "tree must show the mapping and its expression: " + tree);
                var text = JsonSerializer.Serialize(Describe.Run(m, "Collect", 3, []));
                Check(text.Contains("Item = Rec { LastCode = GetApplicants.List.Current.Applicant.Code }"), "describe must print the mapping: " + text);
                var read = ElementPaths.Resolve(m, "Collect/Add/Argument[0]/LastCode/ParsedExpression[0]");
                Check(read is IExpression { Text: "GetApplicants.List.Current.Applicant.Code" }, "the expression below the mapping must resolve");
            }
            var refused = Apply(set, Path.Combine(dir, "mapping-refused.oml"), Edits("""{ "op": "delete", "element": "Collect/GetApplicants" }"""), out var refusedReport);
            Check(refused == "" && refusedReport.Contains("refused: cannot delete") && refusedReport.Contains("LastCode") && refusedReport.Contains("GetApplicants.List.Current.Applicant.Code"),
                "deleting a node that a mapping reads must be refused with the mapping: " + refusedReport);

            // After set, the delete works and the flow is joined again; both in one spec.
            var freed = Apply(set, Path.Combine(dir, "mapping-freed.oml"), Edits("""{ "op": "set", "element": "Collect/Add/Argument[0]/LastCode", "properties": { "value": "Rec.Code" } }, { "op": "delete", "element": "Collect/GetApplicants" }"""), out var freedReport);
            Check(freed != "", "delete after set must work: " + freedReport);
            if (freed != "")
                using (var m = Model.Load(freed))
                {
                    var action = ElementPaths.Resolve(m, "Collect");
                    Check(!ElementPaths.Children(action).Any(n => Model.NameOf(n) == "GetApplicants"), "the node must be gone");
                    Check(Reflect.Get(ElementPaths.Resolve(action, "Start"), "Target") is { } next && Model.NameOf(next) == "Add", "the flow must be joined again");
                    // Rec.Code reads the attribute itself: a delete check on the attribute must name the mapping.
                    var usages = Builder.DataUsages(ElementPaths.Resolve(m, "Applicant/Code"));
                    Check(usages.Any(u => u.Contains("LastCode")), "a data usage check must see the mapping: " + string.Join(" | ", usages));
                }

            // Comment arrows: one to the node that goes, one to a node that stays.
            var arrows = Path.Combine(dir, "mapping-arrows.oml");
            using (var m = Model.Load(built))
            {
                var action = ElementPaths.Resolve(m, "Collect");
                var comment = (ICommentNode)ElementPaths.Children(action).Single(n => n is ICommentNode);
                foreach (var name in new[] { "Add", "Fill" })
                    comment.CreateConnector((IActionNode)ElementPaths.Resolve(action, name));
                Model.Save(m, arrows);
            }
            var deleted = Apply(arrows, Path.Combine(dir, "mapping-arrows-out.oml"), Edits("""{ "op": "delete", "element": "Collect/Add" }"""), out var deletedReport);
            Check(deleted != "" && deletedReport.Contains("comment arrow"), "a node with a comment arrow must delete, and say that the arrow went: " + deletedReport);
            if (deleted != "")
                using (var m = Model.Load(deleted))
                {
                    var comment = (ICommentNode)ElementPaths.Children(ElementPaths.Resolve(m, "Collect")).Single(n => n is ICommentNode);
                    var targets = comment.Connectors.Select(c => Model.NameOf(Reflect.Get(c, "TargetNode")!)).ToList();
                    Check(targets.SequenceEqual(["Fill"]), "the arrow to the other node must stay, the one to the deleted node must go; got " + string.Join(",", targets));
                }
            // The arrows of a comment are not flow: the node that stays can still be deleted, with the arrow in place.
            var other = Apply(arrows, Path.Combine(dir, "mapping-arrows-fill.oml"), Edits("""{ "op": "delete", "element": "Collect/Fill" }"""), out var otherReport);
            Check(other != "", "a comment arrow must not count as a connector: " + otherReport);
        }
        catch (Exception e) { failures.Add("field mappings: " + (e.InnerException ?? e).Message); }
    }
}
