using System.Globalization;
using OutSystems.Model;
using OutSystems.Model.Data;
using OutSystems.Model.Enumerations;
using OutSystems.Model.Types;

namespace OmlCli;

record StaticEntitySpec(string Name, List<AttrSpec>? Attributes = null, List<StaticRecordSpec>? Records = null,
    bool Public = false, string? Description = null);
record StaticRecordSpec(string Identifier, string? Label = null, int? Order = null, bool IsActive = true,
    Dictionary<string, string>? Values = null);

static class StaticEntities
{
    static void Attempt(string label, List<string> problems, Action action)
    {
        try { action(); }
        catch (Exception e) when (e is ModelError or InvalidOperationException or ArgumentException or NotSupportedException
            or System.Reflection.TargetInvocationException)
        { problems.Add($"{label}: {(e.InnerException ?? e).Message}"); }
    }

    public static List<(StaticEntitySpec spec, IStaticEntity entity)> Create(IESpace m, List<StaticEntitySpec>? specs,
        List<string> created, List<string> existing, List<string> problems)
    {
        var result = new List<(StaticEntitySpec, IStaticEntity)>();
        foreach (var s in specs ?? new()) Attempt($"static entity {s.Name}", problems, () =>
        {
            var found = m.Entities.SingleOrDefault(e => e.Name == s.Name);
            // Concrete entities implement several contracts; EntityKind distinguishes storage kinds.
            if (found is not null && Reflect.Get(found, "EntityKind")?.ToString() != "Static") throw new ModelError("existing entity is not static; retyping refused");
            var e = (IStaticEntity?)found ?? (IStaticEntity)Reflect.Call(m, "CreateStaticEntity", s.Name, Model.Services().NewKey())!;
            if (e.Name != s.Name) throw new ModelError($"name taken; model renamed it {e.Name}");
            (found is null ? created : existing).Add($"static entity {s.Name}");
            if (found is null)
            {
                // Adapted from OutSystems/outsystems11-mcp examples/AddStaticEntity.cs (MIT).
                var id = e.Attributes.SingleOrDefault(a => a.Name == "Id") ?? e.CreateAttribute("Id", Model.Services().NewKey());
                id.DataType = m.IntegerType; // as OutSystems examples/AddStaticEntity.cs; record Ids are Integer literals
                id.IsMandatory = true;
                e.IdentifierAttribute = id;
            }
            result.Add((s, e));
        });
        return result;
    }

    public static void Populate(IESpace m, List<(StaticEntitySpec spec, IStaticEntity entity)> entities,
        List<string> created, List<string> existing, List<string> problems)
    {
        foreach (var (s, e) in entities) Attempt($"static entity {s.Name}", problems, () =>
        {
            e.Public = s.Public;
            if (s.Description is not null) e.Description = s.Description;
            var defaults = new[] { new AttrSpec("Label", Length: 50, Mandatory: true),
                new AttrSpec("Order", "Integer", Mandatory: true), new AttrSpec("Is_Active", "Boolean", Mandatory: true) };
            foreach (var a in defaults.Where(d => !(s.Attributes ?? new()).Any(a => a.Name == d.Name)).Concat(s.Attributes ?? new()))
            {
                if (e.Attributes.Any(x => x.Name == a.Name)) { existing.Add($"attribute {s.Name}.{a.Name}"); continue; }
                var resolved = Model.Resolve(m, a.Type, problems, $"{s.Name}.{a.Name}");
                if (resolved is null) continue;
                if (resolved is not IBasicType type) { problems.Add($"{s.Name}.{a.Name}: entity attributes need a basic or Identifier type"); continue; }
                var attr = e.CreateAttribute(a.Name, Model.Services().NewKey());
                if (attr.Name != a.Name) throw new ModelError($"attribute {a.Name} renamed {attr.Name}");
                attr.DataType = type;
                if (type is IIdentifierType) attr.DeleteRule = Enum.Parse<DeleteRule>(a.DeleteRule ?? "Protect", ignoreCase: true);
                Apply.SetCommon(attr, a);
                created.Add($"attribute {s.Name}.{a.Name}");
            }
            e.LabelAttribute ??= e.Attributes.Single(a => a.Name == "Label");
            e.OrderByAttribute ??= e.Attributes.Single(a => a.Name == "Order");
            e.IsActiveAttribute ??= e.Attributes.Single(a => a.Name == "Is_Active");
            if (e.IdentifierAttribute is null || e.LabelAttribute is null || e.OrderByAttribute is null || e.IsActiveAttribute is null)
                throw new ModelError($"incomplete static attribute roles: Id={e.IdentifierAttribute?.Name}, Label={e.LabelAttribute?.Name}, Order={e.OrderByAttribute?.Name}, Active={e.IsActiveAttribute?.Name}");
            foreach (var r in s.Records ?? new()) Attempt($"record {s.Name}.{r.Identifier}", problems, () =>
            {
                if (e.Records.Any(x => x.Identifier == r.Identifier)) { existing.Add($"record {s.Name}.{r.Identifier}"); return; }
                var used = e.Records.SelectMany(x => x.AttributeValues).Where(v => v.Attribute == e.IdentifierAttribute)
                    .Select(v => int.TryParse(v.Value?.Text, out var n) ? n : 0).ToHashSet();
                var id = 1;
                while (used.Contains(id)) id++;
                var record = (IRecord)Reflect.Call(e, "CreateRecord", r.Identifier, Model.Services().NewKey())!;
                record.Identifier = r.Identifier;
                if (record.Identifier != r.Identifier) throw new ModelError($"identifier renamed {record.Identifier}");
                var values = new Dictionary<string, string> {
                    [e.IdentifierAttribute!.Name] = id.ToString(CultureInfo.InvariantCulture),
                    [e.LabelAttribute.Name] = "\"" + (r.Label ?? r.Identifier).Replace("\"", "\"\"") + "\"",
                    [e.OrderByAttribute.Name] = (r.Order ?? id).ToString(CultureInfo.InvariantCulture),
                    [e.IsActiveAttribute.Name] = r.IsActive ? "True" : "False" };
                foreach (var v in r.Values ?? new())
                {
                    if (v.Key == e.IdentifierAttribute.Name) throw new ModelError("values cannot override the generated Id");
                    values[v.Key] = v.Value;
                }
                foreach (var v in values)
                {
                    var slot = record.AttributeValues.SingleOrDefault(a => a.Attribute.Name == v.Key)
                        ?? throw new ModelError($"unknown attribute '{v.Key}'");
                    Reflect.Call(slot, "SetValue", v.Value);
                }
                created.Add($"record {s.Name}.{r.Identifier}");
            });
        });
    }
}
