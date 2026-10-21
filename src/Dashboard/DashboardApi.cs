using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// The dashboard's API: one handler per action of the page. Each builds the arguments of a normal oml command from checked
// input and runs it through Dashboard.Oml. Read-only by design: no install, publish, references, apply, or LifeTime write
// (tag, deploy, abort, continue) is reachable from here. To add an endpoint, add a case to Api and a handler below.
static partial class Dashboard
{
    internal sealed record Reply(int Status, JsonNode Body);

    sealed class BadInput(string message) : Exception(message);

    static async Task<Reply> Api(HttpListenerRequest request, string route)
    {
        try
        {
            var get = request.HttpMethod == "GET";
            JsonObject Body() => ReadBody(request);
            return (get, route) switch
            {
                (true, "info") => Info(),
                (true, "doctor") => Wrap(await Oml("doctor")),
                (true, "studio") => Wrap(await Oml("studio")),
                (false, "pull") => await Pull(Body()),
                (false, "inspect") => await InspectModule(Body()),
                (false, "describe") => await DescribeElement(Body()),
                (false, "find") => await FindElements(Body()),
                (false, "preview") => await PreviewElement(Body()),
                (false, "openapi") => await OpenApiExport(Body()),
                (false, "architecture") => await ArchitectureReport(Body()),
                (false, "convert/fonts") => await ConvertFonts(Body()),
                (false, "convert/images") => await ConvertImages(Body()),
                (true, "spec/schema") => Wrap(await Oml("spec", "schema")),
                (true, "recipes") => Recipes(),
                (true, "doc/rules") => Doc("rules.md"),
                (true, "doc/checklist") => Doc("doc.ui-checklist.md"),
                (true, "lt/environments") => await LifeTimeRead("environments"),
                (true, "lt/apps") => await LifeTimeRead("apps", request.QueryString["env"]),
                (true, "lt/status") => await LifeTimeRead("status", request.QueryString["key"]),
                (true, "log") => UsageLines(),
                (true, "log/summary") => Wrap(await Oml("log", "summary")),
                (false, "shutdown") => Shutdown(),
                _ => new Reply(404, new JsonObject { ["error"] = "No such dashboard action." }),
            };
        }
        catch (BadInput e) { return new Reply(400, new JsonObject { ["error"] = e.Message }); }
    }

    static Reply Wrap(Result r, JsonObject? extra = null)
    {
        var body = new JsonObject { ["code"] = r.Code, ["output"] = r.Output?.DeepClone(), ["stderr"] = r.Stderr, ["cli"] = r.Cli };
        foreach (var (name, value) in extra ?? new JsonObject()) body[name] = value?.DeepClone();
        return new Reply(200, body);
    }

