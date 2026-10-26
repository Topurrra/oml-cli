using OutSystems.Model;
using OutSystems.Model.Enumerations;

namespace OmlCli;

// Replaces a hardcoded list of known builds: every Service Studio build is checked once, on this machine,
// by running the real apply path on a throwaway module: data model, flows (aggregate, if, for each, generated
// CRUD call, raise), a client action, a block, and a screen with widgets, events and a table. A build that passes is remembered.
static partial class SelfTest
{
    static readonly string Store = Locations.SelfTestRecord;

    const string Spec = """
        {
          "exceptions": ["SelfTestError"],
          "entities": [
            { "name": "Customer", "attributes": [ { "name": "Name", "type": "Text", "length": 50, "mandatory": true } ] },
            { "name": "Order", "attributes": [
                { "name": "CustomerId", "type": "Customer Identifier", "mandatory": true },
                { "name": "Total", "type": "Decimal", "length": 37, "decimals": 2 } ] }
          ],
          "structures": [ { "name": "OrderDto", "attributes": [
              { "name": "Id", "type": "Order Identifier" }, { "name": "Orders", "type": "List of Order" } ] } ],
          "serverActions": [ { "name": "FetchOrder",
              "inputs": [ { "name": "OrderId", "type": "Order Identifier", "mandatory": true } ],
              "outputs": [ { "name": "Result", "type": "OrderDto" }, { "name": "Count", "type": "Integer" } ],
              "locals": [ { "name": "Rec", "type": "Order" } ],
              "flow": [
                { "kind": "aggregate", "name": "GetOrders", "sources": ["Order", "Customer"], "joins": [ { "type": "only with", "left": "Order", "right": "Customer", "condition": "Order.CustomerId = Customer.Id" } ], "filters": ["Order.Id = OrderId"], "maxRecords": 1 },
                { "kind": "if", "name": "Found", "condition": "GetOrders.List.Empty", "true": "Fail", "false": "Loop" },
                { "kind": "forEach", "name": "Loop", "recordList": "GetOrders.List", "cycle": "Copy", "next": "CreateOrUpdateOrder" },
                { "kind": "assign", "name": "Copy", "assign": { "Rec": "GetOrders.List.Current.Order" }, "next": "Loop" },
                { "kind": "execute", "action": "CreateOrUpdateOrder", "args": { "Source": "Rec" } },
                { "kind": "assign", "name": "Out", "assign": { "Count": "GetOrders.Count" }, "next": "End" },
                { "kind": "raiseException", "name": "Fail", "exception": "SelfTestError", "message": "\"none\"" } ] } ],
          "clientActions": [ { "name": "Twice", "inputs": [ { "name": "N", "type": "Integer" } ], "outputs": [ { "name": "R", "type": "Integer" } ],
              "flow": [ { "kind": "assign", "name": "Calc", "assign": { "R": "N * 2" } } ] } ],
          "blocks": [ { "name": "Card", "inputs": [ { "name": "Title", "type": "Text" } ],
              "widgets": [ { "type": "Container", "children": [ { "type": "Expression", "value": "Title" } ] } ] } ],
          "screens": [ { "name": "Detail", "inputs": [ { "name": "CustomerId", "type": "Customer Identifier" } ] }, { "name": "Orders", "title": "\"Orders\"", "locals": [ { "name": "Total", "type": "Decimal" } ],
              "aggregates": [ { "name": "GetOrders", "sources": ["Order"], "maxRecords": 10 } ],
              "actions": [ { "name": "Init", "flow": [ { "kind": "assign", "name": "Reset", "assign": { "Total": "0" } } ] } ],
              "onInitialize": "Init",
              "widgets": [
                { "type": "Link", "onClick": "Detail", "args": { "CustomerId": "NullIdentifier()" }, "children": [ { "type": "Text", "text": "Detail" } ] },
                { "type": "Container", "children": [
                    { "type": "Input", "variable": "Total" },
                    { "type": "Button", "onClick": "Init", "children": [ { "type": "Text", "text": "Reset" } ] } ] },
                { "type": "BlockInstance", "sourceBlock": "Card", "args": { "Title": "\"Hi\"" } },
                { "type": "If", "condition": "GetOrders.List.Empty", "falseBranch": [
                    { "type": "TableRecords", "source": "GetOrders.List",
                      "row": [ { "type": "RowCell", "children": [ { "type": "Expression", "value": "GetOrders.List.Current.Order.Total" } ] } ] } ] } ] } ],
          "edits": [
            { "op": "addVariables", "action": "Twice", "locals": [{"name":"Scratch","type":"Integer"}] },
            { "op": "insertNodes", "action": "Twice", "after": "Start", "flow": [
              {"kind":"assign","name":"Remember","assign":{"Scratch":"N"}} ] },
            { "op": "rename", "element": "Twice/Remember", "to": "RememberN" },
            { "op": "insertNodes", "action": "Twice", "after": "Calc", "flow": [
              {"kind":"assign","name":"Temporary","assign":{"Scratch":"R"}} ] },
            { "op": "delete", "element": "Twice/Temporary" },
            { "op": "addParts", "screen": "MainFlow/Orders", "locals": [{"name":"Caption","default":"\"Ready\""}] },
            { "op": "addWidgets", "screen": "MainFlow/Orders", "parent": "Container[0]", "index": 0,
              "widgets": [{"type":"Expression","name":"CaptionText","value":"Caption"}] },
            { "op": "set", "element": "MainFlow/Orders/CaptionText", "properties": {"value":"\"Edited\""} },
            { "op": "addParts", "screen": "Card", "locals": [{"name":"CardCaption"}] },
            { "op": "addWidgets", "screen": "Card", "widgets": [{"type":"Expression","name":"BlockCaption","value":"CardCaption"}] }
          ]
        }
        """;

