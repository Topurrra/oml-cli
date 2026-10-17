using System.Text.Json;
using OutSystems.Model;

namespace OmlCli;

static partial class SelfTest
{
    const string PlatformSpec = """
        {
          "entities": [{ "name": "Order", "attributes": [{ "name": "Name" }] }],
          "roles": [{ "name": "OrderManager", "description": "Manages orders", "persistent": true }],
          "siteProperties": [{ "name": "MaxItems", "type": "Integer", "default": "50", "multiTenant": false }],
          "serverActions": [
            { "name": "CleanupOldOrders", "locals": [{ "name": "Limit", "type": "Integer" }],
              "flow": [{ "kind": "assign", "name": "ReadLimit", "assign": { "Limit": "Site.MaxItems" } }] },
            { "name": "CheckAccess", "flow": [{ "kind": "execute", "action": "CheckOrderManagerRole", "args": { "UserId": "NullIdentifier()" } }] },
            { "name": "GrantAccess", "flow": [{ "kind": "execute", "action": "GrantOrderManagerRole", "args": { "UserId": "NullIdentifier()" } }] },
            { "name": "RevokeAccess", "flow": [{ "kind": "execute", "action": "RevokeOrderManagerRole", "args": { "UserId": "NullIdentifier()" } }] }
          ],
          "timers": [{ "name": "NightlyCleanup", "action": "CleanupOldOrders", "schedule": "WhenPublished", "timeout": 20, "priority": "Normal", "description": "Cleanup" }],
          "screens": [{ "name": "ReviewOrder", "inputs": [{ "name": "OrderId", "type": "Order Identifier" }],
            "access": { "anonymous": false, "registered": false, "roles": ["OrderManager"] } }],
          "edits": [{ "op": "setAccess", "screen": "MainFlow/ReviewOrder", "roles": ["OrderManager"] }],
          "processes": [{ "name": "ApproveOrder", "launchOn": "Order", "detail": "\"Order \" + OrderId",
            "inputs": [{ "name": "Approved", "type": "Boolean", "default": "True" }],
            "flow": [
              { "kind": "start", "name": "Start", "next": "Review" },
              { "kind": "humanActivity", "name": "Review", "destination": "ReviewOrder", "args": { "OrderId": "OrderId" }, "roles": ["OrderManager"], "instructions": "Review this order" },
              { "kind": "automaticActivity", "name": "RecordReview", "action": "CleanupOldOrders" },
              { "kind": "decision", "name": "WasApproved", "condition": "Approved", "true": "ApprovedEnd", "false": "RejectedEnd" },
              { "kind": "end", "name": "ApprovedEnd" },
              { "kind": "end", "name": "RejectedEnd" }
            ] }]
        }
        """;

