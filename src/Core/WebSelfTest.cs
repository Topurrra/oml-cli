using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Enumerations;

namespace OmlCli;

static partial class SelfTest
{
    static void WebTest(string dir, List<string> failures)
    {
        var source = Path.Combine(dir, "web-source.oml");
        var spec = Path.Combine(dir, "web.json");
        var output = Path.Combine(dir, "web.oml");
        var services = Model.Services();
        using (var module = services.CreateESpace(SegmentationKind.WebResponsive, "WebSelfTest", "", false, services.NewKey()))
            Model.Save(module, source);
        File.WriteAllText(spec, """
            {
              "entities": [{ "name": "Item", "attributes": [{ "name": "Name" }] }],
              "blocks": [{ "name": "Card", "inputs": [{ "name": "Caption" }],
                "widgets": [{ "type": "Expression", "value": "Caption" }] }],
              "screens": [
                { "name": "Items", "locals": [{ "name": "ItemName" }, { "name": "Rec", "type": "Item" }],
                  "preparation": [{ "kind": "aggregate", "name": "FetchItems", "sources": ["Item"], "maxRecords": 10 }],
                  "actions": [{ "name": "Save", "flow": [
                    { "kind": "assign", "name": "SetName", "assign": { "Rec.Name": "ItemName" } },
                    { "kind": "execute", "action": "CreateOrUpdateItem", "args": { "Source": "Rec" } } ] }],
                  "widgets": [
                    { "type": "Input", "name": "NameInput", "variable": "ItemName" },
                    { "type": "Button", "name": "SaveButton", "onClick": "Save", "children": [{ "type": "Text", "text": "Save" }] },
                    { "type": "BlockInstance", "name": "Summary", "sourceBlock": "Card", "args": { "Caption": "\"Summary\"" } },
                    { "type": "TableRecords", "name": "ItemTable", "source": "FetchItems.List",
                      "headerRow": [{ "type": "HeaderCell", "children": [{ "type": "Text", "text": "Name" }] }],
                      "row": [{ "type": "RowCell", "children": [
                        { "type": "Link", "name": "OpenDetail", "onClick": "Detail", "args": { "ItemId": "FetchItems.List.Current.Item.Id" },
                          "children": [{ "type": "Expression", "value": "FetchItems.List.Current.Item.Name" }] } ] }] } ] },
                { "name": "Detail", "inputs": [{ "name": "ItemId", "type": "Item Identifier" }] }
              ]
            }
            """);
        var (code, report) = Apply.Execute(source, spec, output);
        if (code != 0) { failures.Add("web apply: " + JsonSerializer.Serialize(report)); return; }
        using (var built = Model.Load(output))
        {
            var screen = built.WebFlows.SelectMany(f => f.Nodes).OfType<OutSystems.Model.UI.Web.IWebScreen>().Single(s => s.Name == "Items");
            var all = Descendants(screen).ToList();
            if (built.MobileFlows.Any()) failures.Add("web UI created a Mobile flow");
            if (screen.Preparation is null || !Descendants(screen.Preparation).Any(o => Model.NameOf(o) == "FetchItems"))
                failures.Add("web preparation aggregate missing");
            foreach (var reference in new[] { "CreateOrUpdateItem", "Save", "Detail", "Card" })
                if (!all.Any(o => new[] { "Action", "Destination", "SourceBlock" }.Any(p =>
                    Reflect.Get(o, p) is { } target && Model.NameOf(target) == reference)))
                    failures.Add("web reference missing: " + reference);
            foreach (var expression in new[] { "FetchItems.List", "FetchItems.List.Current.Item.Id", "FetchItems.List.Current.Item.Name", "ItemName", "Rec", "\"Save\"", "\"Summary\"" })
                if (!all.OfType<IExpression>().Any(e => e.Text == expression)) failures.Add("web binding missing: " + expression);
            if (ElementPaths.Resolve(built, "Items/ItemTable") is not OutSystems.Model.UI.Web.Widgets.ITableRecordsWidget)
                failures.Add("web table path missing");
        }
        foreach (var invalid in new[] {
            """{ "screens": [{ "name": "Bad", "template": "list", "entity": "Item" }] }""",
            """{ "screens": [{ "name": "Bad", "aggregates": [{ "name": "Fetch", "sources": ["Item"] }] }] }""",
            """{ "screens": [{ "name": "Bad", "widgets": [{ "type": "NoSuchWidget" }] }] }"""
        })
        {
            File.WriteAllText(spec, invalid);
            var rejected = Path.Combine(dir, "web-rejected.oml");
            var (invalidCode, _) = Apply.Execute(output, spec, rejected);
            if (invalidCode == 0 || File.Exists(rejected)) failures.Add("invalid web spec was saved: " + invalid);
        }
    }
}
