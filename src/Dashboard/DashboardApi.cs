using System.Globalization;
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
                (true, "doctor") => await DoctorCached(request.QueryString["fresh"] == "1"),
                (true, "queue") => QueueState(),
                (true, "studio") => Wrap(await Oml("studio")),
                (false, "pull") => await Pull(Body()),
                (false, "inspect") => await InspectModule(Body()),
                (false, "describe") => await DescribeElement(Body()),
                (false, "find") => await FindElements(Body()),
                (false, "preview") => await PreviewElement(Body()),
                (false, "openapi") => await OpenApiExport(Body()),
                (false, "architecture") => await ArchitectureReport(Body()),
                (false, "modules/folder") => FolderModules(Body()),
                (false, "modules/app") => Wrap(await Oml("app", "list", PackageFile(Body()))),
                (false, "modules/extract") => await ExtractPackage(Body()),
                (false, "modules/upload") => UploadModules(Body()),
                (false, "extension/inspect") => Wrap(await Oml("extension", "inspect", ExistingFile(Body(), "path", [".xif"], "an extension .xif file"))),
                (true, "templates") => Wrap(await Oml("templates")),
                (false, "translations/export") => await ExportTranslations(Body()),
                (false, "convert/fonts") => await ConvertFonts(Body()),
                (false, "convert/images") => await ConvertImages(Body()),
                (false, "check") => await CheckModules(Body()),
                (false, "check/init-rules") => await CheckInitRules(),
                (false, "review") => await ReviewModules(Body()),
                (false, "search") => await SearchDocs(Body()),
                (false, "doc/open") => await OpenDoc(Body()),
                (false, "find/text") => await FindText(Body()),
                (false, "tree") => await TreeElement(Body()),
                (false, "validate") => await ValidateModule(Body()),
                (false, "test/scaffold") => await ScaffoldTests(Body()),
                (true, "spec/schema") => Wrap(await Oml("spec", "schema")),
                (true, "recipes") => Recipes(),
                (true, "doc/rules") => Doc("rules.md"),
                (true, "doc/checklist") => Doc("doc.ui-checklist.md"),
                (true, "doc/best-practices") => Doc("doc.best-practices.md"),
                (true, "lt/environments") => await LifeTimeRead("environments"),
                (true, "lt/apps") => await LifeTimeRead("apps", request.QueryString["env"]),
                (true, "lt/status") => await LifeTimeRead("status", request.QueryString["key"]),
                (true, "lt/teams") => await LifeTimeRead("teams"),
                (true, "lt/team-apps") => await LifeTimeRead("team-apps", request.QueryString["team"]),
                (false, "lt/download") => await LifeTimeDownload(Body()),
                (false, "lt/report") => await LifeTimeTeamReport(Body()),
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

    static string ExistingFile(JsonObject body, string name, string[] types, string what)
    {
        var path = UserPath(Str(body, name), what);
        if (!types.Contains(Path.GetExtension(path).ToLowerInvariant())) throw new BadInput($"Use {what}: {path}");
        if (!File.Exists(path)) throw new BadInput($"File not found: {path}");
        return path;
    }

    static string PackageFile(JsonObject body) => ExistingFile(body, "path", [".oap", ".osp"], "an application package (.oap or .osp)");

    // An application, team or environment name for LifeTime: any text a person sees there, but never an option.
    static string LifeTimeName(string? text, string what)
    {
        if (text is null) throw new BadInput($"Choose {what}.");
        if (text.Length > 200 || text.StartsWith('-') || text.Any(char.IsControl)) throw new BadInput($"Not {what}: {text}");
        return text;
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

    // doctor sends one MCP initialize to Service Studio. The page gets the last result of this dashboard run, so a page
    // load does not reach Service Studio again; "Check again" asks with fresh=1.
    static Reply? doctorReply;
    static int doctorRuns;

    static async Task<Reply> DoctorCached(bool fresh)
    {
        if (!fresh && doctorReply is { } cached) return cached;
        var run = Interlocked.Increment(ref doctorRuns); // tells a kept result from a new one
        return doctorReply = Wrap(await Oml("doctor"), new JsonObject { ["checkedAt"] = DateTime.Now.ToString("HH:mm:ss"), ["run"] = run });
    }

    static async Task<Reply> Pull(JsonObject body)
    {
        var module = ElementName(Str(body, "module"), "the name of the module open in Service Studio");
        var output = Path.Combine(NewWorkFolder("pulled"), module + ".oml");
        var r = await Oml("pull", module, "--out", output);
        return Wrap(r, new JsonObject { ["path"] = r.Code == 0 ? output : null });
    }

    static async Task<Reply> InspectModule(JsonObject body)
    {
        var args = new List<string> { "inspect", ModuleFile(body), "--own" };
        if (Flag(body, "history")) args.Add("--history");
        return Wrap(await Oml([.. Timed(body, args)]));
    }

    static async Task<Reply> DescribeElement(JsonObject body)
    {
        // A path from find --text or tree (PvCard/PvRoot) is accepted too. --css takes no --in or --history.
        var args = new List<string> { "describe", ModuleFile(body), ElementPath(Str(body, "name")) };
        if (Flag(body, "css")) args.Add("--css");
        else
        {
            foreach (var folder in SearchIn(body)) args.AddRange(["--in", folder]);
            if (Flag(body, "history")) args.Add("--history");
        }
        return Wrap(await Oml([.. Timed(body, args)]));
    }

    static async Task<Reply> FindElements(JsonObject body)
    {
        var args = new List<string> { "find", ElementName(Str(body, "name"), "a name or a pattern such as Order*") };
        if (Str(body, "path") is not null) args.Add(ModuleFile(body));
        var folders = SearchIn(body);
        foreach (var folder in folders) args.AddRange(["--in", folder]);
        if (args.Count == 2) throw new BadInput("Choose a module, or add a folder with producer modules.");
        return Wrap(await Oml([.. Timed(body, args)]));
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
            var options = PreviewOptions(body);
            List<string> args = ["preview", module, element, "--theme", theme, "--width", width.ToString(), .. options, "--out", html];
            var png = Path.Combine(folder, element + ".png");
            var screenshot = body["screenshot"] is JsonValue s && s.TryGetValue<bool>(out var wanted) && wanted;
            if (screenshot) args.AddRange(["--screenshot", png]);
            var r = await Oml([.. Timed(body, args)]);
            var extra = new JsonObject();
            if (File.Exists(html)) extra["view"] = AddView(ReportsHeight(await File.ReadAllTextAsync(html)));
            if (File.Exists(png)) extra["png"] = Convert.ToBase64String(await File.ReadAllBytesAsync(png));
            // The person's version of this command, with names they recognise instead of the temporary folder.
            List<string> shown = ["preview", module, element, "--theme", theme, "--width", width.ToString(), .. options];
            if (screenshot) shown.AddRange(["--screenshot", $"{element}-{theme}-{width}.png"]);
            if (Flag(body, "timings")) shown.Add("--timings");
            return Wrap(r with { Cli = CliText(shown), Output = Scrub(r.Output, folder) }, extra);
        }
        finally { TryDelete(folder); }
    }

    // The dashboard copy of a preview tells the page its height, for the viewer's "Full height" (the sandboxed frame has no
    // origin, so the page cannot read it). Only a number goes out; the page checks that it came from its own frame.
    const string HeightScript = "<script>(()=>{const s=()=>parent.postMessage({omlHeight:document.documentElement.scrollHeight},'*');" +
        "s();new ResizeObserver(s).observe(document.documentElement);addEventListener('load',s);})();</script>";

    static string ReportsHeight(string html)
    {
        var end = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return end < 0 ? html + HeightScript : html.Insert(end, HeightScript);
    }

    static async Task<Reply> OpenApiExport(JsonObject body)
    {
        var module = ModuleFile(body);
        var folder = NewWorkFolder("openapi");
        var args = new List<string> { "openapi", module, "--out", folder };
        if (Str(body, "api") is { } api) args.InsertRange(2, ["--api", ElementName(api, "a REST API name")]);
        foreach (var server in (body["servers"] as JsonArray ?? []).Select(v => v is JsonValue j && j.TryGetValue<string>(out var t) ? t.Trim() : "").Where(t => t.Length > 0))
        {
            if (server.Length > 500 || !Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new BadInput($"A server must be a full http:// or https:// address: {server}");
            args.AddRange(["--server", server]);
        }
        var r = await Oml([.. Timed(body, args)]);
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
            var json = Path.Combine(folder, "architecture.json");
            var args = new List<string> { "architecture" };
            foreach (var input in inputs) args.AddRange(["--in", input]);
            var options = ArchitectureOptions(body);
            args.AddRange(options);
            args.AddRange(["--out", html]);
            var wantJson = body["json"] is JsonValue jv && jv.TryGetValue<bool>(out var j) && j;
            if (wantJson) args.AddRange(["--json", json]);
            var r = await Oml([.. Timed(body, args)]);
            var extra = Report(html, wantJson ? json : null);
            if (upload is not null)
                // The folder is named in words, not quoted as if it were a real path.
                r = r with { Cli = "oml architecture --in <folder with these files> " + CliText(options.Concat(new[] { "--out", "architecture.html" })
                    .Concat(wantJson ? new[] { "--json", "architecture.json" } : []))[4..], Output = Scrub(r.Output, upload) };
            return Wrap(r, extra);
        }
        finally { if (upload is not null) TryDelete(upload); }
    }

    // --app (repeated) and --layers, shared by the architecture page and the LifeTime team report.
    static List<string> ArchitectureOptions(JsonObject body)
    {
        var args = new List<string>();
        foreach (var app in (body["apps"] as JsonArray ?? []).Select(v => v is JsonValue a && a.TryGetValue<string>(out var t) ? t.Trim() : "").Where(t => t.Length > 0))
            args.AddRange(["--app", LifeTimeName(app, "an application name")]);
        if (Str(body, "layers") is not null) args.AddRange(["--layers", ExistingFile(body, "layers", [".json"], "a layers .json file")]);
        return args;
    }

    // A generated HTML report: shown in the sandboxed frame, and downloadable with its JSON from /download/<id>.
    static JsonObject Report(string html, string? json)
    {
        var extra = new JsonObject { ["path"] = File.Exists(html) ? html : null };
        if (File.Exists(html))
        {
            extra["view"] = AddView(File.ReadAllText(html));
            extra["download"] = AddDownload(Path.GetFileName(html), "text/html; charset=utf-8", html);
        }
        if (json is not null && File.Exists(json)) extra["jsonDownload"] = AddDownload(Path.GetFileName(json), "application/json", json);
        return extra;
    }

    // ---- getting modules: a folder, a package, an upload ----

    static readonly string[] ModuleTypes = [".oml", ".oap"];
    static readonly TimeSpan FolderTimeLimit = TimeSpan.FromSeconds(10);

    static Reply FolderModules(JsonObject body)
    {
        var folder = UserPath(Str(body, "folder"), "a folder");
        if (!Directory.Exists(folder)) throw new BadInput($"Folder not found: {folder}");
        const int limit = 500;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 5 };
        // A large tree (a whole drive, a network share) must not hold the page: stop after 10 seconds and say so.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var found = new List<string>();
        var timedOut = false;
        foreach (var file in Directory.EnumerateFiles(folder, "*", options))
        {
            if (clock.Elapsed > FolderTimeLimit) { timedOut = true; break; }
            if (!ModuleTypes.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
            found.Add(file);
            if (found.Count > limit) break;
        }
        var files = new JsonArray();
        foreach (var file in found.Take(limit).Order(StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(file);
            files.Add(new JsonObject { ["path"] = file, ["name"] = info.Name, ["kind"] = info.Extension.TrimStart('.').ToLowerInvariant(), ["bytes"] = info.Length,
                ["modified"] = info.LastWriteTimeUtc.ToString("o") });
        }
        return new Reply(200, new JsonObject { ["folder"] = folder, ["files"] = files, ["truncated"] = found.Count > limit, ["stoppedEarly"] = timedOut,
            ["cli"] = $"Get-ChildItem {Quote(folder)} -Recurse -Include *.oml,*.oap" });
    }

    static async Task<Reply> ExtractPackage(JsonObject body)
    {
        var package = PackageFile(body);
        var output = Path.Combine(NewWorkFolder("extracted"), Path.GetFileNameWithoutExtension(package));
        return Wrap(await Oml("app", "extract", package, output), new JsonObject { ["folder"] = output });
    }

    // Dropped .oml and .oap files are kept (not temporary): they become modules of the working set.
    static Reply UploadModules(JsonObject body)
    {
        var (temp, names) = SaveUploads(body, ModuleTypes);
        try
        {
            var folder = NewWorkFolder("uploaded");
            var files = new JsonArray();
            foreach (var name in names)
            {
                var path = Path.Combine(folder, name);
                File.Move(Path.Combine(temp, "in", name), path);
                files.Add(new JsonObject { ["name"] = name, ["path"] = path, ["kind"] = Path.GetExtension(name).TrimStart('.').ToLowerInvariant(), ["bytes"] = new FileInfo(path).Length });
            }
            return new Reply(200, new JsonObject { ["folder"] = folder, ["files"] = files });
        }
        finally { TryDelete(temp); }
    }

    static async Task<Reply> ExportTranslations(JsonObject body)
    {
        var module = ModuleFile(body);
        var format = Str(body, "format") is "ResX" ? "ResX" : "Excel";
        var folder = NewWorkFolder("translations");
        var target = format == "Excel" ? Path.Combine(folder, Path.GetFileNameWithoutExtension(module) + ".xlsx") : Path.Combine(folder, "resx");
        var r = await Oml("translations", "export", module, target, "--format", format);
        var files = new JsonArray();
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
            files.Add(new JsonObject { ["name"] = Path.GetFileName(file), ["path"] = file, ["download"] = AddDownload(Path.GetFileName(file), "application/octet-stream", file) });
        return Wrap(r, new JsonObject { ["folder"] = folder, ["files"] = files });
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
            return Converted(r, folder, output, names, $"oml font woff2 {Quote(names[0])}", "fonts-woff2.zip");
        }
        finally { TryDelete(folder); }
    }

    static async Task<Reply> ConvertImages(JsonObject body)
    {
        // Every option is checked here with the CLI's ranges, so a wrong value gets a clear 400, never a silent default.
        // An unknown format is refused: silently ignoring it once returned a PNG for "ico".
        var to = Str(body, "to") is { Length: > 0 } t
            ? t is "png" or "jpeg" or "webp" or "svg" or "ico" ? t : throw new BadInput($"{Field("to")} must be png, jpeg, webp, svg or ico, not '{t}'.")
            : null;
        var options = new List<(string key, string option, string value)>();
        if (to is not null) options.Add(("to", "--to", to));
        void Option(string key, string? value, bool allowed, string refusal)
        {
            if (value is null) return;
            if (!allowed) throw new BadInput($"{Field(key)} {refusal}.");
            options.Add((key, ImageFields[key].option, value));
        }
        const string notIco = "does not apply to ICO: the icon sizes set the frames";
        Option("maxWidth", Whole(body, "maxWidth", 1, 100000), to != "ico", notIco);
        Option("maxHeight", Whole(body, "maxHeight", 1, 100000), to != "ico", notIco);
        Option("quality", Whole(body, "quality", 1, 100), to is null or "jpeg" or "webp", "applies to JPEG and WebP only");
        Option("sizes", Sizes(body), to == "ico", "needs the ICO format");
        var mode = Str(body, "mode");
        if (!Empty(body, "mode") && mode is not ("trace" or "embed")) throw new BadInput($"{Field("mode")} must be trace or embed.");
        Option("mode", mode, to == "svg", "needs the SVG format");
        var trace = to == "svg" && mode != "embed";
        const string traceOnly = "only applies to tracing: the SVG format with mode trace";
        Option("colors", Whole(body, "colors", 1, 32), trace, traceOnly);
        Option("tolerance", Number(body, "tolerance", 0.1, 5), trace, traceOnly);
        Option("minArea", Number(body, "minArea", 0, 1_000_000), trace, traceOnly);
        Option("alphaThreshold", Whole(body, "alphaThreshold", 1, 255), trace, traceOnly);
        Option("transparentBg", OptionFlag(body, "transparentBg"), trace, traceOnly);
        Option("precision", Whole(body, "precision", 0, 10), to == "svg", "needs the SVG format");
        Option("removeIds", OptionFlag(body, "removeIds"), to == "svg", "needs the SVG format");
        Option("aggressive", OptionFlag(body, "aggressive"), to == "svg", "needs the SVG format");
        Option("stripTitle", OptionFlag(body, "stripTitle"), to == "svg", "needs the SVG format");
        // The CLI cannot keep ICO, GIF or BMP as they are; ask for a format instead of a per-file error that names a CLI option.
        if (to is null && body["files"] is JsonArray uploads &&
            uploads.Any(f => Path.GetExtension(f?["name"]?.GetValue<string>() ?? "").ToLowerInvariant() is ".ico" or ".gif" or ".bmp"))
            throw new BadInput("ICO, GIF and BMP files need a target format. Pick PNG, WebP, JPEG, SVG or ICO under Format.");
        var (folder, names) = SaveUploads(body, ImageTypes);
        try
        {
            // Only what some file of this request uses: tracing options need a PNG or JPEG, the SVG-only optimizer
            // options need an SVG. So the run and the shown command hold only what applies (the CLI refuses tracing
            // options for an SVG file).
            var svgs = names.Count(n => Path.GetExtension(n).Equals(".svg", StringComparison.OrdinalIgnoreCase));
            var applied = options.Where(o => !(svgs == names.Count && TraceKeys.Contains(o.key)) && !(svgs == 0 && SvgInputKeys.Contains(o.key)))
                .SelectMany(o => o.value.Length == 0 ? [o.option] : new[] { o.option, o.value }).ToList();
            var output = Path.Combine(folder, "out");
            var r = await Oml([.. new[] { "image", "batch", Path.Combine(folder, "in") }, .. applied, "--out", output]);
            // One file: the command a person types for it. Several: the batch form, with the folders named in words.
            var shown = names.Count == 1 ? CliText(new[] { "image", names[0] }.Concat(applied))
                : "oml image batch <folder with these files> " + (applied.Count > 0 ? CliText(applied)[4..] + " " : "") + "--out <output folder>";
            return Converted(r, folder, output, names, shown, "images-optimized.zip");
        }
        finally { TryDelete(folder); }
    }

    // Each image field of the page: its label there and its CLI option. Errors name both: "Colours (--colors) must ...".
    static readonly Dictionary<string, (string label, string option)> ImageFields = new()
    {
        ["to"] = ("Format", "--to"), ["maxWidth"] = ("Max width", "--max-width"), ["maxHeight"] = ("Max height", "--max-height"),
        ["quality"] = ("Quality", "--quality"), ["sizes"] = ("Icon sizes", "--sizes"), ["mode"] = ("Mode", "--mode"),
        ["colors"] = ("Colours", "--colors"), ["tolerance"] = ("Smoothness", "--tolerance"), ["minArea"] = ("Remove specks under", "--min-area"),
        ["alphaThreshold"] = ("Alpha threshold", "--alpha-threshold"), ["transparentBg"] = ("Transparent background", "--transparent-bg"),
        ["precision"] = ("Decimals", "--precision"), ["removeIds"] = ("Remove unused ids", "--remove-ids"),
        ["aggressive"] = ("Smallest result", "--aggressive"), ["stripTitle"] = ("Remove the title and description", "--strip-title"),
    };
    static readonly string[] TraceKeys = ["mode", "colors", "tolerance", "minArea", "alphaThreshold", "transparentBg"];
    static readonly string[] SvgInputKeys = ["removeIds", "aggressive", "stripTitle"];
    static string Field(string key) => ImageFields.TryGetValue(key, out var f) ? $"{f.label} ({f.option})" : key;

    // Checked option values, as command-line text. Absent, null or "" gives null (the CLI default).
    static bool Empty(JsonObject body, string name) => body[name] is null || body[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length == 0;

    static string? Whole(JsonObject body, string name, int min, int max)
    {
        if (Empty(body, name)) return null;
        if (body[name] is JsonValue v && v.TryGetValue<double>(out var n) && n == Math.Floor(n) && n >= min && n <= max) return ((int)n).ToString(CultureInfo.InvariantCulture);
        throw new BadInput($"{Field(name)} must be a whole number from {min} to {max}.");
    }

    static string? Number(JsonObject body, string name, double min, double max)
    {
        if (Empty(body, name)) return null;
        if (body[name] is JsonValue v && v.TryGetValue<double>(out var n) && n >= min && n <= max) return n.ToString("0.###", CultureInfo.InvariantCulture);
        throw new BadInput($"{Field(name)} must be a number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}.");
    }

    // true gives the bare option (""); false or absent gives nothing.
    static string? OptionFlag(JsonObject body, string name) => body[name] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "" : null,
        _ => throw new BadInput($"{Field(name)} must be true or false."),
    };

    // ICO frame sizes: whole numbers 1-256, no repeats, as the CLI checks them.
    static string? Sizes(JsonObject body)
    {
        if (body["sizes"] is null) return null;
        if (body["sizes"] is not JsonArray list || list.Count is 0 or > 16) throw new BadInput($"{Field("sizes")} must be a list of 1 to 16 sizes, such as [16, 32, 48].");
        var sizes = new List<int>();
        foreach (var item in list)
        {
            if (item is not JsonValue v || !v.TryGetValue<double>(out var n) || n != Math.Floor(n) || n is < 1 or > 256) throw new BadInput($"{Field("sizes")}: each size must be a whole number from 1 to 256.");
            if (sizes.Contains((int)n)) throw new BadInput($"{Field("sizes")}: {n} is listed twice.");
            sizes.Add((int)n);
        }
        return string.Join(",", sizes.Order());
    }

    static string Quote(string name) => CliText([name])[4..];

    // One row per uploaded file with sizes and the converted bytes, plus a zip when there is more than one result.
    static Reply Converted(Result r, string folder, string output, List<string> names, string cli, string zipName)
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
                if (item["details"] is { } details) row["details"] = Scrub(details, folder); // mode, colours, sizes, kept and the reason
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
            var zipFile = Path.Combine(NewWorkFolder("zips"), zipName); // a file, not memory: /download keeps only its path
            using (var zip = ZipFile.Open(zipFile, ZipArchiveMode.Create))
                foreach (var (name, data) in made)
                {
                    using var entry = zip.CreateEntry(name, CompressionLevel.NoCompression).Open(); // already compressed formats
                    entry.Write(data);
                }
            extraFields["zipDownload"] = AddDownload(zipName, "application/zip", zipFile);
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
        if (verb == "team-apps") args.Add(LifeTimeName(string.IsNullOrWhiteSpace(value) ? null : value, "a team"));
        if (verb == "status")
        {
            if (value is null || !Regex.IsMatch(value, "^[A-Za-z0-9-]{1,64}$")) throw new BadInput("A deployment key has only letters, digits and dashes.");
            args.Add(value);
        }
        return Wrap(await Oml([.. args]), new JsonObject { ["configured"] = true });
    }

    static bool LifeTimeConfigured() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OML_LIFETIME_HOST")) && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OML_LIFETIME_TOKEN"));

    // `lt download`: a GET of the package running in one environment. The .oap and its modules stay in the work folder.
    static async Task<Reply> LifeTimeDownload(JsonObject body)
    {
        if (!LifeTimeConfigured()) return new Reply(200, new JsonObject { ["configured"] = false });
        var app = LifeTimeName(Str(body, "app"), "an application");
        var env = LifeTimeName(Str(body, "env"), "an environment");
        var folder = NewWorkFolder("lifetime");
        var args = new List<string> { "lt", "download", app, "--env", env, "--out", folder };
        if (body["extract"] is JsonValue e && e.TryGetValue<bool>(out var extract) && extract) args.Add("--extract");
        var r = await Oml([.. args]);
        var extra = new JsonObject { ["configured"] = true, ["folder"] = folder };
        if ((r.Output as JsonObject)?["file"] is JsonValue f && f.TryGetValue<string>(out var file) && File.Exists(file))
            extra["download"] = AddDownload(Path.GetFileName(file), "application/octet-stream", file);
        return Wrap(r with { Cli = CliText(args.Select(a => a == folder ? @"C:\Exports" : a)) }, extra);
    }

    // `architecture --team` and `check --team`: read only; the packages go to a temporary folder that the command removes.
    static async Task<Reply> LifeTimeTeamReport(JsonObject body)
    {
        if (!LifeTimeConfigured()) return new Reply(200, new JsonObject { ["configured"] = false });
        var team = LifeTimeName(Str(body, "team"), "a team");
        var kind = Str(body, "kind") is "check" ? "check" : "architecture";
        var folder = NewWorkFolder(kind);
        var (html, json) = (Path.Combine(folder, kind + ".html"), Path.Combine(folder, kind + ".json"));
        var args = new List<string> { kind, "--team", team };
        args.AddRange(ArchitectureOptions(body));
        args.AddRange(["--out", html, "--json", json]);
        var r = await Oml([.. args]);
        var extra = Report(html, json);
        extra["configured"] = true;
        return Wrap(r with { Cli = CliText(args.Select(a => a == html ? kind + ".html" : a == json ? kind + ".json" : a)) }, extra);
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
