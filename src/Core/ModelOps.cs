using System.Reflection;
using System.Text.Json;
using OutSystems.Model;

namespace OmlCli;

// The Model API types a spec names in the generic operations (edits `add`, `set` with `type`), read by reflection
// from the installed Service Studio, so a new build's types and members work without a change here.
static class ModelTypes
{
    static List<Type>? all;
    internal static List<Type> All() => all ??= ModelCoverage.ApiTypes();

    internal static string Short(Type t) => t.IsGenericType ? t.Name[..t.Name.IndexOf('`')] : t.Name;

    // Model types first (not the extension model, not the plugin API), then plugins: the type a spec most likely means.
    static int Rank(Type t) => t.Namespace!.StartsWith("OutSystems.Model.Extensions") ? 3 : t.Namespace.StartsWith("OutSystems.Model") ? 0
        : t.Namespace.StartsWith("ServiceStudio.PluginAPI") ? 2 : 1;

    /// A type by full name, short name, or short name without the I ("Entity"). Unknown names fail with the closest ones.
    internal static Type Resolve(string name)
    {
        var types = All();
        var exact = types.FirstOrDefault(t => string.Equals(t.FullName, name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var bare = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
        var hits = types.Where(t => string.Equals(Short(t), bare, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(Short(t), "I" + bare, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count > 0)
        {
            var best = hits.Min(Rank);
            var top = hits.Where(t => Rank(t) == best).ToList();
            if (top.Count == 1) return top[0];
            throw new ModelError($"type '{name}' is ambiguous: {string.Join(", ", top.Select(t => t.FullName))}. Write the full name.");
        }
        var close = types.Select(t => (t, d: SpecHelp.Distance(bare.ToLowerInvariant(), Short(t).ToLowerInvariant().TrimStart('i'))))
            .Where(x => x.d <= Math.Max(2, bare.Length / 3) || Short(x.t).Contains(bare.TrimStart('I'), StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.d).ThenBy(x => Rank(x.t)).Select(x => Short(x.t)).Distinct().Take(6).ToList();
        throw new ModelError($"unknown Model API type '{name}'.{(close.Count > 0 ? $" Closest: {string.Join(", ", close)}." : "")} List types with oml spec model-types \"*{bare.TrimStart('I')}*\".");
    }

    static IEnumerable<Type> WithBases(Type t) => new[] { t }.Concat(t.GetInterfaces());

    /// Writable properties and one-argument Set* methods of a type and its bases, by the key a spec writes.
    internal static SortedDictionary<string, Type> Settable(Type t)
    {
        var keys = new SortedDictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in WithBases(t))
        {
            foreach (var p in SafeProperties(i).Where(p => p.CanWrite && p.SetMethod?.IsPublic == true && !History.IsField(p.Name))) keys.TryAdd(p.Name, p.PropertyType);
            foreach (var m in SafeMethods(i).Where(m => m.Name.Length > 3 && m.Name.StartsWith("Set", StringComparison.Ordinal) && !m.IsGenericMethodDefinition && m.GetParameters().Length == 1))
                keys.TryAdd(m.Name[3..], m.GetParameters()[0].ParameterType);
        }
        return keys;
    }

    static PropertyInfo[] SafeProperties(Type t) { try { return t.GetProperties(); } catch (Exception) { return []; } }
    static MethodInfo[] SafeMethods(Type t) { try { return t.GetMethods(); } catch (Exception) { return []; } }

    /// The Model API interfaces an object implements, most specific first (the one oml spec model-types should show).
    internal static Type? Of(object o) => All().Where(t => t.IsInstanceOfType(o))
        .OrderBy(t => Rank(t) == 2 ? 1 : 0).ThenByDescending(t => All().Count(x => x.IsAssignableFrom(t))).ThenBy(Rank).FirstOrDefault();

    /// For an unknown key in set: the closest settable keys of the object's model type, and where to look.
    internal static string SettableHint(object o, string key)
    {
        if (Of(o) is not { } type) return "";
        var keys = Settable(type).Keys.ToList();
        var near = keys.Select(k => (k, d: SpecHelp.Distance(key.ToLowerInvariant(), k.ToLowerInvariant()))).Where(x => x.d <= Math.Max(2, key.Length / 3))
            .OrderBy(x => x.d).Select(x => x.k).Take(3).ToList();
        return $". {(near.Count > 0 ? $"Closest: {string.Join(", ", near)}. " : "")}{Short(type)} takes: {string.Join(", ", keys.Take(30))}{(keys.Count > 30 ? ", ..." : "")} (oml spec model-types {Short(type)})";
    }

    /// What a value of this type is written as in a spec.
    internal static string ValueKind(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(bool)) return "true or false";
        if (t == typeof(int) || t == typeof(long)) return "a whole number";
        if (t == typeof(decimal) || t == typeof(double) || t == typeof(float)) return "a number";
        if (t == typeof(string)) return "text";
        if (t == typeof(DateTime)) return "a date-time as ISO 8601 text";
        if (t.IsEnum) return $"one of {string.Join(", ", Enum.GetNames(t).Take(20))}";
        if (typeof(OutSystems.Model.Types.ITypeSignature).IsAssignableFrom(t) && !typeof(OutSystems.Model.Data.IEntitySignature).IsAssignableFrom(t))
            return "a type name (\"Text\", \"Order Identifier\", \"List of Order\")";
        if (t.IsInterface) return $"the name of a {Short(t)}, or {{ \"element\": \"<path>\" }}";
        return $"a {Short(t)} (a spec cannot write one)";
    }

    internal static string Describe(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => $"the text \"{v.GetString()}\"", JsonValueKind.Number => $"the number {v.GetRawText()}",
        JsonValueKind.True or JsonValueKind.False => v.GetRawText(), JsonValueKind.Object => "an object", JsonValueKind.Array => "a list",
        _ => v.ValueKind.ToString().ToLowerInvariant(),
    };

    /// Whether a value of this JSON kind can be converted at all, without a module; null when it can.
    internal static string? StaticValueProblem(Type type, JsonElement v, string key)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        var ok = v.ValueKind switch
        {
            JsonValueKind.Null => !t.IsValueType || Nullable.GetUnderlyingType(type) is not null,
            JsonValueKind.True or JsonValueKind.False => t == typeof(bool),
            JsonValueKind.Number => t == typeof(int) || t == typeof(long) || t == typeof(decimal) || t == typeof(double) || t == typeof(float),
            JsonValueKind.String => t == typeof(string) || t == typeof(DateTime) || t.IsEnum || t.IsInterface,
            JsonValueKind.Object => t.IsInterface && v.TryGetProperty("element", out _),
            _ => false,
        };
        if (!ok) return $"'{key}' takes {ValueKind(t)}, not {Describe(v)}";
        if (t.IsEnum && v.ValueKind == JsonValueKind.String && !Enum.GetNames(t).Contains(v.GetString(), StringComparer.OrdinalIgnoreCase))
            return $"'{v.GetString()}' is not a {key} value{SpecHelp.Hint(v.GetString()!, Enum.GetNames(t))}; use one of: {string.Join(", ", Enum.GetNames(t).Take(40))}";
        return null;
    }

    /// The Create* methods of `parent` (its interfaces included) that make a `wanted`: by return type, or a generic
    /// Create*<T> whose constraint allows it.
    internal static List<MethodInfo> Factories(Type parent, Type wanted, string? name = null) =>
        WithBases(parent).SelectMany(SafeMethods)
            .Where(m => m.Name.StartsWith("Create", StringComparison.Ordinal) && (name is null || m.Name == name))
            .Where(m => m.IsGenericMethodDefinition
                ? m.GetGenericArguments() is [{ } g] && g.GetGenericParameterConstraints().All(c => c.IsAssignableFrom(wanted)) && m.ReturnType == g
                : wanted.IsAssignableFrom(m.ReturnType) || (name is not null && m.ReturnType.IsAssignableFrom(wanted)))
            .Distinct().OrderBy(m => m.IsGenericMethodDefinition).ThenBy(m => m.GetParameters().Length).ToList();

    /// Every type whose Create* methods make a `wanted`, for the refusal when the parent cannot.
    internal static List<string> Makers(Type wanted) => All().Where(t => Factories(t, wanted).Any(f => f.DeclaringType == t))
        .Select(Short).Distinct().Take(8).ToList();

    internal static string Signature(MethodInfo m) =>
        $"{m.Name}{(m.IsGenericMethodDefinition ? "<T>" : "")}({string.Join(", ", m.GetParameters().Select(p => $"{Short(p.ParameterType)} {p.Name}"))})";
}

partial class Builder
{
    // Set when the spec used a generic operation (add, remove, move, or set with type): apply then also runs the
    // before/after check of `oml review` on the result.
    internal bool UsedModelOps { get; private set; }

    static string? Text(JsonElement edit, string key) => edit.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // { "op": "add", "parent": "<path>" (default: the module), "type": "IEntityAttribute", "factory": "CreateAttribute" (optional),
    //   "name": "Phone", "args": { <factory argument>: value }, "properties": { <key>: value }, "before"|"after": "<sibling>", "first": true }
    void AddOp(JsonElement edit, string label)
    {
        UsedModelOps = true;
        var parentPath = Text(edit, "parent");
        IObjectSignature parent = string.IsNullOrEmpty(parentPath) ? m : ElementPaths.Resolve(m, parentPath, write: true);
        if (ElementPaths.Referenced(parent)) throw ProductionRules.Consumed(parent);
        var factoryName = Text(edit, "factory");
        var typeName = Text(edit, "type");
        if (typeName is null && factoryName is null) throw new ModelError("add needs \"type\" (the Model API type to make, for example \"IEntityAttribute\") or \"factory\" (a Create* method of the parent)");
        var wanted = typeName is null ? typeof(object) : ModelTypes.Resolve(typeName);
        var parentType = ModelTypes.Of(parent) ?? parent.GetType();
        var factories = ModelTypes.Factories(parent.GetType(), wanted, factoryName);
        if (factories.Count == 0)
        {
            var makers = typeName is null ? [] : ModelTypes.Makers(wanted);
            var own = ModelTypes.Factories(parent.GetType(), typeof(object)).Where(f => f.Name.StartsWith("Create")).Select(f => f.Name).Distinct().Take(15).ToList();
            throw new ModelError($"{ModelTypes.Short(parentType)} '{parentPath ?? "(module)"}' has no {factoryName ?? "Create* method"}{(typeName is null ? "" : $" that makes a {ModelTypes.Short(wanted)}")}."
                + (makers.Count > 0 ? $" A {ModelTypes.Short(wanted)} is made by: {string.Join(", ", makers)}; point \"parent\" at one of those." : "")
                + (own.Count > 0 ? $" This parent makes: {string.Join(", ", own)}." : "") + $" See oml spec model-types {ModelTypes.Short(parentType)}.");
        }
        var name = Text(edit, "name");
        var args = edit.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        // The first factory whose arguments the spec gives: name, key (oml makes a new one), the rest from "args" by parameter name.
        string? why = null;
        foreach (var factory in factories)
        {
            var method = factory.IsGenericMethodDefinition ? factory.MakeGenericMethod(wanted) : factory;
            var values = new List<object?>();
            foreach (var p in method.GetParameters())
            {
                if (p.ParameterType == typeof(IKey)) { values.Add(services.NewKey()); continue; }
                if (args.ValueKind == JsonValueKind.Object && args.EnumerateObject().Where(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)).Select(x => (JsonElement?)x.Value).FirstOrDefault() is { } given)
                { values.Add(Reflect.Convert(m, given, p.ParameterType, $"args.{p.Name}")); continue; }
                // The first text parameter is the new element's name (CreateAttribute(name, key), CreateRecord(identifier, key)).
                if (p.ParameterType == typeof(string) && name is not null && !method.GetParameters().TakeWhile(x => x != p).Any(x => x.ParameterType == typeof(string)))
                { values.Add(name); continue; }
                if (p.HasDefaultValue) { values.Add(p.DefaultValue); continue; }
                why = $"{ModelTypes.Signature(factory)} needs \"{(p.Name == "name" ? "name" : "args." + p.Name)}\" ({ModelTypes.ValueKind(p.ParameterType)})";
                values = null!;
                break;
            }
            if (values is null) continue;
            object? made;
            try { made = method.Invoke(parent, values.ToArray()); }
            catch (TargetInvocationException e) { throw new ModelError($"{ModelTypes.Signature(factory)}: {(e.InnerException ?? e).Message}"); }
            if (made is not IObjectSignature element) throw new ModelError($"{ModelTypes.Signature(factory)} made nothing");
            if (name is not null && Model.NameOf(element) is { } actual && actual != name && !Fresh(element, name, label)) return;
            if (name is not null && Model.NameOf(element) is null && Reflect.Property(element, "Name", writable: true) is { } named) named.SetValue(element, name);
            Position(element, edit);
            if (edit.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                foreach (var p in props.EnumerateObject())
                {
                    if (p.Name.Equals("name", StringComparison.OrdinalIgnoreCase)) { problems.Add($"{label}.properties.name: give the name as \"name\" of the add, not as a property"); continue; }
                    Queue(label, m, element, p.Name, p.Value);
                }
            return;
        }
        throw new ModelError(why ?? $"no Create* method of {ModelTypes.Short(parentType)} fits");
    }

    // { "op": "remove", "element": "<path>" }: the same removal as delete. An existing entity or attribute is refused (rule 2);
    // any other existing element saves only with the user's yes (rule 3, needsApproval).
    void RemoveOp(JsonElement edit, string label)
    {
        UsedModelOps = true;
        var path = Text(edit, "element") ?? throw new ModelError("'element' is required");
        ElementPaths.Resolve(m, path, write: true);
        nestedEdits++;
        try { EditOne(JsonSerializer.SerializeToElement(new { op = "delete", element = path }), label); }
        finally { nestedEdits--; }
    }

    // { "op": "move", "element": "<path>", "before"|"after": "<sibling name>" or "first"|"last": true }: the order in its list
    // (attributes, parameters, widgets, static records). Order is not a rename or a type change: existing elements may move.
    void MoveOp(JsonElement edit, string label)
    {
        UsedModelOps = true;
        var path = Text(edit, "element") ?? throw new ModelError("'element' is required");
        var target = ElementPaths.Resolve(m, path, write: true);
        if (ElementPaths.Referenced(target)) throw ProductionRules.Consumed(target);
        if (!Position(target, edit)) throw new ModelError("move needs \"before\" or \"after\" (a sibling's name), or \"first\": true or \"last\": true");
    }

    // Moves an element within the ordered list of its parent that holds it. False when the edit names no position.
    bool Position(IObjectSignature element, JsonElement edit)
    {
        var before = Text(edit, "before");
        var after = Text(edit, "after");
        var first = edit.TryGetProperty("first", out var f) && f.ValueKind == JsonValueKind.True;
        var last = edit.TryGetProperty("last", out var l) && l.ValueKind == JsonValueKind.True;
        if (before is null && after is null && !first && !last) return false;
        var parent = element.Parent ?? throw new ModelError("the element has no parent to order it in");
        // The ISequence<T> property of the parent that holds the element.
        foreach (var p in parent.GetType().GetInterfaces().Concat([parent.GetType()]).SelectMany(i => i.GetProperties()).Where(p => p.GetIndexParameters().Length == 0))
        {
            var seqType = p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition().Name == "ISequence`1" ? p.PropertyType
                : p.PropertyType.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition().Name == "ISequence`1");
            if (seqType is null) continue;
            object? sequence;
            try { sequence = p.GetValue(parent); } catch (Exception) { continue; }
            if (sequence is not System.Collections.IEnumerable items || !items.Cast<object>().Contains(element)) continue;
            var siblings = items.Cast<object>().ToList();
            object Sibling(string name) => siblings.FirstOrDefault(s => !ReferenceEquals(s, element) && Model.NameOf(s) == name)
                ?? throw new ModelError($"no sibling named '{name}' in {p.Name}; siblings: {string.Join(", ", siblings.Select(Model.NameOf).Where(n => n is not null).Take(30))}");
            var item = seqType.GetGenericArguments()[0];
            MethodInfo Op(string n) => seqType.GetMethod(n) ?? throw new ModelError($"{p.Name} cannot be reordered on this build");
            try
            {
                if (first) Op("MoveToStart").Invoke(sequence, [element]);
                else if (last) Op("MoveToEnd").Invoke(sequence, [element]);
                else
                {
                    // MoveBeforeSibling(T sibling, T element): bound by parameter name, so a build that swaps them still works.
                    var op = Op(before is not null ? "MoveBeforeSibling" : "MoveAfterSibling");
                    var sibling = Sibling(before ?? after!);
                    op.Invoke(sequence, op.GetParameters().Select(x => x.Name == "sibling" ? sibling : (object)element).ToArray());
                }
            }
            catch (TargetInvocationException e) { throw new ModelError($"move in {p.Name}: {(e.InnerException ?? e).Message}"); }
            _ = item;
            return true;
        }
        throw new ModelError($"'{Model.NameOf(element)}' is not in an ordered list of its parent; only attributes, parameters, widgets, records and other ordered lists can be moved");
    }

    // edits set { "onClick": "<screen action or screen>" } or { "onClick": { "destination": ..., "args": {...} } } on an existing widget.
    void WidgetEventEdit(IObjectSignature widget, string evt, JsonElement value, string label)
    {
        var holder = Ancestors(widget).FirstOrDefault(o => o is OutSystems.Model.UI.IScreenSignature || Inspect.IsBlock(o) || o is OutSystems.Model.UI.IEmail)
            ?? throw new ModelError($"{evt}: the widget is not in a screen, block or email");
        var actions = Elements(Reflect.Get(holder, "ScreenActions")).Where(x => Model.NameOf(x) is not null).GroupBy(x => Model.NameOf(x)!).ToDictionary(g => g.Key, g => g.First());
        var target = value.ValueKind == JsonValueKind.String ? value.GetString()!
            : (value.TryGetProperty("destination", out var d) ? d : value.TryGetProperty("action", out var a) ? a : default).ValueKind == JsonValueKind.String
                ? (value.TryGetProperty("destination", out d) ? d : value.GetProperty("action")).GetString()!
                : throw new ModelError($"{evt} needs a screen action or screen name, or {{ \"destination\": ..., \"args\": {{...}} }}");
        var handler = actions.GetValueOrDefault(target) ?? Refs.Destination(m, target);
        Event(widget, evt, handler);
        // The old handler's arguments stay on the event with no parameter ("? = ClickedColumn"): they go.
        if (Reflect.Get(widget, evt) is { } eventNow && Reflect.Get(eventNow, "Arguments") is System.Collections.IEnumerable oldArgs)
        {
            var inputs = Elements(Reflect.Get(handler, "InputParameters")).ToHashSet();
            foreach (var arg in oldArgs.Cast<object>().ToList())
                if (Reflect.Get(arg, "Parameter") is not { } parameter || !inputs.Contains(parameter)) Reflect.Call(arg, "Delete");
        }
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("args", out var args) && Reflect.Get(widget, evt) is { } eventObject)
            Defer(widget, ($"{label}.{evt}.args", () => Arguments(eventObject, args)));
    }

    // A `set` with "type": the element must be of that Model API type, and every key must be one it takes.
    void CheckSetType(IObjectSignature target, JsonElement edit, string path)
    {
        if (Text(edit, "type") is not { } typeName) return;
        UsedModelOps = true;
        var type = ModelTypes.Resolve(typeName);
        if (!type.IsInstanceOfType(target))
            throw new ModelError($"'{path}' is a {ModelTypes.Short(ModelTypes.Of(target) ?? target.GetType())}, not a {ModelTypes.Short(type)}");
    }
}
