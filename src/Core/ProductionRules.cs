using System.Text.Json;
using System.Text.RegularExpressions;
using OutSystems.Model;
using OutSystems.Model.Data;
using OutSystems.Model.Types;

namespace OmlCli;

// Capture before any spec work. Global keys survive the data/logic scratch reload.
sealed class ProductionRules
{
    readonly HashSet<string> source;
    readonly HashSet<string> referenced;
    readonly Dictionary<string, string> sourceSql;
    readonly Dictionary<string, (IObjectSignature element, string name, string? attributeShape)> definitions;
    public ProductionRules(IESpace module)
    {
        var elements = Walk(module).ToList();
        source = elements.Select(Key).ToHashSet();
        // Elements of producer modules hold no data here; "Remove Unused Dependencies" may drop them.
        var self = ((OutSystems.Model.Applications.IModule)module).Name;
        referenced = Refs.All(module).Where(x => x.owner != self).Select(x => x.element).OfType<IObjectSignature>()
            .SelectMany(Walk).Select(Key).ToHashSet();
        sourceSql = elements.OfType<OutSystems.Model.Logic.Nodes.ISQLNode>().GroupBy(Key).ToDictionary(g => g.Key, g => g.First().Statement ?? "");
        definitions = elements.Where(Protected).ToDictionary(Key, o => (o, Name(o), AttributeShape(o)));
    }
    static string? AttributeShape(IObjectSignature o) => o is IAttributeSignature
        && o.Parent is IEntitySignature or IStructureSignature
        ? string.Join("|", new[] { "DataType", "Length", "Decimals", "IsMandatory" }.Select(p =>
            Reflect.Get(o, p) is IObjectSignature type ? Key(type) : Reflect.Get(o, p)?.ToString())) : null;
    static string Key(IObjectSignature o) => o.GlobalKey.ToString()!;
    internal static IEnumerable<IObjectSignature> Walk(IObjectSignature root)
    {
        var seen = new HashSet<IObjectSignature>();
        var pending = new Stack<IObjectSignature>();
        pending.Push(root);
        while (pending.TryPop(out var o))
        {
            if (!seen.Add(o)) continue;
            yield return o;
            foreach (var child in ElementPaths.Children(o)) pending.Push(child);
        }
    }
    static readonly HashSet<string> Contracts = new(StringComparer.OrdinalIgnoreCase)
    {
        "IEntitySignature", "IAttributeSignature", "IStructureSignature", "IRecord", "IRecordSignature",
        "IServerActionSignature", "IServiceActionSignature", "IClientActionSignature", "IActionSignature", "IScreenAction",
        "IInputParameterSignature", "IOutputParameterSignature", "IScreenSignature", "IBlockSignature",
        "IRoleSignature", "IUserException", "IUserExceptionSignature", "ISiteProperty", "ISitePropertySignature",
        "ITimer", "ITimerSignature", "IRestClient", "IRestService", "IRestAction", "IRestServiceAction", "IException", "IProcess", "IProcessSignature"
    };
    // SQL that was already in the module is production code the user owns; only new or changed statements are checked.
    public bool UnchangedSql(OutSystems.Model.Logic.Nodes.ISQLNode node) => sourceSql.TryGetValue(Key(node), out var s) && s == (node.Statement ?? "");
    bool Protected(IObjectSignature o) => source.Contains(Key(o)) && !referenced.Contains(Key(o)) && o is not OutSystems.Model.Logic.Nodes.IActionNode &&
        o.GetType().GetInterfaces().Any(i => (i.Namespace?.StartsWith("OutSystems.Model") == true
            || i.Namespace?.StartsWith("ServiceStudio.Plugin.") == true) && Contracts.Contains(i.Name))
        && (o is not IAttributeSignature || o.Parent is IEntitySignature or IStructureSignature)
        && (o is not IParameterSignature || o.Parent is not OutSystems.Model.Logic.Nodes.IActionNode);
    static string Name(IObjectSignature o) => Model.NameOf(o) ?? Reflect.Get(o, "Identifier")?.ToString() ?? ElementPaths.Kind(o);
    static ModelError Refuse(IObjectSignature o, string operation) => new(
        $"refused: '{Name(o)}' is an existing {(o is IEntitySignature ? "entity" : "element")}. oml never {operation} existing {(o is IEntitySignature ? "entities" : "elements")}; {(operation == "deletes" ? "delete" : "rename")} it manually in Service Studio if you really want to.");
    public void Rename(IObjectSignature o)
    {
        if (Protected(o)) throw Refuse(o, "renames");
    }
    // References are not protected against removal, but an existing local name is still an existing name.
    public void RenameReference(IObjectSignature o)
    {
        if (source.Contains(Key(o))) throw Refuse(o, "renames");
    }
    public void Delete(IObjectSignature o)
    {
        // Deleting a containing flow/folder must not bypass protection of its members.
        foreach (var child in Walk(o)) if (Protected(child)) throw Refuse(child, "deletes");
    }
    public void Validate(IESpace module, List<string> problems)
    {
        // Catch indirect SDK mutations too: dependency cleanup, clipboard operations, and property side effects.
        // Static record value slots can share an attribute's key; compare definition objects only.
        var current = Walk(module).Where(o => !o.WasDeleted && Protected(o)).GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        foreach (var (key, before) in definitions)
        {
            if (!current.TryGetValue(key, out var after)) problems.Add(Refuse(before.element, "deletes").Message);
            else if (Name(after) != before.name) problems.Add(Refuse(before.element, "renames").Message);
            else if (AttributeShape(after) != before.attributeShape) problems.Add(AttributeRefusal(before.element).Message);
        }
    }
    static ModelError AttributeRefusal(IObjectSignature o) => new(
        $"refused: '{Name(o.Parent!)}/{Name(o)}' is an existing attribute. oml never changes its type, length, decimals or mandatory setting; change it manually in Service Studio if you really want to.");
    public void Set(object target, string property, JsonElement value)
    {
        if (target is not IObjectSignature o) return;
        if (property.Equals("Name", StringComparison.OrdinalIgnoreCase)
            || (property.Equals("Identifier", StringComparison.OrdinalIgnoreCase) && o is OutSystems.Model.Data.IRecord)) Rename(o);
        if (source.Contains(Key(o)) && o is IAttributeSignature && (o.Parent is IEntitySignature or IStructureSignature)
            && new[] { "DataType", "Type", "Length", "Decimals", "IsMandatory", "Mandatory" }.Contains(property, StringComparer.OrdinalIgnoreCase))
            throw AttributeRefusal(o);
        if (o is OutSystems.Model.Logic.Nodes.ISQLNode && property.Equals("Statement", StringComparison.OrdinalIgnoreCase))
            Sql(o, value.GetString() ?? "");
    }
    public static void Sql(object node, string statement)
    {
        // SQL strings/quoted identifiers and comments are not keywords. Support nested block comments.
        var tokens = new System.Text.StringBuilder();
        for (var i = 0; i < statement.Length;)
        {
            var c = statement[i];
            if (c == '-' && i + 1 < statement.Length && statement[i + 1] == '-')
            { while (i < statement.Length && statement[i] is not ('\n' or '\r')) i++; tokens.Append(' '); }
            else if (c == '/' && i + 1 < statement.Length && statement[i + 1] == '*')
            {
                i += 2; var depth = 1;
                while (i < statement.Length && depth > 0)
                    if (i + 1 < statement.Length && statement[i] == '/' && statement[i + 1] == '*') { depth++; i += 2; }
                    else if (i + 1 < statement.Length && statement[i] == '*' && statement[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                tokens.Append(' ');
            }
            else if (c is '\'' or '"' or '[' or '{')
            {
                var end = c == '[' ? ']' : c == '{' ? '}' : c; i++;
                while (i < statement.Length)
                    if (statement[i++] == end) { if (i < statement.Length && statement[i] == end) i++; else break; }
                tokens.Append(' ');
            }
            else if (c == '@')
            {
                i++;
                while (i < statement.Length && (char.IsLetterOrDigit(statement[i]) || statement[i] is '_' or '@' or '$' or '#')) i++;
                tokens.Append(' ');
            }
            else { tokens.Append(c); i++; }
        }
        var match = Regex.Match(tokens.ToString(), @"\b(DELETE|UPDATE|TRUNCATE|DROP|ALTER|MERGE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success) throw new ModelError($"refused: '{Model.NameOf(node)}' SQL contains {match.Value.ToUpperInvariant()}. oml never applies data-changing SQL; change it manually in Service Studio if you really want to.");
    }
}
