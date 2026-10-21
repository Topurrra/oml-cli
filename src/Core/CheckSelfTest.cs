namespace OmlCli;

// Self-test step for `oml check`: the model properties and Service Studio warning ids the rules read
// (ExpandInline, IsSecret, ExposeReadOnly, AnonymousRole, Cycle connectors, EntityKind, Wake<Timer>, client variable types,
// Escape Content, and the warnings UnusedUserAction, SQLInjection, IteratedServerInteraction, HTMLInjection,
// ClientActionWithModelAssignmentOnLifecycleEvent and SelfRefreshingAggregate) still behave on this build, and the report page escapes its data.
static class CheckSelfTest
{
    const string Spec = """
        {
          "siteProperties": [{ "name": "ApiToken" }, { "name": "ClientSecret", "isSecret": true }],
          "clientVariables": [{ "name": "AuthToken" }, { "name": "TokenCount", "type": "Integer" }],
          "entities": [{ "name": "Customer", "public": true, "description": "c", "attributes": [{ "name": "Name" }] }],
          "serverActions": [
            { "name": "Ping", "flow": [] },
            { "name": "NeverCalled", "flow": [] },
            { "name": "LongWork", "public": true, "description": "d", "flow": [] },
            { "name": "LoopQuery", "public": true, "description": "d", "flow": [
              { "kind": "aggregate", "name": "GetAll", "sources": ["Customer"], "maxRecords": 10 },
              { "kind": "forEach", "name": "Loop", "recordList": "GetAll.List", "cycle": "GetOne", "next": "End" },
              { "kind": "aggregate", "name": "GetOne", "sources": ["Customer"], "filters": ["Customer.Id = GetAll.List.Current.Customer.Id"], "maxRecords": 1, "next": "Loop" } ] },
            { "name": "Inline", "public": true, "description": "d", "inputs": [{ "name": "Filter", "description": "f" }], "flow": [
              { "kind": "sql", "name": "Find", "sql": "SELECT {Customer}.* FROM {Customer} WHERE @Condition", "maxRecords": 5,
                "params": [{ "name": "Condition", "value": "Filter", "expandInline": true }], "outputs": ["Customer"] } ] } ],
          "clientActions": [{ "name": "ClientLoop", "public": true, "description": "d", "locals": [{ "name": "Numbers", "type": "List of Integer" }], "flow": [
            { "kind": "forEach", "name": "Each", "recordList": "Numbers", "cycle": "Ping", "next": "End" },
            { "kind": "execute", "action": "Ping", "next": "Each" } ] }],
          "timers": [{ "name": "LongJob", "action": "LongWork", "schedule": "", "timeout": 60 }],
          "screens": [{ "name": "PublicHome", "access": { "anonymous": true }, "aggregates": [{ "name": "GetPublic", "sources": ["Customer"], "maxRecords": 10, "onAfterFetch": "AfterFetch" }],
            "actions": [{ "name": "AfterFetch", "flow": [{ "kind": "execute", "action": "Ping" }] }] },
            { "name": "Counter", "locals": [{ "name": "Renders", "type": "Integer" }], "onRender": "CountRenders",
              "actions": [{ "name": "CountRenders", "flow": [{ "kind": "assign", "name": "AddOne", "assign": { "Renders": "Renders + 1" } }] }],
              "widgets": [{ "type": "Expression", "value": "Renders" }] },
            { "name": "Quiet", "onRender": "OnlyLocal",
              "actions": [{ "name": "OnlyLocal", "locals": [{ "name": "Step", "type": "Integer" }], "flow": [{ "kind": "assign", "name": "SetStep", "assign": { "Step": "1" } }] }] }],
          "edits": [{ "op": "set", "element": "Customer", "properties": { "ExposeReadOnly": false } }]
        }
        """;

