using System.Text.Json;
using OutSystems.Model;

namespace OmlCli;

static partial class SelfTest
{
    // The production rules on every protected family: rule 2 is always refused, rules 3, 5 and 6 stop with needsApproval
    // and save with the user's yes, renames and public signature changes save (the latter listed as breaking).
    static void RulesTest(string blank, string dir, List<string> failures)
    {
        // A project's autoApprove settings must not change what this test expects.
        var saved = new[] { "OML_AUTO_APPROVE_DELETES", "OML_AUTO_APPROVE_DATA" }.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        foreach (var v in saved.Keys) Environment.SetEnvironmentVariable(v, null);
        try { RulesCases(blank, dir, failures); }
        finally { foreach (var (v, value) in saved) Environment.SetEnvironmentVariable(v, value); }
    }

    static void RulesCases(string blank, string dir, List<string> failures)
    {
        const string seed = """
        {"entities":[{"name":"RuleEntity","attributes":[{"name":"Value","length":50}]}],"staticEntities":[{"name":"RuleStatus","attributes":[{"name":"Color"}],"records":[{"identifier":"Active"}]}],"structures":[{"name":"RuleDto","attributes":[{"name":"Caption"}]}],"serverActions":[{"name":"RuleAction","inputs":[{"name":"Input"}],"outputs":[{"name":"Output"}],"locals":[{"name":"Scratch"}],"flow":[{"kind":"assign","name":"Copy","assign":{"Scratch":"Input"}},{"kind":"sql","name":"Read","sql":"SELECT 'ok'","outputs":["RuleDto"]}]},{"name":"RuleTimerAction"},{"name":"RulePublic","public":true,"inputs":[{"name":"Id","type":"Integer"}]}],"clientActions":[{"name":"RuleClient","inputs":[{"name":"Input"}],"outputs":[{"name":"Output"}]}],"screens":[{"name":"RuleScreen"}],"blocks":[{"name":"RuleBlock"}],"exceptions":["RuleError"],"roles":[{"name":"RuleRole"}],"siteProperties":[{"name":"RuleProperty"}],"timers":[{"name":"RuleTimer","action":"RuleTimerAction"}],"restClients":[{"name":"RuleApi","baseUrl":"https://example.com","actions":[{"name":"GetValue","url":"/value"}]}],"restServices":[{"name":"RuleService","actions":[{"name":"GetValue"}]}],"processes":[{"name":"RuleProcess","flow":[{"kind":"start","name":"Start","next":"End"},{"kind":"end","name":"End"}]}]}
        """;
        var spec = Path.Combine(dir, "rules.json");
        var source = Path.Combine(dir, "rules-source.oml");
        File.WriteAllText(spec, seed);
        var (code, report) = Apply.Execute(blank, spec, source);
        if (code != 0) { failures.Add("rules seed: " + JsonSerializer.Serialize(report)); return; }
        var index = 0;
        // saves: saved without a flag. asks: not saved, with needsApproval (and, when `approve`, saved with --user-approved).
        // refused: not saved, "refused: ... (rule 2)", also with --user-approved. A text in `says` must be in the report.
        void Check(string json, string expect, string label, bool approve = false, string? says = null)
        {
            File.WriteAllText(spec, json);
            var output = Path.Combine(dir, $"rules-{index++}.oml");
            var (result, details) = Apply.Execute(source, spec, output);
            var text = JsonSerializer.Serialize(details);
            var ok = expect switch
            {
                "saves" => result == 0 && File.Exists(output),
                "asks" => result == 1 && !File.Exists(output) && text.Contains("\"needsApproval\":[") && !text.Contains("refused"),
                _ => result == 1 && !File.Exists(output) && text.Contains("(rule 2)"),
            };
            if (!ok || says is not null && !text.Contains(JsonSerializer.Serialize(says)[1..^1])) { failures.Add($"rules {label} ({expect}): {text}"); return; }
            if (expect == "saves" || expect == "asks" && !approve) return;
            output = Path.Combine(dir, $"rules-{index++}.oml");
            (result, details) = Apply.Execute(source, spec, output, userApproved: true);
            text = JsonSerializer.Serialize(details);
            if (expect == "asks" ? result != 0 || !File.Exists(output) || !text.Contains("--user-approved") : result != 1 || File.Exists(output) || !text.Contains("(rule 2)"))
                failures.Add($"rules {label} ({expect}, --user-approved): {text}");
        }
        string Edit(object edit) => JsonSerializer.Serialize(new { edits = new[] { edit } });
        // Rule 3: deleting any other existing element asks; renaming it saves.
        foreach (var target in new[] { "RuleDto", "RuleDto/Caption", "RuleAction", "RuleAction/Input", "RuleAction/Output",
            "RuleClient", "RuleClient/Input", "RuleClient/Output", "RuleScreen", "RuleBlock", "RuleError",
            "RuleRole", "RuleProperty", "RuleTimer", "RuleApi", "RuleApi/GetValue", "RuleService",
            "RuleService/GetValue", "RuleProcess" })
        {
            Check(Edit(new { op = "delete", element = target }), "asks", "delete " + target, approve: target is "RuleClient" or "RuleError" or "RuleProperty" or "RuleScreen");
            Check(Edit(new { op = "rename", element = target, to = "Renamed" }), "saves", "rename " + target);
        }
        Check(Edit(new { op = "remove", element = "RuleClient" }), "asks", "remove RuleClient", approve: true);
        // Rule 2: an existing entity or attribute is never deleted, an attribute never renamed or retyped.
        foreach (var target in new[] { "RuleEntity", "RuleEntity/Value", "RuleStatus", "RuleStatus/Color" })
            Check(Edit(new { op = "delete", element = target }), "refused", "delete " + target, approve: target == "RuleEntity");
        Check(Edit(new { op = "remove", element = "RuleEntity/Value" }), "refused", "remove RuleEntity/Value");
        Check(Edit(new { op = "rename", element = "RuleEntity", to = "RuleThing" }), "saves", "rename entity");
        Check(Edit(new { op = "rename", element = "RuleStatus", to = "RuleState" }), "saves", "rename static entity");
        foreach (var target in new[] { "RuleEntity/Value", "RuleStatus/Color" })
        {
            Check(Edit(new { op = "rename", element = target, to = "Renamed" }), "refused", "rename " + target);
            Check(Edit(new { op = "set", element = target, properties = new { DataType = "Integer" } }), "refused", "retype " + target, approve: target == "RuleEntity/Value");
        }
        Check("""{"entities":[{"name":"RuleEntity","attributes":[{"name":"Value","type":"Integer"}]}]}""", "refused", "retype through entities");
        Check($$"""{"copy":[{"from":{{JsonSerializer.Serialize(source)}},"elements":["RuleEntity"]}]}""", "refused", "copy of an entity");
        using (var module = Model.Load(source))
        {
            var record = ProductionRules.Walk(module).OfType<OutSystems.Model.Data.IRecord>().Single();
            var path = "RuleStatus/" + ElementPaths.Segment(record, record.Parent!);
            Check(Edit(new { op = "delete", element = path }), "asks", "static record delete", approve: true);
            Check(Edit(new { op = "set", element = path, properties = new { Identifier = "Renamed" } }), "saves", "static record rename");
        }
        // Rule 5: what can lose data asks; the rest saves.
        Check(Edit(new { op = "set", element = "RuleEntity/Value", properties = new { Length = 12 } }), "asks", "shorter length", approve: true, says: "shorter length 50 -> 12");
        Check(Edit(new { op = "set", element = "RuleEntity/Value", properties = new { IsMandatory = true } }), "asks", "new mandatory");
        Check(Edit(new { op = "set", element = "RuleEntity/Value", properties = new { Length = 100, Description = "longer" } }), "saves", "longer length");
        Check(Edit(new { op = "set", element = "RuleEntity", properties = new { Description = "settings", UpdateBehavior = "Full" } }), "saves", "entity settings");
        Check(Edit(new { op = "set", element = "RuleDto/Caption", properties = new { DataType = "Integer", Length = 5 } }), "saves", "structure attribute");
        // Rule 6: SQL that changes data asks, new or changed; read-only SQL is free.
        foreach (var keyword in new[] { "DELETE", "Merge" })
            Check(JsonSerializer.Serialize(new { serverActions = new[] { new { name = "BadSql", flow = new[] { new { kind = "sql", name = "Unsafe", sql = "SELECT 'ok'; " + keyword + " RuleEntity", outputs = new[] { "RuleDto" } } } } } }), "asks", "new SQL " + keyword);
        foreach (var keyword in new[] { "update", "TRUNCATE" })
            Check(Edit(new { op = "set", element = "RuleAction/Read", properties = new { Statement = "SELECT 'ok'; " + keyword + " RuleEntity" } }), "asks", "existing SQL " + keyword, approve: keyword == "update");
        Check("""{"edits":[{"op":"set","element":"RuleAction/Read","properties":{"statement":"SELECT 'DELETE '' UPDATE', 'MERGE' /* DROP /* ALTER */ TRUNCATE */ -- DELETE\n"}}]}""", "saves", "SELECT with ignored tokens");
        // Rule 4: a public signature change saves and is listed as breaking.
        Check("""{"edits":[{"op":"addVariables","action":"RulePublic","inputs":[{"name":"Region","mandatory":true}]}]}""", "saves", "public signature", says: "new mandatory input 'Region'");
        Check(Edit(new { op = "set", element = "RulePublic", properties = new { Public = false } }), "saves", "public to private", says: "was public and is now private");
        Check(Edit(new { op = "rename", element = "RulePublic/Id", to = "OrderId" }), "saves", "rename of a public input", says: "was renamed to 'OrderId'");
        // Same-spec elements and flow parts: free.
        Check("""{"entities":[{"name":"Disposable","attributes":[{"name":"Value"}]}],"edits":[{"op":"delete","element":"Disposable"}]}""", "saves", "same-spec delete");
        Check("""{"edits":[{"op":"rename","element":"RuleAction/Copy","to":"CopyAgain"},{"op":"delete","element":"RuleAction/CopyAgain"},{"op":"rename","element":"RuleAction/Scratch","to":"Temporary"},{"op":"delete","element":"RuleAction/Temporary"}]}""", "saves", "flow/local edits");
        Check("""{"entities":[{"name":"RuleEntity","attributes":[{"name":"Added"}]}],"edits":[{"op":"set","element":"RuleEntity/Added","properties":{"Length":120}},{"op":"rename","element":"RuleEntity/Added","to":"NewName"},{"op":"delete","element":"RuleEntity/NewName"}]}""", "saves", "new attribute");
        Check("""{"edits":[{"op":"addVariables","action":"RuleAction","inputs":[{"name":"AddedInput"}],"outputs":[{"name":"AddedOutput"}]}]}""", "saves", "new parameters");
        // The project's autoApprove.deletes (the launcher sets the variable): a delete saves without the flag.
        Environment.SetEnvironmentVariable("OML_AUTO_APPROVE_DELETES", "1");
        try { Check(Edit(new { op = "delete", element = "RuleError" }), "saves", "delete with autoApprove.deletes", says: "autoApprove.deletes"); }
        finally { Environment.SetEnvironmentVariable("OML_AUTO_APPROVE_DELETES", null); }
        // Keywords inside strings, identifiers, parameters and comments are not executable SQL keywords; INSERT is free.
        foreach (var sql in new[] { "SELECT 'DELETE '' UPDATE'", "SELECT [UPDATE], \"DROP\" FROM {MERGE}",
            "SELECT @DELETE -- UPDATE\r /* DROP /* ALTER */ MERGE */", "SELECT UpdatedValue, DeletedAt", "INSERT INTO {RuleEntity} ([Value]) VALUES ('UPDATE')" })
            if (ProductionRules.DataChangingKeyword(sql) is { } found) failures.Add($"rules SQL lexer: {found} in {sql}");
        if (ProductionRules.DataChangingKeyword("select 1; delete from {X}") != "DELETE") failures.Add("rules SQL lexer: DELETE not found");

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
