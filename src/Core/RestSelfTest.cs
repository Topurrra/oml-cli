using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Data;
using OutSystems.Model.Logic.Nodes;
using OutSystems.Model.Types;

namespace OmlCli;

static class RestSelfTest
{
    public static void Run(string blank, string dir, List<string> failures)
    {
        var spec = Path.Combine(dir, "rest.json");
        var output = Path.Combine(dir, "rest.oml");
        File.WriteAllText(spec, """
            {
              "entities": [{ "name": "Order", "attributes": [{ "name": "Total", "type": "Decimal" }] }],
              "structures": [
                { "name": "ForecastDto", "attributes": [{ "name": "Temperature", "type": "Decimal" }] },
                { "name": "ForecastRequest", "attributes": [{ "name": "City" }, { "name": "History", "type": "List of ForecastDto" }] },
                { "name": "OrderDto", "attributes": [{ "name": "Total", "type": "Decimal" }] }
              ],
              "restClients": [{ "name": "Weather", "baseUrl": "https://api.example.com", "actions": [
                { "name": "GetForecast", "method": "GET", "url": "/forecast/{City}",
                  "inputs": [{ "name": "City", "location": "url" }, { "name": "Days", "type": "Integer", "location": "query" },
                    { "name": "ApiKey", "location": "header", "headerName": "X-Api-Key" }],
                  "outputs": [{ "name": "Response", "type": "ForecastDto" }] },
                { "name": "PostForecast", "method": "POST", "url": "/forecast", "inputs": [{ "name": "Body", "type": "ForecastRequest", "location": "body" }] },
                { "name": "PutForecast", "method": "PUT", "url": "/forecast", "inputs": [{ "name": "Body", "location": "body" }], "outputs": [{ "name": "Response" }] },
                { "name": "DeleteForecast", "method": "DELETE", "url": "/forecast" }
              ] }],
              "serverActions": [{ "name": "FetchForecast", "outputs": [{ "name": "Temperature", "type": "Decimal" }], "flow": [
                { "kind": "execute", "action": "Weather.GetForecast", "args": { "City": "\"Lisbon\"", "Days": "3", "ApiKey": "\"test\"" } },
                { "kind": "assign", "assign": { "Temperature": "GetForecast.Response.Temperature" } }
              ] }],
              "restServices": [{ "name": "OrdersApi", "actions": [
                { "name": "GetOrder", "method": "GET", "url": "/orders/{Id}",
                  "inputs": [{ "name": "Id", "type": "Long Integer", "location": "url" }], "outputs": [{ "name": "Order", "type": "OrderDto" }], "flow": [
                    { "kind": "execute", "action": "FetchForecast" },
                    { "kind": "aggregate", "name": "FindOrder", "sources": ["Order"], "filters": ["Order.Id = LongIntegerToIdentifier(Id)"], "maxRecords": 1 },
                    { "kind": "assign", "assign": { "Order.Total": "FindOrder.List.Current.Order.Total" } }
                  ] }
              ] }]
            }
            """);
        var (code, report) = Apply.Execute(blank, spec, output);
        if (code != 0) { failures.Add("REST: " + JsonSerializer.Serialize(report)); return; }
        using (var built = Model.Load(output))
        {
            var client = RestModel.Integrations(built, false).Single();
            var methods = RestModel.Actions(client).ToList();
            var get = methods.Single(a => Model.NameOf(a) == "GetForecast");
            void Check(bool ok, string detail) { if (!ok) failures.Add("REST: " + detail); }
            Check((string?)Reflect.Get(client, "BaseURL") == "https://api.example.com", "base URL lost");
            Check(methods.Count == 4 && (string?)Reflect.Get(get, "URLPath") == "/forecast/{City}?Days={Days}", "methods/query URL lost");
            var inputs = ((System.Collections.IEnumerable)Reflect.Get(get, "InputParameters")!).Cast<object>().ToList();
            var header = inputs.Single(p => Model.NameOf(p) == "ApiKey");
            Check((string?)Reflect.Get(header, "OriginalName") == "X-Api-Key" && Reflect.Get(header, "InputPlacement")?.ToString() == "HeaderPlacement", "header placement/name lost");
            Check((bool?)Reflect.Get(inputs.Single(p => Model.NameOf(p) == "City"), "IsMandatory") == true, "URL input is not mandatory");
            var response = ((System.Collections.IEnumerable)Reflect.Get(get, "OutputParameters")!).Cast<object>().Single();
            Check(Reflect.Get(response, "DataType") is IStructureSignature dto && ReferenceEquals(dto.Parent, client), "response DTO is not client-owned");
            var request = ((System.Collections.IEnumerable)Reflect.Get(client, "Structures")!).Cast<IStructureSignature>().Single(s => s.Name == "WeatherForecastRequest");
            Check(request.Attributes.Single(a => a.Name == "History").DataType is IListType list && list.ElementType is IStructureSignature nested && ReferenceEquals(nested.Parent, client), "nested list DTO is not client-owned");
            var call = built.ServerActions.Single(a => a.Name == "FetchForecast").Nodes.OfType<IExecuteServerActionNode>().Single();
            Check(ReferenceEquals(call.Action, get) && call.Arguments.Count() == 3, "execute reference/arguments lost");
            var service = RestModel.Integrations(built, true).Single();
            var exposed = RestModel.Actions(service).Single();
            var nodes = ((System.Collections.IEnumerable)Reflect.Get(exposed, "Nodes")!).Cast<object>().ToList();
            Check(nodes.OfType<IStartNode>().Count() == 1 && nodes.OfType<IAssignNode>().Count() == 1 && nodes.OfType<IAggregateNode>().Count() == 1
                && nodes.OfType<IExecuteServerActionNode>().Single().Action == built.ServerActions.Single(a => a.Name == "FetchForecast"), "exposed flow missing or duplicated");
            Check((string?)Reflect.Get(exposed, "URLPath") == "/orders/{Id}", "exposed URL lost");
        }

        // Listing existing integrations: a changed base URL is refused; new methods are added, existing ones kept.
        File.WriteAllText(spec, """{ "restClients": [{ "name": "Weather", "baseUrl": "https://changed.example.com" }] }""");
        var (changedCode, changedReport) = Apply.Execute(output, spec, Path.Combine(dir, "rest-changed.oml"));
        if (changedCode != 1 || !JsonSerializer.Serialize(changedReport).Contains("baseUrl differs"))
            failures.Add("REST reuse: a changed base URL must be refused: " + JsonSerializer.Serialize(changedReport));
        File.WriteAllText(spec, """
            { "restClients": [{ "name": "Weather", "baseUrl": "https://api.example.com", "actions": [{ "name": "GetToday", "url": "/today" }] }],
              "restServices": [{ "name": "OrdersApi", "actions": [{ "name": "Ping" }] }] }
            """);
        var reused = Path.Combine(dir, "rest-reused.oml");
        var (reuseCode, reuseReport) = Apply.Execute(output, spec, reused);
        if (reuseCode != 0) failures.Add("REST reuse: " + JsonSerializer.Serialize(reuseReport));
        else
        {
            using var built = Model.Load(reused);
            var client = RestModel.Integrations(built, false).Single();
            if (RestModel.Actions(client).Count() != 5 || (string?)Reflect.Get(client, "BaseURL") != "https://api.example.com"
                || RestModel.Actions(RestModel.Integrations(built, true).Single()).Count() != 2)
                failures.Add("REST reuse: new methods must be added to the existing integrations, and nothing else changed");
        }
        foreach (var invalid in new[] {
            """{ "restServices": [{ "name": "Weather" }] }""",
            """{ "restClients": [{ "name": "Bad", "actions": [{ "name": "Get", "method": "NOTAVERB" }] }] }""",
            """{ "restClients": [{ "name": "Bad", "actions": [{ "name": "Get", "inputs": [{ "name": "X", "location": "cookie" }] }] }] }""",
            """{ "restClients": [{ "name": "Bad", "actions": [{ "name": "Get", "outputs": [{ "name": "X", "type": "UnknownDto" }] }] }] }""",
            """{ "restClients": [{ "name": "Bad", "actions": [{ "name": "Get" }, { "name": "Get" }] }] }"""
        })
        {
            File.WriteAllText(spec, invalid);
            var rejected = Path.Combine(dir, "rest-rejected.oml");
            var (invalidCode, _) = Apply.Execute(output, spec, rejected);
            if (invalidCode == 0 || File.Exists(rejected)) failures.Add("REST invalid spec was saved: " + invalid);
        }
        Import(blank, dir, failures);
    }