    public static void Run(string blank, string dir, List<string> failures)
    {
        void Check(bool condition, string message) { if (!condition) failures.Add("check: " + message); }
        try
        {
            // Nesting on small graphs: guards that only end the flow do not nest; branches that join again and loop bodies do.
            int Depth(params (string from, string to, bool cycle)[] edges)
            {
                var nodes = edges.SelectMany(e => new[] { e.from, e.to }).Distinct().Cast<object>().ToList();
                return CheckRules.Nesting(nodes, n => edges.Where(e => e.from == (string)n).Select(e => ((object)e.to, e.cycle)));
            }
            Check(Depth(("S", "G1", false), ("G1", "E", false), ("G1", "G2", false), ("G2", "E", false), ("G2", "W", false), ("W", "E", false)) == 0, "guard clauses must not nest");
            Check(Depth(("S", "I1", false), ("I1", "I2", false), ("I1", "J", false), ("I2", "A", false), ("I2", "J", false), ("A", "J", false), ("J", "E", false)) == 2, "two joined Ifs nest 2");
            Check(Depth(("S", "L1", false), ("L1", "L2", true), ("L1", "E", false), ("L2", "A", true), ("L2", "L1", false), ("A", "L2", false)) == 2, "a loop in a loop nests 2");

            var spec = Path.Combine(dir, "check-spec.json");
            var built = Path.Combine(dir, "check.oml");
            File.WriteAllText(spec, Spec);
            var (code, report) = Apply.Execute(blank, spec, built);
            if (code != 0) { failures.Add("check: apply: " + System.Text.Json.JsonSerializer.Serialize(report)); return; }
            using var module = Model.Load(built);
            var result = CheckRules.Run(module, _ => true, (rule, key) => CheckCatalog.All.Single(r => r.Id == rule).Options![key]);
            int Count(string rule) => result.Hits.Count(h => h.Rule == rule);
            foreach (var rule in new[] { "timer-long-timeout", "public-entity-writable", "server-call-in-client-event", "site-property-secret", "query-in-loop", "sql-expand-inline-input", "server-call-in-client-loop", "anonymous-screen-server", "unused-action", "client-variable-secret", "render-assign" })
                Check(Count(rule) == 1, $"{rule}: expected 1 finding, got {Count(rule)}");
            Check(result.Hits.Any(h => h.Rule == "unused-action" && h.Element == "NeverCalled"), "unused-action must point at NeverCalled (Service Studio warning id UnusedUserAction)");
            Check(result.Hits.Single(h => h.Rule == "site-property-secret").Element == "ApiToken", "a secret site property must not be reported");
            Check(result.Hits.Single(h => h.Rule == "server-call-in-client-event").Element == "MainFlow/PublicHome/AfterFetch", "the On After Fetch handler must be reported");
            Check(Count("sql-expand-inline") == 0, "an input-fed Expand Inline parameter is reported once, as sql-expand-inline-input");
            Check(result.Hits.Single(h => h.Rule == "client-variable-secret").Element == "AuthToken", "an Integer client variable must not be reported");
            Check(result.Hits.Single(h => h.Rule == "render-assign").Element == "MainFlow/Counter/CountRenders", "render-assign must point at the OnRender handler that changes screen data");

            // Escape Content (Traditional Web): only the Expression with Escape Content = No and an input value is reported.
            var webBlank = Path.Combine(dir, "check-web-blank.oml");
            using (var web = Model.Services().CreateESpace(OutSystems.Model.Enumerations.SegmentationKind.WebResponsive, "CheckWeb", "", false, Model.Services().NewKey()))
                Model.Save(web, webBlank);
            File.WriteAllText(spec, """
                { "screens": [{ "name": "Notice", "inputs": [{ "name": "Message" }], "widgets": [
                  { "type": "Expression", "name": "RawMessage", "value": "Message", "escapeContent": false },
                  { "type": "Expression", "name": "SafeMessage", "value": "EncodeHtml(Message)", "escapeContent": false },
                  { "type": "Expression", "name": "PlainMessage", "value": "Message" } ] }] }
                """);
            var webBuilt = Path.Combine(dir, "check-web.oml");
            var (webCode, webReport) = Apply.Execute(webBlank, spec, webBuilt);
            if (webCode != 0) { failures.Add("check: web apply: " + System.Text.Json.JsonSerializer.Serialize(webReport)); return; }
            using (var webModule = Model.Load(webBuilt))
            {
                var escaped = CheckRules.Run(webModule, _ => true, (rule, key) => CheckCatalog.All.Single(r => r.Id == rule).Options![key]).Hits.Where(h => h.Rule == "escape-content-off").ToList();
                Check(escaped.Count == 1 && escaped[0].Element == "MainFlow/Notice/RawMessage", $"escape-content-off: expected MainFlow/Notice/RawMessage, got {string.Join(", ", escaped.Select(h => h.Element))}");
            }

            // The wake-timer pattern: once the timer's action calls Wake<Timer>, a long timeout is not reported. A spec cannot call it.
            var work = module.ServerActions.Single(a => a.Name == "LongWork");
            var services = Model.Services();
            var start = work.Nodes.OfType<OutSystems.Model.Logic.Nodes.IStartNode>().Single();
            var end = work.Nodes.OfType<OutSystems.Model.Logic.Nodes.IEndNode>().FirstOrDefault() ?? work.CreateNode<OutSystems.Model.Logic.Nodes.IEndNode>("End", services.NewKey());
            var wake = work.CreateNode<OutSystems.Model.Logic.Nodes.IExecuteServerActionNode>("WakeLongJob", services.NewKey());
            wake.Action = (OutSystems.Model.Logic.IActionSignature)ElementPaths.Resolve(module, "LongJob/WakeLongJob");
            start.Target = wake;
            wake.Target = end;
            Check(!CheckRules.Run(module, _ => true, (rule, key) => CheckCatalog.All.Single(r => r.Id == rule).Options![key]).Hits.Any(h => h.Rule == "timer-long-timeout"),
                "a timer whose action calls Wake<Timer> must not be reported");

            // The page escapes <, > and & in the embedded report itself, not only through the JSON encoder.
            var page = CheckPage.Render("{\"label\":\"</script><script>window.x=1</script>&\"}");
            Check(!page.Contains("</script><script>window.x") && page.Contains("\\u003c/script\\u003e") && page.Contains("\\u0026"), "the check page must escape raw markup in its data");

            // A local (client) entity action runs on the device: in a client loop it is not a server request.
            var localSource = Path.Combine(dir, "check-local-source.oml");
            using (var source = Model.Load(blank))
            {
                var local = source.CreateClientEntity("LocalNote", services.NewKey());
                var id = local.CreateAttribute("Id", services.NewKey());
                id.DataType = source.LongIntegerType;
                id.IsAutoNumber = OutSystems.Model.Enumerations.AutoNumber.Yes;
                id.IsMandatory = true;
                local.IdentifierAttribute = id;
                local.CreateAttribute("Text", services.NewKey()).DataType = source.TextType;
                Model.Save(source, localSource);
            }
            File.WriteAllText(spec, """
                { "entities": [{ "name": "ServerNote", "attributes": [{ "name": "Text" }] }],
                  "screens": [{ "name": "Notes", "locals": [{ "name": "Note", "type": "LocalNote" }],
                    "aggregates": [{ "name": "GetNotes", "sources": ["LocalNote"], "maxRecords": 10, "onAfterFetch": "ReloadNotes" }],
                    "actions": [{ "name": "ReadyLocal", "flow": [{ "kind": "refreshData", "dataSource": "GetNotes" },
                      { "kind": "execute", "action": "CreateOrUpdateLocalNote", "args": { "Source": "Note" } }] },
                      { "name": "ReloadNotes", "flow": [{ "kind": "refreshData", "dataSource": "GetNotes" }] }], "onReady": "ReadyLocal",
                    "widgets": [{ "type": "Expression", "value": "GetNotes.Count" }] }],
                  "edits": [{ "op": "set", "element": "Notes/GetNotes", "properties": { "IsClientSide": true } }],
                  "clientActions": [{ "name": "SaveNotes", "public": true, "description": "d",
                    "locals": [{ "name": "Numbers", "type": "List of Integer" }, { "name": "Note", "type": "LocalNote" }], "flow": [
                    { "kind": "forEach", "name": "Each", "recordList": "Numbers", "cycle": "CreateOrUpdateLocalNote", "next": "End" },
                    { "kind": "execute", "action": "CreateOrUpdateLocalNote", "args": { "Source": "Note" }, "next": "Each" } ] },
                  { "name": "SaveServerNotes", "public": true, "description": "d",
                    "locals": [{ "name": "Numbers", "type": "List of Integer" }, { "name": "Note", "type": "ServerNote" }], "flow": [
                    { "kind": "forEach", "name": "Each", "recordList": "Numbers", "cycle": "CreateOrUpdateServerNote", "next": "End" },
                    { "kind": "execute", "action": "CreateOrUpdateServerNote", "args": { "Source": "Note" }, "next": "Each" } ] }] }
                """);
            var localBuilt = Path.Combine(dir, "check-local.oml");
            var (localCode, localReport) = Apply.Execute(localSource, spec, localBuilt);
            if (localCode != 0) { failures.Add("check: local entity apply: " + System.Text.Json.JsonSerializer.Serialize(localReport)); return; }
            using var localModule = Model.Load(localBuilt);
            var localHits = CheckRules.Run(localModule, _ => true, (rule, key) => CheckCatalog.All.Single(r => r.Id == rule).Options![key]).Hits;
            Check(!localHits.Any(h => h.Rule is "server-call-in-client-loop" or "multiple-server-calls" && h.Element.StartsWith("SaveNotes/")), "a local entity action in a client loop is not a server request");
            Check(localHits.Count(h => h.Rule == "server-call-in-client-loop") == 1 && localHits.Any(h => h.Rule == "server-call-in-client-loop" && h.Element.StartsWith("SaveServerNotes/")),
                "a server entity action in a client loop is a server request");
            Check(!localHits.Any(h => h.Rule == "server-call-in-client-event"), "refreshing a local storage aggregate or saving a local entity in OnReady is not a server request");
            Check(localHits.Where(h => h.Rule == "self-refreshing-aggregate").Select(h => h.Element).SequenceEqual(["MainFlow/Notes/GetNotes"]),
                "self-refreshing-aggregate must point at a local storage aggregate that its own On After Fetch refreshes (Service Studio warning SelfRefreshingAggregate)");
        }
        catch (Exception e) { failures.Add("check: " + (e.InnerException ?? e).Message); }
    }
}
