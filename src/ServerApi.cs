using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// `oml server`: the platform server's REST API (server.api/v1), the one Service Studio uses for its module list,
// version list and download, so oml can list and pull any module or version with Service Studio closed. REST only:
// the SOAP service (ServiceStudio.asmx) needs Studio's private password protection, which oml does not copy.
// The routes and the login were read from Service Studio 11.55.95's client code (OutSystems.Communication.dll,
// OutSystems.Communication.Identity.dll) and later confirmed on a Personal Environment:
//   GET  /server.api/v1/environment/handshake-properties   no login; [{id, value}]. 404: the server has no server API.
//   POST /server.identity/connect/token   form: grant_type=password, client_id=service_studio,
//        scope="offline_access server_api allow_system_users", username, password -> {access_token, expires_in, ...}
//   GET  /server.api/v1/modules?embed=application,lastVersion   (Authorization: Bearer <access_token>)
//        [{key, name, publishedVersionId, _embedded: {application: {key, name}, lastVersion: {versionId, versionNumber, uploaderUsername, uploadedDate}}}]
//   GET  /server.api/v1/modules/{key}/versions?limit=9999   [{versionId, versionNumber, uploaderUsername, uploadedDate}]
//   GET  /server.api/v1/modules/{key}/versions/{versionId}/source-code   the .oml bytes
// Service Studio picks REST or SOAP by the server property SoapWSRestMigration (Modules|ModulesVersion|Download|...),
// which it reads through SOAP. oml reads it from the handshake properties when the server lists it there, warns when
// it says SOAP, and tries REST anyway: a 404 on a route is the answer "no REST route on this server".
// The password and the token stay in memory for this one command: nothing is cached on disk, and both are removed
// from every output. Certificates are checked (Service Studio skips that check; oml does not).
// oml never publishes through this API: a pulled module goes back through Service Studio (or oml publish).
// ServerCheck.cs has `server check` (scores over time) and `review --server`.
static partial class ServerApi
{
    internal const string Usage = "oml server modules [--app <Name>] | versions <Module> | pull <Module> [--version N] --out <file.oml> | pull --app <Name> --out <folder> | check [--app <Name>]... [--rules <oml-rules.json>] [--out <report.html>]";
    const string Api = "server.api/v1/";
    const string StudioPull = "Pull through Service Studio instead: open the module in Service Studio, then run oml pull <Module> (a person approves the session there).";
    // In a folder that oml server pull --app (or check, or review --keep) filled: which module version each file is.
    const string ManifestName = "oml-server.json";

    public static int Run(string[] args) => Guarded(ctx =>
    {
        var (verb, module, options) = Parse(args);
        string? One(string option) => options.TryGetValue(option, out var values) ? values[0] : null;
        ctx.Settings();
        return verb switch
        {
            "modules" => ListModules(ctx, One("--app")),
            "versions" => ListVersions(ctx, module!),
            "pull" when module is null => PullApp(ctx, One("--app")!, One("--out")!),
            "pull" => PullOne(ctx, module, One("--version"), One("--out")!),
            _ => Check(ctx, options.GetValueOrDefault("--app") ?? [], One("--rules"), One("--out")),
        };
    });

    // One login per command; every error is one JSON object with exit 2, with the password and the token removed.
    static int Guarded(Func<Ctx, int> body)
    {
        using var ctx = new Ctx();
        try { return body(ctx); }
        catch (ServerError e)
        {
            var result = new JsonObject { ["error"] = e.Message };
            if (e.NoRestRoute) result["noRestRoute"] = true;
            if (e.Next is not null) result["next"] = e.Next;
            ctx.Emit(result);
            return 2;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            ctx.Emit(new JsonObject { ["error"] = "Request to the server failed (connection, TLS or timeout). oml checks the certificate: a self-signed or untrusted one is refused. Check OML_SERVER_HOST." });
            return 2;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            ctx.Emit(new JsonObject { ["error"] = "The server sent an answer oml cannot read (not the JSON Service Studio expects). Check OML_SERVER_HOST." });
            return 2;
        }
    }

