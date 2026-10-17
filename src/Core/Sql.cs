using System.Text.Json;
using OutSystems.Model.Types;

namespace OmlCli;

partial class Builder
{
    // Adapted from OutSystems/outsystems11-mcp examples/AddServerActionWithAdvancedQuery.cs (MIT).
    void Sql(object node, JsonElement spec, string where)
    {
        ProductionRules.Sql(node, spec.GetProperty("sql").GetString() ?? "");
        references.Add(($"{where}.sql", () =>
        {
            foreach (var output in spec.GetProperty("outputs").EnumerateArray())
            {
                var name = output.GetString()!;
                if (Model.Resolve(m, name) is not IRecordTypeSignature record)
                    throw new ModelError($"SQL output '{name}' must be an entity or structure");
                var made = Reflect.Call(node, "CreateOutput", record, name)!;
                Fresh(made, name, where + ".outputs");
            }
            foreach (var p in spec.TryGetProperty("params", out var ps) ? ps.EnumerateArray() : Enumerable.Empty<JsonElement>())
            {
                var name = p.GetProperty("name").GetString()!;
                var typeName = p.TryGetProperty("type", out var t) ? t.GetString()! : "Text";
                if (Model.Resolve(m, typeName) is not IBasicType type)
                    throw new ModelError($"SQL parameter '{name}' needs a basic or Identifier type, got '{typeName}'");
                var parameter = Reflect.Call(node, "CreateInputParameter", name, services.NewKey())!;
                if (!Fresh(parameter, name, where + ".params")) continue;
                Reflect.Property(parameter, "DataType", writable: true)!.SetValue(parameter, type);
                if (p.TryGetProperty("expandInline", out var inline))
                    Queue(where, m, parameter, "expandInline", inline, referencesDone: true);
                var value = p.GetProperty("value").GetString();
                Defer(node, ($"{where}.{name}", () =>
                {
                    var argument = Elements(Reflect.Get(node, "Arguments"))
                        .Single(a => ReferenceEquals(Reflect.Get(a, "Parameter"), parameter));
                    Reflect.Call(argument, "SetValue", value);
                }));
            }
        }));
        Defer(node, ($"{where}.statement", () =>
            Reflect.Property(node, "Statement", writable: true)!.SetValue(node, spec.GetProperty("sql").GetString())));
        if (spec.TryGetProperty("maxRecords", out var max))
            Defer(node, ($"{where}.maxRecords", () => Reflect.Call(node, "SetMaxRecords", max.ToString())));
    }
}
