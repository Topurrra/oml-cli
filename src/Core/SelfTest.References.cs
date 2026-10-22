using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Enumerations;

namespace OmlCli;

static partial class SelfTest
{
    static void ReferencesTest(string blank, string dir, List<string> failures)
    {
        var spec = Path.Combine(dir, "references.json");
        var producerPath = Path.Combine(dir, "producer.oml");
        var source = Path.Combine(dir, "producer-blank.oml");
        using (var module = Model.Services().CreateESpace(SegmentationKind.CrossDevice, "Producer", "", false, Model.Services().NewKey()))
            Model.Save(module, source);
        File.WriteAllText(spec, """
            { "entities": [{ "name": "Customer", "public": true, "attributes": [{ "name": "Name" }] }],
              "structures": [{ "name": "OrderDto", "public": true, "attributes": [{ "name": "Id", "type": "Customer Identifier" }] }],
              "serverActions": [{ "name": "GetCustomerById", "public": true, "flow": [] }, { "name": "PrivateAction", "flow": [] }],
              "blocks": [{ "name": "CustomerCard", "flow": "ProducerFlow" }], "roles": [{ "name": "SupportRole", "public": true }],
              "edits": [{ "op": "set", "element": "CustomerCard", "properties": { "public": true } }] }
            """);
        if (Apply.Execute(source, spec, producerPath).code != 0) { failures.Add("references producer setup failed"); return; }
        using (var producer = Model.Load(producerPath))
        {
            var theme = producer.CreateMobileTheme("PublicTheme", Model.Services().NewKey());
            Reflect.Property(theme, "Public", true)!.SetValue(theme, true);
            var screen = producer.MobileFlows.Single().CreateScreen("PublicScreen", Model.Services().NewKey());
            Reflect.Property(screen, "Public", true)!.SetValue(screen, true);
            producer.CreateUserException("PrivateException", Model.Services().NewKey());
            Model.Save(producer, producerPath);
        }
        var original = File.ReadAllBytes(producerPath);
        File.WriteAllText(spec, JsonSerializer.Serialize(new
        {
            references = new[] { new { producer = producerPath, elements = "*" } },
            structures = new[] { new { name = "ConsumerDto", attributes = new[] { new { name = "Id", type = "Customer Identifier" } } } },
            serverActions = new[] { new { name = "UseProducer", locals = new[] { new { name = "Id", type = "Customer Identifier" }, new { name = "Dto", type = "OrderDto" } },
                flow = new[] { new { kind = "execute", action = "GetCustomerById" }, new { kind = "execute", action = "CheckSupportRoleRole" } } } },
            screens = new[] { new { name = "ConsumerScreen", access = new { roles = new[] { "SupportRole" } }, widgets = new[] { new { type = "BlockInstance", sourceBlock = "CustomerCard" } } } }
        }));
        var output = Path.Combine(dir, "references-consumer.oml");
        var (code, report) = Apply.Execute(blank, spec, output);
        if (code != 0) { failures.Add("references apply: " + JsonSerializer.Serialize(report)); return; }
        // The catch-all: a change to a referenced element that no guarded path made is still found before the save.
        using (var direct = Model.Load(output))
        {
            var rules = new ProductionRules(direct);
            var proxy = direct.References.Single(r => r.Name == "Producer").Entities.Single(e => e.Name == "Customer");
            var clean = new List<string>(); rules.ValidateConsumed(direct, clean);
            if (clean.Count > 0) failures.Add("references catch-all: unchanged module reported " + string.Join("; ", clean));
            try
            {
                Reflect.Property(proxy, "Description", true)!.SetValue(proxy, "changed behind the guards");
                var found = new List<string>(); rules.Validate(direct, found);
                if (!found.Any(p => p.Contains("'Producer.Customer' belongs to the referenced module Producer") && p.Contains("Description")))
                    failures.Add("references catch-all: a changed description of a referenced entity was not found: " + string.Join("; ", found));
            }
            catch (Exception e) when (e is ModelError or InvalidOperationException or NotSupportedException or System.Reflection.TargetInvocationException)
            { failures.Add("references catch-all: the proxy cannot be changed on this build, so the guard is unproven: " + e.Message); }
        }
        using (var built = Model.Load(output))
        {
            using (var target = Model.Load(blank))
            {
                target.RefreshDependencies(new[] { built });
                if (target.References.Any(r => r.Name == "Producer")) failures.Add("refresh unexpectedly imported signature proxies");
                using var producer = Model.Load(producerPath);
                target.RefreshDependencies(new[] { producer });
                if (target.References.Any(r => r.Name == "Producer")) failures.Add("refresh unexpectedly added a new producer");
                var extra = producer.Structures.Single().CreateAttribute("Added", Model.Services().NewKey());
                extra.DataType = producer.TextType;
                built.RefreshDependencies(new[] { producer });
                if (!built.References.Single(r => r.Name == "Producer").Structures.Single().Attributes.Any(a => a.Name == "Added"))
                    failures.Add("refresh of an existing dependency from a concrete producer did not update its signature");
            }
            var reference = built.References.Single(r => r.Name == "Producer");
            foreach (var name in new[] { "Customer", "OrderDto", "GetCustomerById", "CustomerCard", "SupportRole", "PublicScreen", "PublicTheme" })
                if (!ModuleReferences.Elements(reference).Any(e => Model.NameOf(e) == name)) failures.Add("reference missing: " + name);
            var all = Descendants(built).ToList();
            foreach (var name in new[] { "GetCustomerById", "CheckSupportRoleRole" })
                if (!all.Any(e => Reflect.Get(e, "Action") is { } a && Model.NameOf(a) == name)) failures.Add("reference execute missing: " + name);
            if (!all.Any(e => Reflect.Get(e, "SourceBlock") is { } b && Model.NameOf(b) == "CustomerCard")) failures.Add("reference block missing");
        }
        File.WriteAllText(spec, JsonSerializer.Serialize(new { references = new[] { new { producer = producerPath, elements = "*" } } }));
        var (againCode, againReport) = Apply.Execute(output, spec, Path.Combine(dir, "references-again.oml"));
        var again = JsonSerializer.SerializeToElement(againReport);
        if (againCode != 0 || again.GetProperty("created").GetArrayLength() != 0 || again.GetProperty("existing").GetArrayLength() < 5)
            failures.Add("references were not idempotent: " + again);
        File.WriteAllText(spec, JsonSerializer.Serialize(new { references = new[] { new { producer = producerPath, elements = new[] { "PrivateAction" } } } }));
        var refusedPath = Path.Combine(dir, "references-refused.oml");
        var refused = Apply.Execute(blank, spec, refusedPath);
        if (refused.code == 0 || File.Exists(refusedPath) || !JsonSerializer.Serialize(refused.report).Contains("not public")) failures.Add("private reference not refused");
        if (!original.SequenceEqual(File.ReadAllBytes(producerPath))) failures.Add("producer bytes changed");

        var serviceBlank = Path.Combine(dir, "service-producer-blank.oml");
        var servicePath = Path.Combine(dir, "service-producer.oml");
        using (var module = Model.Services().CreateESpace(SegmentationKind.Service, "ServiceProducer", "", false, Model.Services().NewKey()))
            Model.Save(module, serviceBlank);
        File.WriteAllText(spec, """{ "serviceActions": [{ "name": "PublicService", "public": true, "flow": [] }] }""");
        if (Apply.Execute(serviceBlank, spec, servicePath).code != 0) { failures.Add("service producer setup failed"); return; }
        File.WriteAllText(spec, JsonSerializer.Serialize(new
        {
            references = new[] { new { producer = servicePath, elements = "*" } },
            serverActions = new[] { new { name = "UseService", flow = new[] { new { kind = "execute", action = "PublicService" } } } }
        }));
        var serviceResult = Apply.Execute(blank, spec, Path.Combine(dir, "service-consumer.oml"));
        if (serviceResult.code != 0) failures.Add("service reference: " + JsonSerializer.Serialize(serviceResult.report));
    }
}