    static int ListModules(Ctx ctx, string? app)
    {
        var instead = "List the modules in Service Studio instead (oml studio lists the open ones).";
        var client = ctx.Connect("Modules", instead);
        var modules = client.Modules(instead);
        var rows = (app is null ? modules : AppModules(modules, app)).Select(Row)
            .OrderBy(r => r["application"]?.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r["name"]?.GetValue<string>(), StringComparer.OrdinalIgnoreCase).ToArray();
        ctx.Emit(new JsonObject { ["host"] = ctx.Host, ["count"] = rows.Length, ["modules"] = new JsonArray(rows) });
        return 0;
    }

    static int ListVersions(Ctx ctx, string module)
    {
        var client = ctx.Connect("ModulesVersion", StudioPull);
        var found = FindModule(client.Modules(StudioPull), module);
        var key = Text(found, "key");
        var published = Int(Prop(found, "publishedVersionId"));
        var versions = client.Versions(key).Select(x => VersionRow(x, published)).OrderByDescending(x => x["version"]?.GetValue<int>() ?? 0).ToArray();
        ctx.Emit(new JsonObject
        {
            ["module"] = Text(found, "name"), ["key"] = key, ["application"] = Text(Embedded(found, "application"), "name"),
            ["publishedVersionId"] = published, ["count"] = versions.Length, ["versions"] = new JsonArray(versions),
            ["next"] = $"oml server pull {Text(found, "name")} --version <version> --out <file.oml>",
        });
        return 0;
    }

    static JsonObject VersionRow(JsonNode? x, int? published) => new()
    {
        ["version"] = Int(Prop(x, "versionNumber")), ["versionId"] = Int(Prop(x, "versionId")),
        ["published"] = published is not null && Int(Prop(x, "versionId")) == published,
        ["uploadedBy"] = Text(x, "uploaderUsername"), ["uploadedDate"] = Prop(x, "uploadedDate")?.DeepClone(),
    };

    // pull <Module>: --version is the version number; the download takes the version id. No --version: the published version.
    static int PullOne(Ctx ctx, string module, string? version, string outArg)
    {
        var outFile = Path.GetFullPath(outArg);
        if (!outFile.EndsWith(".oml", StringComparison.OrdinalIgnoreCase)) throw new ServerError("--out must be an .oml file.");
        if (File.Exists(outFile) || Directory.Exists(outFile)) throw new ServerError($"Output already exists: {outFile}. Use another --out path, or delete that file first.");
        int? wanted = null;
        if (version is not null)
            wanted = int.TryParse(version, out var n) && n > 0 ? n : throw new ServerError("--version takes a version number, such as 12 (oml server versions <Module> lists them).");
        var client = ctx.Connect("Download", StudioPull);
        var found = FindModule(client.Modules(StudioPull), module);
        var key = Text(found, "key");
        var name = Text(found, "name");
        int? number, versionId;
        if (wanted is not null)
        {
            var match = client.Versions(key).FirstOrDefault(x => Int(Prop(x, "versionNumber")) == wanted)
                ?? throw new ServerError($"Module '{name}' has no version {wanted} on this server. Run oml server versions {name}.");
            (number, versionId) = (wanted, Int(Prop(match, "versionId")));
        }
        else (versionId, number) = PublishedVersion(found);
        if (versionId is not > 0) throw new ServerError($"The server lists no version of module '{name}' to download. Run oml server versions {name}.");
        number ??= NumberOf(client, key, versionId.Value);
        TerminalProgress.Message($"Downloading {name} from {ctx.Host}...");
        var bytes = client.SourceCode(key, versionId.Value, outFile);
        TerminalProgress.Message("Downloaded.");
        ctx.Emit(new JsonObject
        {
            ["module"] = name, ["key"] = key, ["application"] = Text(Embedded(found, "application"), "name"),
            ["version"] = number, ["versionId"] = versionId, ["published"] = versionId == Int(Prop(found, "publishedVersionId")),
            ["file"] = outFile, ["bytes"] = bytes, ["next"] = $"oml header \"{outFile}\"",
        });
        return 0;
    }

