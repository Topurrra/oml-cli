using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

public static class ArchitecturePage
{
    public static string Render(string json)
    {
        // Validate first, then escape HTML-significant characters even inside JSON strings.
        // In particular, a module description must never terminate the data script.
        // The layout object is always recomputed, so the page and its numbers cannot disagree.
        var document = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("Architecture data must be a JSON object.");
        document["layout"] = ArchitectureLayout.Compute(document);
        var safe = document.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
            .Replace("&", "\\u0026").Replace("<", "\\u003c").Replace(">", "\\u003e")
            .Replace("\u2028", "\\u2028").Replace("\u2029", "\\u2029");
        using var stream = typeof(ArchitecturePage).Assembly.GetManifestResourceStream("OmlCli.Architecture.template.html")
            ?? throw new InvalidOperationException("Architecture page template is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("/*__ARCHITECTURE_JSON__*/", safe);
    }
}
