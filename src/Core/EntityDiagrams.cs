using System.Text.Json;
using OutSystems.Model;

namespace OmlCli;

// Entity diagrams (Service Studio: Data > Entity Diagrams): a named picture of entities and the references between them.
//   { "entityDiagrams": [{ "name": "Orders", "entities": ["Order", "OrderLine", "Customer"] }] }   ("entities": "*" for all of the module's)
// Diagrams are not part of the public model API: oml makes them with Service Studio's own classes. The entities are laid out in rows
// of four. Listed again, a diagram gets the entities it lacks; the ones there stay where they are.
partial class Builder
{
    public void EntityDiagrams(List<JsonElement>? diagrams)
    {
        foreach (var d in diagrams ?? new()) Try($"entityDiagram {RequiredName(d)}", () =>
        {
            var name = RequiredName(d);
            var label = $"entityDiagram {name}";
            foreach (var p in d.EnumerateObject().Where(p => p.Name is not ("name" or "entities")))
                throw new ModelError($"unknown key '{p.Name}' (an entity diagram takes name and entities)");
            var framework = m.GetType().Assembly;
            var diagram = Elements(Reflect.Get(m, "EntityDiagrams")).FirstOrDefault(x => Model.NameOf(x) == name);
            if (diagram is null)
            {
                var cls = framework.GetType("ServiceStudio.Model.Flows+EntityDiagram") ?? throw new ModelError("this Service Studio build has no entity diagrams");
                diagram = cls.GetConstructors().First(c => c.GetParameters() is [{ } p0, { ParameterType: var p1 }] && p1 == typeof(string) && p0.ParameterType.IsInstanceOfType(m))
                    .Invoke([m, name]);
                if (!Fresh(diagram, name, label)) return;
                created.Add(label);
            }
            else existing.Add(label);

            if (!d.TryGetProperty("entities", out var list)) return;
            var entities = list.ValueKind == JsonValueKind.String && list.GetString() == "*"
                ? m.Entities.Cast<object>().ToList()
                : list.EnumerateArray().Select(e => Refs.Find(m, e.GetString()!, typeof(OutSystems.Model.Data.IEntitySignature))
                    ?? throw new ModelError($"no entity '{e.GetString()}'")).ToList();
            var shapes = Elements(Reflect.Get(diagram, "Nodes")).Where(n => n.GetType().Name == "DiagramEntity").ToList();
            var shape = framework.GetType("ServiceStudio.Model.Nodes+DiagramEntity")!;
            var count = shapes.Count;
            foreach (var entity in entities)
            {
                if (shapes.Any(s => ReferenceEquals(Reflect.Get(s, "Entity"), entity))) continue;
                var ctor = shape.GetConstructors().FirstOrDefault(c => c.GetParameters() is [{ } p0, { } p1] && p0.ParameterType.IsInstanceOfType(diagram) && p1.ParameterType.IsInstanceOfType(entity))
                    ?? throw new ModelError($"'{Model.NameOf(entity)}' cannot go in an entity diagram");
                var made = ctor.Invoke([diagram, entity]);
                // Rows of four, in the units Service Studio stores (a flow node is about 2660 apart vertically).
                foreach (var (axis, value) in new[] { ("X", 1000 + count % 4 * 6000), ("Y", 1000 + count / 4 * 5000) })
                    if (made.GetType().GetProperty(axis) is { CanWrite: true } position && position.PropertyType == typeof(int)) position.SetValue(made, value);
                count++;
                created.Add($"{label}/{Model.NameOf(entity)}");
            }
        });
    }
}
