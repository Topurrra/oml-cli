using System.Text.Json;
using OutSystems.Model;

namespace OmlCli;

static class ModuleSettings
{
    internal static object Inspect(IESpace m) => new {
        key = ((OutSystems.Model.Applications.IModule)m).Key.ToString(), description = ((OutSystems.Model.Applications.IModule)m).Description, javaScript = m.Kind.ToString() == "Service" ? null : Reflect.Get(m, "JavaScript"), // a Service module has none: the getter throws
        isUserProvider = Reflect.Get(m, "IsUserProvider"), isMultiTenant = Reflect.Get(m, "IsMultiTenant"),
        dbms = Reflect.Get(m, "DBMS")?.ToString(), icon = new { size = (Reflect.Get(m, "Icon") as byte[])?.Length ?? 0 },
        defaultTheme = Theme(m) is { } t ? Model.NameOf(t) : null,
        settings = Settings.Keys.Where(k => k is not ("isUserProvider" or "isMultiTenant")).Select(k => (k, v: Read(m, k))).Where(x => x.v is not null)
            .ToDictionary(x => x.k, x => x.v)
    };

    static object? Read(IESpace m, string key)
    {
        try { return Reflect.Get(m, char.ToUpperInvariant(key[0]) + key[1..]) is { } v ? v is Enum ? v.ToString() : v : null; }
        catch (Exception) { return null; }   // a setting the module kind does not have
    }

    // The module's other settings (Service Studio: the module's properties), by JSON kind. Validation messages are Text
    // expressions; jQueryVersion, defaultTransition and webScreenRenderingMode take a value name of their list.
    static readonly Dictionary<string, JsonValueKind> Settings = new()
    {
        ["isUserProvider"] = JsonValueKind.True, ["isMultiTenant"] = JsonValueKind.True,
        ["useCookies"] = JsonValueKind.True, ["usesCache"] = JsonValueKind.True, ["useDefaultThemeExceptionHandler"] = JsonValueKind.True,
        ["serverRequestTimeout"] = JsonValueKind.Number,
        ["jQueryVersion"] = JsonValueKind.String, ["defaultTransition"] = JsonValueKind.String, ["webScreenRenderingMode"] = JsonValueKind.String,
        ["webServicesNamespace"] = JsonValueKind.String,
        ["mandatoryInput"] = JsonValueKind.String, ["invalidText"] = JsonValueKind.String, ["invalidInteger"] = JsonValueKind.String,
        ["invalidDecimal"] = JsonValueKind.String, ["invalidCurrency"] = JsonValueKind.String, ["invalidDate"] = JsonValueKind.String,
        ["invalidTime"] = JsonValueKind.String, ["invalidDateTime"] = JsonValueKind.String, ["invalidEmail"] = JsonValueKind.String,
        ["invalidPhone"] = JsonValueKind.String, ["invalidNumericPassword"] = JsonValueKind.String,
        ["upgradeComplete"] = JsonValueKind.String, ["upgradeFailed"] = JsonValueKind.String, ["upgradeFailedOnDataModel"] = JsonValueKind.String,
        ["upgradeFailedOnResources"] = JsonValueKind.String, ["upgradeRequired"] = JsonValueKind.String, ["upgradeRequiredWithDataLoss"] = JsonValueKind.String,
    };
    static object? Theme(IESpace m) => Reflect.Get(m, WebUi.IsTraditional(m) ? "DefaultWebTheme" : "DefaultMobileTheme");
    internal static void Apply(IESpace m, JsonElement? settings, List<string> problems, List<string>? warnings = null)
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
                if (p.Name is not ("description" or "javaScript") && !Settings.ContainsKey(p.Name))
                    throw new ModelError($"unsupported setting '{p.Name}'. Settings: description, javaScript, icon, defaultTheme, {string.Join(", ", Settings.Keys)}");
                // A Reactive or Mobile module stores this text but never loads it in the app (no window object appears).
                if (p.Name == "javaScript" && !WebUi.IsTraditional(m) && m.Kind.ToString() != "Service" && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Trim().Length > 0)
                    warnings?.Add("module.javaScript: a Reactive or Mobile module never runs module JavaScript in the app. Put the code in a Script (scripts: [{ name, file }]) and list it in requiredScripts of each screen or block that needs it");
                var kind = Settings.GetValueOrDefault(p.Name, JsonValueKind.String);
                if (kind == JsonValueKind.True ? p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) : p.Value.ValueKind != kind)
                    throw new ModelError(kind switch { JsonValueKind.True => "expected true or false", JsonValueKind.Number => "expected a number (seconds)", _ => "expected a string" });
                if (Reflect.Property(m, char.ToUpperInvariant(p.Name[0]) + p.Name[1..], writable: true) is null && !Reflect.Has(m, "Set" + char.ToUpperInvariant(p.Name[0]) + p.Name[1..]))
                    throw new ModelError($"this {m.Kind} module has no setting '{p.Name}'");
                Reflect.Plan(m, m, p.Name, p.Value).apply();
            }
        } catch (Exception e) { problems.Add($"module.{p.Name}: {(e.InnerException ?? e).Message}"); }
    }
}
