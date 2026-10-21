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
    }
}
