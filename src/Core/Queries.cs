using System.Text.Json;
using OutSystems.Model;

namespace OmlCli;

partial class Builder
{
    static IEnumerable<JsonElement> Items(JsonElement spec, string key) =>
        spec.TryGetProperty(key, out var value) ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();

    static void Set(object owner, string property, object? value) =>
        (Reflect.Property(owner, property, writable: true)
            ?? throw new ModelError($"{owner.GetType().Name} has no writable {property} on this build")).SetValue(owner, value);

    static void SetEnum(object owner, string property, string value)
    {
        var p = Reflect.Property(owner, property, writable: true)
            ?? throw new ModelError($"{owner.GetType().Name} has no writable {property} on this build");
        p.SetValue(owner, Enum.Parse(p.PropertyType, value, ignoreCase: true));
    }

    void QueryAttributes(object db, JsonElement spec, string where)
    {
        foreach (var join in Items(spec, "joins"))
        {
            object? j = null;
            references.Add(($"{where}.joins", () =>
            {
                object Source(string key)
                {
                    var name = join.GetProperty(key).GetString();
                    return ((System.Collections.IEnumerable)Reflect.Get(db, "Sources")!).Cast<object>()
                        .SingleOrDefault(s => Model.NameOf(s) == name)
                        ?? throw new ModelError($"no aggregate source '{name}'");
                }
                var type = join.GetProperty("type").GetString()!.Trim().ToLowerInvariant() switch
                {
                    "only with" => "Inner",
                    "with or without" => "Left",
                    "with" => "Full",
                    var other => throw new ModelError($"unknown join type '{other}' (use only with, with or without, with)"),
                };
                var left = Source("left");
                var right = Source("right");
                j = Reflect.Call(db, "CreateJoin")!;
                Set(j, "LeftSource", left);
                Set(j, "RightSource", right);
                SetEnum(j, "JoinType", type);
            }));
            expressions.Add(($"{where}.joins.condition", () =>
            {
                if (j is not null) Reflect.Call(j, "SetCondition", join.GetProperty("condition").GetString());
            }));
        }
        foreach (var group in Items(spec, "groupBy"))
            expressions.Add(($"{where}.groupBy", () =>
            {
                var attribute = group.GetString()!;
                var g = Reflect.Call(db, "CreateGroupByAttribute", attribute.Split('.').Last(), services.NewKey())!;
                Reflect.Call(g, "SetAttribute", attribute);
            }));
        foreach (var attribute in Items(spec, "aggregates"))
            expressions.Add(($"{where}.aggregates", () =>
            {
                var name = attribute.GetProperty("name").GetString()!;
                var function = attribute.GetProperty("function").GetString()!;
                if (!new[] { "count", "sum", "avg", "min", "max" }.Contains(function.ToLowerInvariant()))
                    throw new ModelError($"unknown aggregate function '{function}'");
                var a = Reflect.Call(db, "CreateAggregateAttribute", name, services.NewKey())!;
                if (!Fresh(a, name, $"{where}.{name}")) return;
                SetEnum(a, "AggregationType", function);
                Reflect.Call(a, "SetAttribute", attribute.GetProperty("attribute").GetString());
            }));
        foreach (var attribute in Items(spec, "calculated"))
            expressions.Add(($"{where}.calculated", () =>
            {
                var name = attribute.GetProperty("name").GetString()!;
                var a = Reflect.Call(db, "CreateCalculatedAttribute", name, services.NewKey())!;
                if (Fresh(a, name, $"{where}.{name}"))
                    Reflect.Call(a, "SetValue", attribute.GetProperty("expression").GetString());
            }));
    }

    void QueryNode(object action, object node, JsonElement spec, string kind, string where)
    {
        if (kind.Equals("destination", StringComparison.OrdinalIgnoreCase) && spec.TryGetProperty("screen", out var destination))
            references.Add(($"{where}.screen", () => Set(node, "Destination",
                Refs.Find(m, destination.GetString()!, typeof(OutSystems.Model.UI.IScreenSignature)))));
        if (kind.Equals("refreshData", StringComparison.OrdinalIgnoreCase))
            references.Add(($"{where}.dataSource", () =>
            {
                var name = spec.GetProperty("dataSource").GetString();
                var screen = ((IObjectSignature)action).Parent;
                while (screen is not null && !Reflect.Has(screen, "CreateScreenAggregate")) screen = screen.Parent;
                if (screen is null) throw new ModelError("refreshData requires an owning screen or block");
                var sources = new[] { "ScreenAggregates", "DataActions" }.SelectMany(key =>
                    Reflect.Get(screen, key) is System.Collections.IEnumerable list ? list.Cast<object>() : Enumerable.Empty<object>());
                Set(node, "DataSource", sources.SingleOrDefault(s => Model.NameOf(s) == name)
                    ?? throw new ModelError($"no data source '{name}' in owning screen '{Model.NameOf(screen)}'"));
            }));
        if (!kind.Equals("execute", StringComparison.OrdinalIgnoreCase) && spec.TryGetProperty("args", out var args))
            expressions.Add(($"{where}.args", () => Arguments(node, args)));
    }

    void SwitchTargets(object node, JsonElement spec, Func<string?, object> resolve)
    {
        foreach (var c in Items(spec, "cases"))
        {
            var condition = Reflect.Call(node, "CreateCondition")!;
            Connect(condition, "Target", resolve(c.GetProperty("next").GetString()));
            expressions.Add(("switch.condition", () => Reflect.Call(condition, "SetValue", c.GetProperty("condition").GetString())));
        }
        Connect(node, "OtherwiseTarget", resolve(spec.TryGetProperty("otherwise", out var otherwise)
            ? otherwise.GetString() : throw new ModelError("switch requires 'otherwise' target")));
    }
}

