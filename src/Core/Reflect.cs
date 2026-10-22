using System.Reflection;
using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Types;

namespace OmlCli;

// The reflection contract (skill §6): read and write through the declaring model interfaces, fall back to the
// concrete class, and match Set*(string) setters by name because their names drift between builds
// (SetJSON -> SetJSONString, SetMessage -> SetExceptionMessage).
static class Reflect
{
    // Lookups are cached per concrete type: a big module repeats the same few types thousands of times, and the
    // uncached reflection was a large part of an apply. The results are the same, in the same order.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Type[]> interfaces = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, MethodInfo[]> methods = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string, bool), PropertyInfo?> properties = new();

    // An assembly's types, read once: widget and node kinds are looked up by name for every widget and node.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<Assembly, Type[]> types = new();
    internal static Type[] Types(Assembly assembly) => types.GetOrAdd(assembly, a => a.GetTypes());

    internal static Type[] Interfaces(object o) => interfaces.GetOrAdd(o.GetType(), t =>
        t.GetInterfaces().Where(i => i.Namespace is { } ns &&
            (ns.StartsWith("OutSystems.Model") || ns.StartsWith("ServiceStudio.Plugin"))).ToArray());

    static MethodInfo[] Methods(object o) => methods.GetOrAdd(o.GetType(), _ =>
        Interfaces(o).SelectMany(i => i.GetMethods()).Concat(o.GetType().GetMethods()).ToArray());

    public static PropertyInfo? Property(object o, string name, bool writable = false) =>
        properties.GetOrAdd((o.GetType(), name, writable), _ =>
            Interfaces(o).Select(i => i.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase))
                .Concat(new[] { o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) })
                .FirstOrDefault(p => p is not null && (!writable || p.CanWrite)));

    public static object? Get(object o, string name)
    {
        var property = Property(o, name);
        if (property is null || !Unreadable.Active) return property?.GetValue(o);
        try { return property.GetValue(o); }
        catch (TargetInvocationException e) when (ProxyLacks(o, e)) { return null; }
        catch (TargetInvocationException e)
        { Unreadable.Add(() => Unreadable.Where(o) + " property " + property.Name, e); return null; }
    }

    // A referenced module's element is a proxy that carries the signature only: its body getters (a block's Widgets,
    // an argument's Parameter) throw NotSupportedException, NullReferenceException or InvalidCastException. That part does not exist there.
    internal static bool ProxyLacks(object o, TargetInvocationException e) =>
        o.GetType().Name.StartsWith("Reference", StringComparison.Ordinal) && e.InnerException is NotSupportedException or NullReferenceException or InvalidCastException;

    public static object? Call(object o, string name, params object?[] args)
    {
        var method = Methods(o).FirstOrDefault(m => m.Name == name && !m.IsGenericMethodDefinition && Fits(m, args))
                     ?? throw new ModelError($"{o.GetType().Name} has no {name}({args.Length} args) on this build");
        return Invoke(method, o, args);
    }

    public static object? CallGeneric(object o, string name, Type typeArg, params object?[] args)
    {
        var method = Methods(o).FirstOrDefault(m => m.Name == name && m.IsGenericMethodDefinition && m.GetParameters().Length == args.Length)
                     ?? throw new ModelError($"{o.GetType().Name} has no {name}<T> on this build");
        return Invoke(method.MakeGenericMethod(typeArg), o, args);
    }

    public static bool Has(object o, string name) => Methods(o).Any(m => m.Name == name);

    static bool Fits(MethodInfo m, object?[] args)
    {
        var ps = m.GetParameters();
        return ps.Length == args.Length && ps.Zip(args).All(p => p.Second is null
            ? !p.First.ParameterType.IsValueType
            : p.First.ParameterType.IsInstanceOfType(p.Second));
    }

    static object? Invoke(MethodInfo m, object o, object?[] args)
    {
        try { return m.Invoke(o, args); }
        catch (TargetInvocationException e) { throw new ModelError($"{m.Name}: {e.InnerException?.Message}"); }
    }

    // A Set*(string) method for `key`: exact Set{key}, else (fuzzy) a unique setter whose name ends or starts with it.
    static MethodInfo? Setter(object o, string key, Type argType, bool fuzzy)
    {
        var candidates = Methods(o).Where(m => m.Name.StartsWith("Set") && !m.IsGenericMethodDefinition
            && m.GetParameters() is [{ } p] && p.ParameterType == argType).ToList();
        var exact = candidates.FirstOrDefault(m => m.Name.Equals("Set" + key, StringComparison.OrdinalIgnoreCase));
        if (exact is not null || !fuzzy) return exact;
        var matches = candidates.Where(m => m.Name[3..].EndsWith(key, StringComparison.OrdinalIgnoreCase)
                                          || m.Name[3..].StartsWith(key, StringComparison.OrdinalIgnoreCase))
            .GroupBy(m => m.Name).ToList();
        return matches.Count == 1 ? matches[0].First() : null;
    }

    public enum Stage { Reference, Expression }

    /// Plans one property write. References (types, actions, exceptions, enums) are applied in the reference
    /// stage; expressions are applied last, after every node, variable and parameter exists (skill §9.2).
    public static (Stage stage, Action apply) Plan(IESpace m, object o, string key, JsonElement value)
    {
        if (History.IsField(key)) throw new ModelError("history fields are written by Service Studio; oml does not change them");
        // Exact setter, then exact writable property, then a fuzzy setter: "exception" must reach the Exception
        // property, not SetExceptionMessage, while "message" may still find SetExceptionMessage.
        if (value.ValueKind == JsonValueKind.String && Setter(o, key, typeof(string), fuzzy: false) is { } set)
            return (Stage.Expression, () => Invoke(set, o, new object?[] { value.GetString() }));
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False && Setter(o, key, typeof(bool), fuzzy: false) is { } setBool)
            return (Stage.Reference, () => Invoke(setBool, o, new object?[] { value.GetBoolean() }));
        var prop = Property(o, key, writable: true);
        if (prop is null && value.ValueKind == JsonValueKind.String && Setter(o, key, typeof(string), fuzzy: true) is { } near)
            return (Stage.Expression, () => Invoke(near, o, new object?[] { value.GetString() }));
        if (prop is null) throw new ModelError($"{o.GetType().Name} has no settable '{key}' on this build");
        var t = prop.PropertyType;
        var target = Nullable.GetUnderlyingType(t) ?? t;
        return (Stage.Reference, () =>
        {
            object? converted = value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.True or JsonValueKind.False => value.GetBoolean(),
                JsonValueKind.Number when target == typeof(int) => value.GetInt32(),
                JsonValueKind.String when target == typeof(string) => value.GetString(),
                JsonValueKind.String when target.IsEnum => Enum.Parse(target, value.GetString()!, ignoreCase: true),
                JsonValueKind.String when typeof(ITypeSignature).IsAssignableFrom(target) =>
                    Model.Resolve(m, value.GetString()!) ?? throw new ModelError(Model.UnknownType(value.GetString()!)),
                JsonValueKind.String => Refs.Find(m, value.GetString()!, target),
                _ => throw new ModelError($"cannot convert {value.ValueKind} for '{key}'"),
            };
            try { prop.SetValue(o, converted); }
            catch (TargetInvocationException e) { throw new ModelError($"{key}: {e.InnerException?.Message}"); }
        });
    }
}