    // pull --app <Name> --out <folder>: the published version of every module of one application, one file per module.
    // A file already there is never overwritten: the same module version from this server (by the folder's manifest and
    // the file's hash) is reported unchanged and not downloaded again; anything else is skipped with the reason.
    static int PullApp(Ctx ctx, string app, string outArg)
    {
        var folder = Path.GetFullPath(outArg);
        if (File.Exists(folder) || folder.EndsWith(".oml", StringComparison.OrdinalIgnoreCase))
            throw new ServerError("With --app, --out is a folder: each module is saved there as <Module>.oml.");
        var client = ctx.Connect("Download", StudioPull);
        var all = client.Modules(StudioPull);
        var mine = AppModules(all, app);
        CreateFolder(folder);
        var manifest = ReadManifest(folder);
        var modules = new JsonArray();
        var skipped = new JsonArray();
        var failed = new JsonArray();
        var number = 0;
        foreach (var m in mine)
        {
            var (name, key) = (Text(m, "name"), Text(m, "key"));
            var file = Path.Combine(folder, FileName(m, mine));
            try
            {
                var (versionId, version) = PublishedVersion(m);
                if (versionId is not > 0) { skipped.Add(new JsonObject { ["name"] = name, ["reason"] = "The server lists no version of this module." }); continue; }
                if (File.Exists(file) || Directory.Exists(file))
                {
                    if (Unchanged(manifest, ctx.Host!, file, key, versionId.Value) is { } same)
                    {
                        modules.Add(PulledRow(name, Int(same["version"]), versionId, file, new FileInfo(file).Length, unchanged: true));
                        continue;
                    }
                    skipped.Add(new JsonObject { ["name"] = name, ["file"] = file, ["reason"] = "The file exists and is not this module version from this server. Delete it, or use another --out folder." });
                    continue;
                }
                version ??= NumberOf(client, key, versionId.Value);
                TerminalProgress.Message($"  {++number}/{mine.Length}: {name}");
                var bytes = client.SourceCode(key, versionId.Value, file);
                Remember(manifest, ctx.Host!, file, m, versionId.Value, version, bytes);
                WriteManifest(folder, manifest);
                modules.Add(PulledRow(name, version, versionId, file, bytes, unchanged: false));
            }
            catch (ServerError e) { failed.Add(new JsonObject { ["name"] = name, ["reason"] = e.Message }); }
        }
        var result = new JsonObject
        {
            ["host"] = ctx.Host, ["application"] = Text(Embedded(mine[0], "application"), "name"), ["folder"] = folder,
            ["count"] = modules.Count, ["modules"] = modules,
        };
        if (skipped.Count > 0) result["skipped"] = skipped;
        if (failed.Count > 0) result["failed"] = failed;
        result["next"] = $"oml check --in \"{folder}\"; oml architecture --in \"{folder}\"";
        ctx.Emit(result);
        return skipped.Count + failed.Count > 0 ? 1 : 0;
    }

    static JsonObject PulledRow(string name, int? version, int? versionId, string file, long bytes, bool unchanged)
    {
        var row = new JsonObject { ["name"] = name, ["version"] = version, ["versionId"] = versionId, ["file"] = file, ["bytes"] = bytes };
        if (unchanged) row["unchanged"] = true;
        return row;
    }

    // The version to take when none is named: the published one, else the last upload. The number is known here only
    // when the published version is also the last upload; NumberOf asks the version list otherwise.
    static (int? versionId, int? number) PublishedVersion(JsonNode found)
    {
        var published = Int(Prop(found, "publishedVersionId"));
        var last = Embedded(found, "lastVersion");
        if (published is > 0) return (published, Int(Prop(last, "versionId")) == published ? Int(Prop(last, "versionNumber")) : null);
        return (Int(Prop(last, "versionId")), Int(Prop(last, "versionNumber")));
    }

    // The number of an older published version is only in the version list (a nice-to-have: null when that call fails).
    static int? NumberOf(Client client, string key, int versionId)
    {
        try { return Int(Prop(client.Versions(key).FirstOrDefault(x => Int(Prop(x, "versionId")) == versionId), "versionNumber")); }
        catch (ServerError) { return null; }
    }

