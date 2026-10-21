using System.Text.Json;
using System.Text.Json.Nodes;
using OutSystems.Model;

namespace OmlCli;

// Self-test step for `oml review`: the GlobalKeys it matches by survive a save and reload on this build, so a renamed
// entity and a moved step are found as such; and every production-rule risk that `oml apply` refuses (so the
// regression suite cannot make it with a spec) is reported, from changes made here through the model API.
static class ReviewSelfTest
{
    const string Spec = """
        {
          "entities": [
            { "name": "Customer", "public": true, "description": "c", "attributes": [{ "name": "Name", "length": 50 }] },
            { "name": "Order", "attributes": [{ "name": "Total", "type": "Decimal" }, { "name": "CustomerId", "type": "Customer Identifier", "deleteRule": "Protect" }] },
            { "name": "Legacy", "attributes": [{ "name": "Code" }] } ],
          "staticEntities": [{ "name": "Status", "records": [{ "identifier": "Open", "label": "Open", "order": 1 }, { "identifier": "Closed", "label": "Closed", "order": 2 }] }],
          "blocks": [{ "name": "Card", "public": true, "events": [{ "name": "OnPick", "mandatory": false }] }],
          "serverActions": [
            { "name": "FindCustomer", "public": true, "description": "d", "inputs": [{ "name": "Id", "type": "Customer Identifier", "mandatory": true }], "flow": [] },
            { "name": "Shared", "public": true, "description": "d", "flow": [] },
            { "name": "Work", "inputs": [{ "name": "Amount", "type": "Decimal" }], "locals": [{ "name": "Count", "type": "Integer" }], "flow": [
              { "kind": "assign", "name": "First", "assign": { "Count": "1" } },
              { "kind": "assign", "name": "Second", "assign": { "Count": "2" } },
              { "kind": "if", "name": "Big", "condition": "Amount > 100", "true": "Third", "false": "End" },
              { "kind": "assign", "name": "Third", "assign": { "Count": "3" }, "next": "End" } ] },
            { "name": "Query", "outputs": [{ "name": "Rows", "type": "List of Order" }], "flow": [
              { "kind": "sql", "name": "Read", "sql": "SELECT {Order}.* FROM {Order}", "outputs": ["Order"], "maxRecords": 10 } ] } ],
          "roles": [{ "name": "Manager" }, { "name": "Clerk" }],
          "screens": [{ "name": "Home", "access": { "roles": ["Manager"] } }, { "name": "Staff", "access": { "roles": ["Manager"] } },
            { "name": "Desk", "access": { "roles": ["Manager"] } }]
        }
        """;

