using System.Text.Json.Nodes;

namespace OmlCli;

// The built-in sample of `oml migrate selftest [--show]`: the pure decisions of Migrate.cs and MigratePlan.cs over hand-written JSON.
static partial class Migrate
{
    // ---------- selftest ----------

    const string SampleInspect = """
    { "name": "OrdersWeb", "kind": "WebResponsive",
      "entities": [
        { "name": "Customer", "public": false, "attributes": [] },
        { "name": "Order", "public": true, "attributes": [] },
        { "name": "Note", "public": false, "attributes": [] },
        { "name": "OrderStatus", "public": false, "kind": "StaticEntity", "attributes": [] } ],
      "staticEntities": [ { "name": "OrderStatus", "public": false, "records": [] } ],
      "structures": [ { "name": "OrderDto", "public": false, "attributes": [] } ],
      "serverActions": [
        { "name": "SaveOrder", "public": false, "inputs": [], "outputs": [] },
        { "name": "CleanupOrders", "public": false, "inputs": [], "outputs": [] },
        { "name": "ReadOnly", "public": false, "inputs": [], "outputs": [] },
        { "name": "Customer_Update", "public": false, "inputs": [], "outputs": [] } ],
      "serviceActions": [], "clientActions": [], "restClients": [], "restServices": [],
      "screens": [
        { "flow": "MainFlow", "name": "Orders", "public": false, "inputs": [], "roles": ["Registered"], "anonymousAccess": false },
        { "flow": "MainFlow", "name": "OrderDetail", "public": false, "inputs": ["OrderId"], "roles": ["OrderManager"], "anonymousAccess": false } ],
      "blocks": [ { "flow": "Common", "name": "OrderCard", "public": false, "inputs": ["Order"], "roles": [] } ],
      "timers": [ { "name": "NightlyCleanup", "action": "CleanupOrders", "schedule": "WhenPublished", "timeout": 20, "priority": "Normal" } ],
      "processes": [ { "name": "ReviewOrder", "launchOn": "Order", "inputs": [], "nodes": [] } ],
      "sessionVariables": [
        { "name": "CurrentCustomerId", "type": "Customer Identifier" },
        { "name": "ThemeName", "type": "Text", "default": "\"light\"" },
        { "name": "LastSearch", "type": "Text" },
        { "name": "ApiToken", "type": "Text" },
        { "name": "Cart", "type": "Order List" },
        { "name": "Unused", "type": "Integer" } ],
      "otherContent": [
        { "kind": "SOAPClient", "name": "TaxService" },
        { "kind": "Email", "name": "OrderMail" },
        { "kind": "EntryPoint", "name": "Home" },
        { "kind": "WebService", "name": "LegacyApi" } ],
      "roles": [ { "name": "OrderManager", "public": false, "persistent": true, "actions": [] } ],
      "siteProperties": [ { "name": "MaxItems", "type": "Integer", "default": "50", "multiTenant": "No" } ],
      "exceptions": [ { "name": "InvalidOrder", "public": false } ],
      "references": [
        { "module": "Producer", "entities": [], "structures": [], "serverActions": ["GetCustomerById"], "clientActions": [], "serviceActions": [],
          "elements": [ { "name": "GetCustomerById", "kind": "IServerAction" }, { "name": "CustomerCard", "kind": "IWebBlock" } ] },
        { "module": "Missing", "elements": [ { "name": "Tax", "kind": "IServerAction" } ] },
        { "module": "System", "elements": [] } ] }
    """;

    // Inspect prints a session variable's name and type, and its default when it has one (an older inspect has none); ThemeName has one, LastSearch has none; otherContent has { kind, name } with the kind as ReviewModel.Kind gives it
    // (the interface name without its I: ISOAPClient -> SOAPClient), sorted by kind and name.
    const string SampleHeader = """
    [ { "path": "/p/Producer.oml", "info": { "Name": "Producer", "Key": "k1", "ESpaceType": "WebResponsive" } },
      { "path": "/p/Other.oml", "info": { "Name": "Other", "Key": "k2" } },
      { "path": "/p/Broken.oml", "error": "not a readable module" } ]
    """;

    // The output of `oml batch` over the tree steps of the copied logic, in the order the steps are written (SampleOrder). SaveOrder
    // calls CreateOrUpdateOrder twice; CleanupOrders calls DeleteOrder inside a loop; Customer_Update is an existing wrapper that calls
    // UpdateCustomer; ReadOnly calls a user action, UpdateCustomer and reads with an aggregate; the process calls nothing. Shapes after Inspect.Tree.
    const string SampleBatch = """
    { "results": [
      { "step": 1, "exitCode": 0, "output": { "kind": "ServerAction", "name": "CleanupOrders", "path": "CleanupOrders", "children": [
          { "kind": "ForEach", "name": "Old", "path": "CleanupOrders/Old", "children": [
            { "kind": "ExecuteServerAction", "name": "Remove", "path": "CleanupOrders/Remove", "ref": "DeleteOrder", "children": [ { "kind": "Argument", "ref": "Id" }, { "kind": "Sequence", "path": "CleanupOrders/Remove/Sequence[0]", "ref": "DeleteOrder" } ] } ] } ] } },
      { "step": 2, "exitCode": 0, "output": { "kind": "ServerAction", "name": "ReadOnly", "path": "ReadOnly", "children": [
          { "kind": "ExecuteServerAction", "name": "Other", "path": "ReadOnly/Other", "ref": "SaveOrder" },
          { "kind": "ExecuteServerAction", "name": "Touch", "path": "ReadOnly/Touch", "ref": "UpdateCustomer" },
          { "kind": "Aggregate", "name": "GetOrder", "path": "ReadOnly/GetOrder", "ref": "Order" } ] } },
      { "step": 3, "exitCode": 0, "output": { "kind": "Process", "name": "ReviewOrder", "path": "ReviewOrder", "children": [] } },
      { "step": 4, "exitCode": 0, "output": { "kind": "ServerAction", "name": "SaveOrder", "path": "SaveOrder", "children": [
          { "kind": "ExecuteServerAction", "name": "Save", "path": "SaveOrder/Save", "ref": "CreateOrUpdateOrder" },
          { "kind": "ExecuteServerAction", "name": "SaveAgain", "path": "SaveOrder/SaveAgain", "ref": "CreateOrUpdateOrder" } ] } },
      { "step": 5, "exitCode": 0, "output": { "kind": "ServerAction", "name": "Customer_Update", "path": "Customer_Update", "children": [
          { "kind": "ExecuteServerAction", "name": "Save", "path": "Customer_Update/Save", "ref": "UpdateCustomer" } ] } } ] }
    """;
    static readonly List<(string, string)> SampleOrder = [("ServerAction:CleanupOrders", "CleanupOrders"), ("ServerAction:ReadOnly", "ReadOnly"), ("Process:ReviewOrder", "ReviewOrder"), ("ServerAction:SaveOrder", "SaveOrder"), ("ServerAction:Customer_Update", "Customer_Update")];

    static int SelfTest(bool show)
    {
        var fails = SelfTestData();
        fails.AddRange(SelfTestWiring(show));
        fails.AddRange(SelfTestCheck());
        fails.AddRange(ScreenTranslator.SelfTest(show).Select(f => "translator: " + f)); // the screen rules, on their own samples
        if (fails.Count == 0) { Console.WriteLine("ok"); return 0; }
        foreach (var f in fails) Console.WriteLine("FAIL " + f);
        return 1;
    }

