using System.Text.Json;
using OutSystems.Model;

namespace OmlCli;

// Service Studio's own test cases (the module tree's Tests, shown once a module has one; run with Run Selected Test Case, which runs
// them on the server). A test case is a flow of its own, with an optional Setup and TearDown flow, and assert nodes:
//   { "tests": [{ "name": "AddsTwoNumbers", "setup": [...], "tearDown": [...],
//       "flow": [{ "kind": "execute", "name": "Run", "action": "Add", "args": { "A": "1", "B": "2" } },
//                { "kind": "assert", "name": "IsThree", "condition": "Run.Sum = 3" }] }] }
// Test flows are not part of the public model API: oml makes them with Service Studio's own classes and node factory, as the API does
// for other flows. Listed again, a test case that exists is left as it is.
partial class Builder
{
    public void Tests(List<JsonElement>? tests)
    {
        foreach (var t in tests ?? new()) Try($"test {RequiredName(t)}", () =>
        {
            var name = RequiredName(t);
            var label = $"test {name}";
            foreach (var p in t.EnumerateObject().Where(p => p.Name is not ("name" or "flow" or "setup" or "tearDown" or "description")))
                throw new ModelError($"unknown key '{p.Name}' (a test takes flow, setup, tearDown and description; use the outputs of execute nodes, as Run.Sum, instead of locals)");
            var all = Elements(Reflect.Get(m, "TestCases")).ToList();
            if (all.FirstOrDefault(x => Model.NameOf(x) == name) is { } found)
            {
                NotChanged(label, "its flows", "delete the test case and list it again");
                existing.Add(label);
                return;
            }
            var framework = m.GetType().Assembly;
            var cls = framework.GetType("ServiceStudio.Model.TestFlows+TestCase") ?? throw new ModelError("this Service Studio build has no test cases");
            var test = cls.GetConstructors().First(c => c.GetParameters() is [{ } p0, { ParameterType: var p1 }] && p1 == typeof(string) && p0.ParameterType.IsInstanceOfType(m))
                .Invoke([m, name]);
            if (!Fresh(test, name, label)) return;
            created.Add(label);
            if (t.TryGetProperty("description", out var d)) Queue(label, m, test, "description", d);
            foreach (var (key, part) in new[] { ("setup", "Setup"), ("tearDown", "TearDown") })
                if (t.TryGetProperty(key, out var steps))
                {
                    var partClass = framework.GetType($"ServiceStudio.Model.TestFlows+{part}")!;
                    var flow = partClass.GetConstructors().First(c => c.GetParameters() is [{ } p0] && p0.ParameterType.IsInstanceOfType(test)).Invoke([test]);
                    flows.Add(($"{label}/{part}", () => Flow(flow, steps.EnumerateArray().ToList(), $"{label}/{part}")));
                    widgets.Add(($"{label}/{part}.layout", () => Line(flow)));
                }
            var body = t.TryGetProperty("flow", out var f) ? f.EnumerateArray().ToList() : throw new ModelError("a test takes flow: its steps, with at least one assert");
            flows.Add(($"{label}/flow", () => Flow(test, body, label)));
            widgets.Add(($"{label}.layout", () => Line(test)));   // after the connectors (targets stage)
        });
    }

    // Test flows get no automatic layout (oml's layout works on public flows): the nodes go top to bottom along their links from Start,
    // as a straight flow is drawn (x 3196, from y 800, 2660 apart); nodes off that path stay where they are.
    static void Line(object flow)
    {
        var nodes = Elements(Reflect.Get(flow, "Nodes")).ToList();
        if (nodes.Count == 0) nodes = ((IObjectSignature)flow).Children.Where(c => c.GetType().GetProperty("X") is not null).Cast<object>().ToList();
        object? Next(object node) => ((IObjectSignature)node).Children.FirstOrDefault(c => c.GetType().Name == "Sequence") is { } link
            ? link.GetType().GetProperty("Target")?.GetValue(link) ?? link.GetType().GetProperty("TargetNode")?.GetValue(link) : null;
        var at = nodes.FirstOrDefault(n => n is OutSystems.Model.Logic.Nodes.IStartNode || n.GetType().Name == "Start");
        var seen = new HashSet<object>();
        for (var y = 800; at is not null && seen.Add(at); y += 2660, at = Next(at))
        {
            foreach (var (h, v) in new[] { ("HorizontalPosition", "VerticalPosition"), ("X", "Y") })
                if (at.GetType().GetProperty(h) is { CanWrite: true } hp && at.GetType().GetProperty(v) is { CanWrite: true } vp && hp.PropertyType == typeof(int))
                { hp.SetValue(at, 3196); vp.SetValue(at, y); break; }
        }
    }

    // A flow node of `owner`. Actions, screens and other public flows have CreateNode<T>; test flows do not, so their nodes come from
    // Service Studio's own factory (the one behind the public CreateNode), and an assert from its class.
    object NewNode(object owner, Type type, string name)
    {
        if (Reflect.HasGeneric(owner, "CreateNode", 2))
            return Reflect.CallGeneric(owner, "CreateNode", type, name, services.NewKey())!;
        var framework = owner.GetType().Assembly;
        if (type == AssertNode)
        {
            var assert = framework.GetType("ServiceStudio.Model.TestNodes+Assert")!.GetConstructors()
                .First(c => c.GetParameters() is [{ } p0] && p0.ParameterType.IsInstanceOfType(owner)).Invoke([owner]);
            Reflect.Property(assert, "Label", writable: true)?.SetValue(assert, name);
            return assert;
        }
        var factory = framework.GetType("ServiceStudio.Model.API.ModelFactory")?.GetMethods().FirstOrDefault(mi => mi.Name == "Create" && mi.IsGenericMethodDefinition)
            ?? throw new ModelError("this Service Studio build cannot make nodes in a test flow");
        var key = framework.GetType("ServiceStudio.Model.AbstractObject")!.GetMethod("ToObjectKey", [typeof(IKey)])!.Invoke(null, [services.NewKey()]);
        try { return factory.MakeGenericMethod(type).Invoke(null, [owner, key, name, Array.Empty<object>()])!; }
        catch (System.Reflection.TargetInvocationException e) { throw new ModelError($"CreateNode: {(e.InnerException ?? e).Message}"); }
    }

    // Stands for the assert node kind, which has no public model type.
    static readonly Type AssertNode = typeof(AssertMarker);
    interface AssertMarker;
}
