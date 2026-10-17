using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Data;
using OutSystems.Model.Types;

namespace OmlCli;

// Expand to ordinary specs so scaffolds share construction order, overrides and validation with hand-built UI.
static class Scaffolding
{
    static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
    static object Text(string text) => new { type = "Text", text };

    public static IEnumerable<ScreenSpec> Expand(IESpace m, List<ScreenSpec> screens, List<string> problems)
    {
        foreach (var s in screens)
        {
            if (s.Template is null) { yield return s; continue; }
            ScreenSpec expanded;
            try { expanded = ExpandOne(m, s, screens); }
            catch (ModelError e) { problems.Add($"screen {s.Name}: {e.Message}"); continue; }
            yield return expanded;
        }
    }

    static ScreenSpec ExpandOne(IESpace m, ScreenSpec s, List<ScreenSpec> screens)
    {
        if (WebUi.IsTraditional(m)) throw new ModelError("screen templates are not supported for Traditional Web; use preparation, actions and web widgets explicitly");
        if (s.Template is not ("list" or "detail")) throw new ModelError($"unknown screen template '{s.Template}' (use list or detail)");
        var entity = Refs.Find(m, s.Entity ?? throw new ModelError("screen template requires entity"), typeof(IEntitySignature)) as IEntitySignature;
        var name = entity!.Name;
        var id = entity.IdentifierAttribute?.Name ?? throw new ModelError($"entity '{name}' has no identifier");
        var aggregate = "Fetch" + name + "Records";
        var record = aggregate + ".List.Current." + name;
        var themed = Reflect.Get(m, "DefaultMobileTheme") is not null
            || m.MobileFlows.Any(f => f.Name == s.Flow && Reflect.Get(f, "Theme") is not null);
        var attrs = entity.Attributes.ToList();
        var detail = screens.FirstOrDefault(x => x.Template == "detail" && x.Entity == s.Entity);
        var list = screens.FirstOrDefault(x => x.Template == "list" && x.Entity == s.Entity);
        var generatedWidgets = new List<JsonElement>();
        var generatedActions = new List<ActionSpec>();
        var lookups = new List<JsonElement>();
        var inputs = new List<VarSpec>();
        JsonElement fetch;
        if (s.Template == "list")
        {
            fetch = J(new { name = aggregate, sources = new[] { name } });
            // The identifier is not a useful column; the link to the detail screen moves to the first shown column.
            var columns = attrs.Where(a => a.Name != id).DefaultIfEmpty(attrs[0]).ToList();
            object Cell(IAttributeSignature a)
            {
                object value = a.DataType == m.BinaryDataType ? Text("(binary data)") : new { type = "Expression", value = record + "." + a.Name };
                if (detail is not null && a == columns[0])
                    value = new { type = "Link", onClick = new { destination = detail.Name, args = new Dictionary<string, string> { [name + "Id"] = record + "." + id } }, children = new[] { value } };
                return new { type = "RowCell", children = new[] { value } };
            }
            generatedWidgets.Add(J(new { type = "If", condition = "not " + aggregate + ".List.Empty",
                trueBranch = new[] { new { type = "TableRecords", name = name + "Table", source = aggregate + ".List", style = themed ? "\"table\"" : "\"\"",
                    headerRow = columns.Select(a => new { type = "HeaderCell", children = new[] { Text(a.Name) } }), row = columns.Select(Cell) } },
                falseBranch = new[] { Text("No " + name + " records yet") } }));
        }
        else
        {
            inputs.Add(new(name + "Id", name + " Identifier"));
            fetch = J(new { name = aggregate, sources = new[] { name }, filters = new[] { name + "." + id + " = " + name + "Id" }, maxRecords = 1 });
            var fields = new List<object>();
            foreach (var a in attrs.Where(a => a.Name != id))
            {
                if (a.DataType == m.BinaryDataType && s.Widgets is null)
                    throw new ModelError($"detail template cannot edit Binary Data attribute '{a.Name}'; supply explicit widgets (for example Upload)");
                fields.Add(new { type = "Label", children = new[] { Text(a.Name) } });
                if (a.DataType is IIdentifierType { Entity: { } related })
                {
                    var lookup = "Fetch" + name + a.Name + "Options";
                    var relatedId = related.IdentifierAttribute?.Name ?? throw new ModelError($"entity '{related.Name}' has no identifier");
                    var labelAttribute = related.Attributes.FirstOrDefault(x => x.DataType == m.TextType)?.Name ?? relatedId;
                    // Dropdown Labels/Values resolve in the option record scope, unlike ordinary expressions.
                    var current = related.Name + ".";
                    lookups.Add(J(new { name = lookup, sources = new[] { related.Name } }));
                    fields.Add(new { type = "Dropdown", name = a.Name + "Input", variable = record + "." + a.Name,
                        list = lookup + ".List", labels = current + labelAttribute, values = current + relatedId, mandatory = a.IsMandatory });
                    continue;
                }
                fields.Add(a.DataType == m.BooleanType
                    ? new { type = "Checkbox", variable = record + "." + a.Name } as object
                    : new { type = "Input", name = a.Name + "Input", variable = record + "." + a.Name, mandatory = a.IsMandatory,
                        inputType = InputType(m, a.DataType), maxLength = a.Length });
            }
            fields.Add(new { type = "Button", onClick = "Save", style = themed ? "\"btn btn-primary\"" : "\"\"", children = new[] { Text("Save") } });
            generatedWidgets.Add(J(new { type = "Form", name = name + "Form", style = themed ? "\"card\"" : "\"\"", children = fields }));
            var flow = new List<JsonElement> { J(new { kind = "execute", action = "CreateOrUpdate" + name, args = new { Source = record } }) };
            if (list is not null) flow.Add(J(new { kind = "destination", screen = list.Name }));
            generatedActions.Add(new("Save", Flow: flow));
        }
        return s with { Title = s.Title ?? (s.Layout is null ? J("\"" + s.Name + "\"") : J(new[] { Text(s.Name) })), Inputs = s.Inputs ?? inputs,
            Aggregates = s.Aggregates ?? new[] { fetch }.Concat(lookups).ToList(), Actions = s.Actions ?? generatedActions, Widgets = s.Widgets ?? generatedWidgets };
    }

    static string InputType(IESpace m, OutSystems.Model.Types.ITypeSignature? type)
    {
        if (type is IIdentifierType identifier)
            return (identifier.Entity?.IdentifierAttribute?.DataType ?? type) == m.TextIdentifierType ? "Text" : "Number";
        var typeName = Model.Describe(m, type);
        return typeName switch
        {
            "Integer" or "Long Integer" or "Decimal" or "Currency" => "Number",
            "Date" => "Date", "Time" => "Time", "Date Time" => "Datetime", "Email" => "Email", "Phone Number" => "Phone", _ => "Text"
        };
    }
}

