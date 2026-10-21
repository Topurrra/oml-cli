using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// Spec errors that teach: a misspelled key gets "did you mean", a wrong value gets a short correct example.
// Examples live here and nowhere else. Keys are spec keys or flow node kinds; each fragment must stay valid
// (SelfTest.Turns applies the self-contained ones).
static class SpecHelp
{
    internal static readonly Dictionary<string, string> Examples = new(StringComparer.OrdinalIgnoreCase)
    {
        ["entities"] = """
            "entities": [{ "name": "Order", "public": true, "attributes": [
              { "name": "Name", "type": "Text", "length": 100, "mandatory": true },
              { "name": "Total", "type": "Decimal", "length": 37, "decimals": 2 } ],
              "indexes": [{ "attributes": ["Name"], "unique": true }] }]
            """,
        ["structures"] = """
            "structures": [{ "name": "OrderDto", "attributes": [
              { "name": "Id", "type": "Order Identifier" }, { "name": "Total", "type": "Decimal" } ] }]
            """,
        ["attributes"] = """
            "attributes": [ { "name": "Name", "type": "Text", "length": 100, "mandatory": true },
              { "name": "CustomerId", "type": "Customer Identifier", "deleteRule": "Delete" } ]
            """,
        ["staticEntities"] = """
            "staticEntities": [{ "name": "OrderStatus", "public": true, "records": [
              { "identifier": "Open", "label": "Open", "order": 1 },
              { "identifier": "Closed", "label": "Closed", "order": 2 } ] }]
            """,
        ["exceptions"] = """
            "exceptions": ["InvalidOrder"]
            """,
        ["serverActions"] = """
            "serverActions": [{ "name": "SaveOrder", "public": true,
              "inputs": [{ "name": "Order", "type": "Order", "mandatory": true }], "outputs": [{ "name": "Id", "type": "Order Identifier" }],
              "flow": [ { "kind": "execute", "name": "Save", "action": "CreateOrUpdateOrder", "args": { "Source": "Order" } },
                        { "kind": "assign", "assign": { "Id": "Save.Id" } } ] }]
            """,
        ["serviceActions"] = """
            "serviceActions": [{ "name": "OrderCount", "outputs": [{ "name": "Count", "type": "Integer" }],
              "flow": [{ "kind": "assign", "assign": { "Count": "0" } }] }]
            """,
        ["clientActions"] = """
            "clientActions": [{ "name": "Double", "inputs": [{ "name": "N", "type": "Integer" }],
              "outputs": [{ "name": "R", "type": "Integer" }],
              "flow": [{ "kind": "assign", "assign": { "R": "N * 2" } }] }]
            """,
        ["flow"] = """
            "flow": [ { "kind": "if", "name": "Valid", "condition": "Total > 0", "true": "Save", "false": "End" },
              { "kind": "assign", "name": "Save", "assign": { "Result": "Total" } } ]
            """,
        ["inputs"] = """
            "inputs": [{ "name": "OrderId", "type": "Order Identifier", "mandatory": true }, { "name": "Note", "type": "Text" }]
            """,
        ["outputs"] = """
            "outputs": [{ "name": "Count", "type": "Integer" }, { "name": "Message", "type": "Text" }]
            """,
        ["locals"] = """
            "locals": [{ "name": "Total", "type": "Decimal", "default": "0" }]
            """,
        ["assign"] = """
            { "kind": "assign", "name": "SetTotal", "assign": { "Total": "Total + 1", "Message": "\"done\"" } }
            """,
        ["if"] = """
            { "kind": "if", "name": "Found", "condition": "GetOrders.List.Empty", "true": "NotFound", "false": "Next" }
            """,
        ["execute"] = """
            { "kind": "execute", "name": "Save", "action": "CreateOrUpdateOrder", "args": { "Source": "Order" } }
            """,
        ["aggregate"] = """
            { "kind": "aggregate", "name": "GetOrders", "sources": ["Order"], "filters": ["Order.Total > 0"],
              "sorts": [{ "attribute": "Order.Total", "descending": true }], "maxRecords": 50 }
            """,
        ["sql"] = """
            { "kind": "sql", "name": "Find", "sql": "SELECT {Order}.* FROM {Order} WHERE {Order}.[Name] LIKE @Pattern",
              "params": [{ "name": "Pattern", "type": "Text", "value": "\"%\" + Search + \"%\"" }],
              "outputs": ["Order"], "maxRecords": 50 }
            """,
        ["javaScript"] = """
            { "kind": "javaScript", "name": "Log", "inputs": [{ "name": "Text", "type": "Text" }],
              "javaScript": "console.log($parameters.Text);" }
            """,
        ["forEach"] = """
            { "kind": "forEach", "name": "Loop", "recordList": "GetOrders.List", "cycle": "Body", "next": "End" }
            """,
        ["screens"] = """
            "screens": [{ "name": "Orders", "title": "\"Orders\"",
              "aggregates": [{ "name": "GetOrders", "sources": ["Order"], "maxRecords": 10 }],
              "widgets": [{ "type": "TableRecords", "source": "GetOrders.List", "row": [
                { "type": "RowCell", "children": [{ "type": "Expression", "value": "GetOrders.List.Current.Order.Name" }] } ] }] }]
            """,
        ["blocks"] = """
            "blocks": [{ "name": "Card", "inputs": [{ "name": "Title", "type": "Text" }],
              "widgets": [{ "type": "Container", "children": [{ "type": "Expression", "value": "Title" }] }] }]
            """,
        ["triggerEvent"] = """
            { "kind": "triggerEvent", "name": "FireSelect", "event": "OnSelect", "args": { "Id": "ItemId" } }
            """,
        ["emails"] = """
            "emails": [{ "name": "Welcome", "subject": "\"Welcome \" + Name", "inputs": [{ "name": "Name", "type": "Text" }],
              "widgets": [{ "type": "Expression", "value": "\"Hello \" + Name" }] }]
            """,
        ["widgets"] = """
            "widgets": [ { "type": "Container", "children": [ { "type": "Text", "text": "Hello" } ] },
              { "type": "Button", "onClick": "Save", "children": [{ "type": "Text", "text": "Save" }] } ]
            """,
        ["edits"] = """
            "edits": [ { "op": "insertNodes", "action": "SaveOrder", "after": "Start",
                "flow": [{ "kind": "assign", "name": "Normalize", "assign": { "Order.Name": "Trim(Order.Name)" } }] },
              { "op": "rename", "element": "SaveOrder/Normalize", "to": "TrimName" } ]
            """,
        ["references"] = """
            "references": [{ "producer": "path/to/Producer.oml", "elements": ["Product", "Product_GetById"] }]
            """,
        ["restClients"] = """
            "restClients": [{ "name": "Catalog", "baseUrl": "https://api.example.com", "actions": [
              { "name": "GetItem", "method": "GET", "url": "/items/{Id}",
                "inputs": [{ "name": "Id", "type": "Integer", "location": "url" }],
                "outputs": [{ "name": "Response", "type": "Text" }] } ] }]
            """,
        ["timers"] = """
            "timers": [{ "name": "Nightly", "action": "CleanupOrders", "schedule": "WhenPublished", "timeout": 20, "priority": "Normal" }]
            """,
        ["roles"] = """
            "roles": [{ "name": "Manager", "persistent": true }]
            """,
        ["siteProperties"] = """
            "siteProperties": [{ "name": "MaxOrders", "type": "Integer", "default": "50", "multiTenant": false }]
            """,
        ["access"] = """
            "access": { "anonymous": false, "registered": true, "roles": ["Manager"] }
            """,
    };

