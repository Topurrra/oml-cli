using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OutSystems.Model;
using OutSystems.Model.Applications;
using OutSystems.Model.Data;
using OutSystems.Model.Types;
using ServiceStudio.Plugin.RESTService;
using ServiceStudio.Plugin.RESTService.Enumerations;

namespace OmlCli;

// Read-only: never saves the source module. JSON nodes avoid the compact report serializer's pruning.
static class OpenApi
{
    const string Fill = "<FILL: ";
    internal static string Placeholder(string what) => Fill + what + ">";
    internal static JsonObject Obj(params (string key, object? value)[] fields)
    {
        var result = new JsonObject();
        foreach (var (key, value) in fields) result[key] = value is JsonNode node ? node : JsonSerializer.SerializeToNode(value);
        return result;
    }
    internal static JsonArray Array(IEnumerable<JsonNode> nodes) => new(nodes.ToArray());
    static string Description(string? value) => string.IsNullOrWhiteSpace(value) ? Placeholder("description") : value;
    static string Pointer(string value) => value.Replace("~", "~0").Replace("/", "~1");
    internal static JsonObject Ref(string category, string name) => Obj(("$ref", "#/components/" + category + "/" + Pointer(name)));

    public static int Run(List<string> args)
    {
        string? api = null, output = null;
        var servers = new List<string>();
        for (var i = 2; i < args.Count; i++)
        {
            var option = args[i];
            if (option is not ("--api" or "--out" or "--server") || i + 1 >= args.Count || args[i + 1].StartsWith("--"))
                throw new CliError("openapi accepts --api <name>, --out <file.json|folder>, and repeated --server <url>.", 2);
            var value = args[++i];
            if (option == "--server") servers.Add(value);
            else if (option == "--api" && api is null) api = value;
            else if (option == "--out" && output is null) output = value;
            else throw new CliError($"Duplicate option {option}.", 2);
        }
        using var module = Model.Load(Path.GetFullPath(args[1]));
        return Cli.Emit(Write(module, api, output, servers));
    }

