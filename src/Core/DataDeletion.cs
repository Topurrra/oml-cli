using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Data;
using OutSystems.Model.Types;

namespace OmlCli;

partial class Builder
{
    public List<string> Warnings { get; } = new();
    readonly List<(string label, Action apply)> dataDeletes = new();

    void DeleteData(IObjectSignature target, JsonElement edit)
    {
        rules.Delete(target);
        if (!edit.TryGetProperty("allowDataLoss", out var allow) || allow.ValueKind != JsonValueKind.True)
            throw new ModelError("data deletion requires allowDataLoss: true: publishing removes the table/column from the model and the app loses access; O11 keeps physical data until cleanup (data handling is platform-dependent)");
        var entity = target as IEntitySignature ?? target.Parent as IEntitySignature;
        if (entity is not null && ReferenceEquals(entity.IdentifierAttribute, target))
            throw new ModelError("the identifier attribute cannot be deleted alone");
        if (entity is not null && !m.Entities.Any(e => ReferenceEquals(e, entity)))
            throw new ModelError("cannot delete data owned by a referenced module");
        var usages = DataUsages(target);
        if (usages.Count > 0)
            throw new ModelError($"cannot delete '{DataPath(target)}'; usages: {string.Join(", ", usages)}. Edit or remove these usages first");
        if (entity is not null && Reflect.Get(entity, "Public") is true)
            Warnings.Add($"{DataPath(target)}: public entity; consumers in other modules cannot be checked and may lose access");
        if (entity is not null && (Reflect.Get(entity, "EntityKind")?.ToString() == "Static" || Reflect.Get(entity, "IsStatic") is true))
            Warnings.Add($"{DataPath(target)}: static entity; deleting it or an attribute may break static-record consumers");
        Reflect.Call(target, "Delete");
    }

    static string DataPath(IObjectSignature target)
    {
        var parts = new List<string>();
        for (var o = target; o.Parent is { } parent; o = parent)
            if (ElementPaths.Children(parent).Contains(o)) parts.Insert(0, ElementPaths.Segment(o, parent));
        return string.Join("/", parts);
    }

    static List<string> DataUsages(IObjectSignature target)
    {
        var owned = new HashSet<IObjectSignature>();
        void Own(IObjectSignature o)
        {
            if (!owned.Add(o)) return;
            foreach (var child in ElementPaths.Children(o)) Own(child);
        }
        Own(target);
        var pending = new Queue<object>(owned.Cast<object>());
        var seen = new HashSet<object>();
        var usages = new SortedSet<string>(StringComparer.Ordinal);
        // Whole-row queries can depend on a column without referencing its attribute object (SELECT *).
        if (target is IAttributeSignature && target.Parent is IEntitySignature entity)
        {
            foreach (var action in Elements(Reflect.Get(entity, "EntityActions")))
                foreach (var call in Elements(Reflect.Get(action, "Referers")).OfType<IObjectSignature>())
                    if (!call.WasDeleted && ReferenceEquals(Reflect.Get(call, "Action"), action))
                        usages.Add(DataPath(call));
            foreach (var referer in Elements(Reflect.Get(entity, "Referers")).OfType<IObjectSignature>())
            {
                if (referer.WasDeleted) continue;
                if (ReferenceEquals(Reflect.Get(referer, "EntityOrStructure"), entity)
                    || ReferenceEquals(Reflect.Get(referer, "Source"), entity))
                    usages.Add(DataPath(referer));
                for (var owner = referer; owner is not null && owner is not IESpace; owner = owner.Parent)
                    if (owner.GetType().GetInterfaces().Any(i => i.Name == "ISQLNode"))
                    {
                        usages.Add(DataPath(owner));
                        break;
                    }
            }
        }
        while (pending.TryDequeue(out var item))
        {
            if (!seen.Add(item)) continue;
            // The concrete reference graph includes parsed expressions, types and generated CRUD signatures.
            if (Reflect.Property(item, "Referers") is null)
                throw new ModelError($"cannot safely inspect usages of {item.GetType().Name} on this build");
            foreach (var referer in Elements(Reflect.Get(item, "Referers")))
            {
                if (referer is IObjectSignature { WasDeleted: true }) continue;
                if (referer is IObjectSignature sample && sample.GetType().Name.StartsWith("SampleRecord")
                    && (ReferenceEquals(sample.Parent, target) || ReferenceEquals(sample.Parent?.Parent, target)
                        || ReferenceEquals(sample.Parent?.Parent, target.Parent))) continue;
                if (referer is IObjectSignature attribute && attribute.Parent is { } typeParent
                    && typeParent.GetType().GetInterfaces().Any(i => i.Name == "IAnonymousType"))
                {
                    pending.Enqueue(typeParent);
                    pending.Enqueue(attribute);
                    continue;
                }
                if (referer is not IObjectSignature o || owned.Contains(o) || o is ITypeSignature)
                {
                    pending.Enqueue(referer);
                    continue;
                }
                usages.Add(DataPath(o));
            }
        }
        return usages.ToList();
    }
}
