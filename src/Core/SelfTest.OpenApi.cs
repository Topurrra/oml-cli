using System.Text.Json;
using System.Text.Json.Nodes;
using OutSystems.Model;
using OutSystems.Model.Data;
using OutSystems.Model.Types;
using ServiceStudio.Plugin.RESTService;
using ServiceStudio.Plugin.RESTService.Enumerations;

namespace OmlCli;

partial class SelfTest
{
    static void OpenApiTest(string blank, string dir, List<string> failures)
    {
        void Check(bool condition, string detail) { if (!condition) failures.Add("OpenAPI: " + detail); }
        var spec = Path.Combine(dir, "openapi-spec.json");
        var staged = Path.Combine(dir, "openapi.oml");
        File.WriteAllText(spec, """
            {
              "entities": [{ "name": "ApiOrder", "attributes": [{ "name": "Total", "type": "Currency" }] }],
              "structures": [
                { "name": "ApiItem", "attributes": [
                  { "name": "Label", "length": 42, "mandatory": true, "default": "\"hello\"" },
                  { "name": "example", "type": "Integer" },
                  { "name": "EmailAddress", "type": "Email" }, { "name": "Phone", "type": "Phone Number" },
                  { "name": "Count", "type": "Integer" }, { "name": "BigCount", "type": "Long Integer" },
                  { "name": "Amount", "type": "Decimal" }, { "name": "Active", "type": "Boolean" },
                  { "name": "Day", "type": "Date" }, { "name": "Clock", "type": "Time" },
                  { "name": "Moment", "type": "Date Time" }, { "name": "Bytes", "type": "Binary Data" } ] },
                { "name": "ApiRequest", "attributes": [{ "name": "Items", "type": "List of ApiItem" }] }
              ],
              "restServices": [
                { "name": "Documented", "actions": [
                  { "name": "Create", "method": "POST", "url": "/orders/{Id}", "inputs": [
                    { "name": "Id", "type": "Long Integer", "location": "url" },
                    { "name": "Limit", "type": "Integer", "location": "query", "mandatory": true },
                    { "name": "Token", "location": "header", "headerName": "X-Token" },
                    { "name": "Body", "type": "ApiRequest", "location": "body", "mandatory": true } ],
                    "outputs": [{ "name": "Order", "type": "ApiOrder" }, { "name": "Trace", "location": "header", "headerName": "X-Trace" }] },
                  { "name": "Ping" }
                ] },
                { "name": "CustomApi", "actions": [{ "name": "Echo", "method": "POST", "inputs": [{ "name": "Body", "location": "body" }], "outputs": [{ "name": "Response" }] }] },
                { "name": "PublicApi", "actions": [{ "name": "Ping" }] }
              ]
            }
            """);
        var (code, report) = Apply.Execute(blank, spec, staged);
        if (code != 0) { failures.Add("OpenAPI fixture: " + JsonSerializer.Serialize(report)); return; }
        using var module = Model.Load(staged);
        var basic = module.Integrations.OfType<IRestService>().Single(a => a.Name == "Documented");
        basic.Authentication = Authentication.Basic;
        basic.Description = "Order API";
        basic.Actions.Single(a => a.Name == "Create").Description = "Create an order";
        var custom = module.Integrations.OfType<IRestService>().Single(a => a.Name == "CustomApi");
        custom.Authentication = Authentication.Custom;
        custom.BaseURL = "/custom/base";
        // Direct typed model settings exercise metadata absent from the apply REST spec.
        var result = OpenApi.Write(module, null, Path.Combine(dir, "openapi-docs"), []);
        Check(result.files.Count == 3, "one file per API");
        foreach (var file in result.files)
        {
            var text = File.ReadAllText(file.file);
            using var json = JsonDocument.Parse(text);
            var root = JsonNode.Parse(text)!.AsObject();
            Check((string?)root["openapi"] == "3.0.3", "version");
            Check(!text.Contains('\r') && File.ReadAllBytes(file.file)[0] == (byte)'{', "LF / UTF-8 without BOM");
            var placeholders = new List<string>();
            void Visit(JsonNode? node, string path, bool example)
            {
                if (node is JsonObject obj)
                {
                    if (obj["$ref"] is JsonValue reference)
                    {
                        JsonNode? target = root;
                        foreach (var token in reference.GetValue<string>()[2..].Split('/')) target = target?[token.Replace("~1", "/").Replace("~0", "~")];
                        Check(target is not null, "unresolved ref " + reference);
                    }
                    foreach (var p in obj) Visit(p.Value, path.Length == 0 ? p.Key : path + "." + p.Key,
                        example || p.Key == "example" && path.Contains(".content."));
                }
                else if (node is JsonArray array)
                    for (var i = 0; i < array.Count; i++) Visit(array[i], path + "[" + i + "]", example);
                else if (example || node is JsonValue v && v.TryGetValue<string>(out var s) && s.Contains("<FILL:")) placeholders.Add(path);
            }
            Visit(root, "", false);
            Check(placeholders.SequenceEqual(file.placeholders), "placeholder paths differ from document/example leaves");
            Check(!file.placeholders.Any(p => p.Contains(".properties.example.")), "schema field named example reported as a placeholder");
            Check(file.placeholders.Contains("servers[0].url"), "missing server placeholder");
            Check((string?)root["servers"]?[0]?["url"] == (root["info"]?["title"]?.GetValue<string>() == "CustomApi"
                ? "https://<FILL: hostname>/custom/base" : "https://<FILL: hostname>/" + ((OutSystems.Model.Applications.IModule)module).Name + "/rest/" + (string?)root["info"]?["title"]), "base path");
            foreach (var path in root["paths"]!.AsObject())
                foreach (var operation in path.Value!.AsObject())
                {
                    var op = operation.Value!;
                    if (op["parameters"] is JsonArray parameters)
                        foreach (var p in parameters.Where(p => (string?)p!["in"] == "path"))
                            Check(path.Key.Contains("{" + (string?)p!["name"] + "}") && (bool?)p["required"] == true, "path input");
                    foreach (var status in new[] { "400", "403", "404", "500" })
                        Check((string?)op["responses"]?[status]?["$ref"] == "#/components/responses/" + status, "error response " + status);
                }
            Check((string?)root["components"]?["responses"]?["400"]?["content"]?["application/json"]?["schema"]?["$ref"] == "#/components/schemas/OutSystemsError", "error schema");
            if ((string?)root["info"]?["title"] == "Documented")
            {
                var op = root["paths"]!["/orders/{Id}"]!["post"]!;
                Check((bool?)op["requestBody"]?["required"] == true, "body mandatory");
                Check(op["parameters"]!.AsArray().Any(p => (string?)p!["name"] == "Limit" && (bool?)p["required"] == true), "query mandatory");
                Check(op["responses"]?["200"]?["headers"]?["X-Trace"] is not null, "response header");
                Check((string?)root["components"]?["securitySchemes"]?["basicAuth"]?["scheme"] == "basic" && root["security"] is JsonArray, "basic auth");
                var item = root["components"]!["schemas"]!["ApiItem"]!;
                Check((int?)item["properties"]?["Label"]?["maxLength"] == 42 && item["required"]!.AsArray().Any(x => (string?)x == "Label"), "length/required");
                Check((string?)item["properties"]?["Label"]?["default"] == "hello", "literal default");
                foreach (var (field, format) in new[] { ("EmailAddress", "email"), ("Count", "int32"), ("BigCount", "int64"), ("Amount", "double"), ("Day", "date"), ("Moment", "date-time"), ("Bytes", "byte") })
                    Check((string?)item["properties"]?[field]?["format"] == format, "format " + field);
                Check(item["properties"]?["Clock"]?["pattern"] is not null && item["properties"]?["Clock"]?["format"] is null, "time pattern");
                Check((string?)root["components"]?["schemas"]?["ApiRequest"]?["properties"]?["Items"]?["type"] == "array", "list");
                Check(root["components"]?["schemas"]?["ApiOrder"]?["properties"]?["Id"] is not null, "entity record");
                Check(root["paths"]?["/Ping"]?["get"]?["responses"]?["200"]?["content"] is null, "empty response");
                Check((string?)root["info"]?["description"] == "Order API" && (string?)op["summary"] == "Create an order", "descriptions");
            }
            else if ((string?)root["info"]?["title"] == "CustomApi")
                Check((string?)root["x-oml-authentication"] == "Custom" && (string?)root["components"]?["securitySchemes"]?["customAuth"]?["type"] == "apiKey", "custom auth");
            else Check((string?)root["x-oml-authentication"] == "None" && root["security"] is null && root["paths"]?["/Ping"]?["get"]?["responses"]?["401"] is null, "no auth");
        }
        try { OpenApi.Write(module, null, Path.Combine(dir, "openapi-docs"), []); Check(false, "overwrite accepted"); }
        catch (CliError e) { Check(e.Message.Contains("already exists"), "overwrite error"); }
        try { OpenApi.Write(module, "Absent", null, []); Check(false, "unknown API accepted"); }
        catch (CliError e) { Check(e.Message.Contains("Documented") && e.Message.Contains("PublicApi"), "unknown API list"); }
        var supplied = OpenApi.Write(module, "PublicApi", Path.Combine(dir, "supplied.json"), ["https://one.example/api", "https://two.example/api"]);
        var suppliedJson = JsonNode.Parse(File.ReadAllText(supplied.files.Single().file))!;
        Check(suppliedJson["servers"]!.AsArray().Count == 2 && !supplied.files.Single().placeholders.Contains("servers[0].url"), "explicit servers");
    }
}
