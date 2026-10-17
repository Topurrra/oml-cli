using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

static class JsonOutput
{
    public static bool Pretty { get; set; }
    static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonSerializerOptions Indented = new(Compact) { WriteIndented = true };

    public static string Serialize(object value, bool progress = false)
    {
        var node = JsonSerializer.SerializeToNode(value, Compact);
        if (!Pretty) Prune(node);
        return node?.ToJsonString(Pretty && !progress ? Indented : Compact) ?? "null";
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
