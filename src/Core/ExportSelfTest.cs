using System.Text.Json;
using System.Text.Json.Nodes;
using OutSystems.Model;
using OutSystems.Model.Enumerations;

namespace OmlCli;

// `oml export` on the installed model: a Traditional screen built here, and the Reactive Orders screen of the main self-test.
static class ExportSelfTest
{
    static string? S(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static IEnumerable<JsonObject> Objects(JsonNode? node) => node is JsonArray a ? a.OfType<JsonObject>() : Enumerable.Empty<JsonObject>();

    // Every widget of the tree, with the widgets in its children, branches, rows and placeholders.
    static IEnumerable<JsonObject> AllWidgets(JsonNode? list)
    {
        foreach (var w in Objects(list))
        {
            yield return w;
            foreach (var key in new[] { "children", "true", "false", "header", "row" })
                foreach (var inner in AllWidgets(w[key])) yield return inner;
            if (w["placeholders"] is JsonObject slots)
                foreach (var (_, content) in slots)
                    foreach (var inner in AllWidgets(content)) yield return inner;
        }
    }

    // True when the widget has this expression in one of its properties (the property's name differs between builds and kinds).
    static bool Shows(JsonObject? widget, string expression) =>
        widget?["props"] is JsonObject props && props.Any(p => S(p.Value) == expression);

    static JsonObject? Named(IEnumerable<JsonObject> items, string name) => items.FirstOrDefault(x => S(x["name"]) == name);

    // Reactive: the Orders screen of the main self-test module (aggregate, lifecycle handler, widgets, block instance).
    // A failure here is one self-test failure: the other self-test steps still run.
    static void Guard(string what, List<string> failures, Action check)
    {
        try { check(); }
        catch (Exception e) when (e is not CliError and not OutOfMemoryException) { failures.Add($"export ({what}): {e.GetType().Name}: {(e.InnerException ?? e).Message}"); }
    }

    public static void Check(IESpace module, List<string> failures) => Guard("Reactive", failures, () => CheckReactive(module, failures));
    public static void Run(string dir, List<string> failures) => Guard("Traditional", failures, () => CheckTraditional(dir, failures));

    static void CheckReactive(IESpace module, List<string> failures)
    {
        var e = Export.One(module, "MainFlow/Orders");
        void Require(bool ok, string what) { if (!ok) failures.Add("export (Reactive): " + what + ": " + e.ToJsonString()[..Math.Min(600, e.ToJsonString().Length)]); }
        Require(S(e["kind"]) == "Screen" && S(e["moduleKind"]) == "Reactive" && S(e["path"]) == "MainFlow/Orders" && S(e["title"]) == "\"Orders\"", "element facts");
        Require(e["preparation"] is null, "a Reactive screen has no preparation");
        Require(Named(Objects(e["locals"]), "Total") is { } total && S(total["type"]) == "Decimal", "local Total");
        var aggregate = Named(Objects(e["aggregates"]), "GetOrders");
        Require(aggregate is not null && aggregate["sources"] is JsonArray sources && sources.Select(S).Contains("Order") && S(aggregate["maxRecords"]) == "10", "screen aggregate GetOrders");
        Require(S(e["onInitialize"]) == "Init", "onInitialize");
        var init = Named(Objects(e["actions"]), "Init");
        var reset = Objects(init?["flow"]?["nodes"]).FirstOrDefault();
        Require(reset is not null && S(reset["kind"]) == "assign" && S(reset["name"]) == "Reset" && S(reset["assign"]?["Total"]) == "0" && S(reset["next"]) == "End"
            && init!["flow"]!["complete"]?.GetValue<bool>() == true, "action Init as a spec flow");
        var widgets = AllWidgets(e["widgets"]).ToList();
        Require(widgets.Any(w => S(w["sourceBlock"]) == "Card" && S(w["args"]?["Title"]) == "\"Hi\""), "block instance Card with its argument");
        Require(widgets.Any(w => S(w["props"]?["onClick"]) == "Detail" && S(w["props"]?["destination"]) == "Detail" && S(w["args"]?["CustomerId"]) == "NullIdentifier()"), "link to Detail with its argument");
        Require(Named(widgets, "CaptionText") is { } caption && Shows(caption, "\"Edited\"") && (S(caption["path"]) ?? "").EndsWith("/CaptionText", StringComparison.Ordinal), "expression CaptionText");
        Require(widgets.Any(w => Objects(w["false"]).Any() && AllWidgets(w["false"]).Any(t => Shows(t, "GetOrders.List"))), "If with the table in its False branch");
    }

    // Traditional: a screen with an input, a local, a Preparation (aggregate and assign), a screen action (if, execute,
    // destination) and container, expression, input, button, table records and block instance widgets.
    static void CheckTraditional(string dir, List<string> failures)
    {
        var source = Path.Combine(dir, "export-web-source.oml");
        var spec = Path.Combine(dir, "export-web.json");
        var output = Path.Combine(dir, "export-web.oml");
        var services = Model.Services();
        using (var module = services.CreateESpace(SegmentationKind.WebResponsive, "ExportSelfTest", "", false, services.NewKey()))
            Model.Save(module, source);
        File.WriteAllText(spec, """
            {
              "entities": [{ "name": "Item", "attributes": [{ "name": "Name" }] }],
              "blocks": [{ "name": "Card", "inputs": [{ "name": "Caption" }], "widgets": [{ "type": "Expression", "value": "Caption" }] }],
              "screens": [
                { "name": "Items", "inputs": [{ "name": "Filter", "mandatory": false }], "locals": [{ "name": "ItemName" }, { "name": "Rec", "type": "Item" }],
                  "preparation": [
                    { "kind": "aggregate", "name": "FetchItems", "sources": ["Item"], "filters": ["Filter = \"\" or Item.Name = Filter"],
                      "sorts": [{ "attribute": "Item.Name" }], "maxRecords": 10 },
                    { "kind": "assign", "name": "Prefill", "assign": { "ItemName": "Filter" } } ],
                  "actions": [{ "name": "Save", "flow": [
                    { "kind": "if", "name": "HasName", "condition": "ItemName <> \"\"", "true": "SetName", "false": "End" },
                    { "kind": "assign", "name": "SetName", "assign": { "Rec.Name": "ItemName" } },
                    { "kind": "execute", "action": "CreateOrUpdateItem", "args": { "Source": "Rec" } },
                    { "kind": "destination", "screen": "Detail", "args": { "ItemId": "Rec.Id" } } ] }],
                  "widgets": [
                    { "type": "Container", "name": "Editor", "style": "\"editor\"", "children": [
                      { "type": "Expression", "name": "Hint", "value": "\"Name: \" + ItemName" },
                      { "type": "Input", "name": "NameInput", "variable": "ItemName" },
                      { "type": "Button", "name": "SaveButton", "onClick": "Save", "children": [{ "type": "Text", "text": "Save" }] } ] },
                    { "type": "BlockInstance", "name": "Summary", "sourceBlock": "Card", "args": { "Caption": "\"Summary\"" } },
                    { "type": "TableRecords", "name": "ItemTable", "source": "FetchItems.List",
                      "headerRow": [{ "type": "HeaderCell", "children": [{ "type": "Text", "text": "Name" }] }],
                      "row": [{ "type": "RowCell", "children": [{ "type": "Expression", "name": "RowName", "value": "FetchItems.List.Current.Item.Name" }] }] } ] },
                { "name": "Detail", "inputs": [{ "name": "ItemId", "type": "Item Identifier" }] }
              ]
            }
            """);
        var (code, report) = Apply.Execute(source, spec, output);
        if (code != 0) { failures.Add("export (Traditional) apply: " + JsonSerializer.Serialize(report)); return; }
        using var built = Model.Load(output);
        var e = Export.One(built, "Items");
        void Require(bool ok, string what) { if (!ok) failures.Add("export (Traditional): " + what + ": " + e.ToJsonString()[..Math.Min(600, e.ToJsonString().Length)]); }
        Require(S(e["kind"]) == "Screen" && S(e["moduleKind"]) == "Traditional" && S(e["flow"]) == "MainFlow" && S(e["path"]) == "MainFlow/Items", "element facts");
        Require(Named(Objects(e["inputs"]), "Filter") is { } filter && filter["mandatory"]?.GetValue<bool>() == false, "input Filter");
        Require(Named(Objects(e["locals"]), "Rec") is { } rec && S(rec["type"]) == "Item", "local Rec");

        var preparation = e["preparation"];
        var prep = Objects(preparation?["nodes"]).ToList();
        Require(prep.Count == 2 && S(prep[0]["kind"]) == "aggregate" && S(prep[0]["name"]) == "FetchItems" && S(prep[0]["next"]) == "Prefill"
            && prep[0]["sources"] is JsonArray sources && sources.Select(S).SequenceEqual(new[] { "Item" })
            && prep[0]["filters"] is JsonArray { Count: 1 } && S(prep[0]["sorts"]?[0]?["attribute"]) == "Item.Name" && S(prep[0]["maxRecords"]) == "10", "preparation aggregate");
        Require(prep.Count == 2 && S(prep[1]["kind"]) == "assign" && S(prep[1]["assign"]?["ItemName"]) == "Filter" && S(prep[1]["next"]) == "End", "preparation assign");
        Require(preparation?["complete"]?.GetValue<bool>() == true && (S(prep.FirstOrDefault()?["_path"]) ?? "").StartsWith("MainFlow/Items/", StringComparison.Ordinal), "preparation complete, with node paths");

        var save = Named(Objects(e["actions"]), "Save");
        var nodes = Objects(save?["flow"]?["nodes"]).ToList();
        Require(nodes.Count == 4 && S(nodes[0]["kind"]) == "if" && S(nodes[0]["name"]) == "HasName" && S(nodes[0]["condition"]) == "ItemName <> \"\""
            && S(nodes[0]["true"]) == "SetName" && S(nodes[0]["false"]) == "End", "if node");
        Require(nodes.FirstOrDefault(n => S(n["kind"]) == "execute") is { } execute && S(execute["action"]) == "CreateOrUpdateItem" && S(execute["_ref"]) == "CreateOrUpdateItem"
            && S(execute["args"]?["Source"]) == "Rec", "execute node");
        Require(nodes.FirstOrDefault(n => S(n["kind"]) == "destination") is { } go && S(go["screen"]) == "Detail" && S(go["_ref"]) == "Detail"
            && S(go["args"]?["ItemId"]) == "Rec.Id" && go["next"] is null, "destination node");
        Require(save?["flow"]?["complete"]?.GetValue<bool>() == true, "screen action flow complete");
        // The nodes go into a spec as they are: every connector names a listed node or End.
        var listed = nodes.Select(n => S(n["name"])).ToHashSet();
        Require(nodes.SelectMany(n => new[] { "next", "true", "false" }.Select(k => S(n[k]))).OfType<string>().All(t => t == "End" || listed.Contains(t)), "connectors name listed nodes");

        var widgets = AllWidgets(e["widgets"]).ToList();
        var editor = Named(widgets, "Editor");
        Require(editor is not null && Objects(editor["children"]).Select(w => S(w["name"])).SequenceEqual(new[] { "Hint", "NameInput", "SaveButton" }), "container children");
        Require(Shows(Named(widgets, "Hint"), "\"Name: \" + ItemName") && Shows(Named(widgets, "NameInput"), "ItemName"), "expression and input");
        Require(S(Named(widgets, "SaveButton")?["props"]?["onClick"]) == "Save", "button onClick");
        Require(Named(widgets, "Summary") is { } summary && S(summary["sourceBlock"]) == "Card" && S(summary["args"]?["Caption"]) == "\"Summary\"", "block instance");
        var table = Named(widgets, "ItemTable");
        Require(table is not null && Shows(table, "FetchItems.List") && Objects(table["header"]).Any() && AllWidgets(table["row"]).Any(w => S(w["name"]) == "RowName"), "table header and row");
        Require(Shows(Named(widgets, "RowName"), "FetchItems.List.Current.Item.Name"), "row expression");
    }
}
