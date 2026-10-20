namespace OmlCli;

// Linked into OmlCli.Core as well (see the Core csproj), so the launcher and the commands look in the same places.
static class Locations
{
    public static string ServiceStudio
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("OML_SERVICE_STUDIO");
            return string.IsNullOrWhiteSpace(env) ? @"C:\Program Files\OutSystems\Service Studio 11\Service Studio" : env;
        }
    }

    // Service Studio's MCP server (Edit > MCP Server > Start MCP server).
    public static int StudioPort => int.TryParse(Environment.GetEnvironmentVariable("OML_STUDIO_PORT"), out var p) ? p : 41820;
    public static string StudioUrl => $"http://127.0.0.1:{StudioPort}/mcp";

    // One line per Service Studio build whose self-test passed.
    public static string SelfTestRecord => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "oml-cli", "selftest-passed.txt");

    // Headless Edge or Chrome for `oml preview --screenshot`. OML_BROWSER wins when it is set.
    public static string? BrowserPath()
    {
        var env = Environment.GetEnvironmentVariable("OML_BROWSER");
        if (!string.IsNullOrEmpty(env)) return File.Exists(env) ? env : null;
        var roots = new[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("LocalAppData") }.Where(r => !string.IsNullOrEmpty(r));
        var tails = new[] { @"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe" };
        return tails.SelectMany(t => roots.Select(r => Path.Combine(r!, t))).FirstOrDefault(File.Exists);
    }
}