    public static string WithExample(string message, string key) =>
        Examples.TryGetValue(key, out var example) ? message + " Example:\n" + example : message;

    internal static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }

    /// The known name nearest to `word`, only when it is a small edit away (1 for short words, 2 for longer ones).
    public static string? Closest(string word, IEnumerable<string> known)
    {
        var lower = word.ToLowerInvariant();
        var limit = lower.Length <= 4 ? 1 : 2;
        return known.Select(k => (k, d: Distance(lower, k.ToLowerInvariant()))).Where(x => x.d <= limit)
            .OrderBy(x => x.d).Select(x => x.k).FirstOrDefault();
    }

    /// "; did you mean `x`? Example: ..." for the closest known name, or "" when nothing is close.
    public static string Hint(string word, IEnumerable<string> known) =>
        Closest(word, known) is { } near ? WithExample($"; did you mean `{near}`?", near) : "";

    static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    /// Problems for keys the typed spec does not have, at every level that is typed. Keys starting with _ or $ are free for notes.
    public static List<string> UnknownKeys(JsonNode? node)
    {
        var problems = new List<string>();
        Walk(node, typeof(Spec), "$", problems);
        return problems;
    }

    static void Walk(JsonNode? node, Type type, string path, List<string> problems)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (node is JsonArray array && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var item = type.GetGenericArguments()[0];
            for (var i = 0; i < array.Count; i++) Walk(array[i], item, $"{path}[{i}]", problems);
        }
        else if (node is JsonObject obj && type.IsClass && !type.IsGenericType && type != typeof(string) && type != typeof(object))
        {
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .ToDictionary(p => p.Name, p => p.PropertyType, StringComparer.OrdinalIgnoreCase);
            var names = props.Keys.Select(Camel).ToList();
            foreach (var (key, value) in obj)
            {
                if (props.TryGetValue(key, out var propertyType)) Walk(value, propertyType, $"{path}.{key}", problems);
                else if (key.StartsWith('_') || key.StartsWith('$')) continue;
                else problems.Add($"{path}.{key}: unknown key" + (Hint(key, names) is { Length: > 0 } hint ? hint
                    : names.Count <= 12 ? ". Known keys: " + string.Join(", ", names) + "." : ". See reference/spec.md for the keys."));
            }
        }
    }

    // The type the spec expects at a path such as $.entities[0].attributes: walked down the typed records.
    static Type? TypeAt(string path)
    {
        var type = typeof(Spec);
        foreach (Match token in Regex.Matches(path, @"\.(\w+)|\[\d+\]"))
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (token.Groups[1].Success)
                type = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(p => p.Name.Equals(token.Groups[1].Value, StringComparison.OrdinalIgnoreCase))?.PropertyType;
            else type = type is { IsGenericType: true } ? type.GetGenericArguments()[0] : null;
            if (type is null) return null;
        }
        return Nullable.GetUnderlyingType(type) ?? type;
    }

    static string? Friendly(Type? t) =>
        t is null || t == typeof(JsonElement) ? null
        : t == typeof(string) ? "a string"
        : t == typeof(bool) ? "true or false"
        : t == typeof(int) || t == typeof(long) ? "a whole number"
        : t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) ? "an array"
        : "an object";

    /// A wrong value type: where it is, what was expected, and an example for the nearest key that has one.
    public static string TypeProblem(JsonException e)
    {
        var path = e.Path ?? "$";
        var message = $"{path}: wrong value type" + (Friendly(TypeAt(path)) is { } expected ? $", expected {expected}." : ".");
        var key = Regex.Matches(path, @"\.(\w+)").Select(x => x.Groups[1].Value).Reverse().FirstOrDefault(Examples.ContainsKey);
        return key is null ? message : WithExample(message, key);
    }
}