    public static bool Passed(string build) =>
        File.Exists(Store) && File.ReadAllLines(Store).Contains(build);

    // True while Run is on the stack. Its `oml run` step calls commands that call EnsurePassed; on a build not
    // recorded yet that started the self-test again inside itself, without end (1.2.0 to 1.3.0).
    static bool running;

    // A self-test that never ends (a Service Studio call that waits forever) must not hang the caller.
    // OML_SELFTEST_LIMIT_SECONDS is for tests only.
    static TimeSpan Limit => TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("OML_SELFTEST_LIMIT_SECONDS"), out var s) && s > 0 ? s : 600);

    // Set by `oml dashboard` (Cli.Capture): a self-test past its limit must not end the server. EnsurePassed then fails
    // the command with Abandoned, and the dashboard refuses every later command with it, because the self-test may
    // still hold the model.
    public static bool KeepProcess;
    public static string? Abandoned { get; private set; }

    static void Abandon(string build, TimeSpan limit)
    {
        if (KeepProcess) return;
        // Console.Out may be captured by an `oml run` step, so write to the real standard output.
        using (var stdout = new StreamWriter(Console.OpenStandardOutput()))
            stdout.WriteLine(JsonOutput.Serialize(new { error = $"The self-test on Service Studio {build} did not finish in {limit.TotalSeconds:0} s, so oml stopped. "
                + "If Service Studio was just updated, open it once, wait until it has started, close it, and retry. If it stops again, run oml doctor --text and report the output." }));
        Environment.Exit(2);
    }

    public static void EnsurePassed()
    {
        if (Abandoned is { } stopped) throw new CliError(stopped, 2);
        var build = Model.InstalledBuild();
        if (running || Passed(build)) return;
        Console.Error.WriteLine($"Service Studio {build} not checked on this machine yet; running self-test...");
        var (code, report) = KeepProcess ? RunWithin(build) : Run();
        if (code != 0)
        {
            Cli.Emit(report);
            throw new CliError($"Self-test failed on Service Studio {build}; the model API changed in a way the CLI does not handle yet. See the report above.", 2);
        }
    }

    // The dashboard's self-test: on another thread, so that a self-test past its limit fails this command instead of
    // ending the process. The late self-test thread is left to finish or hang on its own.
    static (int code, object report) RunWithin(string build)
    {
        var limit = Limit;
        var task = Task.Run(Run);
        if (!task.Wait(limit))
            throw new CliError(Abandoned = $"The self-test on Service Studio {build} did not finish in {limit.TotalSeconds:0} s. The dashboard keeps running, but it refuses "
                + "model commands until you restart it. If Service Studio was just updated, open it once, wait until it has started, close it, and restart oml dashboard. "
                + "If it stops again, run oml doctor --text and report the output.", 2);
        return task.GetAwaiter().GetResult();
    }

    public static (int code, object report) Run()
    {
        var build = Model.InstalledBuild();
        var dir = Path.Combine(Path.GetTempPath(), "oml-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var failures = new List<string>();
        var limit = Limit;
        using var watchdog = new System.Threading.Timer(_ => Abandon(build, limit), null, limit, Timeout.InfiniteTimeSpan);
        running = true;
        try
        {
            var services = Model.Services();
            var blank = Path.Combine(dir, "blank.oml");
            using (var module = services.CreateESpace(SegmentationKind.CrossDevice, "OmlSelfTest", "", false, services.NewKey()))
                Model.Save(module, blank);

            var spec = Path.Combine(dir, "spec.json");
            File.WriteAllText(spec, Spec);
            var (code, report) = Apply.Execute(blank, spec, Path.Combine(dir, "built.oml"));
            if (code != 0) failures.Add("apply: " + System.Text.Json.JsonSerializer.Serialize(report));
            else
            {
                using var built = Model.Load(Path.Combine(dir, "built.oml"));
                DescribeSelfTest.Check(built, failures);
                var order = built.Entities.SingleOrDefault(e => e.Name == "Order");
                var fk = order?.Attributes.SingleOrDefault(a => a.Name == "CustomerId");
                if (fk is null || Model.Describe(built, fk.DataType) != "Customer Identifier")
                    failures.Add("Order.CustomerId did not round-trip as 'Customer Identifier'");
                // A generic identifier type on Id validates but is not what Service Studio creates; keep Id a real type.
                if (order?.IdentifierAttribute?.DataType is var idType && !ReferenceEquals(idType, built.LongIntegerType)
                    && Model.Describe(built, idType) != "Long Integer")
                    failures.Add($"Order.Id is '{Model.Describe(built, idType)}', expected 'Long Integer'");
                var twice = ElementPaths.Resolve(built, "Twice");
                if (ElementPaths.Resolve(twice, "RememberN") is not OutSystems.Model.Logic.Nodes.IAssignNode
                    || twice.Children.Any(n => Model.NameOf(n) == "Temporary"))
                    failures.Add("edited nodes did not round-trip");
                if (ElementPaths.Resolve(built, "MainFlow/Orders/CaptionText") is not OutSystems.Model.UI.IWidgetSignature)
                    failures.Add("added widget missing after reopen");
                // A Button/Link seeds its own Text; spec children must replace it, not render after it ("linkGo").
                static IEnumerable<IObjectSignature> Walk(IObjectSignature o) => new[] { o }.Concat(ElementPaths.Children(o).SelectMany(Walk));
                foreach (var button in Walk(built).Where(o => ElementPaths.Kind(o) == "Button"))
                    if (Walk(button).Count(o => ElementPaths.Kind(o) == "Text") != 1)
                        failures.Add("a Button kept its seeded Text next to the spec's children");
                if (built.ServerActions.Count(a => a.Name == "FetchOrder") != 1)
                    failures.Add("FetchOrder missing or duplicated after reopen");
                if (!built.MobileFlows.Any(f => f.Nodes.Any(n => Model.NameOf(n) == "Orders")))
                    failures.Add("screen Orders missing after reopen");
            }

            RestSelfTest.Run(blank, dir, failures);
            OpenApiTest(blank, dir, failures);
            PreviewTest(blank, dir, failures);
            StaticTest(blank, dir, failures);
            SqlDeletionSelfTest.Run(blank, dir, failures);
            RulesTest(blank, dir, failures);
            Platform(blank, dir, failures);
            WebTest(dir, failures);
            ReferencesTest(blank, dir, failures);
            QualifiedNamesTest(blank, dir, failures);
            MinifyTest(blank, dir, failures);
            FileValuesTest(blank, dir, failures);
            RunCommandTest(blank, dir, failures);
            TurnsTest(blank, dir, failures);
            PackageSelfTest.Run(blank, dir, failures);
            LogicTest(blank, dir, failures);
            FlowLayoutTest(blank, dir, failures);
            ContentSelfTest.Run(blank, dir, failures);
            FormValidationTest(blank, dir, failures);
            EventsTest(blank, dir, failures);
            EventsFollowUpTest(blank, dir, failures);
            DataTest(blank, dir, failures);
            ArchitectureTest(Path.Combine(dir, "built.oml"), failures);
            CheckSelfTest.Run(blank, dir, failures);
            ReviewSelfTest.Run(blank, dir, failures);
            FieldMappingSelfTest.Run(blank, dir, failures);
            StudioCompareSelfTest.Run(blank, failures);
            PublishOrderSelfTest.Run(failures);
            HistoryTest(Path.Combine(dir, "built.oml"), dir, failures);
            ReadersTest(blank, failures);
            NewParsersTest(failures);
            TestScaffoldTest(dir, failures);

            // A clash with a generated CRUD action must be refused, not renamed.
            File.WriteAllText(spec, """{ "entities": [ { "name": "Order", "attributes": [{ "name": "Name" }] } ], "serverActions": [ { "name": "GetOrder" } ] }""");
            var (clashCode, _) = Apply.Execute(blank, spec, Path.Combine(dir, "clash.oml"));
            if (clashCode == 0) failures.Add("name clash with a CRUD action was not detected");

            // Exercise scaffolding on a bare module, including a dependency-owned layout's placeholder proxies.
            var scaffoldSource = Path.Combine(dir, "scaffold-source.oml");
            using (var producer = services.CreateESpace(SegmentationKind.CrossDevice, "Layouts", "", false, services.NewKey()))
            using (var consumer = Model.Load(blank))
            {
                var block = producer.CreateMobileFlow("Common", services.NewKey()).CreateBlock("TestLayout", services.NewKey());
                Reflect.Property(block, "Public", writable: true)!.SetValue(block, true);
                foreach (var placeholder in new[] { "Title", "Actions", "MainContent", "Footer" })
                    block.CreateWidget<OutSystems.Model.UI.Mobile.Widgets.IPlaceholderWidget>(placeholder, services.NewKey());
                consumer.AddDependency<OutSystems.Model.UI.Mobile.IMobileBlock, OutSystems.Model.UI.Mobile.IMobileBlockSignature>(block);
                var theme = consumer.CreateMobileTheme("ScaffoldTheme", services.NewKey());
                Reflect.Property(consumer, "DefaultMobileTheme", writable: true)!.SetValue(consumer, theme);
                Model.Save(consumer, scaffoldSource);
            }
            File.WriteAllText(spec, """
                { "entities": [{ "name": "Category", "attributes": [{ "name": "Name" }] }, { "name": "Item", "attributes": [
                    { "name": "Name", "mandatory": true, "length": 50 }, { "name": "Active", "type": "Boolean" },
                    { "name": "CategoryId", "type": "Category Identifier" } ] }],
                  "screens": [
                    { "name": "Items", "template": "list", "entity": "Item", "layout": "TestLayout",
                      "title": [{ "type": "Text", "text": "Items" }],
                      "placeholders": { "Actions": [{ "type": "Text", "text": "Toolbar" }], "Footer": [{ "type": "Text", "text": "Footer content" }] } },
                    { "name": "ItemDetail", "template": "detail", "entity": "Item", "layout": "TestLayout" },
                    { "name": "BareItems", "template": "list", "entity": "Item" } ] }
                """);
            var scaffoldOut = Path.Combine(dir, "scaffold.oml");
            var (scaffoldCode, scaffoldReport) = Apply.Execute(scaffoldSource, spec, scaffoldOut);
            if (scaffoldCode != 0) failures.Add("scaffold: " + System.Text.Json.JsonSerializer.Serialize(scaffoldReport));
            else
            {
                using var built = Model.Load(scaffoldOut);
                var all = Descendants(built).ToList();
                if (!all.Any(o => Reflect.Get(o, "Action") is { } action && Model.NameOf(action) == "CreateOrUpdateItem"))
                    failures.Add("scaffold Save lost its CRUD reference");
                foreach (var destination in new[] { "Items", "ItemDetail" })
                    if (!all.Any(o => Reflect.Get(o, "Destination") is { } d && Model.NameOf(d) == destination))
                        failures.Add("scaffold navigation missing: " + destination);
                if (!all.Any(o => Reflect.Get(o, "SourceBlock") is { } b && Model.NameOf(b) == "TestLayout"))
                    failures.Add("scaffold referenced layout missing");
                if (!all.OfType<OutSystems.Model.UI.Mobile.Widgets.IPlaceholderContentWidgetSignature>()
                    .Any(p => p.Placeholder.Name == "Footer" && p.Widgets.Any()))
                    failures.Add("scaffold named placeholder content missing");
                if (!all.OfType<IExpression>().Any(e => e.Text == "FetchItemRecords.List.Current.Item.Name"))
                    failures.Add("scaffold form bindings missing");
                if (!all.OfType<IExpression>().Any(e => e.Text == "\"btn btn-primary\""))
                    failures.Add("scaffold theme classes missing");
                // List tables leave out the identifier column; only the detail link passes the Id (both lists link to ItemDetail).
                var idUses = new[] { "Items", "BareItems" }.Sum(s => Descendants(ElementPaths.Resolve(built, s))
                    .OfType<IExpression>().Count(e => e.Text == "FetchItemRecords.List.Current.Item.Id"));
                if (idUses != 2) failures.Add($"scaffold list shows the identifier column ({idUses} Id expressions, expected 2 link arguments)");
            }
            foreach (var invalid in new[] {
                """{ "screens": [{ "name": "MissingLayout", "layout": "NoSuchLayout" }] }""",
                """{ "screens": [{ "name": "BadSlot", "layout": "TestLayout", "placeholders": { "Wrong": [] } }] }""",
                """{ "screens": [{ "name": "BadTemplate", "template": "unknown", "entity": "Item" }] }"""
            })
            {
                File.WriteAllText(spec, invalid);
                var rejectedOut = Path.Combine(dir, "rejected.oml");
                var (invalidCode, _) = Apply.Execute(scaffoldSource, spec, rejectedOut);
                if (invalidCode == 0 || File.Exists(rejectedOut)) failures.Add("invalid scaffold/layout spec was saved: " + invalid);
            }
        }
        catch (Exception e) when (e is not CliError)
        {
            failures.Add($"{e.GetType().Name}: {(e.InnerException ?? e).Message}");
        }
        finally
        {
            running = false;
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }

        if (failures.Count == 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
            if (!Passed(build)) File.AppendAllLines(Store, new[] { build });
        }
        return (failures.Count == 0 ? 0 : 1, new { serviceStudio = build, passed = failures.Count == 0, failures });
    }

    static IEnumerable<IObjectSignature> Descendants(IObjectSignature root)
    {
        foreach (var child in root.Children)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
