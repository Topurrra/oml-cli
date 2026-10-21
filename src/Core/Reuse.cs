using System.Text.Json;
using System.Text.Json.Nodes;
using OutSystems.Model;
using OutSystems.Model.Data;
using OutSystems.Model.Types;

namespace OmlCli;

// An existing element listed in a creation list. New parts are added under the same rules as addParts and
// addVariables; a setting the spec gives that differs from the module is a problem naming the key and the op to use.
// Flows are compared by node names and count only. UI settings (styleSheet, a text title, requiredScripts) are
// replaced and listed in the report's "changed"; widgets, placeholders, layout and processes are not compared, and
// the report's warnings name the edit op for them.
static class Reuse
{

    // The spec's own JSON for one listed element, to tell a key the spec sets from a record default.
    public static JsonObject? Item(JsonNode? parent, string list, string name, string nameKey = "name") =>
        Get(parent as JsonObject, list) is JsonArray items
            ? items.OfType<JsonObject>().FirstOrDefault(i => Get(i, nameKey) is JsonValue v && v.TryGetValue<string>(out var n) && n == name)
            : null;

    public static JsonNode? Get(JsonObject? o, string key) =>
        o?.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    // Without the spec's JSON (for example a screen made by a template) no key counts as given.
    public static bool Has(JsonObject? o, string key) => Get(o, key) is not null;

    public static string Changed(string what, IEnumerable<string> keys, string op) =>
        $"{what} exists, and a creation list does not change it: {string.Join(", ", keys)} differ{(keys.Count() == 1 ? "s" : "")} from the module. {op}";

    // Rule 5 keys of an existing attribute are refused; default, delete rule and description follow other rules.
    public static void Attribute(IESpace m, IAttribute old, AttrSpec a, JsonObject? raw, string where, List<string> problems)
    {
        var shape = new List<string>();
        if (Has(raw, "type") && Model.Resolve(m, a.Type, new List<string>(), where) is { } type
            && Model.Describe(m, type) != Model.Describe(m, old.DataType)) shape.Add($"type ({a.Type}; module: {Model.Describe(m, old.DataType)})");
        if (a.Length is { } length && length != old.Length) shape.Add($"length ({length}; module: {old.Length})");
        if (a.Decimals is { } decimals && decimals != old.Decimals) shape.Add($"decimals ({decimals}; module: {old.Decimals})");
        if (Has(raw, "mandatory") && a.Mandatory != old.IsMandatory) shape.Add($"mandatory ({a.Mandatory.ToString().ToLowerInvariant()}; module: {old.IsMandatory.ToString().ToLowerInvariant()})");
        if (shape.Count > 0)
            problems.Add(Changed($"{where}: attribute", shape, "oml never changes the type, length, decimals or mandatory setting of an existing attribute (rule 5): add a new attribute, or change it by hand in Service Studio."));
        var other = new List<string>();
        if (a.Default is not null && Norm(a.Default) != Norm(old.DefaultValue?.Text)) other.Add("default");
        if (a.DeleteRule is not null && old.DataType is IIdentifierType && !(Reflect.Get(old, "DeleteRule")?.ToString() ?? "").Equals(a.DeleteRule, StringComparison.OrdinalIgnoreCase)) other.Add("deleteRule");
        if (other.Count > 0) problems.Add(Changed($"{where}: attribute", other, "Change it with an edits \"set\" op, or by hand in Service Studio."));
        // A description is documentation only: it is applied, as for the entity itself.
        if (a.Description is not null) old.Description = a.Description;
    }

    // An expression for comparison: outside text literals, case and white space do not count ("false" and False
    // are the same); inside a literal ("a b" and "ab") every character counts. A "" inside a literal is a quote.
    internal static string Norm(string? expression)
    {
        var text = expression ?? "";
        var result = new System.Text.StringBuilder();
        var literal = false;
        foreach (var c in text)
        {
            if (c == '"') literal = !literal;
            if (literal || c == '"') result.Append(c);
            else if (!char.IsWhiteSpace(c)) result.Append(char.ToLowerInvariant(c));
        }
        return result.ToString();
    }

    // CSS for comparison: comments removed as apply removes them; outside quoted strings, white space collapsed, none
    // around { } ; , and none around a declaration colon ("color : red"). A colon is a declaration colon when the next
    // { ; or } after it is not {; otherwise it is in a selector, where a space before it is a descendant selector
    // ("div :hover", also inside @media or nested rules), so that space stays. Text inside quotes is kept as written.
    internal static string Css(string? css)
    {
        var text = Minify.Css(css ?? "");
        var result = new System.Text.StringBuilder();
        var (quote, escaped, space, afterDeclarationColon) = ('\0', false, false, false);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                result.Append(c);
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            var tight = c is '{' or '}' or ';' or ',' || (c == ':' && DeclarationColon(text, i));
            if (space && result.Length > 0 && !tight && result[^1] is not ('{' or '}' or ';' or ',') && !afterDeclarationColon) result.Append(' ');
            space = false;
            afterDeclarationColon = c == ':' && tight;
            if (c is '"' or '\'') quote = c;
            result.Append(c);
        }
        return result.ToString();
    }

    // True when the first { ; or } after the colon at i, outside quoted strings, is not {.
    static bool DeclarationColon(string text, int i)
    {
        var quote = '\0';
        for (var j = i + 1; j < text.Length; j++)
        {
            var c = text[j];
            if (quote != '\0') { if (c == '\\') j++; else if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c is '{' or ';' or '}') return c != '{';
        }
        return true;
    }
}