    // migrate check over two small inspects: a retyped output and a missing input fail, a new input warns, a lost role and Anonymous
    // fail, a missing block input warns, the stubs are counted, a module-qualified type is the same type, a wrapper is not compared.
    static List<string> SelfTestCheck()
    {
        var fails = new List<string>();
        void Expect(bool ok, string what) { if (!ok) fails.Add("check: " + what); }
        var oldI = JsonNode.Parse("""
            { "name": "OrdersWeb", "entities": [{ "name": "Order" }],
              "serverActions": [
                { "name": "SaveOrder", "inputs": [{ "name": "Id", "type": "Order Identifier" }, { "name": "Note", "type": "Text" }], "outputs": [{ "name": "Ok", "type": "Boolean" }] },
                { "name": "Total", "inputs": [{ "name": "Id", "type": "Order Identifier" }], "outputs": [{ "name": "Sum", "type": "Decimal" }] },
                { "name": "Order_Create", "inputs": [{ "name": "Source", "type": "Order" }], "outputs": [] } ],
              "screens": [ { "name": "Orders", "inputs": ["Filter"], "roles": ["OrderManager"] }, { "name": "Home", "inputs": [], "roles": ["Registered"] },
                { "name": "Audit", "inputs": ["Day"], "roles": ["Auditor", "OrderManager"] } ],
              "blocks": [ { "name": "Card", "inputs": ["Title", "Body"] } ] }
            """)!;
        var newI = JsonNode.Parse("""
            { "name": "OrdersApp",
              "serverActions": [
                { "name": "SaveOrder", "inputs": [{ "name": "Id", "type": "OrdersWeb.Order Identifier" }, { "name": "Note", "type": "Text" }, { "name": "SessionKey", "type": "Text" }], "outputs": [{ "name": "Ok", "type": "Boolean" }] },
                { "name": "Total", "inputs": [], "outputs": [{ "name": "Sum", "type": "Integer" }] } ],
              "screens": [ { "name": "Orders", "inputs": ["Filter"], "roles": ["Registered"] }, { "name": "Home", "inputs": [], "roles": ["Anonymous", "Registered"] },
                { "name": "Audit", "inputs": [], "roles": ["Auditor"] } ],
              "blocks": [ { "name": "Card", "inputs": ["Title"] } ] }
            """)!;
        var validation = JsonNode.Parse("""{ "errorCount": 0, "warningCount": 2 }""")!;
        var r = Check(oldI, newI, validation, new Dictionary<string, string> { ["Screen:Orders"] = "action Save\n  comment TODO migrate: x\n  comment TODO migrate: y\n", ["Screen:Home"] = "fine" });
        JsonObject C(string name) => r["checks"]!.AsArray().OfType<JsonObject>().First(c => S(c, "check") == name);
        string D(string name) => string.Join(" | ", Strings(C(name), "details"));
        Expect(S(C("validation"), "status") == "warn", "validation: warnings warn");
        Expect(S(C("signatures"), "status") == "fail" && D("signatures").Contains("Total: input Id is missing") && D("signatures").Contains("Total: output Sum was Decimal, is Integer")
            && D("signatures").Contains("SaveOrder: new input SessionKey") && !D("signatures").Contains("SaveOrder: input Id") && !D("signatures").Contains("Order_Create"),
            "signatures: " + D("signatures"));
        Expect(S(C("screens"), "status") == "fail" && D("screens").Contains("Orders: every signed-in user can open it now") && D("screens").Contains("Home: Anonymous can open it now")
            && D("screens").Contains("Audit: input Day is missing") && D("screens").Contains("Audit: role OrderManager can no longer open it") && !D("screens").Contains("Orders: role OrderManager"),
            "screens: " + D("screens"));
        Expect(S(C("blocks"), "status") == "warn" && D("blocks").Contains("Card: input Body is missing"), "blocks: " + D("blocks"));
        Expect(S(C("todos"), "status") == "warn" && D("todos") == "Screen:Orders: 2", "todos: " + D("todos"));
        Expect(B(r, "ok") == false, "ok is false when a check fails");
        return fails;
    }

