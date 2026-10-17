using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Logic.Nodes;

namespace OmlCli;

static class SqlDeletionSelfTest
{
    public static void Run(string blank, string dir, List<string> failures)
    {
        var spec = Path.Combine(dir, "sql-delete.json");
        var output = Path.Combine(dir, "sql-delete.oml");
        File.WriteAllText(spec, """
            {
              "entities": [
                { "name": "Customer", "attributes": [{ "name": "Name", "length": 100 }, { "name": "Unused" }] },
                { "name": "Disposable", "public": true, "attributes": [{ "name": "Name" }] }
              ],
              "structures": [{ "name": "CaptionDto", "attributes": [{ "name": "Caption" }] }],
              "serverActions": [
                { "name": "SearchCustomers", "inputs": [{ "name": "Search" }], "outputs": [{ "name": "LastName" }], "flow": [
                  { "kind": "sql", "name": "GetTopCustomers", "sql": "SELECT {Customer}.* FROM {Customer} WHERE {Customer}.[Name] LIKE @Pattern",
                    "params": [{ "name": "Pattern", "value": "\"%\" + Search + \"%\"" }], "outputs": ["Customer"], "maxRecords": 50 },
                  { "kind": "forEach", "name": "Loop", "recordList": "GetTopCustomers.List", "cycle": "CopyName", "next": "End" },
                  { "kind": "assign", "name": "CopyName", "assign": { "LastName": "GetTopCustomers.List.Current.Customer.Name" }, "next": "Loop" }
                ] },
                { "name": "Summarize", "outputs": [{ "name": "Caption" }], "flow": [
                  { "kind": "sql", "name": "GetCaption", "sql": "SELECT 'Caption'", "outputs": ["CaptionDto"] },
                  { "kind": "assign", "assign": { "Caption": "GetCaption.List.Current.CaptionDto.Caption" } }
                ] }
              ],
              "edits": [
                { "op": "insertNodes", "action": "Summarize", "after": "Start", "flow": [
                  { "kind": "sql", "name": "InsertedSql", "sql": "SELECT @Caption", "params": [{ "name": "Caption", "value": "\"Inline\"", "expandInline": true }], "outputs": ["CaptionDto"] },
                  { "kind": "sql", "name": "MixedSql", "sql": "SELECT {Customer}.*, @Caption FROM {Customer} WHERE {Customer}.[Id] > @Minimum",
                    "params": [{ "name": "Minimum", "type": "Integer", "value": "0" }, { "name": "Caption", "value": "\"Caption\"" }], "outputs": ["Customer", "CaptionDto"] }
                ] },
                { "op": "delete", "element": "Disposable", "allowDataLoss": true }
              ]
            }
            """);
        var (code, report) = Apply.Execute(blank, spec, output);
        if (code != 0) { failures.Add("SQL/delete: " + JsonSerializer.Serialize(report)); return; }
        void Check(bool ok, string detail) { if (!ok) failures.Add("SQL/delete: " + detail); }
        using (var built = Model.Load(output))
        {
            var sql = (ISQLNode)ElementPaths.Resolve(built, "SearchCustomers/GetTopCustomers");
            Check(sql.Statement.Contains("@Pattern") && sql.Outputs.Single().EntityOrStructure == built.Entities.Single(e => e.Name == "Customer"), "SQL statement/entity output lost");
            Check(sql.Arguments.Single().Value.Text == "\"%\" + Search + \"%\"" && sql.MaxRecords.Text == "50", "SQL argument/maxRecords lost");
            var inserted = (ISQLNode)ElementPaths.Resolve(built, "Summarize/InsertedSql");
            Check(inserted.InputParameters.Single().ExpandInline, "inline parameter lost");
            var mixed = (ISQLNode)ElementPaths.Resolve(built, "Summarize/MixedSql");
            Check(mixed.Outputs.Select(o => Model.NameOf(o.EntityOrStructure)).SequenceEqual(new[] { "Customer", "CaptionDto" }), "mixed SQL output order lost");
            Check(Model.Describe(built, mixed.InputParameters.Single(p => p.Name == "Minimum").DataType) == "Integer"
                && mixed.Arguments.Single(a => a.Parameter.Name == "Minimum").Value.Text == "0", "typed SQL parameter lost");
            Check(!built.Entities.Any(e => e.Name == "Disposable"), "entity deletion lost");
        }
        var json = JsonSerializer.SerializeToElement(report);
        Check(json.GetProperty("warnings").EnumerateArray().Any(w => w.GetString()!.Contains("public entity")), "public deletion warning missing");

        // An unused attribute on a separate entity can be removed without touching any consumer.
        File.WriteAllText(spec, """{"entities":[{"name":"UnusedEntity","attributes":[{"name":"Name"},{"name":"Unused"}]}],"edits":[{"op":"delete","element":"UnusedEntity/Unused","allowDataLoss":true}]}""");
        var attrOutput = Path.Combine(dir, "sql-delete-attr.oml");
        var (attrCode, attrReport) = Apply.Execute(blank, spec, attrOutput);
        Check(attrCode == 0, "unused attribute: " + JsonSerializer.Serialize(attrReport));
        if (attrCode == 0)
        {
            using var built = Model.Load(attrOutput);
            Check(!built.Entities.Single().Attributes.Any(a => a.Name == "Unused"), "attribute deletion lost");
        }
        var i = 0;
        foreach (var (edit, message) in new[] {
            ("""{"op":"delete","element":"Customer"}""", "refused:"),
            ("""{"op":"delete","element":"Customer/Name"}""", "refused:"),
            ("""{"op":"delete","element":"Customer","allowDataLoss":true}""", "refused:"),
            ("""{"op":"delete","element":"Customer/Name","allowDataLoss":true}""", "refused:"),
            ("""{"op":"delete","element":"Customer/Id","allowDataLoss":true}""", "refused:")
        })
        {
            File.WriteAllText(spec, "{\"edits\":[" + edit + "]}");
            var rejected = Path.Combine(dir, $"sql-delete-refused-{i++}.oml");
            var (badCode, badReport) = Apply.Execute(output, spec, rejected);
            Check(badCode == 1 && !File.Exists(rejected) && JsonSerializer.Serialize(badReport).Contains(message), "deletion guard failed: " + edit);
        }

        var staticSource = Path.Combine(dir, "static-delete-source.oml");
        using (var built = Model.Load(blank))
        {
            var entity = Reflect.Call(built, "CreateStaticEntity", "Status", Model.Services().NewKey())!;
            var id = Reflect.Call(entity, "CreateAttribute", "Id", Model.Services().NewKey())!;
            Reflect.Property(id, "DataType", writable: true)!.SetValue(id, built.IntegerType);
            Reflect.Property(id, "IsMandatory", writable: true)!.SetValue(id, true);
            Reflect.Property(entity, "IdentifierAttribute", writable: true)!.SetValue(entity, id);
            Model.Save(built, staticSource);
        }
        File.WriteAllText(spec, """{"edits":[{"op":"delete","element":"Status","allowDataLoss":true}]}""");
        var (staticCode, staticReport) = Apply.Execute(staticSource, spec, Path.Combine(dir, "static-deleted.oml"));
        Check(staticCode == 1 && !File.Exists(Path.Combine(dir, "static-deleted.oml"))
            && JsonSerializer.Serialize(staticReport).Contains("refused:"), "static deletion refusal failed: " + JsonSerializer.Serialize(staticReport));
    }
}