// `oml apply a.json b.json out.oml`: the parts become one spec. Arrays are joined per key, objects merged,
// and two parts that disagree (a scalar, or the same element name in one list) are refused naming both files.
static class SpecMerge
{
    public sealed class Conflict(string message) : Exception(message);

    // Lists where a repeated name is normal: edits run in order, the rest hold plain values.
    static readonly HashSet<string> Repeatable = new(StringComparer.OrdinalIgnoreCase)
        { "edits", "tidyFlows", "locales", "translate", "copy", "references" };

    public static JsonNode Merge(IReadOnlyList<(string file, JsonNode? node)> specs)
    {
        var result = new JsonObject();
        var origin = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, node) in specs)
        {
            if (node is not JsonObject obj) throw new Conflict($"{file}: a spec must be a JSON object.");
            Into(result, obj, "$", file, origin, top: true);
        }
        return result;
    }

    static void Into(JsonObject target, JsonObject source, string path, string file, Dictionary<string, string> origin, bool top)
    {
        foreach (var (key, value) in source)
        {
            var at = $"{path}.{key}";
            var existing = target.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && target[existing] is { } current)
            {
                if (current is JsonObject currentObject && value is JsonObject sourceObject)
                    Into(currentObject, sourceObject, at, file, origin, top: false);
                else if (current is JsonArray currentArray && value is JsonArray sourceArray)
                    foreach (var item in sourceArray)
                    {
                        if (top && !Repeatable.Contains(key)) Claim(key, item, file, origin);
                        currentArray.Add(item?.DeepClone());
                    }
                else if (!JsonNode.DeepEquals(current, value))
                    throw new Conflict($"Specs conflict at {at}: {origin.GetValueOrDefault(at, "an earlier spec")} sets {current.ToJsonString()}, {file} sets {value?.ToJsonString() ?? "null"}. Set it in one spec only.");
                continue;
            }
            target[key] = value?.DeepClone();
            Register(value, at, file, origin, top ? key : null);
        }
    }

    // Records which file set each scalar and named list item, so a later part can be checked against it.
    static void Register(JsonNode? node, string path, string file, Dictionary<string, string> origin, string? listKey)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj) Register(value, $"{path}.{key}", file, origin, null);
                break;
            case JsonArray array:
                if (listKey is not null && !Repeatable.Contains(listKey))
                    foreach (var item in array) Claim(listKey, item, file, origin);
                break;
            default:
                origin.TryAdd(path, file);
                break;
        }
    }

    static void Claim(string list, JsonNode? item, string file, Dictionary<string, string> origin)
    {
        var name = item switch
        {
            JsonObject o when o.FirstOrDefault(p => p.Key.Equals("name", StringComparison.OrdinalIgnoreCase)).Value is JsonValue v && v.TryGetValue<string>(out var n) => n,
            JsonValue v when v.TryGetValue<string>(out var n) => n,
            _ => null,
        };
        if (name is null) return;
        var id = $"{list}#{name}";
        if (origin.TryGetValue(id, out var first) && first != file)
            throw new Conflict($"Specs conflict: {first} and {file} both create {list} '{name}'. Put it in one spec only.");
        origin.TryAdd(id, file);
    }
}