// A model write that failed: reported as a problem, never swallowed (skill rule 12).
class ModelError(string message) : Exception(message);

// Name lookup of model elements a spec can point at: actions (including generated entity CRUD actions and
// referenced producers' actions), exceptions, screens, blocks, entities and structures.
static class Refs
{
    public const string ReferenceHint =
        "If it lives in another module, add that module in Service Studio (Manage Dependencies) or pass its producer .oml in \"references\"; " +
        "'oml find <Name> --in <folder of .oml files>' lists the modules that have it.";

    /// Resolves a spec name to one model element. "Module.Element" picks that module (this module or a
    /// reference). A plain name prefers this module's own element, then a reference that alone has it, and
    /// refuses when several references have it, because picking one silently can call the wrong action.
    public static object Find(IESpace m, string name, Type wanted)
    {
        // Built-in exceptions have no Name; specs use the IESpace property names.
        if (typeof(OutSystems.Model.Logic.IException).IsAssignableFrom(wanted)
            && !m.UserExceptions.Any(e => e.Name == name)
            && Reflect.Get(m, name) is { } system && wanted.IsInstanceOfType(system)) return system;
        return Pick(m, name,
            wanted == typeof(IObjectSignature) ? "action or element" : wanted.Name.TrimStart('I').Replace("Signature", ""),
            All(m).Where(c => wanted.IsInstanceOfType(c.element)).ToList(),
            () => RestModel.FindAction(m, name, wanted))!;
    }