    internal sealed record Written(string file, int operationCount, int schemaCount, List<string> placeholders);
    internal sealed record Report(List<Written> files);
    internal static Report Write(IESpace module, string? name, string? output, IReadOnlyList<string> servers)
    {
        var apis = module.Integrations.OfType<IRestService>().Where(a => ReferenceEquals(a.GetESpace(), module)).ToList();
        if (name is not null)
        {
            var selected = apis.Where(a => a.Name == name).ToList();
            if (selected.Count == 0) throw new CliError($"Unknown REST API '{name}'. Available APIs: {string.Join(", ", apis.Select(a => a.Name).DefaultIfEmpty("(none)"))}.", 2);
            apis = selected;
        }
        var fileOutput = output is not null && !Directory.Exists(output) && Path.GetExtension(output).Equals(".json", StringComparison.OrdinalIgnoreCase);
        if (fileOutput && apis.Count != 1) throw new CliError("--out <file.json> requires exactly one API; use a folder or --api.", 2);
        var pending = new List<(string path, JsonObject document, int operations)>();
        foreach (var api in apis)
        {
            var filename = ((IModule)module).Name + "." + api.Name + ".openapi.json";
            if (filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new CliError("Module/API name cannot be used as an output filename.", 2);
            var path = Path.GetFullPath(fileOutput ? output! : Path.Combine(output ?? ".", filename));
            if (File.Exists(path)) throw new CliError($"Output already exists: {path}. Use a new output path, or delete that file first.", 2);
            var document = new Generator(module).Generate(api, servers);
            pending.Add((path, document, api.Actions.Count()));
        }
        var report = new Report(new());
        foreach (var (path, document, operations) in pending)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // CreateNew also refuses a file created after preflight. UTF-8 without BOM, platform-independent LF.
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(document.ToJsonString(new JsonSerializerOptions { WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace("\r\n", "\n") + "\n");
            }
            catch (IOException) when (File.Exists(path))
            { throw new CliError($"Output already exists: {path}. Use a new output path, or delete that file first.", 2); }
            report.files.Add(new(path, operations, document["components"]!["schemas"]!.AsObject().Count, Placeholders(document)));
        }
        return report;
    }

    // In addition to searchable strings, every generated example leaf is a placeholder, including 0/true/dates.
    internal static List<string> Placeholders(JsonNode document)
    {
        var result = new List<string>();
        var examples = new HashSet<JsonNode>();
        void ContentExamples(JsonNode? content)
        {
            if (content is JsonObject mediaTypes)
                foreach (var media in mediaTypes)
                    if (media.Value?["example"] is { } example) examples.Add(example);
        }
        // Locate Media Type examples by their OpenAPI role, rather than by a field's name.
        foreach (var path in document["paths"]!.AsObject())
            foreach (var operation in path.Value!.AsObject())
            {
                ContentExamples(operation.Value?["requestBody"]?["content"]);
                foreach (var response in operation.Value!["responses"]!.AsObject()) ContentExamples(response.Value?["content"]);
            }
        void Walk(JsonNode? node, string path, bool example)
        {
            example |= node is not null && examples.Contains(node);
            if (node is JsonObject obj)
                foreach (var p in obj) Walk(p.Value, path.Length == 0 ? p.Key : path + "." + p.Key, example);
            else if (node is JsonArray array)
                for (var i = 0; i < array.Count; i++) Walk(array[i], path + "[" + i + "]", example);
            else if (example || node is JsonValue v && v.TryGetValue<string>(out var s) && s.Contains(Fill, StringComparison.Ordinal)) result.Add(path);
        }
        Walk(document, "", false);
        return result;
    }

    sealed class Generator(IESpace module)
    {
        readonly JsonObject schemas = new();
        readonly Dictionary<ITypeSignature, string> names = new();
        readonly HashSet<ITypeSignature> records = new();
        void Discover(ITypeSignature type)
        {
            if (type is IListType list) Discover(list.ElementType);
            else if (type is IRecordTypeSignature record && records.Add(type))
                foreach (var attr in record.Attributes) Discover(attr.DataType);
        }
        string TypeName(ITypeSignature type) => type switch
        { IEntitySignature e => e.Name, IStructureSignature s => s.Name, _ => Model.NameOf(type) ?? "Record" };
        string Owner(ITypeSignature type) => Refs.All(module).FirstOrDefault(p => ReferenceEquals(p.element, type)).owner ?? ((IModule)module).Name;

        public JsonObject Generate(IRestService api, IReadOnlyList<string> servers)
        {
            foreach (var action in api.Actions)
                foreach (var p in action.InputParameters.Cast<IParameterSignature>().Concat(action.OutputParameters)) Discover(p.DataType);
            var used = new HashSet<string>(StringComparer.Ordinal) { "OutSystemsError" };
            foreach (var type in records.OrderBy(TypeName, StringComparer.Ordinal).ThenBy(Owner, StringComparer.Ordinal))
            {
                var name = TypeName(type);
                if (name == "OutSystemsError" || records.Count(t => TypeName(t) == name) > 1) name = Owner(type) + "." + name;
                var candidate = name;
                for (var suffix = 2; !used.Add(candidate); suffix++) candidate = name + "." + suffix;
                names[type] = candidate;
            }
            foreach (var type in records) schemas[names[type]] = Record((IRecordTypeSignature)type);
            schemas["OutSystemsError"] = Obj(("type", "object"), ("properties", Obj(
                ("Errors", Obj(("type", "array"), ("items", Obj(("type", "string"))))),
                ("StatusCode", Obj(("type", "integer"), ("format", "int32"))))));
            var responses = new JsonObject();
            foreach (var (code, description) in new[] { ("400", "Bad Request"), ("401", "Unauthorized"), ("403", "Forbidden"), ("404", "Not Found"), ("500", "Internal Server Error") })
                if (code != "401" || api.Authentication != Authentication.None)
                    responses[code] = Obj(("description", description), ("content", Obj(("application/json", Obj(("schema", Ref("schemas", "OutSystemsError")))))));
            var paths = new JsonObject();
            foreach (var action in api.Actions)
            {
                var raw = action.URLPath ?? "";
                var path = raw.Split('?')[0];
                if (string.IsNullOrEmpty(path)) path = "/" + action.Name;
                if (!path.StartsWith('/')) path = "/" + path;
                var verb = action.HTTPMethod.ToString().ToLowerInvariant();
                if (verb is not ("get" or "post" or "put" or "delete" or "patch" or "head" or "options" or "trace"))
                    throw new CliError($"Unsupported HTTP verb '{verb}' for {api.Name}.{action.Name}.");
                var op = Obj(("operationId", action.Name), ("summary", Description(action.Description)),
                    ("description", Description(action.Description)), ("tags", new[] { api.Name }));
                var parameters = new JsonArray();
                var bodies = action.InputParameters.Where(p => p.ReceiveIn == ReceiveIn.Body).ToList();
                foreach (var p in action.InputParameters.Where(p => p.ReceiveIn != ReceiveIn.Body))
                {
                    var wire = string.IsNullOrEmpty(p.ReceiveAs) ? p.Name : p.ReceiveAs;
                    var location = p.ReceiveIn == ReceiveIn.Header ? "header" : path.Contains("{" + wire + "}", StringComparison.Ordinal) ? "path" : "query";
                    // Some modules keep the internal name in URLPath while ReceiveAs supplies a wire alias.
                    if (location == "query" && path.Contains("{" + p.Name + "}", StringComparison.Ordinal))
                    { path = path.Replace("{" + p.Name + "}", "{" + wire + "}", StringComparison.Ordinal); location = "path"; }
                    parameters.Add(Obj(("name", wire), ("in", location), ("required", location == "path" || p.IsMandatory),
                        ("description", Description(p.Description)), ("schema", Schema(p.DataType))));
                }
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(path, @"\{([^{}]+)\}"))
                    if (!parameters.Any(p => (string?)p!["in"] == "path" && (string?)p["name"] == match.Groups[1].Value))
                        throw new CliError($"{api.Name}.{action.Name}: path token '{match.Value}' has no URL input.");
                if (parameters.Count > 0) op["parameters"] = parameters;
                if (bodies.Count > 0)
                    op["requestBody"] = Obj(("required", bodies.Any(p => p.IsMandatory)),
                        ("description", Description(bodies.Count == 1 ? bodies[0].Description : null)),
                        ("content", Content(Body(bodies), Media(bodies.Select(p => p.DataType)))));
                var outputs = action.OutputParameters.Where(p => p.SendIn == SendIn.Body).ToList();
                // O11 exposed REST uses 200 by default, including methods without a response body.
                // Runtime SetStatusCode / OnResponse callbacks can override this; they are not signature metadata.
                var success = Obj(("description", "Success"));
                if (outputs.Count > 0) success["content"] = Content(Body(outputs), Media(outputs.Select(p => p.DataType)));
                var headers = new JsonObject();
                foreach (var p in action.OutputParameters.Where(p => p.SendIn == SendIn.Header))
                    headers[string.IsNullOrEmpty(p.SendAs) ? p.Name : p.SendAs] = Obj(("description", Description(p.Description)), ("schema", Schema(p.DataType)));
                if (headers.Count > 0) success["headers"] = headers;
                var operationResponses = Obj(("200", success));
                foreach (var code in new[] { "400", "401", "403", "404", "500" })
                    if (code != "401" || api.Authentication != Authentication.None) operationResponses[code] = Ref("responses", code);
                op["responses"] = operationResponses;
                if (paths[path] is null) paths[path] = new JsonObject();
                if (paths[path]![verb] is not null) throw new CliError($"Duplicate REST operation {verb.ToUpperInvariant()} {path} in {api.Name}.");
                paths[path]![verb] = op;
            }
            // O11's default BaseURL is /rest/API, relative to the module deployment root.
            var basePath = api.BaseURL;
            if (string.IsNullOrWhiteSpace(basePath)) basePath = "/" + ((IModule)module).Name + "/rest/" + api.Name;
            else if (basePath.StartsWith("/rest/", StringComparison.OrdinalIgnoreCase)) basePath = "/" + ((IModule)module).Name + basePath;
            if (Uri.TryCreate(basePath, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") basePath = uri.AbsolutePath;
            if (!basePath.StartsWith('/')) basePath = "/" + basePath;
            var serverNodes = servers.Count > 0 ? Array(servers.Select(s => Obj(("url", s))))
                : new JsonArray(Obj(("url", "https://<FILL: hostname>" + basePath), ("description", Placeholder("environment name"))));
            var info = Obj(("title", api.Name), ("description", Description(api.Description)), ("version", Placeholder("version")));
            info["contact"] = Obj(("name", Placeholder("contact name")), ("email", Placeholder("contact email")), ("url", Placeholder("contact URL")));
            info["license"] = Obj(("name", Placeholder("license name")), ("url", Placeholder("license URL")));
            info["termsOfService"] = Placeholder("terms of service URL");
            var document = Obj(("openapi", "3.0.3"), ("info", info), ("servers", serverNodes), ("paths", paths),
                ("components", Obj(("schemas", schemas), ("responses", responses))),
                ("tags", new JsonArray(Obj(("name", api.Name), ("description", Placeholder("tag description"))))),
                ("externalDocs", Obj(("url", Placeholder("external documentation URL")))));
            if (api.Authentication == Authentication.Basic)
            {
                document["components"]!["securitySchemes"] = Obj(("basicAuth", Obj(("type", "http"), ("scheme", "basic"))));
                document["security"] = new JsonArray(Obj(("basicAuth", new JsonArray())));
            }
            else
            {
                document["x-oml-authentication"] = api.Authentication.ToString();
                if (api.Authentication == Authentication.Custom)
                {
                    document["components"]!["securitySchemes"] = Obj(("customAuth", Obj(("type", "apiKey"), ("name", Placeholder("header name")), ("in", "header"))));
                    document["security"] = new JsonArray(Obj(("customAuth", new JsonArray())));
                }
            }
            return document;
        }

        // Exposed REST has no RequestFormat/ResponseFormat property. Text uses its plain text wire representation.
        string Media(IEnumerable<ITypeSignature> types) => types.Count() == 1 && ReferenceEquals(types.First(), module.TextType) ? "text/plain" : "application/json";
        JsonObject Body<T>(IReadOnlyList<T> parameters) where T : IParameterSignature
        {
            if (parameters.Count == 1) return Schema(parameters[0].DataType);
            var properties = new JsonObject();
            foreach (var p in parameters) properties[p.Name] = Schema(p.DataType);
            return Obj(("type", "object"), ("properties", properties));
        }
        JsonObject Content(JsonObject schema, string media) => Obj((media, Obj(("schema", schema), ("example", Example(schema, new HashSet<string>())))));
        JsonObject Record(IRecordTypeSignature type)
        {
            var properties = new JsonObject();
            var required = new List<string>();
            foreach (var attr in type.Attributes)
            {
                var schema = Schema(attr.DataType);
                var description = attr.Description;
                if (!string.IsNullOrEmpty(description)) schema = Annotate(schema, "description",
                    JsonValue.Create(schema["description"] is { } typeDescription ? description + "\n\n" + typeDescription.GetValue<string>() : description)!);
                if (attr.Length is > 0 && (string?)schema["type"] == "string") schema["maxLength"] = attr.Length.Value;
                if (Literal(attr.DefaultValue?.Text, schema) is { } literal) schema = Annotate(schema, "default", literal);
                properties[attr.Name] = schema;
                if (attr.IsMandatory) required.Add(attr.Name);
            }
            var result = Obj(("type", "object"), ("properties", properties));
            if (required.Count > 0) result["required"] = JsonSerializer.SerializeToNode(required);
            var desc = type is IEntitySignature e ? e.Description : type is IStructureSignature s ? s.Description : null;
            if (!string.IsNullOrEmpty(desc)) result["description"] = desc;
            return result;
        }
        static JsonObject Annotate(JsonObject schema, string key, JsonNode value)
        {
            if (schema.ContainsKey("$ref")) schema = Obj(("allOf", new JsonArray(schema)));
            schema[key] = value;
            return schema;
        }
        static JsonNode? Literal(string? expression, JsonObject schema)
        {
            if (string.IsNullOrWhiteSpace(expression)) return null;
            var text = expression.Trim();
            if ((string?)schema["type"] == "string" && text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            {
                var inner = text[1..^1];
                if (!inner.Replace("\"\"", "").Contains('"')) return JsonValue.Create(inner.Replace("\"\"", "\""));
            }
            if ((string?)schema["type"] == "boolean" && bool.TryParse(text, out var b)) return JsonValue.Create(b);
            if ((string?)schema["type"] == "integer" && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return JsonValue.Create(i);
            if ((string?)schema["type"] == "number" && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
            return null;
        }
        JsonObject Schema(ITypeSignature type)
        {
            if (type is IListType list) return Obj(("type", "array"), ("items", Schema(list.ElementType)));
            if (type is IIdentifierType id) return Obj(("type", "integer"), ("format", "int64"), ("description", "Identifier of " + (id.Entity?.Name ?? Model.Describe(module, type))));
            if (names.TryGetValue(type, out var name)) return Ref("schemas", name);
            var label = Model.Describe(module, type);
            return label switch
            {
                "Text" or "Phone Number" or "Text Identifier" => Obj(("type", "string")),
                "Email" => Obj(("type", "string"), ("format", "email")),
                "Integer" => Obj(("type", "integer"), ("format", "int32")),
                "Long Integer" or "Integer Identifier" or "Long Integer Identifier" => Obj(("type", "integer"), ("format", "int64")),
                "Decimal" or "Currency" => Obj(("type", "number"), ("format", "double")),
                "Boolean" => Obj(("type", "boolean")),
                "Date" => Obj(("type", "string"), ("format", "date")),
                "Date Time" => Obj(("type", "string"), ("format", "date-time")),
                "Time" => Obj(("type", "string"), ("pattern", "^([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]$")),
                "Binary Data" => Obj(("type", "string"), ("format", "byte")),
                _ => Obj(("description", "OutSystems type: " + label)),
            };
        }
        JsonNode Example(JsonObject schema, HashSet<string> seen)
        {
            if (schema["$ref"] is { } reference)
            {
                var key = reference.GetValue<string>().Split('/').Last().Replace("~1", "/").Replace("~0", "~");
                if (!seen.Add(key)) return JsonValue.Create(Placeholder("recursive example"))!;
                var value = Example(schemas[key]!.AsObject(), seen);
                seen.Remove(key);
                return value;
            }
            if (schema["allOf"] is JsonArray all) return Example(all[0]!.AsObject(), seen);
            if ((string?)schema["type"] == "object")
            {
                var value = new JsonObject();
                foreach (var p in schema["properties"]!.AsObject()) value[p.Key] = Example(p.Value!.AsObject(), seen);
                return value;
            }
            if ((string?)schema["type"] == "array") return new JsonArray(Example(schema["items"]!.AsObject(), seen));
            return (string?)schema["type"] switch
            {
                "integer" or "number" => JsonValue.Create(0)!, "boolean" => JsonValue.Create(true)!,
                _ => JsonValue.Create((string?)schema["format"] switch { "date" => "2026-01-01", "date-time" => "2026-01-01T00:00:00Z", _ => schema.ContainsKey("pattern") ? "00:00:00" : Placeholder("example") })!,
            };
        }
    }
}
