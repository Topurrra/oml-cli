using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

static class JsonOutput
{
    public static bool Pretty { get; set; }
    static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonSerializerOptions Indented = new(Compact) { WriteIndented = true };

    // A built JSON tree as it is, with empty values kept (Serialize drops them).
    public static string Write(JsonNode node) => Safe(node.ToJsonString(Pretty ? Indented : Compact), Pretty);

    public static string Serialize(object value, bool progress = false)
    {
        var node = JsonSerializer.SerializeToNode(value, Compact);
        if (!Pretty) Prune(node);
        return node is null ? "null" : Safe(node.ToJsonString(Pretty && !progress ? Indented : Compact), Pretty && !progress);
    }

    // JSON for stdout. Every character above 0x7F becomes \uXXXX, so the output is pure ASCII: valid and lossless
    // in any code page and for any reader (PowerShell decodes a pipe with the console code page, Git Bash and Node
    // with UTF-8). These characters occur only inside JSON strings, where the escape means the same character.
    // Indented output to a console window keeps the real characters, for people to read.
    public static string Safe(string json, bool indented)
    {
        var first = json.AsSpan().IndexOfAnyExceptInRange('\0', '\x7F');
        if (first < 0 || indented && !Console.IsOutputRedirected) return json;
        var ascii = new StringBuilder(json.Length + 64).Append(json, 0, first);
        foreach (var c in json.AsSpan(first))
            if (c > '\x7F') ascii.Append("\\u").Append(((int)c).ToString("X4")); else ascii.Append(c);
        return ascii.ToString();
    }

    public const string UnpairedSurrogate = "text with an unpaired surrogate escape (\\uD800-\\uDFFF without its other half). Write the character itself, or both halves (an emoji is \\uD83D\\uDE00).";

    // The path of the first string (or key) in a parsed spec that .NET cannot read as text, or null. Reading one
    // throws InvalidOperationException anywhere later, so apply and spec check refuse it up front.
    public static string? UnpairedSurrogatePath(JsonNode? node, string path = "")
    {
        try
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj)
                        if (UnpairedSurrogatePath(value, path.Length == 0 ? key : $"{path}.{key}") is { } found) return found;
                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                        if (UnpairedSurrogatePath(array[i], $"{path}[{i}]") is { } found) return found;
                    break;
                case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                    value.GetValue<string>();
                    break;
            }
            return null;
        }
        catch (InvalidOperationException) { return path.Length == 0 ? "$" : path; }
    }

    static bool Prune(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToArray())
                if (Prune(obj[key])) obj.Remove(key);
            return obj.Count == 0;
        }
        if (node is JsonArray array)
        {
            for (var i = array.Count - 1; i >= 0; i--)
                if (Prune(array[i])) array.RemoveAt(i);
            return array.Count == 0;
        }
        return node is null;
    }
}