    static void Platform(string blank, string dir, List<string> failures)
    {
        var spec = Path.Combine(dir, "platform.json");
        var output = Path.Combine(dir, "platform.oml");
        File.WriteAllText(spec, PlatformSpec);
        var (code, report) = Apply.Execute(blank, spec, output);
        if (code != 0)
        {
            failures.Add("platform: " + JsonSerializer.Serialize(report));
            return;
        }
        using var built = Model.Load(output);
        var all = Descendants(built).ToList();
        var timer = built.Timers.Single(t => Model.NameOf(t) == "NightlyCleanup");
        if (Model.NameOf(timer.Action) != "CleanupOldOrders" || timer.Schedule != "WhenPublished"
            || timer.TimeoutInMinutes != 20 || timer.Priority.ToString() != "Normal")
            failures.Add("platform timer did not round-trip");
        var property = built.SiteProperties.Single(p => p.Name == "MaxItems");
        if (Model.Describe(built, property.DataType) != "Integer" || property.DefaultValue.Text != "50"
            || property.IsMultiTenant.ToString() != "No")
            failures.Add("platform site property did not round-trip");
        var role = built.Roles.Single(r => Model.NameOf(r) == "OrderManager");
        if (!role.IsPersistent) failures.Add("platform role persistence lost");
        var screen = ElementPaths.Resolve(built, "MainFlow/ReviewOrder");
        var roles = ((System.Collections.IEnumerable)Reflect.Get(screen, "Roles")!).Cast<object>().Select(Model.NameOf).ToArray();
        if (!roles.SequenceEqual(new[] { "OrderManager" })) failures.Add("platform screen permissions lost");
        var automatic = built.Processes.Single().Nodes.OfType<OutSystems.Model.Processes.Nodes.IAutomaticActivityNode>().Single();
        if (!automatic.Nodes.OfType<OutSystems.Model.Logic.Nodes.IExecuteServerActionNode>()
            .Any(n => Model.NameOf(n.Action) == "CleanupOldOrders"))
            failures.Add("platform automatic activity action missing");
        foreach (var action in new[] { "CheckOrderManagerRole", "GrantOrderManagerRole", "RevokeOrderManagerRole", "CleanupOldOrders" })
            if (!all.Any(o => Reflect.Get(o, "Action") is { } a && Model.NameOf(a) == action))
                failures.Add("platform action reference missing: " + action);
        var process = built.Processes.Single(p => Model.NameOf(p) == "ApproveOrder");
        if (Model.NameOf(Reflect.Get(process.LaunchOn, "Entity")!) != "Order")
            failures.Add("platform process launch trigger lost");
        var human = process.Nodes.OfType<OutSystems.Model.Processes.Nodes.IHumanActivityNode>().Single();
        if (Model.NameOf(human.Destination) != "ReviewOrder"
            || !human.Roles.Select(Model.NameOf).SequenceEqual(new[] { "OrderManager" })
            || human.Arguments.Single().Value.Text != "OrderId")
            failures.Add("platform human activity bindings lost");
        var decision = process.Nodes.OfType<OutSystems.Model.Processes.Nodes.IDecisionNode>().Single();
        if (decision.Connectors.Count() != 2 || !decision.Nodes.OfType<OutSystems.Model.Logic.Nodes.IIfNode>().Any(n => n.Condition.Text == "Approved"))
            failures.Add("platform decision outcomes lost");
        if (!all.OfType<IExpression>().Any(e => e.Text == "Site.MaxItems"))
            failures.Add("platform site property expression lost");
        File.WriteAllText(spec, """{ "edits": [{ "op": "setAccess", "screen": "ReviewOrder", "anonymous": true, "registered": true }] }""");
        var publicOutput = Path.Combine(dir, "platform-public.oml");
        var (publicCode, publicReport) = Apply.Execute(output, spec, publicOutput);
        if (publicCode != 0) failures.Add("platform access edit: " + JsonSerializer.Serialize(publicReport));
        else
        {
            using var publicModule = Model.Load(publicOutput);
            var publicScreen = ElementPaths.Resolve(publicModule, "ReviewOrder");
            var publicRoles = ((System.Collections.IEnumerable)Reflect.Get(publicScreen, "Roles")!).Cast<object>().ToList();
            if (!publicRoles.Contains(publicModule.AnonymousRole) || !publicRoles.Contains(publicModule.RegisteredRole))
                failures.Add("platform access edit lost Anonymous or Registered");
        }
        foreach (var invalid in new[] {
            """{ "timers": [{ "name": "BadTimer", "action": "MissingAction" }] }""",
            """{ "roles": [{ "name": "Manager" }], "screens": [{ "name": "NoAccess", "access": { "roles": ["MissingRole"] } }] }""",
            """{ "processes": [{ "name": "BadProcess", "flow": [{ "kind": "decision", "name": "Broken", "condition": "True", "true": "Missing", "false": "End" }] }] }"""
        })
        {
            File.WriteAllText(spec, invalid);
            var rejected = Path.Combine(dir, "platform-rejected.oml");
            var (invalidCode, _) = Apply.Execute(blank, spec, rejected);
            if (invalidCode == 0 || File.Exists(rejected)) failures.Add("invalid platform spec was saved: " + invalid);
        }
    }
}