    // The pure decisions over the sample: data and logic (as before), session variables, other content, layout, style sheets, status.
    static List<string> SelfTestData()
    {
        var fails = new List<string>();
        void Check(bool ok, string what) { if (!ok) fails.Add(what); }
        var inspect = JsonNode.Parse(SampleInspect)!;
        var (map, headerNotes) = ProducerMap(JsonNode.Parse(SampleHeader)!, "OrdersWeb");
        Check(map.Count == 2 && map["Producer"] == "/p/Producer.oml" && headerNotes.Count == 1, "producer map: two modules, one note for the broken file");
        Check(ProducerMap(JsonNode.Parse("""{ "path": "/p/A.oml", "info": { "Name": "A" } }""")!, "X").map.ContainsKey("A"), "producer map: one file prints an object");
        Check(IsTraditional(inspect) && !IsTraditional(JsonNode.Parse("""{ "kind": "CrossDevice" }""")!), "traditional kind");

        var first = Build(inspect, "/p/OrdersWeb.oml", "OrdersNew", "/out", map, []);
        Check(first.Logic.Select(l => l.name).Order().SequenceEqual(["CleanupOrders", "Customer_Update", "ReadOnly", "ReviewOrder", "SaveOrder"]), "logic to read: copied actions and processes");
        var notes = new List<string>();
        var calls = FindCalls(JsonNode.Parse(SampleBatch)!, SampleOrder, first.EntityNames, notes);
        Check(calls.Count == 5 && notes.Count == 0, "calls found: Delete, two CreateOrUpdate, two Update");
        Check(calls.Any(c => c.Path == "CleanupOrders/Remove" && c.Entity == "Order" && c.Op == "Delete") && calls.Count(c => c.Op == "CreateOrUpdate" && c.Element == "SaveOrder") == 2 && calls.Count(c => c.Path.StartsWith("ReadOnly")) == 1, "calls: paths and operations; a read and a user action are not entity writes");
        Check(FindCalls(JsonNode.Parse("""{ "results": [ { "step": 1, "exitCode": 2, "output": { "error": "x" } } ] }""")!, [("A", "A")], ["Order"], notes).Count == 0 && notes.Count == 1, "a tree that failed is noted");

        var r = Build(inspect, "/p/OrdersWeb.oml", "OrdersNew", "/out", map, calls);
        string D(string kind, string name) => Rows(r.Plan, "elements").FirstOrDefault(e => S(e, "kind") == kind && S(e, "name") == name) is { } e ? S(e, "decision")! : "(absent)";
        Check(D("entity", "Customer") == "stay" && D("entity", "Order") == "stay" && D("entity", "Note") == "stay" && D("staticEntity", "OrderStatus") == "stay" && D("role", "OrderManager") == "stay", "entities, static entity and role stay");
        Check(Rows(r.Plan, "elements").Count(e => S(e, "kind") == "entity") == 3, "a static entity is not also an entity");
        Check(D("serverAction", "SaveOrder") == "copy" && D("structure", "OrderDto") == "copy" && D("exception", "InvalidOrder") == "copy" && D("siteProperty", "MaxItems") == "copy" && D("timer", "NightlyCleanup") == "copy" && D("process", "ReviewOrder") == "copy", "logic and data copy");
        Check(D("serverAction", "Customer_Update") == "stay", "an existing wrapper that is needed is not copied");
        Check(D("screen", "Orders") == "(absent)" && D("sessionVariable", "CurrentCustomerId") == "manual" && D("sessionVariable", "ThemeName") == "manual", "screens are planned by status (plan.json screens), not as elements; without screen use a session variable is manual");
        Check(r.Counts["client"] == 0 && r.Counts["manual"] == 7 && r.Counts["copy"] == 9 && r.Others.Single(o => o.Name == "LegacyApi").Decision == "stay", "counts: six session variables and the entry point manual, TaxService copied, the exposed LegacyApi stays");
        var w = Rows(r.Plan, "wrappers");
        Check(w.Length == 3 && r.WrapperCreate == 2 && r.WrapperExists == 1, "wrappers: Order_CreateOrUpdate and Order_Delete created, Customer_Update exists; Note has none");
        Check(w.Any(x => S(x, "name") == "Customer_Update" && S(x, "decision") == "exists") && w.Any(x => S(x, "name") == "Order_Delete" && S(x, "decision") == "create" && S(x, "entityAction") == "DeleteOrder") && w.All(x => S(x, "kind") == "wrapper"), "plan wrappers");
        var rf = Rows(r.Plan, "references");
        string RD(string m) => rf.First(x => S(x, "module") == m) is { } x ? S(x, "decision")! : "";
        Check(RD("Producer") == "add" && RD("Missing") == "unresolved" && RD("System") == "builtin", "reference decisions");
        Check(Strings(rf.First(x => S(x, "module") == "Producer"), "elements").SequenceEqual(["GetCustomerById"]) && Strings(rf.First(x => S(x, "module") == "Producer"), "skippedUi").SequenceEqual(["CustomerCard"]), "the web block is left out of the producer's elements");
        Check(r.Notes.Any(n => n.Contains("Missing") && n.Contains("--in")) && r.Notes.Any(n => n.Contains("CurrentCustomerId")) && r.Notes.Any(n => n.Contains("NightlyCleanup") && n.Contains("both modules")) && r.Notes.Any(n => n.Contains("service module")) && r.Notes.Any(n => n.Contains("Customer_Update") && n.Contains("already exist")) && r.Notes.Any(n => n.Contains("Wrappers are made for the entity actions")) && r.Notes.Any(n => n.Contains("read only") && n.Contains("gains 2 public wrapper")), "notes");

        var prep = r.Prepare;
        Check(prep is not null && Names(prep, "entities").SequenceEqual(["Customer", "Note"]) && Names(prep, "staticEntities").SequenceEqual(["OrderStatus"]) && Names(prep, "roles").SequenceEqual(["OrderManager"]), "prepare: the private entities, static entity and role are made public");
        Check(prep is not null && !prep.ToJsonString().Contains("exposeReadOnly"), "prepare never sets exposeReadOnly");
        var acts = Rows(prep, "serverActions");
        Check(acts.Length == 3 && acts.Count(a => S(a, "name") == "Customer_Update" && Rows(a, "flow").Length == 0 && B(a, "public") == true) == 1 && Names(prep, "serverActions").Contains("Order_CreateOrUpdate") && Names(prep, "serverActions").Contains("Order_Delete"), "prepare: two new wrappers and the private existing one made public");
        var cu = acts.First(a => S(a, "name") == "Order_CreateOrUpdate");
        Check(B(cu, "public") == true && Rows(cu, "inputs")[0]["name"]!.ToString() == "Source" && Rows(cu, "inputs")[0]["type"]!.ToString() == "Order" && Rows(cu, "outputs")[0]["name"]!.ToString() == "Id" && Rows(cu, "outputs")[0]["type"]!.ToString() == "Order Identifier" && Rows(cu, "flow")[0]["action"]!.ToString() == "CreateOrUpdateOrder" && Rows(cu, "flow")[0]["args"]!["Source"]!.ToString() == "Source", "wrapper Order_CreateOrUpdate has the signature of the entity action");
        var del = acts.First(a => S(a, "name") == "Order_Delete");
        Check(Rows(del, "inputs")[0]["name"]!.ToString() == "Id" && Rows(del, "inputs")[0]["type"]!.ToString() == "Order Identifier" && Rows(del, "outputs").Length == 0 && Rows(del, "flow")[0]["action"]!.ToString() == "DeleteOrder", "wrapper Order_Delete");
        Check(r.Prepared == Path.Combine("/out", "OrdersWeb.prepared.oml"), "prepared file");

        var refs = Rows(r.NewModule, "references");
        Check(refs.Length == 2 && S(refs[0], "producer") == r.Prepared && S(refs[1], "producer") == Path.GetFullPath("/p/Producer.oml"), "new module: the prepared old module first, then the producer");
        var fromOld = Strings(refs[0], "elements");
        Check(fromOld.Order().SequenceEqual(new[] { "Customer", "Order", "Note", "OrderStatus", "OrderManager", "Customer_Update", "Order_CreateOrUpdate", "Order_Delete" }.Order()), "new module: entities, static entity, role and the three wrappers from the old module");
        var copied = Strings(Rows(r.NewModule, "copy")[0], "elements");
        Check(copied.Order().SequenceEqual(new[] { "OrderDto", "InvalidOrder", "MaxItems", "SaveOrder", "CleanupOrders", "ReadOnly", "NightlyCleanup", "ReviewOrder", "TaxService" }.Order()), "new module: copy lists the copied elements only");
        var edits = Rows(r.Source, "edits");
        Check(edits.Length == 4 && edits.All(e => S(e, "op") == "repoint") && r.Source!.Select(kv => kv.Key).SequenceEqual(["edits"]), "2-source.json: only edits, one repoint per call (the wrapper's own call is left alone)");
        Check(edits.Any(e => S(e, "element") == "CleanupOrders/Remove" && S(e, "action") == "Order_Delete") && edits.Any(e => S(e, "element") == "SaveOrder/Save" && S(e, "action") == "Order_CreateOrUpdate") && edits.Any(e => S(e, "element") == "SaveOrder/SaveAgain") && edits.All(e => !S(e, "action")!.Contains('.')), "repoint edits: node path and the plain wrapper name");
        Check(r.MigrateSource == Path.Combine("/out", "OrdersWeb.migrate-source.oml") && S(Rows(r.NewModule, "copy")[0], "from") == r.MigrateSource && S(r.Plan, "migrateSource") == r.MigrateSource, "the copy is from the working copy, named in the plan");
        Check(r.NewModule.Select(kv => kv.Key).SequenceEqual(["references", "copy"]), "3-new-module.json: references, then copy, no edits");
        var webMail = (JsonObject)inspect.DeepClone(); ((JsonObject)((JsonArray)webMail["otherContent"]!)[1]!)["kind"] = "WebEmail";
        Check(Build(webMail, "/p/OrdersWeb.oml", "OrdersNew", "/out", map, []).Screens.Any(x => x.Kind == "Email" && x.Query == "WebEmail:OrderMail"), "an email is queried by the kind word inspect printed for it");
        Check(r.Screens.Count == 4 && r.Screens.Any(s => s.Name == "OrderDetail" && s.Kind == "Screen" && s.Inputs.SequenceEqual(["OrderId"]) && s.File == "screens/OrderDetail.txt" && s.Export == "screens/OrderDetail.json") && r.Screens.Any(s => s.Name == "OrderCard" && s.Kind == "Block") && r.Screens.Any(s => s.Name == "OrderMail" && s.Kind == "Email" && s.Query == "Email:OrderMail"), "screens, blocks and the email of otherContent are elements to export");
        var next0 = Next(r, "/p/OrdersWeb.oml", "OrdersNew", "/out");
        var exposedCopy = Build(inspect, "/p/OrdersWeb.oml", "OrdersNew", "/out", map, calls, Extra.None with { CopyExposed = true });
        Check(exposedCopy.Others.Single(o => o.Name == "LegacyApi").Decision == "copy" && exposedCopy.ExposedKept.Count == 0 && !Next(exposedCopy, "/p/OrdersWeb.oml", "OrdersNew", "/out")[0].Contains("Ask the person"), "--copy-exposed: the exposed SOAP service is copied and nothing is asked");
        Check(next0.Count == 11 && next0[0].Contains("Ask the person") && next0[0].Contains("LegacyApi") && next0[0].Contains("--copy-exposed") && next0[3].Contains(Path.Combine("/out", "OrdersWeb.prepared.oml")) && next0[3].Contains("2-source.json") && next0[3].Contains("never install it") && next0[4].Contains("<a Reactive template module") && next0[5].Contains("Manage Dependencies") && next0[5].Contains("Missing (") && next0[6].Contains("3-new-module.json") && next0[8].Contains("4-screens.md") && next0[9].Contains("--plan") && next0[10].Contains("migrate check"), "next without screens spec: eleven steps (check last), the exposed-API question first, the missing reference before the copy, the working copy third, the template placeholder");

        // No write at all: no wrapper, no edits.
        var none = Build(inspect, "/p/OrdersWeb.oml", "OrdersNew", "/out", map, []);
        Check(none.WrapperNames.Count == 0 && none.Source is null && S(Rows(none.NewModule, "copy")[0], "from") == none.Prepared && Rows(none.Prepare, "serverActions").Length == 0 && none.Notes.Any(n => n.Contains("gains no wrapper")), "no entity write: no wrapper, no 2-source.json, the copy is from the old module");

        var newer = JsonNode.Parse("""
        { "name": "OrdersNew", "kind": "CrossDevice", "screens": [ { "name": "Orders" } ], "blocks": [], "structures": [ { "name": "OrderDto" } ],
          "serverActions": [ { "name": "SaveOrder" } ], "timers": [ { "name": "NightlyCleanup" } ], "processes": [], "roles": [],
          "references": [ { "module": "OrdersWeb", "elements": [ { "name": "OrderManager", "kind": "IRole" } ] } ] }
        """)!;
        var s = Compare(inspect, newer);
        Check(Strings(s["screens"], "both").SequenceEqual(["Orders"]) && Strings(s["screens"], "onlyOld").SequenceEqual(["OrderDetail"]), "status: screens");
        Check(Strings(s["serverActions"], "onlyOld").Order().SequenceEqual(["CleanupOrders", "ReadOnly"]) && Strings(s["serverActions"], "both").SequenceEqual(["SaveOrder"]), "status: server actions leave out the wrappers");
        Check(Strings(s["duplicates"], "timers").SequenceEqual(["NightlyCleanup"]) && Strings(s["roles"], "both").SequenceEqual(["OrderManager"]), "status: duplicates and referenced role");
        Check(Strings(s["wrappers"], "presentInOld").SequenceEqual(["Customer_Update"]) && S(s, "progress")!.StartsWith("screens 1 of 2; blocks 0 of 1;"), "status: wrappers and progress");

        // Server functions the screens call in the browser: copied as client functions when their flow can run there (with the functions they
        // call, exported in a later round); one with a query, a session read, a call of one not copied, or no export is refused with why.
        var fnExports = JsonNode.Parse("""
        { "Twice": { "name": "Twice", "function": true, "inputs": [ { "name": "N", "type": "Integer", "mandatory": true } ], "outputs": [ { "name": "R", "type": "Integer" } ],
            "flow": { "complete": true, "nodes": [ { "kind": "if", "name": "Neg", "condition": "N < 0", "true": "Zero", "false": "Do", "_path": "Twice/Neg" },
              { "kind": "assign", "name": "Zero", "assign": { "R": "0" }, "next": "End" }, { "kind": "assign", "name": "Do", "assign": { "R": "Half(N) * 4" }, "next": "End", "_path": "Twice/Do" } ] } },
          "Half": { "name": "Half", "function": true, "inputs": [ { "name": "X", "type": "Integer" } ], "outputs": [ { "name": "Y", "type": "Integer" } ], "flow": { "complete": true, "nodes": [ { "kind": "assign", "name": "A", "assign": { "Y": "X / 2" } } ] } },
          "AppName": { "name": "AppName", "function": true, "flow": { "complete": true, "nodes": [ { "kind": "aggregate", "name": "GetApp", "sources": ["Application"] } ] } },
          "Who": { "name": "Who", "function": true, "flow": { "complete": true, "nodes": [ { "kind": "assign", "name": "A", "assign": { "R": "Session.UserName" } } ] } },
          "Outer": { "name": "Outer", "function": true, "flow": { "complete": true, "nodes": [ { "kind": "assign", "name": "A", "assign": { "R": "AppName() + \"!\"" } } ] } },
          "Ghost": { "error": "no server action named Ghost" } }
        """)!.AsObject();
        var rounds = new List<string>();
        var (fnCopies, fnRefused) = ClientFunctions(["Twice", "Who", "Outer", "Ghost"], names =>
        { rounds.Add(string.Join(",", names)); return names.ToDictionary(n => n, n => (JsonObject)fnExports[n]!.DeepClone(), StringComparer.OrdinalIgnoreCase); },
            new HashSet<string>(["Twice", "Half", "AppName", "Who", "Outer", "Ghost"], StringComparer.OrdinalIgnoreCase));
        Check(fnCopies.Select(c => S(c, "name")).SequenceEqual(["Half", "Twice"]) && rounds.SequenceEqual(["Twice,Who,Outer,Ghost", "Half,AppName"])
            && fnCopies[1]["function"]!.GetValue<bool>() && S(fnCopies[1]["inputs"]![0]!, "name") == "N" && S(fnCopies[1]["outputs"]![0]!, "name") == "R"
            && S(fnCopies[1]["flow"]![2]!["assign"]!, "R") == "Half(N) * 4" && fnCopies[1]["flow"]![0]!["_path"] is null && fnCopies[1]["locals"] is null,
            $"server functions: Twice and the Half it calls are client functions with their inputs, outputs and flow: {string.Join(" ", fnCopies.Select(c => c.ToJsonString()))}");
        Check(fnRefused.Count == 4 && fnRefused["AppName"].Contains("aggregate node GetApp runs on the server") && fnRefused["Who"].Contains("reads Session.UserName")
            && fnRefused["Outer"].StartsWith("it calls AppName, which is not copied") && fnRefused["Ghost"].Contains("no server action named Ghost"),
            $"server functions: a query, a session read, a call of one not copied and a failed export are refused with why: {string.Join(" | ", fnRefused.Select(kv => kv.Key + ": " + kv.Value))}");
        Check(ServerFunctionNames(JsonNode.Parse("""{ "serverActions": [ { "name": "A", "function": true }, { "name": "B" } ] }""")).SequenceEqual(["A"])
            && ExportActions(JsonNode.Parse("""{ "name": "A" }"""), ["A"])["A"]["name"] is not null && ExportActions(null, ["A", "B"])["B"]["error"] is not null,
            "server functions: names from inspect; export answers by name, a missing one is an error");

        // UI flow OnException actions: translated into the spec's onException; the template's own handler of a flow stays (a note says what the
        // old one did); one that navigates to a screen the new module will not have is a note; a flow without one is left alone.
        var onexInspect = JsonNode.Parse("""{ "uiFlows": [ { "name": "Common", "onException": "OnException" }, { "name": "MainFlow", "onException": "HandleMain" }, { "name": "Admin", "onException": "OnAdminError" }, { "name": "Plain" } ] }""");
        var onexTemplate = JsonNode.Parse("""{ "uiFlows": [ { "name": "Common", "onException": "OnException" } ], "screens": [ { "name": "Login" } ] }""");
        var onexExports = JsonNode.Parse("""
        { "Common/OnException": { "kind": "OnException", "name": "OnException", "uiFlow": "Common", "moduleKind": "Traditional", "flow": { "complete": true, "nodes": [
            { "kind": "exceptionHandler", "name": "All", "exception": "AllExceptions", "next": "Go" }, { "kind": "destination", "name": "Go", "screen": "Login" } ] } },
          "MainFlow/HandleMain": { "kind": "OnException", "name": "HandleMain", "uiFlow": "MainFlow", "moduleKind": "Traditional", "locals": [ { "name": "Msg", "type": "Text" } ], "flow": { "complete": true, "nodes": [
            { "kind": "exceptionHandler", "name": "All", "exception": "AllExceptions", "abortTransaction": true, "logError": true, "next": "Tell" },
            { "kind": "execute", "name": "Tell", "action": "RichWidgets.Feedback_Message", "_ref": "RichWidgets.Feedback_Message", "args": { "Message": "AllExceptions.ExceptionMessage", "Type": "Entities.MessageType.Error" }, "next": "Go" },
            { "kind": "destination", "name": "Go", "screen": "Oops" } ] } },
          "Admin/OnAdminError": { "kind": "OnException", "name": "OnAdminError", "uiFlow": "Admin", "moduleKind": "Traditional", "flow": { "complete": true, "nodes": [
            { "kind": "exceptionHandler", "name": "Sec", "exception": "SecurityException", "next": "Away" }, { "kind": "destination", "name": "Away", "screen": "Gone" } ] } } }
        """)!.AsObject();
        var onexNotes = new List<string>();
        var asked = new List<string>();
        var onexItems = new List<Item> { new(new Screen("Oops", "Screen", "MainFlow", [], [], "Oops.json"), "translated", [], JsonNode.Parse("""{ "name": "Oops" }""")!.AsObject(), [], [], null) };
        var onex = OnExceptionHandlers(onexNotes, onexInspect, onexTemplate, onexItems, paths => { asked.AddRange(paths); return paths.ToDictionary(p => p, p => (JsonObject)onexExports[p]!.DeepClone()); },
            new ScreenTranslator.Context("Old", new Dictionary<string, string>(), new HashSet<string>(), new HashSet<string>(), null, []));
        Check(onex.Count == 1 && S(onex[0], "name") == "HandleMain" && S(onex[0], "flow") == "MainFlow" && S(onex[0]["locals"]![0]!, "name") == "Msg"
            && S(onex[0]["nodes"]![0]!, "kind") == "exceptionHandler" && onex[0]["nodes"]![0]!["abortTransaction"] is null && onex[0]["nodes"]![0]!["logError"]!.GetValue<bool>()
            && S(onex[0]["nodes"]![1]!, "kind") == "message" && S(onex[0]["nodes"]![1]!, "type") == "error" && S(onex[0]["nodes"]![2]!, "screen") == "Oops"
            && asked.SequenceEqual(["Common/OnException", "MainFlow/HandleMain", "Admin/OnAdminError"]),
            $"OnException: MainFlow's handler is an onException entry (Feedback_Message a message node, abort transaction gone, Log Error kept): {string.Join(" ", onex.Select(h => h.ToJsonString()))}");
        Check(onexNotes.Count == 2 && onexNotes[0].StartsWith("The template's Common/OnException stays") && onexNotes[0].Contains("did: AllExceptions: navigate to Login")
            && onexNotes[1].Contains("Admin/OnAdminError navigates to Gone, which the new module will not have") && onexNotes[1].Contains("SecurityException: navigate to Gone"),
            $"OnException: the template's handler stays and a missing screen keeps one out, each with a note: [{string.Join(" | ", onexNotes)}]");

        return fails;
    }