    // "openapi": Service Studio's own importer, for OpenAPI 3 and Swagger 2.0 JSON next to the spec.
    static void Import(string blank, string dir, List<string> failures)
    {
        void Fail(string detail) => failures.Add("REST openapi: " + detail);
        File.WriteAllText(Path.Combine(dir, "pets3.json"), """
            { "openapi": "3.0.3", "info": { "title": "Pet Store", "version": "1" }, "servers": [{ "url": "https://pets.example.test/v1" }],
              "components": { "schemas": {
                "Pet": { "type": "object", "properties": { "id": { "type": "integer", "format": "int64" }, "name": { "type": "string" },
                  "status": { "type": "string", "enum": ["available", "sold"] },
                  "owner": { "type": "object", "properties": { "phones": { "type": "array", "items": { "type": "object", "properties": { "number": { "type": "string" } } } } } } } } } },
              "paths": {
                "/pets": { "get": { "operationId": "listPets",
                    "parameters": [{ "name": "limit", "in": "query", "schema": { "type": "integer" } }, { "name": "X-Trace", "in": "header", "schema": { "type": "string" } }],
                    "responses": { "200": { "description": "ok", "content": { "application/json": { "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Pet" } } } } } } },
                  "post": { "operationId": "createPet", "requestBody": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Pet" } } } },
                    "responses": { "201": { "description": "made", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Pet" } } } } } } },
                "/pets/{petId}": { "delete": { "operationId": "deletePet", "parameters": [{ "name": "petId", "in": "path", "required": true, "schema": { "type": "integer", "format": "int64" } }],
                    "responses": { "204": { "description": "gone" } } } } } }
            """);
        File.WriteAllText(Path.Combine(dir, "orders2.json"), """
            { "swagger": "2.0", "info": { "title": "Orders", "version": "1" }, "host": "orders.example.test", "basePath": "/api", "schemes": ["https"],
              "securityDefinitions": { "key": { "type": "apiKey", "in": "header", "name": "X-Key" } }, "security": [{ "key": [] }],
              "definitions": { "Order": { "type": "object", "properties": { "id": { "type": "string" }, "lines": { "type": "array", "items": { "type": "string" } } } } },
              "paths": { "/orders/{id}": { "put": { "operationId": "UpdateOrder", "consumes": ["application/json"], "produces": ["application/json"], "parameters": [
                { "name": "id", "in": "path", "required": true, "type": "string" }, { "name": "body", "in": "body", "schema": { "$ref": "#/definitions/Order" } }],
                "responses": { "200": { "description": "ok", "schema": { "$ref": "#/definitions/Order" } } } } } } }
            """);
        var spec = Path.Combine(dir, "rest-import.json");
        var output = Path.Combine(dir, "rest-import.oml");
        File.WriteAllText(spec, """
            { "restClients": [{ "name": "Pets", "openapi": "pets3.json" }, { "name": "Shop", "openapi": "orders2.json", "baseUrl": "https://test.example.test/api" }] }
            """);
        var (code, report) = Apply.Execute(blank, spec, output);
        if (code != 0) { Fail(JsonSerializer.Serialize(report)); return; }
        var text = JsonSerializer.Serialize(report);
        if (!text.Contains("Swagger 2.0 import")) Fail("the unsupported apiKey scheme is not in the warnings: " + text);
        using (var built = Model.Load(output))
        {
            var pets = RestModel.Integrations(built, false).SingleOrDefault(i => Model.NameOf(i) == "Pets");
            var shop = RestModel.Integrations(built, false).SingleOrDefault(i => Model.NameOf(i) == "Shop");
            if (pets is null || shop is null) { Fail("the clients do not have the spec's names"); return; }
            object Method(object client, string name) => RestModel.Actions(client).Single(a => Model.NameOf(a) == name);
            List<object> Inputs(object method) => ((System.Collections.IEnumerable)Reflect.Get(method, "InputParameters")!).Cast<object>().ToList();
            if ((string?)Reflect.Get(pets, "BaseURL") != "https://pets.example.test/v1") Fail("the server URL is not the base URL");
            if ((string?)Reflect.Get(shop, "BaseURL") != "https://test.example.test/api") Fail("the spec's baseUrl does not win over the document's");
            if (RestModel.Actions(pets).Count() != 3 || RestModel.Actions(shop).Count() != 1) Fail("methods missing");
            var list = Method(pets, "listPets");
            if ((string?)Reflect.Get(list, "URLPath") != "/pets?limit={limit}") Fail("query input not in the URL: " + Reflect.Get(list, "URLPath"));
            if (!Inputs(list).Any(p => Reflect.Get(p, "InputPlacement")?.ToString() == "HeaderPlacement" && (string?)Reflect.Get(p, "OriginalName") == "X-Trace")) Fail("header input lost");
            if (Reflect.Get(Method(pets, "deletePet"), "HTTPMethod")?.ToString() != "DELETE") Fail("verb lost");
            var structures = ((System.Collections.IEnumerable)Reflect.Get(pets, "Structures")!).Cast<IStructureSignature>().ToList();
            var pet = structures.SingleOrDefault(s => s.Name == "Pet");
            if (pet is null || pet.Attributes.Single(a => a.Name == "owner").DataType is not IStructureSignature owner
                || owner.Attributes.Single(a => a.Name == "phones").DataType is not IListType { ElementType: IStructureSignature })
                Fail("nested object or array of objects lost");
            var update = Method(shop, "UpdateOrder");
            if (!Inputs(update).Any(p => Reflect.Get(p, "InputPlacement")?.ToString() == "BodyPlacement")) Fail("body input lost");
        }
        foreach (var (invalid, file, content) in new[] {
            ("""{ "restServices": [{ "name": "Bad", "openapi": "pets3.json" }] }""", "", ""),
            ("""{ "restClients": [{ "name": "Bad", "openapi": "missing.json" }] }""", "", ""),
            ("""{ "restClients": [{ "name": "Bad", "openapi": "bad.yaml" }] }""", "bad.yaml", "openapi: 3.0.0\ninfo: {}\n"),
            ("""{ "restClients": [{ "name": "Bad", "openapi": "bad.json" }] }""", "bad.json", """{ "title": "not a document" }"""),
            ("""{ "restClients": [{ "name": "Bad", "openapi": "ext.json" }] }""", "ext.json",
             """{ "swagger": "2.0", "info": { "title": "x", "version": "1" }, "paths": { "/a": { "get": { "responses": { "200": { "description": "ok", "schema": { "$ref": "other.json#/definitions/A" } } } } } } }"""),
        })
        {
            if (file.Length > 0) File.WriteAllText(Path.Combine(dir, file), content);
            File.WriteAllText(spec, invalid);
            var rejected = Path.Combine(dir, "rest-import-rejected.oml");
            var (invalidCode, _) = Apply.Execute(blank, spec, rejected);
            if (invalidCode == 0 || File.Exists(rejected)) Fail("invalid spec was saved: " + invalid);
        }
    }
}
