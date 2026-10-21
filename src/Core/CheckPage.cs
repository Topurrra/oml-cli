using System.Text.Json;

namespace OmlCli;

// The `oml check --out` page: one offline HTML file with the report embedded as JSON.
static class CheckPage
{
    public static string Render(string json) => Embed(json, "OmlCli.Check.template.html", "/*__CHECK_JSON__*/");

    // One offline page: the template with the report in its JSON script block. `oml review --out` uses it too.
    internal static string Embed(string json, string resource, string placeholder)
    {
        // Validate first, then escape HTML-significant characters even inside JSON strings, as ArchitecturePage does.
        using var document = JsonDocument.Parse(json);
        var safe = document.RootElement.GetRawText().Replace("&", "\\u0026")
            .Replace("<", "\\u003c").Replace(">", "\\u003e")
            .Replace("\u2028", "\\u2028").Replace("\u2029", "\\u2029");
        using var stream = typeof(CheckPage).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Page template {resource} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace(placeholder, safe);
    }
}
