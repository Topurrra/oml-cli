using OutSystems.Model;
using OutSystems.Model.Data;
using OutSystems.Model.Enumerations;

namespace OmlCli;

// Entity-only spec keys. Structures keep the plain RecordSpec shape.
record IndexSpec(List<string> Attributes, string? Name = null, bool Unique = false);
record EntitySpec(string Name, List<AttrSpec>? Attributes = null, bool? Public = null, string? Description = null,
    string? Identifier = null, bool? AutoNumber = null, string? LabelAttribute = null, string? OrderByAttribute = null,
    string? IsActiveAttribute = null, bool? ExposeReadOnly = null, string? UpdateBehavior = null, List<IndexSpec>? Indexes = null,
    bool? MultiTenant = null, bool? ExposeProcessEvents = null, string? Label = null, string? LabelPlural = null)
    : RecordSpec(Name, Attributes, Public, Description);

/// Entity options and indexes. New and existing entities take every option and new indexes. On an existing entity a
/// new identifier, auto number or multi-tenant setting changes how published rows are keyed: apply then asks for the
/// user's yes (rule 5, ProductionRules.Validate). Existing indexes are not changed.
static class EntityOptions
{
    public static void Apply(IESpace m, IEntity e, EntitySpec s, bool isNew, List<string> created, List<string> existing, List<string> problems, List<string> warnings)
    {
        var before = problems.Count;
        IEntityAttribute? Attr(string? name, string key)
        {
            if (name is null) return null;
            var a = e.Attributes.SingleOrDefault(x => x.Name == name);
            if (a is null) problems.Add($"entity {s.Name}: {key} '{name}' is not an attribute of {s.Name}");
            return a;
        }
        var identifier = Attr(s.Identifier, "identifier");
        var label = Attr(s.LabelAttribute, "labelAttribute");
        var orderBy = Attr(s.OrderByAttribute, "orderByAttribute");
        var isActive = Attr(s.IsActiveAttribute, "isActiveAttribute");
        UpdateEntityBehaviorType? update = s.UpdateBehavior?.ToLowerInvariant() switch
        {
            null => null,
            "changedattributes" => UpdateEntityBehaviorType.Optimized,
            "allattributes" => UpdateEntityBehaviorType.Full,
            _ => Fail<UpdateEntityBehaviorType>(problems, $"entity {s.Name}: updateBehavior '{s.UpdateBehavior}' is not changedAttributes or allAttributes"),
        };
        if (problems.Count > before) return;
        // Label and Label (plural): the names Service Studio shows for the entity. Display text only, so an existing entity takes them too.
        if (s.Label is { } shown) Reflect.Property(e, "Label", writable: true)?.SetValue(e, shown);
        if (s.LabelPlural is { } shownPlural) Reflect.Property(e, "LabelPlural", writable: true)?.SetValue(e, shownPlural);

        if (isNew)
        {
            if (identifier is not null && !ReferenceEquals(e.IdentifierAttribute, identifier))
            {
                var seeded = e.IdentifierAttribute;
                e.IdentifierAttribute = identifier;
                identifier.IsMandatory = true;
                // The Id that Apply adds to every new entity goes, unless the spec asked for an attribute named Id.
                if (seeded is { Name: "Id" } && s.Attributes?.Any(a => a.Name == "Id") != true) seeded.Delete();
            }
            if (s.AutoNumber is { } auto && e.IdentifierAttribute is IEntityAttribute id)
            {
                // The model accepts auto number on any type, but the database can only number integers.
                if (auto && !ReferenceEquals(id.DataType, m.IntegerType) && !ReferenceEquals(id.DataType, m.LongIntegerType))
                    problems.Add($"entity {s.Name}: autoNumber needs an Integer or Long Integer identifier; {id.Name} is {Model.Describe(m, id.DataType)}");
                else id.IsAutoNumber = auto ? AutoNumber.Yes : AutoNumber.No;
            }
            if (label is not null) e.LabelAttribute = label;
            if (orderBy is not null) e.OrderByAttribute = orderBy;
            if (isActive is not null) e.IsActiveAttribute = isActive;
            if (s.ExposeReadOnly is { } readOnly) e.ExposeReadOnly = readOnly;
            if (update is { } u) e.UpdateBehavior = u;
            if (s.MultiTenant is { } tenant) e.IsMultiTenant = tenant ? BooleanWithInheritance.Yes : BooleanWithInheritance.No;
            if (s.ExposeProcessEvents is { } events) e.ExposeProcessEvents = events;
        }
        else
        {
            // An existing entity takes them too. The identifier, auto number and multi-tenant setting change how rows are
            // keyed: ProductionRules.Validate asks for the user's yes (rule 5). The others may change.
            if (identifier is not null && !ReferenceEquals(e.IdentifierAttribute, identifier)) e.IdentifierAttribute = identifier;
            if (s.AutoNumber is { } auto && e.IdentifierAttribute is IEntityAttribute id && (id.IsAutoNumber == AutoNumber.Yes) != auto)
            {
                if (auto && !ReferenceEquals(id.DataType, m.IntegerType) && !ReferenceEquals(id.DataType, m.LongIntegerType))
                    problems.Add($"entity {s.Name}: autoNumber needs an Integer or Long Integer identifier; {id.Name} is {Model.Describe(m, id.DataType)}");
                else id.IsAutoNumber = auto ? AutoNumber.Yes : AutoNumber.No;
            }
            if (label is not null) e.LabelAttribute = label;
            if (orderBy is not null) e.OrderByAttribute = orderBy;
            if (isActive is not null) e.IsActiveAttribute = isActive;
            if (s.ExposeReadOnly is { } readOnly && e.ExposeReadOnly != readOnly) e.ExposeReadOnly = readOnly;
            if (update is { } u && e.UpdateBehavior != u) e.UpdateBehavior = u;
            if (s.MultiTenant is { } tenant && e.IsMultiTenant.ToString() != (tenant ? "Yes" : "No")) e.IsMultiTenant = tenant ? BooleanWithInheritance.Yes : BooleanWithInheritance.No;
            if (s.ExposeProcessEvents is { } events && e.ExposeProcessEvents != events) e.ExposeProcessEvents = events;
        }

        foreach (var ix in s.Indexes ?? new()) Index(e, s.Name, ix, isNew, created, existing, problems, warnings);
    }