    // missingIsNull: report "not found" as null (types: the caller names the unknown type); ambiguity always throws.
    public static T? Pick<T>(IESpace m, string name, string kind, List<(string owner, T element)> pool, Func<T?>? fallback = null,
        bool missingIsNull = false) where T : class
    {
        var own = ((OutSystems.Model.Applications.IModule)m).Name;
        var dot = name.IndexOf('.');
        var (module, element) = dot > 0 ? (name[..dot], name[(dot + 1)..]) : ((string?)null, name);
        var hits = pool.Where(c => Model.NameOf(c.element) == element).GroupBy(c => c.element).Select(g => g.First()).ToList();
        if (module is not null)
        {
            if (hits.FirstOrDefault(c => c.owner == module).element is { } qualified) return qualified;
            if (fallback?.Invoke() is { } other) return other; // e.g. a REST Client.Method
            if (missingIsNull) return null;
            throw new ModelError(pool.Any(c => c.owner == module)
                ? $"module '{module}' has no {kind} named '{element}'"
                : $"no module '{module}' among this module and its references ({string.Join(", ", pool.Select(c => c.owner).Distinct())}). {ReferenceHint}");
        }
        if (hits.FirstOrDefault(c => c.owner == own).element is { } mine) return mine;
        var owners = hits.Select(c => c.owner).Distinct().ToList();
        if (owners.Count == 1) return hits[0].element;
        if (owners.Count > 1)
            throw new ModelError($"'{element}' is ambiguous: {string.Join(", ", hits.OrderBy(c => c.owner, StringComparer.Ordinal).Select(c => $"{c.owner}.{element}" + (c.element is IObjectSignature o ? $" ({Describe.Kind(o)})" : "")))}. Write the qualified name.");
        return fallback?.Invoke() ?? (missingIsNull ? null : throw new ModelError($"no {kind} named '{name}' in this module or its references. {ReferenceHint}"));
    }

    // Every element a spec can point at, with the module that owns it.
    public static IEnumerable<(string owner, object element)> All(IESpace m)
    {
        var own = ((OutSystems.Model.Applications.IModule)m).Name;
        foreach (var o in Owned(m)) yield return (own, o);
        foreach (var r in m.References)
            foreach (var o in Referenced(r)) yield return (r.Name, o);
    }

    static IEnumerable<object> Owned(IESpace m)
    {
        foreach (var o in m.ServerActions) yield return o;
        foreach (var o in m.ClientActions) yield return o;
        foreach (var o in m.ServiceActions) yield return o;
        foreach (var e in m.Entities) foreach (var a in EntityActions(e)) yield return a;
        foreach (var role in m.Roles)
        {
            yield return role;
            foreach (var key in new[] { "CheckRoleAction", "GrantRoleAction", "RevokeRoleAction" })
                if (Reflect.Get(role, key) is { } action) yield return action;
        }
        foreach (var o in m.UserExceptions) yield return o;
        foreach (var name in new[] { "AllExceptions", "UserException", "DatabaseException", "SecurityException",
                     "NotRegisteredException", "CommunicationException", "InvalidLoginException" })
            if (Reflect.Get(m, name) is { } ex) yield return ex;
        foreach (var f in m.MobileFlows) foreach (var n in f.Nodes) yield return n;
        foreach (var f in m.WebFlows) foreach (var n in f.Nodes) yield return n;
        foreach (var o in m.Entities) yield return o;
        foreach (var o in m.Structures) yield return o;
        foreach (var o in m.Images) yield return o;
        foreach (var o in m.Scripts) yield return o;
        foreach (var o in m.Resources) yield return o;
        foreach (var o in m.MobileThemes) yield return o;
        foreach (var o in m.WebThemes) yield return o;
    }

    static IEnumerable<object> Referenced(IReference r)
    {
        foreach (var o in ModuleReferences.Elements(r).Where(o => o is OutSystems.Model.UI.IImageSignature
            or OutSystems.Model.UI.IScriptSignature or OutSystems.Model.UI.IThemeSignature or OutSystems.Model.Data.IResourceSignature)) yield return o;
        foreach (var o in ModuleReferences.Elements(r).OfType<OutSystems.Model.UI.IScreenSignature>()) yield return o;
        foreach (var o in r.ServerActions) yield return o;
        foreach (var o in r.ClientActions) yield return o;
        foreach (var o in r.ServiceActions) yield return o;
        foreach (var e in r.Entities) foreach (var a in EntityActions(e)) yield return a;
        foreach (var f in r.MobileFlows) foreach (var n in f.Nodes) yield return n;
        foreach (var f in r.WebFlows) foreach (var n in f.Nodes) yield return n;
        foreach (var o in r.Entities) yield return o;
        foreach (var o in r.Structures) yield return o;
        foreach (var role in r.Roles)
        {
            yield return role;
            foreach (var key in new[] { "CheckRoleAction", "GrantRoleAction", "RevokeRoleAction" })
                if (Reflect.Get(role, key) is { } action) yield return action;
        }
    }

    // Generated CRUD actions live only on the concrete class (skill §6.3), and only after a save and reopen.
    static IEnumerable<object> EntityActions(object entity) =>
        entity.GetType().GetProperty("EntityActions")?.GetValue(entity) is System.Collections.IEnumerable actions
            ? actions.Cast<object>() : Enumerable.Empty<object>();
}
