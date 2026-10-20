using System.Text.Json;

namespace OmlCli;

// `oml spec`: reference lookup, shape check and JSON Schema for spec files. Runs in the launcher,
// so it works without Service Studio.
static class SpecCommand
{
    const string Usage = """
        oml spec                                  list every spec key, one line each
        oml spec <key>                            print the reference for one key or topic (entities, aggregate, screens, tidyFlows, ...)
        oml spec check <spec.json>                check a spec's shape without Service Studio: {"ok":bool,"problems":[{"path","message"}]}; exit 0 ok, 1 problems
        oml spec schema [--out <file.json>] [--force]   JSON Schema (draft 2020-12) for spec files; stdout without --out
        """;

    public static int Run(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h")) { Console.WriteLine(Usage); return 0; }
        try
        {
            return args switch
            {
                [] => SpecDocs.List(),
                ["--verify"] => SpecDocs.Verify(),
                ["check", var file] => Check(file),
                ["schema", .. var rest] => Schema(rest),
                ["check", ..] => Fail("Usage: oml spec check <spec.json>"),
                [var key] => SpecDocs.Show(key),
                _ => SpecDocs.Show(string.Join(" ", args)),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or FileNotFoundException or TypeLoadException)
        {
            return Fail(e.Message);
        }
    }

    static int Fail(string message) { Console.Error.WriteLine(message); return 2; }

    static int Check(string file)
    {
        if (!File.Exists(file)) return Fail($"Spec file not found: {file}");
        var problems = SpecChecker.Check(File.ReadAllText(file));
        var report = new { ok = problems.Count == 0, problems = problems.Select(p => new { p.path, p.message }).ToArray() };
        var options = new JsonSerializerOptions
        {
            WriteIndented = JsonOutput.Pretty,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        Console.WriteLine(JsonSerializer.Serialize(report, options));
        return problems.Count == 0 ? 0 : 1;
    }

    static int Schema(string[] args)
    {
        string? output = null;
        var force = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length) output = args[++i];
            else if (args[i] == "--force") force = true;
            else return Fail($"Unknown option {args[i]}. Usage: oml spec schema [--out <file.json>] [--force]");
        }
        var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            SpecSchema.Build().WriteTo(writer);
        var json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        if (output is null) { Console.WriteLine(json); return 0; }
        var path = Path.GetFullPath(output);
        if (File.Exists(path) && !force) return Fail($"Output already exists: {path}. Use --force to replace it.");
        File.WriteAllText(path, json + "\n");
        Console.WriteLine(JsonSerializer.Serialize(new { written = path }));
        return 0;
    }
}