    static JsonObject ReadBody(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaxBody) throw new BadInput("The upload is larger than 200 MB. Send fewer or smaller files.");
        using var limited = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = request.InputStream.Read(buffer)) > 0)
        {
            if (limited.Length + read > MaxBody) throw new BadInput("The upload is larger than 200 MB. Send fewer or smaller files.");
            limited.Write(buffer, 0, read);
        }
        if (limited.Length == 0) return new JsonObject();
        try { return JsonNode.Parse(limited.ToArray()) as JsonObject ?? throw new BadInput("The request must be a JSON object."); }
        catch (JsonException) { throw new BadInput("The request is not valid JSON."); }
    }

    // ---- checked input ----

    static string? Str(JsonObject body, string name) => body[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;

    // A path a person typed or pasted. Windows "Copy as path" adds quotes; a leading dash would read as an option.
    static string UserPath(string? text, string what)
    {
        if (text is null) throw new BadInput($"Type the path of {what}.");
        text = text.Trim().Trim('"');
        if (text.StartsWith('-')) throw new BadInput($"A path cannot start with '-': {text}");
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(text)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { throw new BadInput($"This is not a valid path: {text}"); }
    }

    static string ModuleFile(JsonObject body, string name = "path")
    {
        var path = UserPath(Str(body, name), "an .oml module file");
        if (!path.EndsWith(".oml", StringComparison.OrdinalIgnoreCase)) throw new BadInput($"The module must be an .oml file: {path}");
        if (!File.Exists(path)) throw new BadInput($"File not found: {path}");
        return path;
    }

    // Producer folders or files for --in.
    static List<string> SearchIn(JsonObject body)
    {
        var result = new List<string>();
        foreach (var item in body["in"] as JsonArray ?? [])
        {
            if (item is not JsonValue v || !v.TryGetValue<string>(out var text) || text.Trim().Length == 0) continue;
            var path = UserPath(text, "a folder or .oml file");
            if (!Directory.Exists(path) && !File.Exists(path)) throw new BadInput($"Folder or file not found: {path}");
            result.Add(path);
        }
        return result;
    }

    static readonly Regex NamePattern = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_*][A-Za-z0-9_*]*)?$|^[A-Za-z0-9_*]+$");

    static string ElementName(string? text, string what = "an element name")
    {
        if (text is null) throw new BadInput($"Type {what}.");
        if (text.Length > 200 || !NamePattern.IsMatch(text)) throw new BadInput($"'{text}' is not a valid OutSystems name. Use letters, digits and _, or * as a wildcard.");
        return text;
    }

    // ---- pages ----

    static Reply Info()
    {
        var host = Environment.GetEnvironmentVariable("OML_LIFETIME_HOST");
        var hasToken = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OML_LIFETIME_TOKEN"));
        return new Reply(200, new JsonObject
        {
            ["version"] = typeof(Dashboard).Assembly.GetName().Version?.ToString(3),
            ["workFolder"] = WorkRoot,
            ["serviceStudio"] = Locations.ServiceStudio,
            ["lifeTime"] = new JsonObject { ["configured"] = !string.IsNullOrWhiteSpace(host) && hasToken, ["host"] = string.IsNullOrWhiteSpace(host) ? null : host },
            ["log"] = new JsonObject { ["enabled"] = UsageLog.Enabled, ["file"] = UsageLog.FilePath ?? UsageLog.DefaultPath },
            ["browser"] = Locations.BrowserPath() is not null,
        });
    }

    static async Task<Reply> Pull(JsonObject body)
    {
        var module = ElementName(Str(body, "module"), "the name of the module open in Service Studio");
        var output = Path.Combine(NewWorkFolder("pulled"), module + ".oml");
        var r = await Oml("pull", module, "--out", output);
        return Wrap(r, new JsonObject { ["path"] = r.Code == 0 ? output : null });
    }

    static async Task<Reply> InspectModule(JsonObject body) => Wrap(await Oml("inspect", ModuleFile(body), "--own"));

    static async Task<Reply> DescribeElement(JsonObject body)
    {
        var args = new List<string> { "describe", ModuleFile(body), ElementName(Str(body, "name")) };
        foreach (var folder in SearchIn(body)) args.AddRange(["--in", folder]);
        return Wrap(await Oml([.. args]));
    }

    static async Task<Reply> FindElements(JsonObject body)
    {
        var args = new List<string> { "find", ElementName(Str(body, "name"), "a name or a pattern such as Order*") };
        if (Str(body, "path") is not null) args.Add(ModuleFile(body));
        var folders = SearchIn(body);
        foreach (var folder in folders) args.AddRange(["--in", folder]);
        if (args.Count == 2) throw new BadInput("Choose a module, or add a folder with producer modules.");
        return Wrap(await Oml([.. args]));
    }

    static async Task<Reply> PreviewElement(JsonObject body)
    {
        var module = ModuleFile(body);
        var element = ElementName(Str(body, "element"), "a block or screen name");
        var theme = Str(body, "theme") is "dark" ? "dark" : "light";
        var width = body["width"] is JsonValue w && w.TryGetValue<int>(out var px) && px is >= 200 and <= 4000 ? px : 1200;
        var folder = Directory.CreateTempSubdirectory("oml-dashboard-").FullName; // intermediate files: deleted below
        try
        {
            var html = Path.Combine(folder, element + ".preview.html");
            var args = new List<string> { "preview", module, element, "--theme", theme, "--width", width.ToString(), "--out", html };
            var png = Path.Combine(folder, element + ".png");
            var screenshot = body["screenshot"] is JsonValue s && s.TryGetValue<bool>(out var wanted) && wanted;
            if (screenshot) args.AddRange(["--screenshot", png]);
            var r = await Oml([.. args]);
            var extra = new JsonObject();
            if (File.Exists(html)) extra["view"] = AddView(await File.ReadAllTextAsync(html));
            if (File.Exists(png)) extra["png"] = Convert.ToBase64String(await File.ReadAllBytesAsync(png));
            // The person's version of this command, with names they recognise instead of the temporary folder.
            var shown = new List<string> { "preview", module, element, "--theme", theme, "--width", width.ToString() };
            if (screenshot) shown.AddRange(["--screenshot", $"{element}-{theme}-{width}.png"]);
            return Wrap(r with { Cli = CliText(shown), Output = Scrub(r.Output, folder) }, extra);
        }
        finally { TryDelete(folder); }
    }

    static async Task<Reply> OpenApiExport(JsonObject body)
    {
        var module = ModuleFile(body);
        var folder = NewWorkFolder("openapi");
        var args = new List<string> { "openapi", module, "--out", folder };
        if (Str(body, "api") is { } api) args.InsertRange(2, ["--api", ElementName(api, "a REST API name")]);
        var r = await Oml([.. args]);
        var files = new JsonArray();
        foreach (var file in Directory.GetFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            files.Add(new JsonObject { ["name"] = Path.GetFileName(file), ["path"] = file, ["text"] = await File.ReadAllTextAsync(file) });
        return Wrap(r, new JsonObject { ["files"] = files, ["folder"] = folder });
    }

    static async Task<Reply> ArchitectureReport(JsonObject body)
    {
        string? upload = null;
        try
        {
            var inputs = new List<string>();
            if (body["files"] is JsonArray)
            {
                upload = SaveUploads(body, [".oml", ".oap"]).folder;
                inputs.Add(upload);
            }
            else
            {
                foreach (var item in (body["in"] as JsonArray ?? []).Select(i => i is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).Where(i => !string.IsNullOrWhiteSpace(i)))
                {
                    var path = UserPath(item, "a folder or file");
                    if (!Directory.Exists(path) && !File.Exists(path)) throw new BadInput($"Folder or file not found: {path}");
                    inputs.Add(path);
                }
                if (inputs.Count == 0) throw new BadInput("Type a folder with .oap or .oml files, or drop the files here.");
            }
            var folder = NewWorkFolder("architecture");
            var html = Path.Combine(folder, "architecture.html");
            var args = new List<string> { "architecture" };
            foreach (var input in inputs) args.AddRange(["--in", input]);
            args.AddRange(["--out", html]);
            var r = await Oml([.. args]);
            var extra = new JsonObject { ["path"] = File.Exists(html) ? html : null };
            if (File.Exists(html))
            {
                var text = await File.ReadAllTextAsync(html);
                extra["view"] = AddView(text);
                extra["html"] = text;
            }
            if (upload is not null) r = r with { Cli = "oml architecture --in C:\\Exports\\Payments --out architecture.html", Output = Scrub(r.Output, upload) };
            return Wrap(r, extra);
        }
        finally { if (upload is not null) TryDelete(upload); }
    }

    // ---- fonts and images ----

    static readonly string[] FontTypes = [".ttf", ".otf", ".woff", ".woff2"];
    static readonly string[] ImageTypes = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".svg"];

    static async Task<Reply> ConvertFonts(JsonObject body)
    {
        var (folder, names) = SaveUploads(body, FontTypes);
        try
        {
            var output = Path.Combine(folder, "out");
            var r = await Oml("font", "batch", Path.Combine(folder, "in"), "--out", output);
            return Converted(r, folder, output, names, $"oml font woff2 {Quote(names[0])}");
        }
        finally { TryDelete(folder); }
    }

    static async Task<Reply> ConvertImages(JsonObject body)
    {
        var to = Str(body, "to") is { } t && t is "png" or "jpeg" or "webp" or "svg" ? t : null;
        var maxWidth = body["maxWidth"] is JsonValue mw && mw.TryGetValue<int>(out var w) && w is >= 1 and <= 100000 ? w : (int?)null;
        var quality = body["quality"] is JsonValue q && q.TryGetValue<int>(out var qv) && qv is >= 1 and <= 100 ? qv : (int?)null;
        var (folder, names) = SaveUploads(body, ImageTypes);
        try
        {
            var output = Path.Combine(folder, "out");
            var options = new List<string>();
            if (to is not null) options.AddRange(["--to", to]);
            if (maxWidth is not null) options.AddRange(["--max-width", maxWidth.ToString()!]);
            if (quality is not null) options.AddRange(["--quality", quality.ToString()!]);
            var r = await Oml([.. new[] { "image", "batch", Path.Combine(folder, "in") }, .. options, "--out", output]);
            return Converted(r, folder, output, names, CliText(new[] { "image", names[0] }.Concat(options)));
        }
        finally { TryDelete(folder); }
    }

    static string Quote(string name) => CliText([name])[4..];

    // One row per uploaded file with sizes and the converted bytes, plus a zip when there is more than one result.
    static Reply Converted(Result r, string folder, string output, List<string> names, string cli)
    {
        var rows = new JsonArray();
        var made = new List<(string name, byte[] data)>();
        var results = (r.Output as JsonObject)?["results"] as JsonArray ?? [];
        foreach (var item in results.OfType<JsonObject>())
        {
            var input = item["input"]?.GetValue<string>() ?? "";
            var row = new JsonObject { ["name"] = Path.GetFileName(input), ["inputBytes"] = item["inputBytes"]?.DeepClone() };
            if (item["error"] is { } error) row["error"] = Scrub(error, folder)?.ToString();
            else if (item["status"]?.GetValue<string>() is { } status) row["note"] = status;
            else if (item["output"]?.GetValue<string>() is { } file && File.Exists(file) && Path.GetFullPath(file).StartsWith(output, StringComparison.OrdinalIgnoreCase))
            {
                var data = File.ReadAllBytes(file);
                row["output"] = Path.GetFileName(file);
                row["outputBytes"] = data.Length;
                foreach (var extra in new[] { "inputFormat", "outputFormat", "before", "after" })
                    if (item[extra] is { } value) row[extra] = value.DeepClone();
                row["data"] = Convert.ToBase64String(data);
                made.Add((Path.GetFileName(file), data));
            }
            rows.Add(row);
        }
        if (results.Count == 0 && r.Output is JsonObject o && o["error"] is { } failure)
            rows.Add(new JsonObject { ["name"] = string.Join(", ", names), ["error"] = Scrub(failure, folder)?.ToString() });
        var extraFields = new JsonObject { ["files"] = rows };
        if (made.Count > 1)
        {
            using var zipBytes = new MemoryStream();
            using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (name, data) in made)
                {
                    using var entry = zip.CreateEntry(name, CompressionLevel.NoCompression).Open(); // already compressed formats
                    entry.Write(data);
                }
            extraFields["zip"] = Convert.ToBase64String(zipBytes.ToArray());
        }
        return Wrap(r with { Output = null, Cli = cli }, extraFields);
    }

    // Uploaded files go to a fresh temporary folder (in\<name>) that the caller deletes after the response is built.
    static (string folder, List<string> names) SaveUploads(JsonObject body, string[] allowed)
    {
        if (body["files"] is not JsonArray files || files.Count == 0) throw new BadInput("Drop at least one file.");
        if (files.Count > 200) throw new BadInput("Send at most 200 files at once.");
        var folder = Directory.CreateTempSubdirectory("oml-dashboard-").FullName;
        var names = new List<string>();
        try
        {
            var input = Directory.CreateDirectory(Path.Combine(folder, "in")).FullName;
            foreach (var file in files.OfType<JsonObject>())
            {
                var name = Str(file, "name") ?? "";
                if (name != Path.GetFileName(name) || name.Length == 0 || name.Length > 150 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.StartsWith('.') || name.StartsWith('-'))
                    throw new BadInput($"Not a usable file name: '{Str(file, "name")}'.");
                if (!allowed.Contains(Path.GetExtension(name).ToLowerInvariant()))
                    throw new BadInput($"{name}: use {string.Join(", ", allowed)} files here.");
                if (names.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new BadInput($"{name} was sent twice.");
                byte[] data;
                try { data = Convert.FromBase64String(Str(file, "data") ?? ""); }
                catch (FormatException) { throw new BadInput($"{name}: the file data did not arrive correctly."); }
                File.WriteAllBytes(Path.Combine(input, name), data);
                names.Add(name);
            }
            return (folder, names);
        }
        catch { TryDelete(folder); throw; }
    }

    // Temporary folder names mean nothing to a person and should not reach the page.
    static JsonNode? Scrub(JsonNode? node, string folder) =>
        node is null ? null : JsonNode.Parse(node.ToJsonString().Replace(JsonEncodedText.Encode(folder + Path.DirectorySeparatorChar).ToString(), "")
            .Replace(JsonEncodedText.Encode(folder).ToString(), ""));

    static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // shortcut: a locked temp folder stays in %TEMP%; the OS cleans it later
    }

    // ---- reference pages (embedded at build time, so they work from a release zip) ----

    static string? Resource(string name)
    {
        using var stream = typeof(Dashboard).Assembly.GetManifestResourceStream("OmlCli." + name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    static Reply Doc(string name) => new(200, new JsonObject { ["markdown"] = Resource(name) ?? "" });

    static Reply Recipes()
    {
        var list = new JsonArray();
        foreach (var resource in typeof(Dashboard).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("OmlCli.recipes.")).Order(StringComparer.Ordinal))
        {
            var text = Resource(resource["OmlCli.".Length..]) ?? "";
            var header = text.Split('\n').Select(l => l.Trim()).TakeWhile(l => l.StartsWith("//")).Select(l => l.TrimStart('/').Trim());
            list.Add(new JsonObject { ["file"] = resource["OmlCli.recipes.".Length..], ["description"] = string.Join("\n", header), ["text"] = text });
        }
        return new Reply(200, new JsonObject { ["recipes"] = list });
    }

    // ---- LifeTime: read only ----

    static async Task<Reply> LifeTimeRead(string verb, string? value = null)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OML_LIFETIME_HOST")) || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OML_LIFETIME_TOKEN")))
            return new Reply(200, new JsonObject { ["configured"] = false });
        var args = new List<string> { "lt", verb };
        if (verb == "apps" && !string.IsNullOrWhiteSpace(value))
        {
            if (value.Length > 100 || value.StartsWith('-')) throw new BadInput("Not an environment name.");
            args.AddRange(["--env", value]);
        }
        if (verb == "status")
        {
            if (value is null || !Regex.IsMatch(value, "^[A-Za-z0-9-]{1,64}$")) throw new BadInput("A deployment key has only letters, digits and dashes.");
            args.Add(value);
        }
        return Wrap(await Oml([.. args]), new JsonObject { ["configured"] = true });
    }

    // ---- agent activity ----

    static Reply UsageLines()
    {
        var file = UsageLog.FilePath;
        var body = new JsonObject { ["enabled"] = file is not null, ["file"] = file ?? UsageLog.DefaultPath };
        if (file is null || !File.Exists(file)) return new Reply(200, body);
        var lines = new List<string>();
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
                if (lines.Count > 400) lines.RemoveRange(0, 200);
            }
        var rows = new JsonArray();
        foreach (var line in Enumerable.Reverse(lines).Take(200))
        {
            try { if (JsonNode.Parse(line) is JsonObject row) rows.Add(row); }
            catch (JsonException) { } // a damaged line is skipped, as in oml log summary
        }
        body["lines"] = rows;
        return new Reply(200, body);
    }

    static Reply Shutdown()
    {
        _ = Task.Delay(200).ContinueWith(_ => RequestStop()); // after this answer has gone out
        return new Reply(200, new JsonObject { ["stopping"] = true });
    }
}
