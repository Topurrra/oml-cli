using System.Reflection;
using System.Text.Json;
using OutSystems.Model;

namespace OmlCli;

record ReferenceSpec(string Producer, JsonElement Elements);

static class ModuleReferences
{
    public static void Alias(IESpace m, Dictionary<string, string>? aliases, List<string> problems, ProductionRules rules)
    {
        foreach (var (qualified, alias) in aliases ?? new())
        {
            try
            {
                if (!qualified.Contains('.')) throw new ModelError("reference alias needs Module.Element");
                var dot = qualified.IndexOf('.');
                var owner = qualified[..dot];
                var target = Refs.All(m).FirstOrDefault(x => x.owner == owner && Model.NameOf(x.element) == qualified[(dot + 1)..]).element
                    ?? Refs.Find(m, owner + "." + alias, typeof(IObjectSignature));
                if (!Refs.All(m).Any(x => x.owner != ((OutSystems.Model.Applications.IModule)m).Name && ReferenceEquals(x.element, target)))
                    throw new ModelError("only referenced elements can be aliased");
                var property = Reflect.Property(target, "Name", writable: true) ?? throw new ModelError("reference name is not writable on this build");
                rules.RenameReference((IObjectSignature)target);
                property.SetValue(target, alias);
                if (Model.NameOf(target) != alias) throw new ModelError($"alias {alias}: {NameClash.Taken(target, alias)}; the model renamed it {Model.NameOf(target)}");
            }
            catch (Exception e) when (e is ModelError or ArgumentException or InvalidOperationException or TargetInvocationException)
            { problems.Add($"reference alias {qualified}: {(e.InnerException ?? e).Message}"); }
        }
    }

    static readonly string[] Collections = { "Entities", "Structures", "ServerActions", "ServiceActions",
        "ClientActions", "Roles", "UserExceptions", "MobileThemes", "WebThemes",
        "Images", "Scripts", "Resources" };

    internal static IEnumerable<IObjectSignature> Elements(object module)
    {
        var seen = new HashSet<IObjectSignature>();
        foreach (var key in Collections.Concat(new[] { "MobileFlows", "WebFlows" }))
            if (Reflect.Get(module, key) is System.Collections.IEnumerable items)
                foreach (var item in items.Cast<IObjectSignature>())
                    if (key.EndsWith("Flows"))
                    {
                        if (Reflect.Get(item, "Nodes") is System.Collections.IEnumerable nodes)
                            foreach (var node in nodes.OfType<IObjectSignature>())
                                if (seen.Add(node)) yield return node;
                    }
                    else if (seen.Add(item)) yield return item;
        // Referenced public screens can be children outside the flow's Nodes collection.
        if (module is IReference reference)
            foreach (var screen in Descendants(reference).OfType<OutSystems.Model.UI.IScreenSignature>())
                if (seen.Add(screen)) yield return screen;
    }

    internal static IEnumerable<IObjectSignature> Descendants(IObjectSignature root)
    {
        var seen = new HashSet<IObjectSignature>();
        var pending = new Stack<IObjectSignature>(root.Children);
        while (pending.TryPop(out var item))
            if (seen.Add(item))
            {
                yield return item;
                foreach (var child in item.Children) pending.Push(child);
            }
    }

    internal static bool Same(IObjectSignature a, IObjectSignature b) => Equals(a.GlobalKey, b.GlobalKey);

    public static void Add(IESpace consumer, List<ReferenceSpec>? specs,
        List<string> created, List<string> existing, List<string> problems)
    {
        foreach (var spec in specs ?? new())
        {
            var label = $"reference {spec.Producer}";
            try
            {
                // LoadESpace reads bytes, so the producer never has a writable file path.
                using var producer = Model.Load(Path.GetFullPath(spec.Producer));
                if (Equals(producer.ObjectKey, consumer.ObjectKey)) throw new ModelError("a module cannot reference itself");
                var all = Elements(producer).ToList();
                List<IObjectSignature> selected;
                if (spec.Elements.ValueKind == JsonValueKind.String && spec.Elements.GetString() == "*")
                    selected = all.Where(e => Reflect.Get(e, "Public") is true).ToList();
                else if (spec.Elements.ValueKind == JsonValueKind.Array)
                {
                    selected = new();
                    foreach (var value in spec.Elements.EnumerateArray())
                    {
                        if (value.ValueKind != JsonValueKind.String) throw new ModelError("elements must contain names");
                        var name = value.GetString();
                        var hits = all.Where(e => Model.NameOf(e) == name).ToList();
                        if (hits.Count != 1) throw new ModelError($"element '{name}' is {(hits.Count == 0 ? "missing" : "ambiguous; use a unique producer element name")}");
                        if (Reflect.Get(hits[0], "Public") is not true) throw new ModelError($"element '{name}' is not public");
                        selected.Add(hits[0]);
                    }
                }
                else throw new ModelError("elements must be an array of names or \"*\"");

                foreach (var element in selected.Distinct())
                {
                    var where = $"reference {Model.NameOf(producer)}.{Model.NameOf(element)}";
                    try
                    {
                        if (consumer.References.SelectMany(Descendants).Any(e => Same(e, element)))
                        { existing.Add(where); continue; }
                        var contract = element.GetType().GetInterfaces().Where(t => t.IsGenericType
                            && t.GetGenericTypeDefinition() == typeof(IShareable<,>))
                            .OrderByDescending(t => t.GetGenericArguments()[0] == element.GetInterface())
                            .ThenByDescending(t => t.GetGenericArguments()[0].GetInterfaces().Length).FirstOrDefault()
                            ?? throw new ModelError($"{element.GetInterface().Name} is not shareable on this build");
                        var factory = typeof(IESpace).GetMethods().Single(m => m.Name == "AddDependency" && m.IsGenericMethodDefinition);
                        var result = factory.MakeGenericMethod(contract.GetGenericArguments()).Invoke(consumer, new object[] { element });
                        if (result is not IObjectSignature imported || !Same(imported, element))
                            throw new ModelError("AddDependency did not return the requested element");
                        if (Model.NameOf(imported) != Model.NameOf(element))
                            throw new ModelError($"name clash: {NameClash.Taken(imported, Model.NameOf(element)!)}; model renamed the reference to {Model.NameOf(imported)}");
                        created.Add(where);
                    }
                    catch (Exception e) { problems.Add($"{where}: {Reason(e)}"); }
                }
            }
            catch (Exception e) { problems.Add($"{label}: {Reason(e)}"); }
        }
    }

    static string Reason(Exception e)
    {
        while (e is TargetInvocationException && e.InnerException is { } inner) e = inner;
        return e is NotImplementedException
            ? "the headless model could not complete this operation (a Service Studio dialog, including IPP protection, may be required); open the producer in its authorized infrastructure. " + e.Message
            : e.Message;
    }
}
