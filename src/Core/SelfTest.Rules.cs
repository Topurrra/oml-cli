using System.Text.Json;
using OutSystems.Model;

namespace OmlCli;

static partial class SelfTest
{
    static void RulesTest(string blank, string dir, List<string> failures)
    {
        const string seed = """
        {"entities":[{"name":"RuleEntity","attributes":[{"name":"Value","length":50}]}],"staticEntities":[{"name":"RuleStatus","attributes":[{"name":"Color"}],"records":[{"identifier":"Active"}]}],"structures":[{"name":"RuleDto","attributes":[{"name":"Caption"}]}],"serverActions":[{"name":"RuleAction","inputs":[{"name":"Input"}],"outputs":[{"name":"Output"}],"locals":[{"name":"Scratch"}],"flow":[{"kind":"assign","name":"Copy","assign":{"Scratch":"Input"}},{"kind":"sql","name":"Read","sql":"SELECT 'ok'","outputs":["RuleDto"]}]},{"name":"RuleTimerAction"}],"clientActions":[{"name":"RuleClient","inputs":[{"name":"Input"}],"outputs":[{"name":"Output"}]}],"screens":[{"name":"RuleScreen"}],"blocks":[{"name":"RuleBlock"}],"exceptions":["RuleError"],"roles":[{"name":"RuleRole"}],"siteProperties":[{"name":"RuleProperty"}],"timers":[{"name":"RuleTimer","action":"RuleTimerAction"}],"restClients":[{"name":"RuleApi","baseUrl":"https://example.com","actions":[{"name":"GetValue","url":"/value"}]}],"restServices":[{"name":"RuleService","actions":[{"name":"GetValue"}]}],"processes":[{"name":"RuleProcess","flow":[{"kind":"start","name":"Start","next":"End"},{"kind":"end","name":"End"}]}]}
        """;
        var spec = Path.Combine(dir, "rules.json");
        var source = Path.Combine(dir, "rules-source.oml");
        File.WriteAllText(spec, seed);
        var (code, report) = Apply.Execute(blank, spec, source);
        if (code != 0) { failures.Add("rules seed: " + JsonSerializer.Serialize(report)); return; }
        var index = 0;
        void Check(string json, bool allowed, string label)
        {
            File.WriteAllText(spec, json);
            var output = Path.Combine(dir, $"rules-{index++}.oml");
            var (result, details) = Apply.Execute(source, spec, output);
            if (allowed ? result != 0 || !File.Exists(output)
                : result != 1 || File.Exists(output) || !JsonSerializer.Serialize(details).Contains("refused:"))
                failures.Add($"rules {label}: " + JsonSerializer.Serialize(details));
        }
        // Every protected family, including both REST method implementations.
        foreach (var target in new[] { "RuleEntity", "RuleEntity/Value", "RuleStatus", "RuleStatus/Color",
            "RuleDto", "RuleDto/Caption", "RuleAction", "RuleAction/Input", "RuleAction/Output",
            "RuleClient", "RuleClient/Input", "RuleClient/Output", "RuleScreen", "RuleBlock", "RuleError",
            "RuleRole", "RuleProperty", "RuleTimer", "RuleApi", "RuleApi/GetValue", "RuleService",
            "RuleService/GetValue", "RuleProcess" })
        {
            foreach (var op in new[] { "rename", "delete" })
                Check(JsonSerializer.Serialize(new { edits = new[] { new { op, element = target, to = "Renamed", allowDataLoss = true } } }), false, op + " " + target);
            Check(JsonSerializer.Serialize(new { edits = new[] { new { op = "set", element = target, properties = new { Name = "Renamed" } } } }), false, "set Name " + target);
        }
        using (var module = Model.Load(source))
        {
            var record = ProductionRules.Walk(module).OfType<OutSystems.Model.Data.IRecord>().Single();
            var path = "RuleStatus/" + ElementPaths.Segment(record, record.Parent!);
            Check(JsonSerializer.Serialize(new { edits = new[] { new { op = "delete", element = path } } }), false, "static record delete");
            Check(JsonSerializer.Serialize(new { edits = new[] { new { op = "set", element = path, properties = new { Identifier = "Renamed" } } } }), false, "static record rename");
        }
        foreach (var target in new[] { "RuleEntity/Value", "RuleStatus/Color", "RuleDto/Caption" })
        foreach (var property in new[] { "DataType", "Length", "Decimals", "IsMandatory" })
            Check(JsonSerializer.Serialize(new { edits = new[] { new { op = "set", element = target,
                properties = new Dictionary<string, object> { [property] = property == "DataType" ? "Integer" : property == "IsMandatory" ? true : 12 } } } }), false, "attribute " + property);
        foreach (var keyword in new[] { "DELETE", "update", "Truncate", "DROP", "ALTER", "MERGE" })
        {
            var statement = "SELECT 'ok'; " + keyword + " RuleEntity";
            Check(JsonSerializer.Serialize(new { serverActions = new[] { new { name = "BadSql", flow = new[] { new { kind = "sql", name = "Unsafe", sql = statement, outputs = new[] { "RuleDto" } } } } } }), false, "new SQL " + keyword);
            Check(JsonSerializer.Serialize(new { edits = new[] { new { op = "set", element = "RuleAction/Read", properties = new { Statement = statement } } } }), false, "existing SQL " + keyword);
        }
        Check("""{"entities":[{"name":"Disposable","attributes":[{"name":"Value"}]}],"edits":[{"op":"delete","element":"Disposable","allowDataLoss":true}]}""", true, "same-spec delete");
        Check("""{"edits":[{"op":"rename","element":"RuleAction/Copy","to":"CopyAgain"},{"op":"delete","element":"RuleAction/CopyAgain"},{"op":"rename","element":"RuleAction/Scratch","to":"Temporary"},{"op":"delete","element":"RuleAction/Temporary"}]}""", true, "flow/local edits");
        Check("""{"entities":[{"name":"RuleEntity","attributes":[{"name":"Added"}]}],"edits":[{"op":"set","element":"RuleEntity/Added","properties":{"Length":120}},{"op":"rename","element":"RuleEntity/Added","to":"NewName"},{"op":"delete","element":"RuleEntity/NewName","allowDataLoss":true}]}""", true, "new attribute");
        Check("""{"edits":[{"op":"set","element":"RuleAction/Read","properties":{"statement":"SELECT 'DELETE '' UPDATE', 'MERGE' /* DROP /* ALTER */ TRUNCATE */ -- DELETE\n"}}]}""", true, "SELECT with ignored tokens");
        var originalSource = source;
        var serviceBlank = Path.Combine(dir, "rules-service-blank.oml");
        using (var module = Model.Services().CreateESpace(OutSystems.Model.Enumerations.SegmentationKind.Service,
            "RulesService", "", false, Model.Services().NewKey())) Model.Save(module, serviceBlank);
        source = Path.Combine(dir, "rules-service.oml");
        File.WriteAllText(spec, """{"serviceActions":[{"name":"RuleServiceAction","inputs":[{"name":"Input"}],"outputs":[{"name":"Output"}]}]}""");
        var (serviceCode, serviceReport) = Apply.Execute(serviceBlank, spec, source);
        if (serviceCode != 0) failures.Add("rules service seed: " + JsonSerializer.Serialize(serviceReport));
        else foreach (var target in new[] { "RuleServiceAction", "RuleServiceAction/Input", "RuleServiceAction/Output" })
            foreach (var op in new[] { "rename", "delete" })
                Check(JsonSerializer.Serialize(new { edits = new[] { new { op, element = target, to = "Renamed" } } }), false, op + " " + target);
        source = originalSource;
        Check("""{"edits":[{"op":"addVariables","action":"RuleAction","inputs":[{"name":"AddedInput"}],"outputs":[{"name":"AddedOutput"}]}]}""", true, "new parameters");
        // Keywords inside identifiers and parameters are not executable SQL keywords.
        foreach (var sql in new[] { "SELECT 'DELETE '' UPDATE'", "SELECT [UPDATE], \"DROP\" FROM {MERGE}",
            "SELECT @DELETE -- UPDATE\r /* DROP /* ALTER */ MERGE */", "SELECT UpdatedValue, DeletedAt" })
            try { ProductionRules.Sql(new object(), sql); }
            catch (ModelError e) { failures.Add("rules SQL lexer: " + e.Message); }
        // INSERT is allowed by the keyword guard (SQL itself is validated by the Model API).
        try { ProductionRules.Sql(new object(), "INSERT INTO {RuleEntity} ([Value]) VALUES ('UPDATE')"); }
        catch (ModelError e) { failures.Add("rules INSERT: " + e.Message); }

        // SQL already in the module is the user's production code: an unrelated change must still apply.
        var legacySource = Path.Combine(dir, "rules-legacy-sql.oml");
        using (var module = Model.Load(source))
        {
            var read = (OutSystems.Model.Logic.Nodes.ISQLNode)ElementPaths.Resolve(module, "RuleAction/Read");
            Reflect.Property(read, "Statement", writable: true)!.SetValue(read, "DELETE FROM {RuleEntity}");
            Model.Save(module, legacySource);
        }
        File.WriteAllText(spec, """{ "entities": [{ "name": "RuleExtra", "attributes": [{ "name": "Note" }] }] }""");
        var legacyOut = Path.Combine(dir, "rules-legacy-out.oml");
        var (legacyCode, legacyReport) = Apply.Execute(legacySource, spec, legacyOut);
        if (legacyCode != 0) failures.Add("rules: existing data-changing SQL blocked an unrelated change: " + JsonSerializer.Serialize(legacyReport));
    }
}
