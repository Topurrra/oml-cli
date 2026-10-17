using System.Text.Json;

namespace OmlCli;

public static class ArchitecturePage
{
    public static string Render(string json)
    {
        // Validate first, then escape HTML-significant characters even inside JSON strings.
        // In particular, a module description must never terminate the data script.
        using var document = JsonDocument.Parse(json);
        var safe = document.RootElement.GetRawText().Replace("&", "\\u0026")
            .Replace("<", "\\u003c").Replace(">", "\\u003e")
            .Replace("\u2028", "\\u2028").Replace("\u2029", "\\u2029");
        using var stream = typeof(ArchitecturePage).Assembly.GetManifestResourceStream("OmlCli.Architecture.template.html")
            ?? throw new InvalidOperationException("Architecture page template is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("/*__ARCHITECTURE_JSON__*/", safe);
    }
}