    // ---------- the wiring: the whole plan over canned answers of the child processes ----------

    // Exports in the form Export.cs writes (table at the top of MigrateScreens.cs): two screens, a block, and an email whose export fails.
    const string SampleExportOrders = """
    { "kind": "Screen", "name": "Orders", "flow": "MainFlow", "path": "MainFlow/Orders", "moduleKind": "Traditional", "title": "\"Orders\"",
      "locals": [ { "name": "Search", "type": "Text", "default": "\"\"" } ], "access": { "anonymous": false, "registered": true },
      "styleSheet": ".orders { margin: 0; }", "javaScript": "", "preparation": { "complete": true },
      "actions": [ { "name": "Save", "_path": "MainFlow/Orders/Save", "flow": { "nodes": [ { "kind": "execute", "name": "Save", "action": "CreateOrUpdateOrder", "_ref": "CreateOrUpdateOrder", "_path": "MainFlow/Orders/Save/Save" } ], "complete": true } } ],
      "widgets": [ { "kind": "WebContainer", "type": "Container", "name": "OrdersTable", "path": "MainFlow/Orders/OrdersTable", "props": { "style": "\"card\"" },
        "children": [ { "kind": "WebButton", "type": "Button", "name": "SaveButton", "path": "MainFlow/Orders/OrdersTable/SaveButton", "props": { "label": "\"Save\"", "onClick": "Save" } } ] } ],
      "notes": [ "MainFlow/Orders/OrdersTable/SaveButton: the method of its On Click (Submit, Ajax Submit or Navigate) could not be read" ] }
    """;
    const string SampleExportDetail = """
    { "kind": "Screen", "name": "OrderDetail", "flow": "MainFlow", "path": "MainFlow/OrderDetail", "moduleKind": "Traditional", "title": "\"Order\"",
      "inputs": [ { "name": "OrderId", "type": "Order Identifier", "mandatory": true, "default": "" } ], "access": { "anonymous": false, "registered": false, "roles": ["OrderManager"] },
      "styleSheet": "", "javaScript": "",
      "widgets": [ { "kind": "WebExpression", "type": "Expression", "name": "Title", "path": "MainFlow/OrderDetail/Title", "props": { "value": "Session.LastSearch" } } ], "notes": [ "could not read properties of MainFlow/OrderDetail: a property could not be read" ] }
    """;
    const string SampleExportCard = """
    { "kind": "Block", "name": "OrderCard", "flow": "Common", "path": "Common/OrderCard", "moduleKind": "Traditional", "inputs": [ { "name": "Order", "type": "Order", "mandatory": true, "default": "" } ],
      "access": { "anonymous": true, "registered": true }, "styleSheet": ".card { padding: 4px; }", "javaScript": "" }
    """;
    const string SampleExportMail = """
    { "element": "Email:OrderMail", "error": "no e-mail named OrderMail" }
    """;

