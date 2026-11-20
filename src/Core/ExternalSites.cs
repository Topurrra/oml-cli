using System.Text.Json;
using OutSystems.Model;

namespace OmlCli;

// External Sites (Service Studio: a UI flow's External Site): a named URL outside the module, with input parameters that become its
// query string. A screen goes there as it goes to a screen: a Link or Button onClick, or a destination node. Reactive and Traditional
// Web each have one (OutSystems.Model.UI.Mobile.IExternalSite, ...Web.IExternalSite).
//   { "externalSites": [{ "name": "Help", "url": "https://help.example.com", "flow": "MainFlow",
//       "inputs": [{ "name": "Topic", "type": "Text" }], "description": "...", "isFrequentDestination": true }] }
partial class Builder
{
    public void ExternalSites(List<JsonElement>? sites)
    {
        foreach (var s in sites ?? new()) Try($"externalSite {RequiredName(s)}", () =>
        {
            var name = RequiredName(s);
            var label = $"externalSite {name}";
            var flowName = s.TryGetProperty("flow", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()! : "MainFlow";
            var web = WebUi.IsTraditional(m);
            object flow = web
                ? m.WebFlows.FirstOrDefault(x => x.Name == flowName) ?? (object)m.CreateWebFlow(flowName, services.NewKey())
                : m.MobileFlows.FirstOrDefault(x => x.Name == flowName) ?? (object)NewMobileFlow(flowName);
            var type = web ? typeof(OutSystems.Model.UI.Web.IExternalSite) : typeof(OutSystems.Model.UI.Mobile.IExternalSite);
            var site = Elements(Reflect.Get(flow, "Nodes")).FirstOrDefault(n => type.IsInstanceOfType(n) && Model.NameOf(n) == name);
            if (site is null)
            {
                site = Reflect.CallGeneric(flow, "CreateNode", type, name, services.NewKey())!;
                if (!Fresh(site, name, label)) return;
                created.Add(label);
            }
            else existing.Add(label);
            if (s.TryGetProperty("url", out var url))
                Reflect.Property(site, "URL", writable: true)!.SetValue(site, url.GetString());
            if (s.TryGetProperty("inputs", out var inputs))
            {
                var wanted = JsonSerializer.Deserialize<List<VarSpec>>(inputs.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
                    .Where(v => !Elements(Reflect.Get(site, "InputParameters")).Any(x => Model.NameOf(x) == v.Name)).ToList();   // an existing site keeps its inputs
                Variables(site, "CreateInputParameter", wanted, label);
            }
            if (!web && s.TryGetProperty("isFrequentDestination", out _))
                throw new ModelError("isFrequentDestination is a Traditional Web setting; a Reactive External Site has no such property");
            foreach (var p in s.EnumerateObject().Where(p => p.Name is not ("name" or "flow" or "url" or "inputs")))
                Queue(label, m, site, p.Name, p.Value);
        });
    }
}