    // The modules of one application, by name in any case or by key, sorted by name. An unknown name lists the closest ones.
    static JsonNode[] AppModules(JsonNode?[] modules, string app)
    {
        var mine = modules.OfType<JsonNode>().Where(m => string.Equals(Text(Embedded(m, "application"), "name"), app, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Text(Embedded(m, "application"), "key"), app, StringComparison.Ordinal))
            .OrderBy(m => Text(Embedded(m, "application"), "name"), StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => Text(m, "name"), StringComparer.OrdinalIgnoreCase).ToArray();
        if (mine.Length > 0) return mine;
        var names = modules.Select(m => Text(Embedded(m, "application"), "name")).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var close = ClosestNames(app, names);
        throw new ServerError($"No module of application '{app}' on this server. "
            + (close.Length > 0 ? $"Closest: {string.Join(", ", close)}." : $"Applications: {string.Join(", ", names.Order(StringComparer.OrdinalIgnoreCase).Take(10))}{(names.Length > 10 ? ", ..." : "")}."))
        { Next = "oml server modules" };
    }

    // Names that contain the word (or the other way round), or are a few typing errors away; at most 5, closest first.
    internal static string[] ClosestNames(string word, IEnumerable<string> names)
    {
        var w = word.ToLowerInvariant();
        return names.Select(n => (n, d: n.ToLowerInvariant() is var l && (l.Contains(w) || w.Contains(l)) ? 0 : Distance(w, l)))
            .Where(x => x.d <= Math.Max(2, w.Length / 3)).OrderBy(x => x.d).ThenBy(x => x.n, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.n).Take(5).ToArray();
    }