    // What `oml describe` prints for the copied actions that read a session variable: SaveOrder reads Session.CurrentCustomerId; CleanupOrders only
    // names Session.ThemeNameX and Session.Other.ThemeName, which are not the variable ThemeName.
    static readonly Dictionary<string, string> SampleDescribe = new()
    {
        ["ServerAction:SaveOrder"] = "ServerAction SaveOrder\n  assign Customer = Session.CurrentCustomerId\n",
        ["ServerAction:CleanupOrders"] = "ServerAction CleanupOrders\n  assign X = Session.ThemeNameX + Session.Other.ThemeName\n",
        ["ServerAction:ReadOnly"] = "ServerAction ReadOnly\n",
    };

    const string SampleTemplate = """
    { "name": "ReactiveStarter", "kind": "CrossDevice", "blocks": [ { "name": "Footer", "flow": "Common" }, { "name": "LayoutSideMenu", "flow": "Layouts" }, { "name": "LayoutTopMenu", "flow": "Layouts" } ] }
    """;
    const string SampleLayoutExport = """
    { "kind": "Block", "name": "LayoutTopMenu", "moduleKind": "Reactive", "widgets": [ { "kind": "Container", "type": "Container", "name": "Wrap", "children": [
        { "kind": "MobilePlaceholderWidget", "type": "Placeholder", "name": "Header" }, { "kind": "If", "type": "If", "name": "Cond", "true": [ { "kind": "Placeholder", "type": "Placeholder", "name": "Content" } ] } ] } ] }
    """;
    const string SampleCss = """
    [ { "element": "Common/Theme", "kind": "WebTheme", "styleSheet": ".a { color: red; }" },
      { "element": "MainFlow/Orders", "kind": "Screen", "styleSheet": ".orders { margin: 0; }" },
      { "element": "Common/OrderCard", "kind": "Block", "styleSheet": ".card { padding: 4px; }" },
      { "element": "Common.Theme", "kind": "WebTheme", "styleSheet": ".b {}" } ]
    """;

    static readonly Dictionary<string, string> SampleExports = new()
    {
        ["Screen:Orders"] = SampleExportOrders, ["Screen:OrderDetail"] = SampleExportDetail, ["Block:OrderCard"] = SampleExportCard, ["Email:OrderMail"] = SampleExportMail,
    };

