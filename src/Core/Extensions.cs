using System.Text.Json;
using OutSystems.Model.Applications;
using E = OutSystems.Model.Extensions;

namespace OmlCli;

record ExtensionSpec(List<RecordSpec>? Structures = null, List<ActionSpec>? Actions = null);

static class Extensions
{
    static E.IType Resolve(E.IExtension extension, string name)
    {
        if (name.StartsWith("List of ", StringComparison.OrdinalIgnoreCase))
            return (E.IType)Reflect.Call(extension, "GetOrCreateListType", Resolve(extension, name[8..]))!;
        var basic = typeof(E.IExtension).GetProperties().FirstOrDefault(p => p.Name.Equals(name.Replace(" ", "") + "Type", StringComparison.OrdinalIgnoreCase));
        return basic?.GetValue(extension) as E.IType ?? extension.Structures.SingleOrDefault(s => s.Name == name)
            ?? throw new ModelError($"Unknown extension type '{name}'");
    }
    static string Type(E.IExtension extension, E.IType type) => type is E.IListType list ? "List of " + Type(extension, list.ElementType)
        : typeof(E.IExtension).GetProperties().FirstOrDefault(p => p.Name.EndsWith("Type") && ReferenceEquals(p.GetValue(extension), type))?.Name[..^4]
          ?? Model.NameOf(type) ?? type.GetType().Name;
    internal static object Inspect(E.IExtension extension) => new {
        name = ((IModule)extension).Name, key = ((IModule)extension).Key.ToString(), description = ((IModule)extension).Description ?? "",
        actions = extension.Actions.OrderBy(a => a.Name).Select(a => new { name = a.Name, description = a.Description ?? "", function = a.Function,
            parameters = a.Parameters.Select(p => new { name = p.Name, type = Type(extension, p.DataType), direction = p is E.IInputParameter ? "input" : "output" }) }).ToArray(),
        entities = extension.Entities.OrderBy(e => e.Name).Select(e => new { name = e.Name, attributes = e.Attributes.Select(a => new { name = a.Name, type = Type(extension, a.DataType) }) }).ToArray(),
        structures = extension.Structures.OrderBy(s => s.Name).Select(s => new { name = s.Name, attributes = s.Attributes.Select(a => new { name = a.Name, type = Type(extension, a.DataType) }) }).ToArray()
    };
    static void Set(object o, string name, object? value) =>
        (Reflect.Property(o, name, true) ?? throw new ModelError($"{name} is not writable on this build")).SetValue(o, value);
    static void Named(object o, string requested)
    {
        if (Model.NameOf(o) != requested) throw new ModelError($"Name '{requested}' was renamed to '{Model.NameOf(o)}'");
    }
    internal static void Create(string output, string name, string? specPath)
    {
        Packages.Fresh(output);
        var spec = specPath is null ? new ExtensionSpec() : JsonSerializer.Deserialize<ExtensionSpec>(File.ReadAllText(specPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new ModelError("Empty extension spec");
        var services = Model.Services();
        using var extension = (E.IExtension)Reflect.Call(services, "CreateExtension", name, "", services.NewKey())!;
        Named(extension, name);
        var structures = (spec.Structures ?? new()).Select(s => {
            var st = (E.IStructure)Reflect.Call(extension, "CreateStructure", s.Name, services.NewKey())!;
            Named(st, s.Name); if (s.Description is not null) Set(st, "Description", s.Description);
            return (s, st);
        }).ToArray();
        foreach (var (s, st) in structures) foreach (var a in s.Attributes ?? new()) {
            var attr = (E.IStructureAttribute)Reflect.Call(st, "CreateAttribute", a.Name, services.NewKey())!;
            Named(attr, a.Name); Set(attr, "DataType", Resolve(extension, a.Type));
            Set(attr, "IsMandatory", a.Mandatory);
            if (a.Length is { } length) Set(attr, "Length", length);
            if (a.Decimals is { } decimals) Set(attr, "Decimals", decimals);
            if (a.Description is not null) Set(attr, "Description", a.Description);
            if (a.Default is not null) Reflect.Call(attr, "SetDefaultValue", a.Default);
        }
        foreach (var a in spec.Actions ?? new()) {
            if (a.Flow is { Count: > 0 } || a.Locals is { Count: > 0 }) throw new ModelError("Extension actions contain signatures only; flow and locals are unsupported");
            var action = (E.IAction)Reflect.Call(extension, "CreateAction", a.Name, services.NewKey())!;
            Named(action, a.Name); Set(action, "Function", a.Function);
            if (a.Description is not null) Set(action, "Description", a.Description);
            foreach (var (parameters, factory) in new[] { (a.Inputs, "CreateInputParameter"), (a.Outputs, "CreateOutputParameter") })
                foreach (var p in parameters ?? new()) {
                    var parameter = Reflect.Call(action, factory, p.Name, services.NewKey())!;
                    Named(parameter, p.Name); Set(parameter, "DataType", Resolve(extension, p.Type));
                    if (parameter is E.IInputParameter) Set(parameter, "IsMandatory", p.Mandatory);
                    if (p.Default is not null) Reflect.Call(parameter, "SetDefaultValue", p.Default);
                    if (p.Description is not null) Set(parameter, "Description", p.Description);
                }
        }
        var expected = JsonSerializer.Serialize(Inspect(extension));
        var bytes = (byte[])Reflect.Call(extension, "Save", false)!;
        using (var reopened = (E.IExtension)Reflect.Call(services, "LoadExtension", bytes)!)
            if (JsonSerializer.Serialize(Inspect(reopened)) != expected) throw new ModelError("Extension signatures changed after reload");
        using var stream = new FileStream(output, FileMode.CreateNew); stream.Write(bytes);
    }
    public static int Run(List<string> args) => Packages.Guard(() => {
        if (args.Count == 3 && args[1] == "inspect") {
            using var extension = (E.IExtension)Reflect.Call(Model.Services(), "LoadExtension", File.ReadAllBytes(args[2]))!;
            return Cli.Emit(Inspect(extension));
        }
        if (args.Count is 5 or 7 && args[1] == "new" && args[3] == "--name" && (args.Count == 5 || args[5] == "--spec")) {
            Create(args[2], args[4], args.Count == 7 ? args[6] : null);
            return Cli.Emit(new { saved = true, created = Path.GetFullPath(args[2]) });
        }
        throw new CliError("Usage: oml extension inspect <file.xif> | new <out.xif> --name <Name> [--spec <spec.json>]", 2);
    });
}