    static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var current = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = current;
            }
        }
        return row[b.Length];
    }

    // <Module>.oml; when two modules of the list share a name, <Module>.<key>.oml for each of them.
    static string FileName(JsonNode module, IEnumerable<JsonNode?> list)
    {
        var name = Text(module, "name");
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var twins = list.Count(m => string.Equals(Text(m, "name"), name, StringComparison.OrdinalIgnoreCase));
        return twins > 1 ? $"{safe}.{string.Concat(Text(module, "key").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c))}.oml" : safe + ".oml";
    }

    static void CreateFolder(string folder)
    {
        try { Directory.CreateDirectory(folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new ServerError($"Could not create the folder {folder}. Check the path."); }
    }

    // ---- The folder manifest (oml-server.json): host, and per file the module key, version id, number, size and hash.

    static JsonObject ReadManifest(string folder)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(Path.Combine(folder, ManifestName))) is JsonObject o && o["files"] is JsonObject) return o;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new JsonObject { ["files"] = new JsonObject() };
    }

    static void WriteManifest(string folder, JsonObject manifest)
    {
        var path = Path.Combine(folder, ManifestName);
        try
        {
            File.WriteAllText(path + ".tmp", manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new ServerError($"Could not write {path}. Check that the folder is writable."); }
    }

    // The manifest entry when the file is that module version from this host and its bytes are the ones pulled; else null.
    static JsonObject? Unchanged(JsonObject manifest, string host, string file, string key, int versionId)
    {
        if (!string.Equals(manifest["host"]?.GetValue<string>(), host, StringComparison.OrdinalIgnoreCase)) return null;
        if (manifest["files"]?[Path.GetFileName(file)] is not JsonObject entry || !File.Exists(file)) return null;
        if (entry["key"]?.GetValue<string>() != key || entry["versionId"]?.GetValue<int>() != versionId) return null;
        if (entry["bytes"]?.GetValue<long>() != new FileInfo(file).Length || entry["sha256"]?.GetValue<string>() != Sha256(file)) return null;
        return entry;
    }

    static void Remember(JsonObject manifest, string host, string file, JsonNode module, int versionId, int? version, long bytes)
    {
        if (!string.Equals(manifest["host"]?.GetValue<string>(), host, StringComparison.OrdinalIgnoreCase))
        {
            manifest["host"] = host;
            manifest["files"] = new JsonObject();
        }
        manifest["files"]![Path.GetFileName(file)] = new JsonObject
        {
            ["name"] = Text(module, "name"), ["key"] = Text(module, "key"), ["application"] = Text(Embedded(module, "application"), "name"),
            ["version"] = version, ["versionId"] = versionId, ["bytes"] = bytes, ["sha256"] = Sha256(file),
            ["pulledAt"] = DateTime.UtcNow.ToString("o"),
        };
    }

    static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // One module of the list, flat: name, key, application, the published version id and the last uploaded version.
    static JsonObject Row(JsonNode? module)
    {
        var app = Embedded(module, "application");
        var last = Embedded(module, "lastVersion");
        return new JsonObject
        {
            ["name"] = Text(module, "name"), ["key"] = Text(module, "key"),
            ["application"] = Text(app, "name"), ["applicationKey"] = Text(app, "key"),
            ["publishedVersionId"] = Int(Prop(module, "publishedVersionId")),
            ["lastVersion"] = Int(Prop(last, "versionNumber")), ["lastVersionId"] = Int(Prop(last, "versionId")),
            ["uploadedBy"] = Text(last, "uploaderUsername"), ["uploadedDate"] = Prop(last, "uploadedDate")?.DeepClone(),
        };
    }

    // By exact key, else by name without case. Service Studio matches a name exactly; a key is unique.
    static JsonNode FindModule(JsonNode?[] modules, string name)
    {
        var byKey = modules.Where(m => Text(m, "key") == name).ToArray();
        if (byKey.Length == 1) return byKey[0]!;
        var byName = modules.Where(m => string.Equals(Text(m, "name"), name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (byName.Length == 1) return byName[0]!;
        if (byName.Length > 1)
            throw new ServerError($"{byName.Length} modules are named '{name}'. Use the key instead: {string.Join(", ", byName.Select(m => Text(m, "key")))}.");
        throw new ServerError($"Module '{name}' is not on this server, or this user cannot see it.") { Next = "oml server modules" };
    }

    // OML_SERVER_HOST: a hostname with an optional port, as OML_LIFETIME_HOST. HTTPS only; the test switch allows HTTP
    // for 127.0.0.1 or localhost alone (as Service Studio does for a loopback host's token request).
    static Uri Endpoint(string host)
    {
        if (host.Any(char.IsWhiteSpace) || host.IndexOfAny(['/', '\\', '@', '?', '#']) >= 0
            || !Uri.TryCreate("https://" + host + "/", UriKind.Absolute, out var uri)
            || !(uri.Authority.Equals(host, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(":443", StringComparison.Ordinal) && uri.Authority.Equals(host[..^4], StringComparison.OrdinalIgnoreCase)))
            throw new ServerError("OML_SERVER_HOST must be a hostname with an optional port, without https://, path or credentials, such as dev.example.com.");
        var insecure = Environment.GetEnvironmentVariable("OML_SERVER_INSECURE_LOCALHOST") == "1";
        if (insecure && uri.Host is not ("127.0.0.1" or "localhost"))
            throw new ServerError("OML_SERVER_INSECURE_LOCALHOST is permitted only for 127.0.0.1 or localhost.");
        return new Uri($"{(insecure ? "http" : "https")}://{host}/");
    }

    static (string verb, string? module, Dictionary<string, List<string>> options) Parse(string[] args)
    {
        if (args.Length == 0) throw new ServerError(Usage);
        var verb = args[0];
        var appPull = verb == "pull" && args.Length > 1 && args[1] == "--app";
        var allowed = verb switch
        {
            "modules" => new[] { "--app" }, "versions" => System.Array.Empty<string>(),
            "pull" => appPull ? new[] { "--app", "--out" } : new[] { "--version", "--out" },
            "check" => new[] { "--app", "--rules", "--out" },
            _ => throw new ServerError(Usage),
        };
        int i = 1;
        string? module = null;
        if (verb is "versions" or "pull" && !appPull)
        {
            if (i == args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--")) throw new ServerError("Name the module (name or key). " + Usage);
            module = args[i++];
        }
        var options = new Dictionary<string, List<string>>();
        while (i < args.Length)
        {
            var option = args[i++];
            // check takes --app more than once (several applications); every other option once.
            if (!allowed.Contains(option) || options.ContainsKey(option) && !(verb == "check" && option == "--app")) throw new ServerError($"Unknown or repeated option {option}. " + Usage);
            if (i == args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--")) throw new ServerError($"{option} needs a value. " + Usage);
            (options.TryGetValue(option, out var list) ? list : options[option] = []).Add(args[i++]);
        }
        if (verb == "pull" && !options.ContainsKey("--out")) throw new ServerError((appPull ? "pull --app needs --out <folder>. " : "pull needs --out <file.oml>. ") + Usage);
        return (verb, module, options);
    }

    static JsonNode? Prop(JsonNode? node, string property) => node is JsonObject obj
        ? obj.FirstOrDefault(p => p.Key.Equals(property, StringComparison.OrdinalIgnoreCase)).Value : null;
    // `_embedded.<name>`: the HAL-style nested object Service Studio reads with its nested-path converter.
    static JsonNode? Embedded(JsonNode? node, string name) => Prop(Prop(node, "_embedded"), name) ?? Prop(node, "_embedded." + name);
    static string Text(JsonNode? node, string property) => Prop(node, property) switch
    {
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        JsonValue value => value.ToJsonString(),
        _ => "",
    };
    static int? Int(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue<int>(out var n) ? n : value.TryGetValue<string>(out var s) && int.TryParse(s, out var p) ? p : null
        : null;

    // One command's settings, connection and secrets. Emit is the only way out to stdout.
    sealed class Ctx : IDisposable
    {
        public readonly string? Password = Environment.GetEnvironmentVariable("OML_SERVER_PASSWORD");
        public string? Host, User, Token, Warning;
        HttpClientHandler? handler;
        HttpClient? http;

        public void Emit(JsonObject value)
        {
            if (Warning is not null && value["warning"] is null) value["warning"] = Warning;
            // No output holds them by design; this is the second guard. A value under 6 characters would match ordinary words.
            foreach (var secret in new[] { Password, Token })
                if (secret is { Length: >= 6 }) LifeTime.Redact(value, secret);
            Console.Out.WriteLine(TerminalOutput.Render(JsonOutput.Serialize(value)));
        }

        // The three settings, before any request and before the command's own input checks.
        public void Settings()
        {
            Host = Environment.GetEnvironmentVariable("OML_SERVER_HOST");
            User = Environment.GetEnvironmentVariable("OML_SERVER_USER");
            var missing = new[] { ("OML_SERVER_HOST", Host), ("OML_SERVER_USER", User), ("OML_SERVER_PASSWORD", Password) }
                .Where(p => string.IsNullOrWhiteSpace(p.Item2)).Select(p => p.Item1).ToArray();
            if (missing.Length > 0)
                throw new ServerError($"Set {string.Join(", ", missing)} in the environment: OML_SERVER_HOST is the server's hostname (no https://), OML_SERVER_USER and OML_SERVER_PASSWORD are the login you use in Service Studio.");
            if (User!.Any(char.IsControl) || Password!.Any(char.IsControl)) throw new ServerError("OML_SERVER_USER or OML_SERVER_PASSWORD holds a control character.");
        }

        // The server's flags (no login), then the password login as Service Studio's client. feature: the
        // SoapWSRestMigration name of what the command needs; instead: what to do when the server has no REST API.
        public Client Connect(string feature, string instead)
        {
            var root = Endpoint(Host!);
            handler = new HttpClientHandler { AllowAutoRedirect = false };
            http = new HttpClient(handler) { BaseAddress = root, Timeout = TimeSpan.FromSeconds(300) };
            var client = new Client(http);
            var properties = client.HandshakeProperties(instead);
            string Property(string id) => properties.FirstOrDefault(p => string.Equals(Text(p, "id"), id, StringComparison.OrdinalIgnoreCase)) is { } p ? Text(p, "value") : "";
            var migration = Property("SoapWSRestMigration");
            if (migration.Length > 0 && !migration.Split('|').Contains(feature, StringComparer.OrdinalIgnoreCase))
                Warning = $"This server's flag SoapWSRestMigration={migration} has no {feature}: Service Studio uses SOAP for this here. oml tried REST anyway.";
            Token = client.Login(User!, Password!, Property("IdentityService_ServiceURL").Length > 0);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            return client;
        }

        public void Dispose()
        {
            http?.Dispose();
            handler?.Dispose();
        }
    }

    sealed class Client(HttpClient http)
    {
        public JsonNode?[] HandshakeProperties(string instead)
        {
            using var response = Get(Api + "environment/handshake-properties");
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new ServerError("This server has no REST server API (server.api/v1 answered 404), so oml cannot reach it without Service Studio. " + instead) { NoRestRoute = true };
            Check(response, "reading the server's flags");
            return Json(response) is JsonArray list ? list.ToArray() : throw new InvalidOperationException();
        }

        public string Login(string user, string password, bool identityService)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "server.identity/connect/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "password", ["client_id"] = "service_studio",
                    ["scope"] = "offline_access server_api allow_system_users", ["username"] = user, ["password"] = password,
                }),
            };
            using var response = http.Send(request);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new ServerError("This server has no REST login (server.identity/connect/token answered 404). " + StudioPull) { NoRestRoute = true };
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                throw new ServerError($"Login refused (HTTP {(int)response.StatusCode}). Check OML_SERVER_USER and OML_SERVER_PASSWORD: the same login as in Service Studio."
                    + (identityService ? " This environment signs in through an external identity provider, which may not take a password here." : ""));
            Check(response, "logging in");
            var token = Json(response) is JsonObject o && o["access_token"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : "";
            if (token.Length == 0 || token.Any(char.IsControl)) throw new ServerError("The server's login answer has no usable access token.");
            return token;
        }

        public JsonNode?[] Modules(string instead)
        {
            using var response = Get(Api + "modules?embed=application,lastVersion");
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new ServerError("This server has no REST route for the module list (server.api/v1/modules answered 404). " + instead) { NoRestRoute = true };
            Check(response, "listing modules");
            return Json(response) is JsonArray list ? list.ToArray() : throw new InvalidOperationException();
        }

        public JsonNode?[] Versions(string key)
        {
            using var response = Get($"{Api}modules/{Uri.EscapeDataString(key)}/versions?limit=9999");
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new ServerError("This server has no REST route for module versions (answered 404), or the module was just deleted. Use the module's version history in Service Studio instead.") { NoRestRoute = true };
            Check(response, "listing versions");
            return Json(response) is JsonArray list ? list.ToArray() : throw new InvalidOperationException();
        }

        // Writes the version's .oml to path: CreateNew, so an existing file is never touched; a failed or empty download leaves no file.
        public long SourceCode(string key, int versionId, string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}modules/{Uri.EscapeDataString(key)}/versions/{versionId}/source-code");
            using var response = http.Send(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new ServerError($"The server answered 404 for version id {versionId}: the version is gone, or this server has no REST download route. Check oml server versions; else {StudioPull}") { NoRestRoute = true };
            Check(response, "downloading the module");
            FileStream output;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { throw new ServerError($"Could not create {path}. Check the --out path and that the file does not exist."); }
            using var input = response.Content.ReadAsStream();
            try
            {
                input.CopyTo(output);
                if (output.Length == 0) throw new ServerError("The server sent an empty file. Nothing was saved.");
                var length = output.Length;
                output.Dispose();
                return length;
            }
            catch
            {
                output.Dispose();
                File.Delete(path);
                throw;
            }
        }

        HttpResponseMessage Get(string path) => http.Send(new HttpRequestMessage(HttpMethod.Get, path));

        static void Check(HttpResponseMessage response, string step)
        {
            var code = (int)response.StatusCode;
            if (code is >= 200 and < 300) return;
            throw new ServerError(code switch
            {
                401 => $"The server refused the login token while {step} (HTTP 401). Check that this user may use Service Studio on this environment.",
                403 => $"Permission denied while {step} (HTTP 403). The user needs the right to open this module or application in Service Studio.",
                >= 300 and < 400 => $"The server answered with a redirect while {step}; oml does not follow it. Set OML_SERVER_HOST to the final HTTPS hostname.",
                _ => $"The server answered HTTP {code} while {step}. Try again later, or check the server in Service Center.",
            });
        }

        static JsonNode? Json(HttpResponseMessage response)
        {
            var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
    }

    sealed class ServerError(string message) : Exception(message)
    {
        public bool NoRestRoute { get; init; }
        public string? Next { get; init; }
    }
}