    // The child processes of oml, as the plan calls them.
    static JsonNode Canned(string[] args, string? stdin, string template)
    {
        JsonNode Many(IEnumerable<string> names, Func<string, JsonNode> one) { var l = names.Select(one).ToList(); return l.Count == 1 ? l[0] : new JsonArray(l.ToArray()); }
        switch (args[0])
        {
            case "inspect" when args[1] == template: return JsonNode.Parse(SampleTemplate)!;
            case "export": return JsonNode.Parse(SampleLayoutExport)!;
            case "batch":
                var steps = (JsonArray)JsonNode.Parse(stdin!)!["steps"]!;
                var results = steps.Select((st, i) =>
                {
                    var a = ((JsonArray)st!).Select(x => x!.ToString()).ToArray();
                    return (JsonNode?)(a[0] switch
                    {
                        "tree" => JsonNode.Parse(SampleBatch)!["results"]![SampleOrder.FindIndex(o => o.Item1 == a[2])]!.DeepClone(),
                        _ => new JsonObject { ["step"] = i + 1, ["exitCode"] = 1, ["output"] = Many(a.Skip(2), q => JsonNode.Parse(SampleExports[q])!) },
                    });
                }).ToArray();
                return new JsonObject { ["results"] = new JsonArray(results) };
            case "describe" when args.Contains("--css"): return JsonNode.Parse(SampleCss)!;
            case "describe":
                return Many(args.Skip(2).TakeWhile(a => !a.StartsWith("--")), q => new JsonObject { ["element"] = q, ["text"] = SampleDescribe.TryGetValue(q, out var t) ? t : "description of " + q });
            default: throw new InvalidOperationException("unexpected oml call " + string.Join(' ', args));
        }
    }

