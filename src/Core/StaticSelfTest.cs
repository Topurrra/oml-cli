using System.Text.Json;
using OutSystems.Model.Data;

namespace OmlCli;

static partial class SelfTest
{
    static void StaticTest(string blank, string dir, List<string> failures)
    {
        var spec = Path.Combine(dir, "static.json");
        File.WriteAllText(spec, """
        { "staticEntities": [{ "name": "Status", "attributes": [{"name":"Color"}], "records": [
          { "identifier": "Active", "label": "Active", "order": 7, "values": {"Color":"\"#16a34a\""} } ] }],
          "entities": [{"name":"Item","attributes":[{"name":"StatusId","type":"Status Identifier"}]}],
          "blocks": [{"name":"Styled","styleSheet":".check { color: green; }","onReady":"Ready","actions":[
            {"name":"Ready","flow":[{"kind":"javaScript","name":"Motion","inputs":[{"name":"Caption","type":"Text"}],"args":{"Caption":"\"Ready\""},"javaScript":"void $parameters.Caption;"}]}]}],
          "screens": [{"name":"Home","isDefault":true,"styleSheet":".home { color: green; }","access":{"registered":true}}],
          "clientActions": [{"name":"StatusDefault","outputs":[{"name":"Id","type":"Status Identifier"}],
            "flow":[{"kind":"assign","assign":{"Id":"Entities.Status.Active"}}]}] }
        """);
        var output = Path.Combine(dir, "static.oml");
        var (code, report) = Apply.Execute(blank, spec, output);
        if (code != 0) { failures.Add("static: " + JsonSerializer.Serialize(report)); return; }
        using (var m = Model.Load(output))
        {
            var e = m.Entities.OfType<IStaticEntity>().Single(x => x.Name == "Status");
            var r = e.Records.Single(x => x.Identifier == "Active");
            // Static Ids are plain Integer (OutSystems examples/AddStaticEntity.cs); records then type-check without warnings.
            if (Model.Describe(m, e.IdentifierAttribute?.DataType) != "Integer") failures.Add($"static Id is '{Model.Describe(m, e.IdentifierAttribute?.DataType)}', expected 'Integer'");
            if (Model.Warnings(m).Any(w => Model.NameOf(w.Owner) == "Status")) failures.Add("static entity records produce type warnings");
            if (r.AttributeValues.Single(x => x.Attribute.Name == "Color").Value.Text != "\"#16a34a\""
                || r.AttributeValues.Single(x => x.Attribute.Name == "Order").Value.Text != "7") failures.Add("static values lost after reopen");
            if ((string?)Reflect.Get(ElementPaths.Resolve(m, "Styled"), "StyleSheet") != ".check { color: green; }") failures.Add("block stylesheet lost");
            if ((string?)Reflect.Get(ElementPaths.Resolve(m, "Styled/Ready/Motion"), "JavaScript") != "void $parameters.Caption;") failures.Add("JavaScript lost");
            if (m.DefaultScreen is not { } home || Model.NameOf(home) != "Home") failures.Add("default screen lost");
            var js = ElementPaths.Resolve(m, "Styled/Ready/Motion");
            if (!Descendants(js).OfType<OutSystems.Model.IExpression>().Any(x => x.Text == "\"Ready\"")) failures.Add("JavaScript argument lost");
        }
        var repeat = Path.Combine(dir, "static-repeat.oml");
        var (again, againReport) = Apply.Execute(output, spec, repeat);
        if (again != 0) failures.Add("static additive: " + JsonSerializer.Serialize(againReport));
        else
        {
            using var m = Model.Load(repeat);
            if (m.Entities.OfType<IStaticEntity>().Single(x => x.Name == "Status").Records.Count() != 1) failures.Add("static record duplicated");
        }
        File.WriteAllText(spec, """{"staticEntities":[{"name":"Item"}]}""");
        var refused = Path.Combine(dir, "static-refused.oml");
        if (Apply.Execute(output, spec, refused).code == 0 || File.Exists(refused)) failures.Add("static retype was not refused");
        using (var producer = Model.Services().CreateESpace(OutSystems.Model.Enumerations.SegmentationKind.CrossDevice,
                   "StaticProducer", "", false, Model.Services().NewKey()))
        using (var consumer = Model.Load(blank))
        {
            var items = StaticEntities.Create(producer, new() { new("Status", Public: true) }, new(), new(), failures);
            StaticEntities.Populate(producer, items, new(), new(), failures);
            var status = items.Single().entity;
            consumer.AddDependency<IStaticEntity, IStaticEntitySignature>(status);
            Model.Save(consumer, Path.Combine(dir, "alias-source.oml"));
        }
        File.WriteAllText(spec, """{"referenceAliases":{"StaticProducer.Status":"ProducerStatus"}}""");
        var aliasOut = Path.Combine(dir, "alias.oml");
        var (aliasCode, aliasReport) = Apply.Execute(Path.Combine(dir, "alias-source.oml"), spec, aliasOut);
        if (aliasCode != 1 || File.Exists(aliasOut) || !JsonSerializer.Serialize(aliasReport).Contains("refused:"))
            failures.Add("existing reference alias refusal: " + JsonSerializer.Serialize(aliasReport));
    }
}
