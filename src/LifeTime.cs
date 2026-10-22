using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// C# adaptation of OutSystems/outsystems-pipeline; HTTP and CLI handling changed for this host.
// Ported from OutSystems/outsystems-pipeline outsystems/lifetime/lifetime_base.py (Apache-2.0).
// Ported from OutSystems/outsystems-pipeline outsystems/lifetime/lifetime_environments.py (Apache-2.0).
// Ported from OutSystems/outsystems-pipeline outsystems/lifetime/lifetime_applications.py (Apache-2.0).
static class LifeTime
{
    internal const string Usage = "oml lt environments | teams | team-apps Team | apps [--env Name] | download App --env Name --out Folder [--extract] | tag App --version x.y.z [--description Text] [--env Development] | deploy App[,App2] --from Env --to Env [--version x.y.z|latest] [--notes Text] [--wait] | status Key | abort Key | continue Key";

    // runCore runs one Core command (for `download --extract`: `app extract`); null when Core is not available here.
    public static int Run(string[] args, Func<string[], int>? runCore = null)
    {
        string? token = Environment.GetEnvironmentVariable("OML_LIFETIME_TOKEN");
        string? deploymentKey = null;
        void Emit(object value, bool progress = false)
        {
            var text = JsonOutput.Serialize(value, progress);
            if (!string.IsNullOrEmpty(token))
            {
                // Redact decoded strings before final serialization: tokens can contain escaped characters.
                var node = JsonNode.Parse(text);
                Redact(node, token);
                text = JsonOutput.Serialize(node!, progress);
            }
            (progress ? Console.Error : Console.Out).WriteLine(text);
        }
        try
        {
            var (verb, positional, options) = Parse(args);
            var host = Environment.GetEnvironmentVariable("OML_LIFETIME_HOST");
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(token))
                throw new LtError("Set OML_LIFETIME_HOST (hostname, no scheme) and OML_LIFETIME_TOKEN in the environment.", 2);
            var root = Endpoint(host);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { BaseAddress = root, Timeout = TimeSpan.FromSeconds(100) };
            if (token.Any(char.IsControl)) throw new LtError("Invalid OML_LIFETIME_TOKEN format.", 2);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var client = new Client(http);
            if (verb == "teams") { Emit(client.List("teams")); return 0; }
            if (verb == "team-apps") { Emit(client.TeamApplications(positional!)); return 0; }
            string Opt(string name, string fallback = "") => options.GetValueOrDefault(name) ?? fallback;
            if (verb == "environments") { Emit(client.List("environments")); return 0; }
            if (verb == "download") return Download(client, root, positional!, Opt("--env"), Opt("--out"), options.ContainsKey("--extract"), runCore, Emit);
            if (verb is "status" or "abort" or "continue")
            {
                deploymentKey = positional!;
                if (verb != "status") client.Send("POST", $"deployments/{Escape(deploymentKey)}/{verb}", expected: 202);
                var status = client.Status(deploymentKey);
                Emit(new { deploymentKey, status });
                return Failed(status) ? 1 : 0;
            }

            var environments = client.List("environments");
            var applications = client.List("applications?IncludeModules=false&IncludeEnvStatus=true");
            if (verb == "apps")
            {
                var env = options.ContainsKey("--env") ? Find(environments, Opt("--env"), "environment") : null;
                var result = new List<object>();
                foreach (var app in applications)
                {
                    var states = Array(app?["AppStatusInEnvs"]);
                    if (env is not null) states = states.Where(s => Text(s, "EnvironmentKey") == Text(env, "Key")).ToArray();
                    if (env is not null && states.Length == 0) continue;
                    var running = new List<object>();
                    foreach (var state in states)
                    {
                        var key = Text(state, "BaseApplicationVersionKey");
                        var version = key.Length == 0 ? null : client.Version(app!, key);
                        running.Add(new { environmentKey = Text(state, "EnvironmentKey"),
                            environment = environments.FirstOrDefault(e => Text(e, "Key") == Text(state, "EnvironmentKey"))?["Name"]?.GetValue<string>(),
                            versionKey = key, version = version?["Version"], isModified = state?["IsModified"] });
                    }
                    result.Add(new { key = Text(app, "Key"), name = Text(app, "Name"), environments = running });
                }
                Emit(result); return 0;
            }
            if (verb == "tag")
            {
                var app = Find(applications, positional!, "application");
                var env = Find(environments, Opt("--env", "Development"), "environment");
                // Ported from OutSystems/outsystems-pipeline outsystems/lifetime/lifetime_applications.py (Apache-2.0).
                var version = client.Send("POST", $"environments/{Escape(Text(env, "Key"))}/applications/{Escape(Text(app, "Key"))}/versions",
                    new { ChangeLog = Opt("--description"), Version = Opt("--version"), MobileVersions = System.Array.Empty<object>() }, 201);
                Emit(new { application = Text(app, "Name"), environment = Text(env, "Name"), version = Opt("--version"), result = version });
                return 0;
            }

            var source = Find(environments, Opt("--from"), "environment");
            var target = Find(environments, Opt("--to"), "environment");
            if (Text(source, "Key") == Text(target, "Key")) throw new LtError("Source and target environments must differ.", 2);
            var operations = new List<object>();
            var selected = new List<object>();
            var seen = new HashSet<string>();
            foreach (var name in positional!.Split(','))
            {
                var app = Find(applications, name.Trim(), "application");
                if (!seen.Add(Text(app, "Key"))) throw new LtError("Duplicate application in deployment.", 2);
                var state = Array(app["AppStatusInEnvs"]).FirstOrDefault(s => Text(s, "EnvironmentKey") == Text(source, "Key"))
                    ?? throw new LtError("Application is not running in the source environment.", 2);
                var requested = Opt("--version", "latest");
                // 'latest' follows the pipeline: the source environment's current base tag, not the highest version number.
                var versionKey = requested == "latest" ? Text(state, "BaseApplicationVersionKey")
                    : Text(Find(client.List($"applications/{Escape(Text(app, "Key"))}/versions?MaximumVersionsToReturn=0"), requested, "version", "Version"), "Key");
                if (versionKey.Length == 0) throw new LtError("Source application has no tagged base version; tag it first.", 2);
                var version = client.Version(app, versionKey);
                // Ported from OutSystems/outsystems-pipeline outsystems/pipeline/deploy_latest_tags_to_target_env.py (Apache-2.0).
                operations.Add(new { ApplicationVersionKey = versionKey, DeploymentZoneKey = "" });
                selected.Add(new { application = Text(app, "Name"), applicationKey = Text(app, "Key"), versionKey, version = version?["Version"], isModified = state?["IsModified"] });
            }
            // Ported from OutSystems/outsystems-pipeline outsystems/lifetime/lifetime_deployments.py (Apache-2.0).
            var plan = client.Send("POST", "deployments", new { ApplicationOperations = operations,
                SourceEnvironmentKey = Text(source, "Key"), TargetEnvironmentKey = Text(target, "Key"), Notes = Opt("--notes") }, 201);
            deploymentKey = plan is JsonValue v && v.TryGetValue<string>(out var keyValue) ? keyValue : Text(plan, "Key");
            if (string.IsNullOrWhiteSpace(deploymentKey)) throw new LtError("LifeTime did not return a deployment key. Check LifeTime before retrying.", 1);
            Emit(new { deploymentKey, step = "created", applications = selected }, true);
            client.Send("POST", $"deployments/{Escape(deploymentKey)}/start", expected: 202);
            if (!options.ContainsKey("--wait")) { Emit(new { deploymentKey, started = true, applications = selected }); return 0; }

            // Ported from OutSystems/outsystems-pipeline outsystems/pipeline/start_deployment_to_target_env.py (Apache-2.0).
            // Ported from OutSystems/outsystems-pipeline outsystems/vars/pipeline_vars.py (Apache-2.0).
            var until = DateTime.UtcNow.AddHours(1);
            var messages = new HashSet<string>();
            while (true)
            {
                var status = client.Status(deploymentKey);
                foreach (var message in Array(status?["Messages"]))
                    if (messages.Add(message?.ToJsonString() ?? "null")) Emit(new { deploymentKey, message }, true);
                var state = Text(status, "DeploymentStatus").ToLowerInvariant();
                Emit(new { deploymentKey, deploymentStatus = state, info = status?["Info"] }, true);
                if (state is not ("saved" or "running" or "aborting"))
                {
                    Emit(new { deploymentKey, status, applications = selected,
                        needsContinue = state == "needs_user_intervention" });
                    return state == "finished_successful" ? 0 : 1;
                }
                if (DateTime.UtcNow >= until) throw new LtError("Deployment polling timed out; use lt status before taking further action.", 1);
                Thread.Sleep(TimeSpan.FromSeconds(20));
            }
        }
        catch (LtError e) { Emit(new { error = e.Message, deploymentKey }); return e.Code; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        { Emit(new { error = "LifeTime request failed (connection, TLS or timeout). Check lt status before retrying a mutation.", deploymentKey }); return 2; }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException)
        { Emit(new { error = "Invalid LifeTime response or configuration; check LifeTime before retrying a mutation.", deploymentKey }); return 2; }
    }

    // `lt download`: GET only. The package of the version running in one environment, from the same content endpoint
    // that `architecture --team` uses. The LifeTime API v2 docs we have do not describe a download of another tagged
    // version, so there is no --version.
    static int Download(Client client, Uri root, string appName, string envName, string outFolder, bool extract,
        Func<string[], int>? runCore, Action<object, bool> emit)
    {
        var app = Find(client.List("applications?IncludeModules=false&IncludeEnvStatus=true"), appName, "application");
        var env = Find(client.List("environments"), envName, "environment");
        if (!Array(app["AppStatusInEnvs"]).Any(s => Text(s, "EnvironmentKey") == Text(env, "Key")))
            throw new LtError($"Application '{Text(app, "Name")}' is not running in environment '{Text(env, "Name")}'.", 2);
        var name = Text(app, "Name");
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            name = "application-" + Text(app, "Key");
        var folder = Path.GetFullPath(outFolder);
        var file = Path.Combine(folder, name + ".oap");
        if (File.Exists(file)) throw new LtError($"Output already exists: {file}. Use another --out folder, or delete that file first.", 2);
        var extractTo = Path.Combine(folder, name);
        if (extract && (File.Exists(extractTo) || Directory.Exists(extractTo)))
            throw new LtError($"Output already exists: {extractTo}. Use another --out folder, or delete that folder first.", 2);
        var url = SafePackageUrl(client.Send("GET", $"environments/{Escape(Text(env, "Key"))}/applications/{Escape(Text(app, "Key"))}/content"), root);
        Directory.CreateDirectory(folder);
        client.Download(url, file);
        var result = new JsonObject
        {
            ["application"] = Text(app, "Name"), ["environment"] = Text(env, "Name"), ["file"] = file, ["bytes"] = new FileInfo(file).Length,
        };
        if (!extract) { emit(result, false); return 0; }
        // `app extract` runs in Core (Service Studio's model). Its JSON is captured and nested here.
        if (runCore is null) throw new LtError("--extract needs Service Studio's model, which is not loaded here. Run: oml app extract " + file + " <folder>", 2);
        var (previous, captured) = (Console.Out, new StringWriter());
        int code;
        Console.SetOut(captured);
        try { code = runCore(["app", "extract", file, extractTo]); }
        finally { Console.SetOut(previous); }
        JsonNode? extracted;
        try { extracted = JsonNode.Parse(captured.ToString().Trim() is { Length: > 0 } t ? t : "null"); }
        catch (JsonException) { extracted = JsonValue.Create(captured.ToString().Trim()); }
        result["folder"] = extractTo;
        result["extracted"] = extracted is JsonObject o && o["extracted"] is JsonArray files ? files.DeepClone() : null;
        if (code != 0)
            result["error"] = "The package was downloaded but not extracted: " + (extracted is JsonObject e && (e["error"] ?? (e["problems"] as JsonArray)?.FirstOrDefault()) is { } m
                ? m.ToString() : "oml app extract failed") + ". The .oap file is kept.";
        emit(result, false);
        return code == 0 ? 0 : 2;
    }

    // Runs before loading Core. Only this invocation owns the temporary package folder.
    public static int Architecture(string[] args, Func<string[], int> runCore)
    {
        string? folder = null;
        var token = Environment.GetEnvironmentVariable("OML_LIFETIME_TOKEN");
        int Unavailable(string reason)
        {
            if (!string.IsNullOrEmpty(token)) reason = reason.Replace(token, "[REDACTED]", StringComparison.Ordinal);
            Console.Error.WriteLine($"LifeTime is not available ({reason}). Export the team's applications as .oap files into a folder and run: oml {args.FirstOrDefault(a => a != "--pretty")} --in <folder>");
            return 2;
        }
        try
        {
            string[] rewritten;
            try
            {
                var index = System.Array.IndexOf(args, "--team");
                if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1])
                    || args[index + 1].StartsWith("--") || args.Skip(index + 1).Contains("--team") || args.Contains("--team-label"))
                    throw new LtError("--team requires one team name and cannot be repeated", 2);
                var team = args[index + 1];
                var host = Environment.GetEnvironmentVariable("OML_LIFETIME_HOST");
                if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(token))
                    throw new LtError("set OML_LIFETIME_HOST and OML_LIFETIME_TOKEN", 2);
                var root = Endpoint(host);
                if (token.Any(char.IsControl)) throw new LtError("invalid OML_LIFETIME_TOKEN format", 2);
                using var handler = new HttpClientHandler { AllowAutoRedirect = false };
                using var http = new HttpClient(handler) { BaseAddress = root, Timeout = TimeSpan.FromSeconds(100) };
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var client = new Client(http);
                var apps = client.TeamApplications(team);
                if (apps.Length == 0) throw new LtError("the team has no applications available to this account", 2);
                var environments = client.List("environments");
                folder = Directory.CreateTempSubdirectory("oml-architecture-").FullName;
                int number = 0;
                foreach (var app in apps)
                {
                    var key = Text(app, "Key");
                    if (key.Length == 0) throw new LtError("invalid application key", 2);
                    // Includes the module list; the OAP manifest preserves application membership for Core.
                    var details = client.Send("GET", $"applications/{Escape(key)}?IncludeModules=true&IncludeEnvStatus=true");
                    if (Property(details, "Modules") is not JsonArray)
                        throw new LtError("LifeTime did not return application modules", 2);
                    var states = Array(Property(details, "AppStatusInEnvs"));
                    var env = environments.Where(e => states.Any(s => Text(s, "EnvironmentKey") == Text(e, "Key")))
                        .OrderByDescending(e => Text(e, "EnvironmentType").Equals("Development", StringComparison.OrdinalIgnoreCase)
                            || Text(e, "Name").Equals("Development", StringComparison.OrdinalIgnoreCase))
                        .ThenBy(e => Property(e, "Order")?.GetValue<int>() ?? int.MaxValue).FirstOrDefault()
                        ?? throw new LtError("no running application environment is available", 2);
                    var url = SafePackageUrl(client.Send("GET", $"environments/{Escape(Text(env, "Key"))}/applications/{Escape(key)}/content"), root);
                    client.Download(url, Path.Combine(folder, $"application-{++number}.oap"));
                }
                rewritten = args.Take(index).Concat(new[] { "--in", folder, "--team-label", team }).Concat(args.Skip(index + 2)).ToArray();
            }
            catch (LtError e) { return Unavailable(e.Message.TrimEnd('.')); }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
            { return Unavailable("connection, TLS, timeout or temporary file failure"); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException)
            { return Unavailable("invalid LifeTime response or configuration"); }

            if (Environment.GetEnvironmentVariable("OML_DEBUG_ARGS") == "1")
            {
                var debug = JsonSerializer.SerializeToNode(new { args = rewritten,
                    files = Directory.GetFiles(folder!).Select(f => new { name = Path.GetFileName(f), bytes = new FileInfo(f).Length }) });
                if (!string.IsNullOrEmpty(token)) Redact(debug, token);
                Console.WriteLine(JsonOutput.Serialize(debug!));
                return 0;
            }
            return runCore(rewritten);
        }
        finally
        {
            if (folder is not null)
            {
                var owned = Path.GetFullPath(folder);
                var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.Equals(Path.GetDirectoryName(owned), temp, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(owned).StartsWith("oml-architecture-", StringComparison.Ordinal))
                    throw new IOException("Refused to remove an unexpected temporary package directory.");
                Directory.Delete(owned, recursive: true);
            }
        }
    }

    // The content endpoint answers with a download address. Follow it only on the LifeTime host itself (same scheme and
    // port), without a user name or a fragment: the bearer token goes with the request.
    static Uri SafePackageUrl(JsonNode? link, Uri root)
    {
        if (!Uri.TryCreate(Text(link, "url"), UriKind.Absolute, out var url)
            || url.Scheme != root.Scheme || url.Authority != root.Authority || url.UserInfo.Length != 0 || url.Fragment.Length != 0)
            throw new LtError("LifeTime offered no safe package download URL (another host, credentials or a fragment).", 2);
        return url;
    }

    static Uri Endpoint(string host)
    {
        if (host.Any(char.IsWhiteSpace) || host.IndexOfAny(new[] { '/', '\\', '@', '?', '#' }) >= 0
            || !Uri.TryCreate("https://" + host + "/", UriKind.Absolute, out var uri)
            || !(uri.Authority.Equals(host, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(":443", StringComparison.Ordinal) && uri.Authority.Equals(host[..^4], StringComparison.OrdinalIgnoreCase)))
            throw new LtError("OML_LIFETIME_HOST must be a hostname with optional port, without scheme, path or credentials.", 2);
        var insecure = Environment.GetEnvironmentVariable("OML_LIFETIME_INSECURE_LOCALHOST") == "1";
        if (insecure && uri.Host is not ("127.0.0.1" or "localhost"))
            throw new LtError("OML_LIFETIME_INSECURE_LOCALHOST is permitted only for 127.0.0.1 or localhost.", 2);
        return new Uri($"{(insecure ? "http" : "https")}://{host}/lifetimeapi/rest/v2/");
    }

    static (string verb, string? positional, Dictionary<string, string?> options) Parse(string[] args)
    {
        if (args.Length == 0) throw new LtError(Usage, 2);
        var verb = args[0];
        var allowed = verb switch {
            "environments" or "teams" or "team-apps" or "status" or "abort" or "continue" => System.Array.Empty<string>(),
            "apps" => new[] { "--env" }, "download" => new[] { "--env", "--out", "--extract" }, "tag" => new[] { "--version", "--description", "--env" },
            "deploy" => new[] { "--from", "--to", "--version", "--notes", "--wait" },
            _ => throw new LtError(Usage, 2) };
        int i = 1;
        string? positional = null;
        if (verb is "tag" or "deploy" or "download" or "team-apps" or "status" or "abort" or "continue")
        {
            if (i == args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--")) throw new LtError(Usage, 2);
            positional = args[i++];
        }
        var options = new Dictionary<string, string?>();
        while (i < args.Length)
        {
            var option = args[i++];
            if (!allowed.Contains(option) || options.ContainsKey(option)) throw new LtError("Unknown or duplicate option. " + Usage, 2);
            string? value = null;
            if (option is not ("--wait" or "--extract"))
            {
                if (i == args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--")) throw new LtError("Option requires a value. " + Usage, 2);
                value = args[i++];
            }
            options.Add(option, value);
        }
        if (verb == "tag" && !options.ContainsKey("--version") || verb == "download" && (!options.ContainsKey("--env") || !options.ContainsKey("--out")) || verb == "deploy" && (!options.ContainsKey("--from") || !options.ContainsKey("--to")))
            throw new LtError(Usage, 2);
        return (verb, positional, options);
    }

    static string Escape(string value) => Uri.EscapeDataString(value);
    static JsonNode? Property(JsonNode? node, string property) => node is JsonObject obj
        ? obj.FirstOrDefault(p => p.Key.Equals(property, StringComparison.OrdinalIgnoreCase)).Value : null;
    static string Text(JsonNode? node, string property) => Property(node, property)?.GetValue<string>() ?? "";
    static JsonNode?[] Array(JsonNode? node) => node is JsonArray array ? array.ToArray() : System.Array.Empty<JsonNode?>();
    static JsonNode Find(JsonNode?[] nodes, string name, string kind, string field = "Name")
    {
        var matches = nodes.Where(n => string.Equals(Text(n, field), name, StringComparison.OrdinalIgnoreCase)
            || field == "Name" && Text(n, "Key") == name).ToArray();
        if (matches.Length != 1) throw new LtError($"Expected one matching {kind}; found {matches.Length}. List available names and keys first.", 2);
        return matches[0]!;
    }
    static bool Failed(JsonNode? status) => Text(status, "DeploymentStatus").ToLowerInvariant() is "aborted" or "finished_with_errors";

    static void Redact(JsonNode? node, string token)
    {
        if (node is JsonObject obj)
            foreach (var pair in obj.ToArray())
            {
                if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text)) obj[pair.Key] = text.Replace(token, "[REDACTED]", StringComparison.Ordinal);
                else Redact(pair.Value, token);
                if (pair.Key.Contains(token, StringComparison.Ordinal)) { var child = obj[pair.Key]; obj.Remove(pair.Key); obj[pair.Key.Replace(token, "[REDACTED]", StringComparison.Ordinal)] = child; }
            }
        else if (node is JsonArray array)
            for (int i = 0; i < array.Count; i++)
                if (array[i] is JsonValue value && value.TryGetValue<string>(out var text)) array[i] = text.Replace(token, "[REDACTED]", StringComparison.Ordinal);
                else Redact(array[i], token);
    }

    sealed class Client(HttpClient http)
    {
        public JsonNode?[] TeamApplications(string name)
        {
            var team = Find(List("teams"), name, "team");
            var details = Send("GET", $"teams/{Escape(Text(team, "Key"))}?IncludeUsers=false&IncludeApplications=true");
            if (Property(details, "applications") is not JsonArray)
                throw new LtError("LifeTime did not return team applications.", 2);
            return Array(Property(details, "applications"));
        }
        public void Download(Uri url, string path)
        {
            using var timeout = new CancellationTokenSource(http.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            using var response = http.Send(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
                throw new LtError($"package download refused (HTTP {(int)response.StatusCode})", 2);
            using var input = response.Content.ReadAsStream();
            // CreateNew: an existing file is never touched. Only the file made here is deleted when the copy fails.
            var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            try
            {
                input.CopyToAsync(output, timeout.Token).GetAwaiter().GetResult();
                if (output.Length == 0) throw new LtError("package download was empty", 2);
                output.Dispose();
            }
            catch
            {
                output.Dispose();
                File.Delete(path);
                throw;
            }
        }
        public JsonNode? Send(string method, string path, object? payload = null, int expected = 200)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (payload is not null) request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = http.Send(request);
            var code = (int)response.StatusCode;
            if (code == 204 && method == "GET") return new JsonArray();
            if (code != expected)
                throw new LtError(code switch {
                    401 => "LifeTime authentication failed (HTTP 401). Check OML_LIFETIME_TOKEN.",
                    403 => "LifeTime permission denied (HTTP 403). Check service account permissions.",
                    404 => "LifeTime resource not found (HTTP 404). Check the host, API v2 and resource keys.",
                    400 => "LifeTime refused the request (HTTP 400). Check the application/version and deployment state in LifeTime.",
                    >= 300 and < 400 => "LifeTime redirect refused. Configure the final HTTPS host directly.",
                    _ => $"LifeTime request failed (HTTP {code}). Check LifeTime before retrying a mutation." }, code is 400 or 409 ? 1 : 2);
            var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return JsonNode.Parse(text); }
            catch (JsonException) when (method == "POST") { return JsonValue.Create(text.Trim()); }
        }
        public JsonNode?[] List(string path)
        {
            var node = Send("GET", path);
            if (node is not JsonArray) throw new LtError("LifeTime returned an invalid list response.", 2);
            return Array(node);
        }
        public JsonNode? Version(JsonNode app, string key) => Send("GET", $"applications/{Escape(Text(app, "Key"))}/versions/{Escape(key)}?IncludeModules=false&IncludeEnvStatus=false");
        public JsonNode Status(string key)
        {
            var status = Send("GET", $"deployments/{Escape(key)}/status");
            if (status is not JsonObject || Text(status, "DeploymentStatus").Length == 0) throw new LtError("LifeTime returned an invalid deployment status.", 2);
            return status;
        }
    }
    sealed class LtError(string message, int code) : Exception(message) { public int Code { get; } = code; }
}