    static List<string> SelfTestWiring(bool show)
    {
        var fails = new List<string>();
        void Check(bool ok, string what) { if (!ok) fails.Add(what); }
        var inspect = JsonNode.Parse(SampleInspect)!;
        var (map, headerNotes) = ProducerMap(JsonNode.Parse(SampleHeader)!, "OrdersWeb");
        const string tpl = "/p/Starter.oml";
        JsonNode Run(string[] a, string? i) => Canned(a, i, tpl);

        // Helper checks first.
        Check(IsBasicType("Long Integer") && IsBasicType("Order Identifier") && IsBasicType("phone number") && !IsBasicType("Order List") && !IsBasicType("Order"), "basic types");
        Check(SensitiveName("ApiToken") && SensitiveName("user_password") && SensitiveName("UserPIN") && SensitiveName("CreditCard") && SensitiveName("Api_Key") && !SensitiveName("ShippingMethod") && !SensitiveName("Discard") && !SensitiveName("ThemeName"), "sensitive names are matched by word");
        Check(PickLayout(JsonNode.Parse(SampleTemplate)!) == "LayoutTopMenu" && PickLayout(JsonNode.Parse("""{ "blocks": [ { "name": "LayoutSideMenu" }, { "name": "LayoutBlank" } ] }""")!) == "LayoutSideMenu"
            && PickLayout(JsonNode.Parse("""{ "blocks": [ { "name": "Menu" }, { "name": "LayoutZ" }, { "name": "LayoutBlank" } ] }""")!) == "LayoutZ" && PickLayout(JsonNode.Parse("""{ "blocks": [ { "name": "Menu" } ] }""")!) is null, "layout: top menu, then side menu, then the first, else none");
        Check(Placeholders(JsonNode.Parse(SampleLayoutExport)).SequenceEqual(["Header", "Content"]) && Placeholders(null).Count == 0, "placeholders are read from the widget tree, in an If too");
        Check(DecideOther("SOAPClient").decision == "copy" && DecideOther("ISOAPClient").decision == "copy" && DecideOther("WebReference").decision == "copy" && DecideOther("Integration").decision == "copy" && DecideOther("Email").decision == "translate" && DecideOther("IEmail").decision == "translate" && DecideOther("EntryPoint").decision == "manual" && DecideOther("WebService").decision == "stay" && DecideOther("OnExceptionAction").decision == "translate", "other content by kind name, with or without the leading I");
        var texts = SessionUse(SampleDescribe, ["ThemeName", "CurrentCustomerId"]);
        Check(texts.Count == 1 && texts["CurrentCustomerId"].SequenceEqual(["SaveOrder"]), "Session.X is matched as a whole identifier: ThemeNameX and Session.Other.ThemeName are not ThemeName");
        var shapes = ExportChunks(Enumerable.Range(0, 60).Select(i => new Screen("S" + i, "Screen", "", [], [], $"screens/S{i}.txt")));
        Check(shapes.Count == 3 && shapes[0].Count == 25 && shapes[2].Count == 10, "export steps hold at most 25 elements");
        var two = new List<List<Screen>> { new() { new Screen("A", "Screen", "", [], [], "screens/A.txt"), new Screen("B", "Block", "", [], [], "screens/B.txt") }, new() { new Screen("C", "Screen", "", [], [], "screens/C.txt") } };
        var parsed = ParseExports(JsonNode.Parse("""{ "results": [ { "step": 1, "exitCode": 1, "output": [ { "kind": "Screen", "name": "A" }, { "element": "Block:B", "error": "nope" } ] }, { "step": 2, "exitCode": 0, "output": { "kind": "Screen", "name": "C" } } ] }"""), two);
        Check(parsed["screens/A.txt"]["name"]?.ToString() == "A" && parsed["screens/B.txt"]["error"]?.ToString() == "nope" && parsed["screens/C.txt"]["kind"]?.ToString() == "Screen", "export results: array, error entry, single object");
        Check(ParseExports(JsonNode.Parse("""{ "results": [ { "step": 1, "exitCode": 2, "output": { "error": "cannot load" } } ] }"""), two)["screens/B.txt"]["error"]?.ToString() == "cannot load", "a failed export step gives every element its error");

        // The plan with the real translator (the stub in this build, the translator when it is built in): the wiring, not the statuses.
        var real = Assemble("/p/OrdersWeb.oml", "OrdersNew", "/out", inspect, map, headerNotes, tpl, Run);
        var names = real.Files.Select(f => f.file).ToList();
        Check(new[] { "plan.json", "1-prepare-old.json", "2-source.json", "3-new-module.json", "4-screens.md", "screens/Orders.txt", "screens/Orders.json", "screens/OrderDetail.json", "screens/OrderCard.json", "screens/OrderMail.txt", "css/README.md", "css/Common_Theme.css", "css/MainFlow_Orders.css", "css/Common_Theme-2.css" }.All(names.Contains) && !names.Contains("screens/OrderMail.json") && names.Distinct().Count() == names.Count, "files: plan, specs, worklist, a description and an export per element (none for the failed export), css per sheet with unique names");
        var c = real.Context;
        Check(c.OldModule == "OrdersWeb" && c.LocalBlocks.SetEquals(["OrderCard"]) && c.Layout == "LayoutTopMenu" && c.LayoutPlaceholders.SequenceEqual(["Header", "Content"]), "context: old module, local blocks, layout and its placeholders");
        Check(c.Wrappers["CreateOrUpdateOrder"] == "Order_CreateOrUpdate" && c.Wrappers["DeleteOrder"] == "Order_Delete" && c.Wrappers["UpdateCustomer"] == "Customer_Update", "context: wrappers from the copied logic");
        Check(real.Items.Count == 4 && StatusCounts(real.Items).Values.Sum() == 4 && real.Output["coverage"]!.ToString().StartsWith("screens: ") && real.Output["coverage"]!.ToString().EndsWith("of 4"), "four elements, a coverage line");
        Check(real.Items.Single(i => i.S.Name == "OrderMail").Status == "worklist" && real.Items.Single(i => i.S.Name == "OrderMail").Todo[0].Contains("no e-mail named OrderMail"), "a failed export is a worklist item with the reason");
        Check(real.R.Notes.Any(n => n.Contains("OrderMail was not translated")) && real.R.Notes.Any(n => n.Contains("style sheet(s)") && n.Contains("css/README.md")), "notes: the failed export and the style sheets");
        var sheets = real.Files.First(f => f.file == "css/README.md").text;
        Check(sheets.Contains("Common_Theme.css") && sheets.Contains("bytes") && sheets.Contains("theme: port by hand") && sheets.Contains("travels with the translated element") && sheets.Contains("selector by selector"), "css/README.md lists each sheet with its size and says what happens");
        var planJson = JsonNode.Parse(real.Files.First(f => f.file == "plan.json").text)!;
        Check(Rows(planJson, "screens").Length == 4 && Rows(planJson, "screens").All(x => S(x, "status") is not null && x["todo"] is not null) && S(planJson, "coverage") == real.Output["coverage"]!.ToString() && S(P(planJson, "layout"), "block") == "LayoutTopMenu", "plan.json: one entry per element with status and todo count, the coverage, the layout");

        // Without a template: no layout, a note, and the placeholder in `next`.
        var bare = Assemble("/p/OrdersWeb.oml", "OrdersNew", "/out", inspect, map, headerNotes, null, Run);
        Check(bare.Context.Layout is null && bare.Context.LayoutPlaceholders.Count == 0 && bare.R.Notes.Any(n => n.Contains("without a layout block") && n.Contains("--template")) && Next(bare.R, "/p/OrdersWeb.oml", "OrdersNew", "/out").Any(x => x.Contains("<a Reactive template module")), "no --template: no layout, a note that says how to get one");
        Check(real.Output["next"]!.AsArray().Any(x => x!.ToString().Contains(tpl)) && !real.Output["next"]!.AsArray().Any(x => x!.ToString().Contains("<a Reactive template module")), "--template: the next list uses its path");

        // A translator that reports what it should: statuses, entity actions, session variables, two passes.
        var seen = new List<ScreenTranslator.Context>();
        ScreenTranslator.Result Fake(JsonObject el, ScreenTranslator.Context ctx)
        {
            seen.Add(ctx);
            return S(el, "name") switch
            {
                "Orders" => new(new JsonObject { ["name"] = "Orders" }, "Screen", "translated", [], ["CreateOrUpdateOrder", "UpdateNote", "Producer.Thing"], ["ThemeName", "LastSearch", "ApiToken", "Cart"]),
                "OrderDetail" => new(new JsonObject { ["name"] = "OrderDetail" }, "Screen", "partial", ["wire the delete button", "check the title"], [], ["Session.LastSearch"]),
                _ => new(null, S(el, "kind") ?? "Block", "worklist", ["no widget of this block is known"], [], []),
            };
        }
        var o = Assemble("/p/OrdersWeb.oml", "OrdersNew", "/out", inspect, map, headerNotes, tpl, Run, Fake);
        Check(seen.Count == 6 && seen.Take(3).All(x => x.ClientVariables.Count == 0 && x.Wrappers.ContainsKey("DeleteNote")) && seen.Skip(3).All(x => x.ClientVariables.SetEquals(["ThemeName", "LastSearch", "CurrentCustomerId"])), "two passes: the first with every wrapper and no client variables, the second with the decided ones");
        Check(o.Context.Wrappers["UpdateNote"] == "Note_Update" && !o.Context.Wrappers.ContainsKey("DeleteNote") && o.Context.Wrappers["CreateOrUpdateOrder"] == "Order_CreateOrUpdate" && o.Context.Wrappers.Count == 4, "context: the wrapper of an entity action only a screen calls is added; a foreign action is ignored");
        Check(Names(o.R.Prepare, "serverActions").Contains("Note_Update") && o.R.WrapperCreate == 3 && o.R.WrapperExists == 1 && o.R.Calls.Count == 4, "1-prepare-old.json creates the wrapper for the screen, no repoint edit for it");
        Check(Strings(Rows(o.R.NewModule, "references")[0], "elements").Contains("Note_Update"), "3-new-module.json references it");
        var dec = o.R.SessionDecisions.ToDictionary(d => d.Name);
        Check(dec["ThemeName"].Decision == "client" && dec["LastSearch"].Decision == "client" && dec["ThemeName"].Default == "\"light\"" && !dec["ThemeName"].Why.Contains("not read") && dec["ThemeName"].Why.Contains("with its default") && dec["LastSearch"].Default is null && dec["LastSearch"].Why.Contains("default value is not read"), "session variables used only by screens, basic type, harmless name: client; with a default it is kept and the reason does not say it is unread, without one the reason says so");
        Check(o.R.Notes.Any(n => n.Contains("default value of a session variable is not read") && n.Contains("by hand") && n.Split("not read by this oml inspect (")[^1].Split(')')[0] is var unread && unread.Contains("LastSearch") && unread.Contains("CurrentCustomerId") && !unread.Contains("ThemeName")), "note: only the client variables without a default are to be set by hand");
        // Read (not written) by copied server logic: a client variable that reaches SaveOrder as an input; ReadOnly calls SaveOrder, so it gets the input too and passes it on.
        var srcEdits = Rows(o.R.Source, "edits");
        Check(dec["CurrentCustomerId"].Decision == "client" && dec["CurrentCustomerId"].Why.Contains("SaveOrder") && dec["CurrentCustomerId"].Why.Contains("input")
            && srcEdits.Any(e => S(e, "op") == "addVariables" && S(e, "action") == "ServerAction:SaveOrder" && S(Rows(e, "inputs")[0], "name") == "CurrentCustomerId" && S(Rows(e, "inputs")[0], "type") == "Customer Identifier")
            && srcEdits.Any(e => S(e, "op") == "addVariables" && S(e, "action") == "ServerAction:ReadOnly")
            && srcEdits.Any(e => S(e, "op") == "rewrite" && S(e, "element") == "ServerAction:SaveOrder" && S(e, "from") == "Session.CurrentCustomerId" && S(e, "to") == "CurrentCustomerId")
            && srcEdits.Any(e => S(e, "op") == "setArgs" && S(e, "element") == "ReadOnly/Other" && S(P(e, "args"), "CurrentCustomerId") == "CurrentCustomerId")
            && o.Context.SessionInputs?["SaveOrder"].Contains(("CurrentCustomerId", "CurrentCustomerId")) == true,
            $"session variable read by copied server logic: a client variable passed as an input through the callers: {o.R.Source?.ToJsonString()}");
        var written = ThreadSession("Total", "Decimal", null, ["Sum"], new Dictionary<string, string> { ["Sum"] = "ServerAction Sum\n  set Session.Total = 1\n" }, [], [("ServerAction:Sum", "Sum")]);
        Check(written.Why is not null && written.Why.Contains("writes"), "a session variable that server logic writes is not passed as an input");
        // The server store: the entity, a box structure and the accessors per variable; the user gets the key and a local loaded after Start
        // and saved after each write; the caller passes the key.
        var store = StoreSessions([new StoreVariable("Cart", "CartState", null, ["AddToCart", "CartTotal"])],
            [new ActionCall("AddToCart", "AddToCart/Check", "CartTotal")], [new SessionWrite("AddToCart", "AddToCart/Add", "Cart")],
            [("ServerAction:AddToCart", "AddToCart"), ("ServerAction:CartTotal", "CartTotal")]);
        var se = store.Edits;
        Check(Names(store.Creation, "entities").SequenceEqual(["SessionValue"]) && Names(store.Creation, "structures").SequenceEqual(["SessionBox_Cart"])
            && Names(store.Creation, "serverActions").SequenceEqual(["GetSession_Cart", "SetSession_Cart"]) && P(Rows(store.Creation, "serverActions")[0], "function")?.GetValue<bool>() == true
            && se.Any(e => S(e, "op") == "addVariables" && S(e, "action") == "ServerAction:AddToCart" && S(Rows(e, "inputs")[0], "name") == "SessionKey" && S(Rows(e, "locals")[0], "name") == "SessionCart")
            && se.Any(e => S(e, "op") == "rewrite" && S(e, "element") == "ServerAction:CartTotal" && S(e, "to") == "SessionCart")
            && se.Count(e => S(e, "op") == "insertNodes" && S(e, "action") == "ServerAction:AddToCart") == 2 && se.Any(e => S(e, "op") == "insertNodes" && S(e, "after") == "Add")
            && se.Any(e => S(e, "op") == "setArgs" && S(e, "element") == "AddToCart/Check" && S(P(e, "args"), "SessionKey") == "SessionKey")
            && store.CopyPaths.Contains("SessionValue") && store.CopyPaths.Contains("SetSession_Cart") && store.Actions.SequenceEqual(["AddToCart", "CartTotal"]),
            $"server store: entity, box, accessors, the key, the local loaded and saved, the caller passes the key: {string.Join(" ", se.Select(e => e.ToJsonString()))}");
        Check(dec["ApiToken"].Decision == "store" && dec["ApiToken"].Why.Contains("SessionValues") && dec["Cart"].Decision == "store" && dec["Unused"].Decision == "manual" && dec["Unused"].Why.Contains("no screen or block")
            && o.R.Store is not null && Names(o.R.Store.Creation, "serverActions").Contains("GetSession_ApiToken") && Names(o.R.Store.Creation, "structures").Contains("SessionBox_Cart"),
            "a sensitive name or a record that screens use goes to the server store (read through SessionValues); an unused one is manual");
        // The project configuration turns the server store and merged server calls off (migrate.serverStore, migrate.mergeServerCalls).
        var plain = Assemble("/p/OrdersWeb.oml", "OrdersNew", "/out", inspect, map, headerNotes, tpl, Run, Fake, serverStore: false, mergeServerCalls: false);
        var plainDec = plain.R.SessionDecisions.ToDictionary(d => d.Name);
        Check(plainDec["ApiToken"].Decision == "manual" && plainDec["ApiToken"].Why.Contains("migrate.serverStore") && plain.R.Store is null && plain.Context.StoreVariables is not { Count: > 0 }
            && plain.Context.ServerOutputs is null && o.Context.ServerOutputs is not null,
            "migrate.serverStore off: a stored variable stays manual and names the setting; migrate.mergeServerCalls off: the translator gets no server outputs to merge");
        Check(o.Context.ClientVariables.SetEquals(["ThemeName", "LastSearch", "CurrentCustomerId"]) && o.R.Counts["client"] == 3, "context: the client variables");
        Check(StatusCounts(o.Items).Select(kv => kv.Key + kv.Value).SequenceEqual(["partial1", "translated1", "worklist2"]) && o.Output["coverage"]!.ToString() == "screens: 1 translated, 1 partial, 2 worklist of 4", "coverage line");
        var spec = JsonNode.Parse(o.Files.First(f => f.file == "4-screens.json").text)!;
        var cv = Rows(spec, "clientVariables");
        Check(spec is JsonObject so && so.Select(kv => kv.Key).SequenceEqual(["clientVariables", "screens", "systemEvents", "clientActions"]) && cv.Length == 4 && cv.Any(c => S(c, "name") == "SessionKey") && cv.First(c => S(c, "name") == "ThemeName") is var tn && S(tn, "type") == "Text" && S(tn, "default") == "\"light\"" && cv.First(c => S(c, "name") == "LastSearch")["default"] is null && Names(spec, "screens").SequenceEqual(["Orders", "OrderDetail"]), $"4-screens.json: client variables with type and default, then the non-null specs: {spec.ToJsonString()}");
        var md = o.Files.First(f => f.file == "4-screens.md").text;
        Check(md.Contains("| Orders | Screen | translated |") && md.Contains("| OrderCard | Block | worklist |") && md.Contains("| OrderMail | Email | worklist |") && md.Contains("## OrderDetail (screen, partial)") && md.Contains("- [ ] wire the delete button") && md.Contains("`screens/OrderDetail.json`") && md.Contains("`screens/OrderDetail.txt`") && !md.Contains("## Orders ") && md.Contains("## OrderMail (email, worklist)") && md.Contains("Order_CreateOrUpdate") && md.Contains("screens: 1 translated"), "4-screens.md: summary table, a section per partial or worklist element with checkboxes and pointers");
        var pl = Rows(JsonNode.Parse(o.Files.First(f => f.file == "plan.json").text), "screens");
        Check(pl.Length == 4 && pl.Any(x => S(x, "name") == "OrderDetail" && S(x, "status") == "partial" && (int)x["todo"]! == 2), "plan.json: status and todo count per element");
        // otherContent
        Check(Strings(Rows(o.R.NewModule, "copy")[0], "elements").Contains("TaxService") && o.R.Others.Count == 4, "SOAP reference is copied by name");
        Check(o.R.Notes.Any(n => n.StartsWith("SOAPClient (TaxService)") && n.Contains("Service Center")) && o.R.Notes.Any(n => n.StartsWith("EntryPoint (Home)") && n.Contains("no entry points")) && o.R.Notes.Any(n => n.StartsWith("WebService (LegacyApi)")) && !o.R.Notes.Any(n => n.Contains("did not report otherContent")), "notes name each other element by kind");
        Check(Rows(o.R.Plan, "otherContent").Length == 4 && Rows(o.R.Plan, "otherContent").Any(x => S(x, "name") == "OrderMail" && S(x, "decision") == "translate"), "plan.json lists the other content with a decision");
        var old = JsonNode.Parse(SampleInspect)!.DeepClone(); ((JsonObject)old).Remove("otherContent");
        Check(Build(old, "/p/OrdersWeb.oml", "OrdersNew", "/out", map, []).Notes.Any(n => n.Contains("did not report otherContent")), "an older Core without otherContent: a note");
        var nx = o.Output["next"]!.AsArray().Select(x => x!.ToString()).ToList();
        Check(nx.Count == 12 && nx[0].Contains("--copy-exposed") && nx[3].Contains("2-source.json") && nx[4].Contains(tpl) && nx[5].Contains("Manage Dependencies") && nx[7].Contains("4-screens.json") && nx[7].Contains("OrdersNew.screens.oml") && nx[9].Contains("4-screens.md") && nx[10].Contains("--plan") && nx[10].Contains("OrdersNew.screens.oml") && nx[11].Contains("migrate check") && nx[11].Contains("OrdersNew.screens.oml"), "next: the template path, check last, the screens apply after the new module, status with --plan");

        // status with the plan
        var newer = JsonNode.Parse("""{ "name": "OrdersNew", "screens": [ { "name": "Orders" }, { "name": "OrderDetail" } ], "blocks": [] }""")!;
        var pr = ScreenProgress(JsonNode.Parse(o.Files.First(f => f.file == "plan.json").text)!, newer);
        Check(pr["line"]!.ToString() == "screens in the new module: translated 1 of 1; partial 1 of 1; worklist 0 of 2" && Strings(pr["byStatus"]!["worklist"], "missing").Order().SequenceEqual(["OrderCard", "OrderMail"]), "status --plan: per status how many exist in the new module");

        if (show)
        {
            foreach (var (file, text) in o.Files.Where(f => !f.file.StartsWith("screens/") && !f.file.StartsWith("css/") || f.file == "css/README.md"))
                Console.WriteLine($"--- {file}\n{text.TrimEnd()}");
            Console.WriteLine("--- next\n" + string.Join("\n", nx));
            Console.WriteLine("--- status\n" + Compare(inspect, newer).ToJsonString(Indented) + "\n" + pr.ToJsonString(Indented));
            Console.WriteLine("--- notes\n" + string.Join("\n", o.R.Notes));
            Console.WriteLine("--- with the real translator\n" + real.Output["coverage"] + "\n" + string.Join("\n", real.R.SessionDecisions.Select(d => $"{d.Name}: {d.Decision}")));
        }
        return fails;
    }
}
