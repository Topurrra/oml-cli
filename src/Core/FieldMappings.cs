using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using OutSystems.Model;

namespace OmlCli;

// Per-field record mappings. When a record is passed to a parameter or assigned to a variable of another record type,
// Service Studio keeps one mapping per target field (`LastId = Source.List.Current.Id`). The typed V1 model does not
// list them as children, so tree, describe and every usage check missed them. They hang off an Argument or an
// Assignment (ValueTypeConversion.AttributesMappings), and each one holds a source expression and a target field.
// oml shows each as a child "FieldMapping" named after the target field, with the source expression below it.
static class FieldMappings
{
    public const string Kind = "FieldMapping";

    public static bool Is(object o) => o.GetType().Name == "AttributeMapping";

    static readonly ConcurrentDictionary<Type, PropertyInfo?> conversions = new();

    // The mappings that carry a decision: all but the default one of a field that the source record also has
    // (`Name = Name`). An unmapped field is listed too, so that `set` can fill it.
    public static List<IObjectSignature> Of(object owner)
    {
        var property = conversions.GetOrAdd(owner.GetType(), t => t.GetProperty("ValueTypeConversion", BindingFlags.Public | BindingFlags.Instance));
        if (property is null) return [];
        try
        {
            if (property.GetValue(owner) is not { } conversion || Reflect.Get(conversion, "AttributesMappings") is not System.Collections.IEnumerable all) return [];
            return all.OfType<IObjectSignature>().Where(m => !m.WasDeleted && Field(m) is not null && !IsDefault(m)).ToList();
        }
        catch (TargetInvocationException) { return []; } // a referenced module's proxy carries no body
    }

    public static string? Field(object mapping) => (Reflect.Get(Part(mapping, "TargetValue") ?? mapping, "Value") as IExpression)?.Text is { Length: > 0 } t ? t : null;

    public static IExpression? Source(object mapping) => Part(mapping, "SourceValue") is { } s ? Reflect.Get(s, "Value") as IExpression : null;

    // Empty text for a field with no source yet.
    public static string Text(object mapping) => Source(mapping)?.Text ?? "";

    static bool IsDefault(object mapping) => Text(mapping) == Field(mapping);

    static object? Part(object mapping, string name) => Reflect.Get(mapping, name);

    public static void SetSource(object mapping, string text) =>
        Reflect.Call(Part(mapping, "SourceValue") ?? throw new ModelError("field mapping has no source"), "SetValue", text);

    // ` { A = x, B = y }` for the mappings with a source, or "".
    public static string Suffix(object owner) =>
        Of(owner).Where(m => Text(m).Length > 0).ToList() is { Count: > 0 } set
            ? " { " + string.Join(", ", set.Select(m => Field(m) + " = " + Text(m)).Order(StringComparer.Ordinal)) + " }" : "";

    // Every expression under `root`, through the model: children (field mappings included) and the expression
    // properties that are not children. Same walk as `oml expr --corpus`. `owner` is the part that holds it.
    public static IEnumerable<(IObjectSignature owner, string text)> Expressions(IObjectSignature root)
    {
        var seen = new HashSet<IObjectSignature>();
        var done = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<IObjectSignature>();
        stack.Push(root);
        while (stack.TryPop(out var o))
        {
            if (!seen.Add(o) || o.WasDeleted) continue;
            if (o is IExpression { Text.Length: 0 }) continue;
            if (o is IExpression e && done.Add(e)) yield return (o, e.Text);
            foreach (var p in o.GetType().GetProperties().Where(p => typeof(IExpression).IsAssignableFrom(p.PropertyType) && p.GetIndexParameters().Length == 0))
            {
                string? text;
                object? value;
                try { value = p.GetValue(o); text = value is IExpression { Root: not null } x ? x.Text : null; }
                catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or NotSupportedException) { continue; }
                if (!string.IsNullOrWhiteSpace(text) && done.Add(value!)) yield return (value as IObjectSignature ?? o, text!);
            }
            foreach (var c in ElementPaths.Children(o)) stack.Push(c);
        }
    }

    // True when the expression text reads an output of the node (`Node.List`, `Node.Count`), not a string or a longer name.
    public static bool Reads(string text, string node) =>
        Regex.IsMatch(Regex.Replace(text, "\"(?:[^\"]|\"\")*\"", "\"\""), @"(?<![\w.])" + Regex.Escape(node) + @"\s*\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