    public static void Run(string blank, string dir, List<string> failures)
    {
        void Check(bool condition, string message) { if (!condition) failures.Add("review: " + message); }
        try
        {
            var spec = Path.Combine(dir, "review-spec.json");
            var before = Path.Combine(dir, "review-before.oml");
            var after = Path.Combine(dir, "review-after.oml");
            File.WriteAllText(spec, Spec);
            var (code, report) = Apply.Execute(blank, spec, before);
            if (code != 0) { failures.Add("review: apply: " + JsonSerializer.Serialize(report)); return; }
            var services = Model.Services();
            using (var m = Model.Load(before))
            {
                void Set(object o, string property, object? value) => Reflect.Property(o, property, writable: true)!.SetValue(o, value);
                var entities = m.Entities.ToList();
                Set(entities.Single(e => e.Name == "Order"), "Name", "Purchase");                                     // rule 3: rename
                Set(entities.Single(e => e.Name == "Customer").Attributes.Single(a => a.Name == "Name"), "Length", 100); // rule 5
                ((IObjectSignature)entities.Single(e => e.Name == "Legacy")).Delete();                                 // rule 2
                Set(m.ServerActions.Single(a => a.Name == "Shared"), "Public", false);                                  // rule 4: private
                var input = Reflect.Call(m.ServerActions.Single(a => a.Name == "FindCustomer"), "CreateInputParameter", "Region", services.NewKey())!;
                Set(input, "DataType", m.TextType); Set(input, "IsMandatory", true);                                   // rule 4: signature
                Set(ElementPaths.Resolve(m, "Query/Read"), "Statement", "DELETE FROM {Purchase}");                     // rule 6
                var home = ElementPaths.Resolve(m, "Home");
                Reflect.Call(Reflect.Get(home, "Roles")!, "Add", Reflect.Get(m, "AnonymousRole"));                    // rule 7
                // Work: Start -> First -> Second -> Big becomes Start -> Second -> First -> Big, and Big's condition changes.
                var work = m.ServerActions.Single(a => a.Name == "Work");
                IFlowNodeSignature Node(string name) => (IFlowNodeSignature)work.Nodes.Single(n => Model.NameOf(n) == name);
                var (start, first, second, big) = (Node("Start"), Node("First"), Node("Second"), Node("Big"));
                Set(start, "Target", second); Set(second, "Target", first); Set(first, "Target", big);
                Reflect.Plan(m, big, "Condition", JsonDocument.Parse("\"Amount > 200\"").RootElement).apply();
                ((IObjectSignature)ElementPaths.Resolve(m, "Work/Amount")).Delete();                                    // rule 3: a parameter
                // A static record's value: it shares its key with the entity's attribute.
                var record = ((System.Collections.IEnumerable)Reflect.Get(ElementPaths.Resolve(m, "Status"), "Records")!).Cast<object>().Single(r => Reflect.Get(r, "Identifier")?.ToString() == "Open");
                var label = ((System.Collections.IEnumerable)Reflect.Get(record, "AttributeValues")!).Cast<object>().Single(v => Model.NameOf(Reflect.Get(v, "Attribute")!) == "Label");
                Reflect.Plan(m, label, "Value", JsonDocument.Parse("\"\\\"Opened\\\"\"").RootElement).apply();
                // Rule 4: a public block gets a new event, and its optional event becomes mandatory.
                var card = ElementPaths.Resolve(m, "Card");
                Set(((System.Collections.IEnumerable)Reflect.Get(card, "Events")!).Cast<object>().Single(), "IsMandatory", true);
                Set(Reflect.Call(card, "CreateEvent", "OnClear", services.NewKey())!, "IsMandatory", false);
                Set(entities.Single(e => e.Name == "Customer"), "ExposeReadOnly", false);                              // rule 7: writable public entity
                Reflect.Call(Reflect.Get(ElementPaths.Resolve(m, "Staff"), "Roles")!, "Add", Reflect.Get(m, "RegisteredRole")); // rule 7: Registered
                Reflect.Call(Reflect.Get(ElementPaths.Resolve(m, "Desk"), "Roles")!, "Add", ElementPaths.Resolve(m, "Clerk"));  // info: a role added
                var reference = entities.Single(e => e.Name == "Purchase").Attributes.Single(a => a.Name == "CustomerId"); // info: cascade deletes
                var deleteRule = Reflect.Property(reference, "DeleteRule", writable: true)!;
                deleteRule.SetValue(reference, Enum.Parse(deleteRule.PropertyType, "Delete"));
                Model.Save(m, after);
            }
            var document = Review.Build(before, after, null);
            var changes = document["changes"]!.AsArray().Select(c => c!.AsObject()).ToList();
            JsonObject? Change(string name) => changes.FirstOrDefault(c => c["name"]!.GetValue<string>() == name);
            string Details(string name) => string.Join(" | ", Change(name)?["details"]!.AsArray().Select(d => d!.GetValue<string>()) ?? []);
            Check(Change("Purchase")?["change"]?.GetValue<string>() == "renamed" && Details("Purchase").Contains("renamed from Order to Purchase") && Change("Order") is null,
                "a renamed entity must be one 'renamed' change, not a delete and an add: " + JsonSerializer.Serialize(changes));
            Check(Change("Legacy")?["change"]?.GetValue<string>() == "removed", "a deleted entity is 'removed'");
            Check(Details("Customer").Contains("attribute Name: length 50 -> 100"), "attribute length change: " + Details("Customer"));
            Check(Details("Work").Contains("step Second moved: now after Start (was after First)"), "a moved step: " + Details("Work"));
            Check(Details("Work").Contains("step Big: condition Amount > 100 -> Amount > 200"), "a changed condition: " + Details("Work"));
            Check(!System.Text.RegularExpressions.Regex.IsMatch(Details("Work"), "step \\w+ (added|removed)"), "a moved step is not an add and a remove: " + Details("Work"));
            var risks = document["risks"]!.AsArray().Select(r => (rule: r!["rule"]!.GetValue<int>(), text: r["text"]!.GetValue<string>(), info: r["info"] is not null)).ToList();
            bool Risk(int rule, string words) => risks.Any(r => r.rule == rule && r.text.Contains(words));
            Check(Risk(3, "'Order' was renamed to 'Purchase'"), "rule 3 rename");
            Check(Risk(2, "'Legacy' was deleted"), "rule 2 deleted entity");
            Check(!risks.Any(r => r.text.Contains("'Code'")), "a deleted entity's attributes are not risks of their own");
            Check(Risk(5, "length 50 -> 100"), "rule 5 attribute length");
            Check(Risk(4, "'Shared' was public and is now private"), "rule 4 public to private");
            Check(Risk(4, "new mandatory input 'Region'"), "rule 4 signature");
            Check(Risk(6, "contains DELETE"), "rule 6 data-changing SQL");
            Check(Risk(7, "'Home' allows Anonymous now"), "rule 7 Anonymous");
            Check(Risk(3, "'Work/Amount' was deleted: the modules that use its owner break"), "rule 3 deleted parameter, named Owner/Name");
            Check(Risk(4, "public block 'Card' has a new optional event 'OnClear'"), "rule 4 new event on a public block");
            Check(Risk(4, "event 'OnPick' of public block 'Card' is mandatory now"), "rule 4 event made mandatory");
            Check(Risk(7, "other modules can now write to this public entity"), "rule 7 Expose Read Only on a public entity");
            Check(!risks.Any(r => r.rule == 5 && r.text.Contains("expose read only")), "a writable public entity is rule 7, not rule 5");
            Check(Risk(7, "'Staff' allows Registered now"), "rule 7 Registered added to a screen with only custom roles");
            Check(risks.Any(r => r.rule == 7 && r.info && r.text.Contains("'Desk' can now be opened by the role Clerk")), "info: a role added to a screen");
            Check(risks.Any(r => r.rule == 1 && r.info && r.text.Contains("delete rule Protect -> Delete") && r.text.Contains("cascade deletes")), "info: delete rule changed to Delete");
            Check(risks.Count(r => !r.info) == 12 && risks.Count(r => r.info) == 2, $"expected 12 risks and 2 info, got: " + string.Join(" | ", risks.Select(r => r.text)));
            Check(document["summary"]!["risks"]!.GetValue<int>() == 12 && document["summary"]!["info"]!.GetValue<int>() == 2 && document["note"]!.GetValue<string>() == Review.NotCovered, "summary and note");
            Check(Details("Status").Contains("record Open: value Label \"Open\" -> \"Opened\""), "a static record's value: " + Details("Status"));

            // Changed lines for --md and --out: one line changed between unchanged ones.
            var diff = Review.Diff("a\nb\nc", "a\nB\nc");
            Check(diff.SequenceEqual(["  a", "- b", "+ B", "  c"]), "changed lines: " + string.Join(" | ", diff));

            // Same file twice: nothing changed, nothing new.
            var same = Review.Build(before, before, null);
            Check(same["changes"]!.AsArray().Count == 0 && same["risks"]!.AsArray().Count == 0 && same["newFindings"]!.AsArray().Count == 0,
                "a module compared with itself must have no changes: " + same.ToJsonString());

            // The page escapes markup in its data.
            document["module"] = "</script><script>window.x=1</script>&";
            var page = ReviewReport.Html(document);
            Check(!page.Contains("</script><script>window.x") && page.Contains("\\u003c/script\\u003e", StringComparison.OrdinalIgnoreCase), "the review page must escape raw markup in its data");
            // Raw markup that no JSON encoder escaped first: the page escapes it itself.
            var raw = CheckPage.Embed("{\"module\":\"</script><script>window.x=1</script>&\"}", "OmlCli.Review.template.html", "/*__REVIEW_JSON__*/");
            Check(!raw.Contains("</script><script>window.x") && raw.Contains("\\u003c/script\\u003e") && raw.Contains("\\u0026"), "the review page must escape raw markup itself");
            var markdown = ReviewReport.Markdown(document);
            Check(!markdown.Contains("</script><script>") && markdown.Contains("\\<script>") && markdown.Contains(Review.NotCovered), "the Markdown report escapes markup and carries the note");
            // No mention of people, and no link to an issue or a merge request, from names in the module.
            document["module"] = "@team #123 !45 #x";
            Check(ReviewReport.Markdown(document).Contains("# Review: \\@team \\#123 \\!45 #x"), "the Markdown report escapes @, #123 and !45");
        }
        catch (Exception e) { failures.Add("review: " + (e.InnerException ?? e).Message); }
    }
}
