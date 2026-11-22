using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// `oml server`: the platform server's REST API (server.api/v1), the one Service Studio uses for its module list,
// version list and download, so oml can list and pull any module or version with Service Studio closed. REST only:
// the SOAP service (ServiceStudio.asmx) needs Studio's private password protection, which oml does not copy.
// The routes and the login were read from Service Studio 11.55.95's client code (OutSystems.Communication.dll,
// OutSystems.Communication.Identity.dll); no real server was called:
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
static class ServerApi
{
    internal const string Usage = "oml server modules [--app <Name>] | versions <Module> | pull <Module> [--version N] --out <file.oml>";
    const string Api = "server.api/v1/";
    const string StudioPull = "Pull through Service Studio instead: open the module in Service Studio, then run oml pull <Module> (a person approves the session there).";

    public static int Run(string[] args)
    {
        var password = Environment.GetEnvironmentVariable("OML_SERVER_PASSWORD");
        string? token = null;
        string? warning = null;
        void Emit(JsonObject value)
        {
            if (warning is not null && value["warning"] is null) value["warning"] = warning;
            // No output holds them by design; this is the second guard. A value under 6 characters would match ordinary words.
            foreach (var secret in new[] { password, token })
                if (secret is { Length: >= 6 }) LifeTime.Redact(value, secret);
            Console.Out.WriteLine(TerminalOutput.Render(JsonOutput.Serialize(value)));
        }
        try
        {
            var (verb, module, options) = Parse(args);
            var host = Environment.GetEnvironmentVariable("OML_SERVER_HOST");
            var user = Environment.GetEnvironmentVariable("OML_SERVER_USER");
            var missing = new[] { ("OML_SERVER_HOST", host), ("OML_SERVER_USER", user), ("OML_SERVER_PASSWORD", password) }
                .Where(p => string.IsNullOrWhiteSpace(p.Item2)).Select(p => p.Item1).ToArray();
            if (missing.Length > 0)
                throw new ServerError($"Set {string.Join(", ", missing)} in the environment: OML_SERVER_HOST is the server's hostname (no https://), OML_SERVER_USER and OML_SERVER_PASSWORD are the login you use in Service Studio.");
            if (user!.Any(char.IsControl) || password!.Any(char.IsControl)) throw new ServerError("OML_SERVER_USER or OML_SERVER_PASSWORD holds a control character.");
            string? outFile = null;
            if (verb == "pull")
            {
                outFile = Path.GetFullPath(options["--out"]!);
                if (!outFile.EndsWith(".oml", StringComparison.OrdinalIgnoreCase)) throw new ServerError("--out must be an .oml file.");
                if (File.Exists(outFile) || Directory.Exists(outFile)) throw new ServerError($"Output already exists: {outFile}. Use another --out path, or delete that file first.");
            }
            int? wanted = null;
            if (options.GetValueOrDefault("--version") is { } v)
                wanted = int.TryParse(v, out var n) && n > 0 ? n : throw new ServerError("--version takes a version number, such as 12 (oml server versions <Module> lists them).");

            var root = Endpoint(host!);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { BaseAddress = root, Timeout = TimeSpan.FromSeconds(300) };
            var client = new Client(http);

            // 1. The server's flags, before the login. 404: no server API at all (an older platform server).
            var instead = verb == "modules" ? "List the modules in Service Studio instead (oml studio lists the open ones)." : StudioPull;
            var properties = client.HandshakeProperties(instead);
            string Property(string id) => properties.FirstOrDefault(p => string.Equals(Text(p, "id"), id, StringComparison.OrdinalIgnoreCase)) is { } p ? Text(p, "value") : "";
            var migration = Property("SoapWSRestMigration");
            var feature = verb switch { "modules" => "Modules", "versions" => "ModulesVersion", _ => "Download" };
            if (migration.Length > 0 && !migration.Split('|').Contains(feature, StringComparer.OrdinalIgnoreCase))
                warning = $"This server's flag SoapWSRestMigration={migration} has no {feature}: Service Studio uses SOAP for this here. oml tried REST anyway.";

            // 2. The password login, as Service Studio's client.
            token = client.Login(user, password, Property("IdentityService_ServiceURL").Length > 0);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // 3. The command.
            var modules = client.Modules(instead);
            if (verb == "modules")
            {
                var app = options.GetValueOrDefault("--app");
                var rows = modules.Select(Row).Where(r => app is null || string.Equals(r["application"]?.GetValue<string>(), app, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(r["applicationKey"]?.GetValue<string>(), app, StringComparison.Ordinal))
                    .OrderBy(r => r["application"]?.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r["name"]?.GetValue<string>(), StringComparer.OrdinalIgnoreCase).ToArray();
                if (app is not null && rows.Length == 0)
                    throw new ServerError($"No module of application '{app}' on this server. Run oml server modules to see the application names.");
                Emit(new JsonObject { ["host"] = host, ["count"] = rows.Length, ["modules"] = new JsonArray(rows) });
                return 0;
            }

            var found = FindModule(modules, module!);
            var key = Text(found, "key");
            var published = Int(Prop(found, "publishedVersionId"));
            var last = Embedded(found, "lastVersion");
            if (verb == "versions")
            {
                var versions = client.Versions(key)
                    .Select(x => new JsonObject
                    {
                        ["version"] = Int(Prop(x, "versionNumber")), ["versionId"] = Int(Prop(x, "versionId")),
                        ["published"] = published is not null && Int(Prop(x, "versionId")) == published,
                        ["uploadedBy"] = Text(x, "uploaderUsername"), ["uploadedDate"] = Prop(x, "uploadedDate")?.DeepClone(),
                    })
                    .OrderByDescending(x => x["version"]?.GetValue<int>() ?? 0).ToArray();
                Emit(new JsonObject
                {
                    ["module"] = Text(found, "name"), ["key"] = key, ["application"] = Text(Embedded(found, "application"), "name"),
                    ["publishedVersionId"] = published, ["count"] = versions.Length, ["versions"] = new JsonArray(versions),
                    ["next"] = $"oml server pull {Text(found, "name")} --version <version> --out <file.oml>",
                });
                return 0;
            }

            // pull: --version is the version number; the download takes the version id. No --version: the published version.
            int? number = null, versionId;
            if (wanted is not null)
            {
                var match = client.Versions(key).FirstOrDefault(x => Int(Prop(x, "versionNumber")) == wanted)
                    ?? throw new ServerError($"Module '{Text(found, "name")}' has no version {wanted} on this server. Run oml server versions {Text(found, "name")}.");
                (number, versionId) = (wanted, Int(Prop(match, "versionId")));
            }
            else if (published is > 0)
            {
                versionId = published;
                if (Int(Prop(last, "versionId")) == published) number = Int(Prop(last, "versionNumber"));
            }
            else (versionId, number) = (Int(Prop(last, "versionId")), Int(Prop(last, "versionNumber")));
            if (versionId is not > 0) throw new ServerError($"The server lists no version of module '{Text(found, "name")}' to download. Run oml server versions {Text(found, "name")}.");
            // The published version is older than the last upload: its number is only in the version list (a nice-to-have).
            if (number is null)
                try { number = Int(Prop(client.Versions(key).FirstOrDefault(x => Int(Prop(x, "versionId")) == versionId), "versionNumber")); }
                catch (ServerError) { }
            TerminalProgress.Message($"Downloading {Text(found, "name")} from {host}...");
            var bytes = client.SourceCode(key, versionId.Value, outFile!);
            TerminalProgress.Message("Downloaded.");
            Emit(new JsonObject
            {
                ["module"] = Text(found, "name"), ["key"] = key, ["application"] = Text(Embedded(found, "application"), "name"),
                ["version"] = number, ["versionId"] = versionId, ["published"] = versionId == published,
                ["file"] = outFile, ["bytes"] = bytes, ["next"] = $"oml header \"{outFile}\"",
            });
            return 0;
        }
        catch (ServerError e)
        {
            var result = new JsonObject { ["error"] = e.Message };
            if (e.NoRestRoute) result["noRestRoute"] = true;
            if (e.Next is not null) result["next"] = e.Next;
            Emit(result);
            return 2;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            Emit(new JsonObject { ["error"] = "Request to the server failed (connection, TLS or timeout). oml checks the certificate: a self-signed or untrusted one is refused. Check OML_SERVER_HOST." });
            return 2;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            Emit(new JsonObject { ["error"] = "The server sent an answer oml cannot read (not the JSON Service Studio expects). Check OML_SERVER_HOST." });
            return 2;
        }
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

    static (string verb, string? module, Dictionary<string, string?> options) Parse(string[] args)
    {
        if (args.Length == 0) throw new ServerError(Usage);
        var verb = args[0];
        var allowed = verb switch
        {
            "modules" => new[] { "--app" }, "versions" => System.Array.Empty<string>(), "pull" => new[] { "--version", "--out" },
            _ => throw new ServerError(Usage),
        };
        int i = 1;
        string? module = null;
        if (verb is "versions" or "pull")
        {
            if (i == args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--")) throw new ServerError("Name the module (name or key). " + Usage);
            module = args[i++];
        }
        var options = new Dictionary<string, string?>();
        while (i < args.Length)
        {
            var option = args[i++];
            if (!allowed.Contains(option) || options.ContainsKey(option)) throw new ServerError($"Unknown or repeated option {option}. " + Usage);
            if (i == args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--")) throw new ServerError($"{option} needs a value. " + Usage);
            options.Add(option, args[i++]);
        }
        if (verb == "pull" && !options.ContainsKey("--out")) throw new ServerError("pull needs --out <file.oml>. " + Usage);
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