    static T Fail<T>(List<string> problems, string message) { problems.Add(message); return default!; }

    // ---- indexes ----
    // The typed API has no index factory. The entity's EntityIndices collection and its element types come from the
    // installed model, read by reflection; no concrete class is named here.

    static void Index(IEntity e, string entity, IndexSpec ix, bool isNew, List<string> created, List<string> existing, List<string> problems, List<string> warnings)
    {
        var names = ix.Attributes ?? new();
        var name = ix.Name ?? "IX_" + string.Join("_", names);
        var label = $"index {entity}.{name}";
        if (!Builder.IsName(name)) { problems.Add($"{label}: '{name}' is not a valid index name (ASCII letters, digits and _, not starting with a digit)"); return; }
        if (names.Count == 0) { problems.Add($"{label}: attributes must name at least one attribute"); return; }
        if (names.Distinct().Count() != names.Count) { problems.Add($"{label}: an attribute is listed twice"); return; }
        var attributes = new List<IEntityAttribute>();
        foreach (var n in names)
            if (e.Attributes.SingleOrDefault(a => a.Name == n) is { } a) attributes.Add(a);
            else problems.Add($"{label}: '{n}' is not an attribute of {entity}");
        if (attributes.Count != names.Count) return;
        try
        {
            if (Indexes(e).FirstOrDefault(i => IndexName(i) == name) is { } old)
            {
                if (Columns(old).SequenceEqual(names) && Reflect.Get(old, "Unique") is bool unique && unique == ix.Unique) existing.Add(label);
                else problems.Add($"{label}: an index with this name already exists with other attributes or another unique setting; oml does not change existing indexes");
                return;
            }
            var index = Add(e, "EntityIndices", name);
            Reflect.Call(index, "SetName", name);
            if (IndexName(index) != name)
                throw new ModelError($"index {name}: name taken by {(Indexes(e).FirstOrDefault(i => !ReferenceEquals(i, index) && string.Equals(IndexName(i), name, StringComparison.OrdinalIgnoreCase)) is { } held
                    ? $"index {IndexName(held)} of entity {e.Name} (index names ignore case)" : "another index of entity " + e.Name)}; the model named it '{IndexName(index)}'");
            Reflect.Property(index, "Unique", writable: true)!.SetValue(index, ix.Unique);
            foreach (var a in attributes) Add(index, "IndexAttributes", a);
            if (!Columns(index).SequenceEqual(names)) throw new ModelError("the model did not keep the index attributes in the requested order");
            created.Add(label);
            if (ix.Unique && !isNew)
                warnings.Add($"unique index {name} on existing entity {entity}: publish fails if production has duplicate values in ({string.Join(", ", names)}); check before publishing.");
        }
        catch (ModelError err) { problems.Add($"{label}: {err.Message}"); }
    }

    /// Custom indexes of an entity in model order; indexes the platform generates (foreign keys) are left out.
    internal static IEnumerable<object> Indexes(object entity) =>
        (Reflect.Get(entity, "EntityIndices") as System.Collections.IEnumerable)?.Cast<object>().Where(i => Reflect.Get(i, "AutoGenerated") is not true)
        ?? Enumerable.Empty<object>();

    // Indexes have no typed interface, so Model.NameOf cannot read their name.
    internal static string? IndexName(object index) => Reflect.Get(index, "Name") as string;

    internal static List<string> Columns(object index) =>
        ((Reflect.Get(index, "IndexAttributes") as System.Collections.IEnumerable)?.Cast<object>() ?? Enumerable.Empty<object>())
        .Select(c => (Reflect.Get(c, "Attribute") is { } a ? Model.NameOf(a) : null) ?? "?").ToList();

    // Builds one element of a model collection with its (parent, value) constructor, then adds it if the constructor did not.
    // The collection is read again after construction: an empty one can be a read-only placeholder.
    static object Add(object owner, string collection, object value)
    {
        var current = Reflect.Get(owner, collection) ?? throw new ModelError($"{collection} is not available on this build");
        var type = current.GetType().GetInterfaces().Concat(new[] { current.GetType() })
            .Where(t => t.IsGenericType && t.GetGenericTypeDefinition().Name.StartsWith("ICollection"))
            .Select(t => t.GetGenericArguments()[0]).FirstOrDefault()
            ?? throw new ModelError($"cannot tell the element type of {collection}");
        var ctor = type.GetConstructors().FirstOrDefault(c => c.GetParameters() is [var p, var q]
                       && !p.ParameterType.IsInterface && p.ParameterType.IsInstanceOfType(owner) && q.ParameterType.IsInstanceOfType(value))
                   ?? throw new ModelError($"{type.Name} has no ({owner.GetType().Name}, {value.GetType().Name}) constructor on this build");
        object item;
        try { item = ctor.Invoke(new[] { owner, value }); }
        catch (System.Reflection.TargetInvocationException ex) { throw new ModelError($"{type.Name}: {ex.InnerException?.Message}"); }
        var after = Reflect.Get(owner, collection)!;
        if (!((System.Collections.IEnumerable)after).Cast<object>().Contains(item)) Reflect.Call(after, "Add", item);
        return item;
    }
}
