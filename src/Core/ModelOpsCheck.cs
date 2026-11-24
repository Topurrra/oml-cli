using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

public static partial class Cli
{
    // `oml spec check` without a module: the generic operations' types, keys and value kinds against the installed Model
    // API (edits `add`, and `set` with "type"). Names of elements need the module (--module).
    public static string SpecCheckModelOps(string serviceStudioDir, string specText)
    {
        Model.ServiceStudioDir = serviceStudioDir;
        Model.Services();
        var problems = new JsonArray();
        void Add(string path, string message) => problems.Add(new JsonObject { ["path"] = path, ["message"] = message });
        using var doc = JsonDocument.Parse(specText, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!doc.RootElement.TryGetProperty("edits", out var edits) || edits.ValueKind != JsonValueKind.Array) return problems.ToJsonString();
        var i = 0;
        foreach (var edit in edits.EnumerateArray())
        {
            var at = $"edits[{i++}]";
            if (edit.ValueKind != JsonValueKind.Object || !edit.TryGetProperty("op", out var op)) continue;
            var typeName = edit.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (op.GetString() is not ("add" or "set") || typeName is null)
            {
                if (op.GetString() == "add" && typeName is null && !edit.TryGetProperty("factory", out _))
                    Add(at, "add needs \"type\" (the Model API type to make, for example \"IEntityAttribute\") or \"factory\" (a Create* method of the parent)");
                continue;
            }
            Type type;
            try { type = ModelTypes.Resolve(typeName); }
            catch (ModelError e) { Add(at + ".type", e.Message); continue; }
            if (op.GetString() == "add" && ModelTypes.Makers(type) is { Count: 0 } && !edit.TryGetProperty("factory", out _))
                Add(at + ".type", $"no Model API type has a Create* method that makes a {ModelTypes.Short(type)}; it is made by Service Studio or by another spec key (oml spec --search \"{ModelTypes.Short(type).TrimStart('I')}\")");
            if (!edit.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) continue;
            var settable = ModelTypes.Settable(type);
            foreach (var p in props.EnumerateObject())
            {
                if (History.IsField(p.Name)) { Add($"{at}.properties.{p.Name}", "history fields are written by Service Studio; oml does not change them"); continue; }
                if (p.Name.Equals("name", StringComparison.OrdinalIgnoreCase) && op.GetString() == "add") { Add($"{at}.properties.{p.Name}", "give the name as \"name\" of the add, not as a property"); continue; }
                if (!settable.TryGetValue(p.Name, out var propType))
                {
                    // Widget and node keys oml adds (targetWidget, attributes, ...) are not Model API members; leave them to apply.
                    if (op.GetString() == "set" && p.Name is "targetWidget" or "attributes" or "value") continue;
                    var near = settable.Keys.Select(k => (k, d: SpecHelp.Distance(p.Name.ToLowerInvariant(), k.ToLowerInvariant())))
                        .Where(x => x.d <= Math.Max(2, p.Name.Length / 3)).OrderBy(x => x.d).Select(x => x.k).Take(3).ToList();
                    Add($"{at}.properties.{p.Name}", $"{ModelTypes.Short(type)} has no settable '{p.Name}'.{(near.Count > 0 ? $" Closest: {string.Join(", ", near)}." : "")} It takes: {string.Join(", ", settable.Keys.Take(30))}{(settable.Count > 30 ? ", ..." : "")} (oml spec model-types {ModelTypes.Short(type)})");
                    continue;
                }
                // A Set*(string) method takes an expression or text: any string is fine.
                if (ModelTypes.StaticValueProblem(propType, p.Value, p.Name) is { } bad) Add($"{at}.properties.{p.Name}", bad);
            }
        }
        return problems.ToJsonString();
    }
}
