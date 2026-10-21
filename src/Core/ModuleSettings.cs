using System.Text.Json;
using OutSystems.Model;

namespace OmlCli;

static class ModuleSettings
{
    internal static object Inspect(IESpace m) => new {
        key = ((OutSystems.Model.Applications.IModule)m).Key.ToString(), description = ((OutSystems.Model.Applications.IModule)m).Description, javaScript = m.Kind.ToString() == "Service" ? null : Reflect.Get(m, "JavaScript"), // a Service module has none: the getter throws
        isUserProvider = Reflect.Get(m, "IsUserProvider"), isMultiTenant = Reflect.Get(m, "IsMultiTenant"),
        dbms = Reflect.Get(m, "DBMS")?.ToString(), icon = new { size = (Reflect.Get(m, "Icon") as byte[])?.Length ?? 0 },
        defaultTheme = Theme(m) is { } t ? Model.NameOf(t) : null
    };
    static object? Theme(IESpace m) => Reflect.Get(m, WebUi.IsTraditional(m) ? "DefaultWebTheme" : "DefaultMobileTheme");
    internal static void Apply(IESpace m, JsonElement? settings, List<string> problems)
    {
        if (settings is null) return;
        if (settings.Value.ValueKind != JsonValueKind.Object) { problems.Add("module: expected object"); return; }
        foreach (var p in settings.Value.EnumerateObject())
        try {
            if (p.Name == "icon") {
                var bytes = File.ReadAllBytes(p.Value.GetString()!);
                if (bytes.Length < 8 || !bytes.Take(8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) throw new ModelError("icon must be a PNG");
                (Reflect.Property(m, "Icon", true) ?? throw new ModelError("Icon is not writable")).SetValue(m, bytes);
            } else if (p.Name == "defaultTheme") {
                var property = Reflect.Property(m, WebUi.IsTraditional(m) ? "DefaultWebTheme" : "DefaultMobileTheme", true)
                    ?? throw new ModelError("Default theme is not writable");
                var name = p.Value.GetString();
                var candidates = new[] { (object)m }.Concat(m.References).SelectMany(o =>
                    new[] { "MobileThemes", "WebThemes" }.SelectMany(key => Reflect.Get(o, key) is System.Collections.IEnumerable items ? items.Cast<object>() : Array.Empty<object>()));
                var hits = candidates.Where(t => property.PropertyType.IsInstanceOfType(t) && Model.NameOf(t) == name).Distinct().ToArray();
                if (hits.Length != 1) throw new ModelError($"Theme '{name}' not found or ambiguous");
                property.SetValue(m, hits[0]);
            } else if (p.Name == "styleSheet") {
                // OutSystems 11 has no module style sheet: the theme holds module-wide CSS.
                throw new ModelError("a module has no style sheet of its own; put module-wide CSS in one of its themes: \"themes\": [{ \"name\": \"<Theme>\", \"styleSheet\": \"...\" }] replaces the styleSheet of an existing theme or creates the theme; \"module\": { \"defaultTheme\": \"<Theme>\" } selects it");
            } else {
                if (p.Name is not ("description" or "javaScript" or "isUserProvider" or "isMultiTenant")) throw new ModelError($"unsupported setting '{p.Name}'");
                if (p.Name is "isUserProvider" or "isMultiTenant") {
                    if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ModelError("expected boolean");
                } else if (p.Value.ValueKind != JsonValueKind.String) throw new ModelError("expected string");
                Reflect.Plan(m, m, p.Name, p.Value).apply();
            }
        } catch (Exception e) { problems.Add($"module.{p.Name}: {(e.InnerException ?? e).Message}"); }
    }
}
