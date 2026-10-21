using System.Reflection;
using System.Runtime.Loader;
using ServiceStudio.PluginAPI;

namespace OmlCli;

// Read the installed build's API surface, so new builds can be explored without a decompiler.
static class Api
{
    static readonly string[] Assemblies =
    {
        "OutSystems.Model.V1", "ServiceStudio.PluginAPI", "ServiceStudio.Framework", "ServiceStudio.Mcp", "ServiceStudio.Plugin.NRWidgets",
        "ServiceStudio.Plugin.Widgets", "ServiceStudio.Plugin.REST", "ServiceStudio.Plugin.RESTService", "ServiceStudio.Plugin.SOAP",
    };

    // `oml api` with no name. Every name here is checked by tests/api-check.ps1 to match a type on the build.
    public const string Usage = """
        oml api <TypeName>...   (members of model and plugin types on this build; '*' is a wildcard; case does not count)
        A name matches a type's short name (IServerAction) or full name (OutSystems.Model.Logic.IServerAction).
        Useful types:
          OutSystems.Model.IESpace                      the module: its lists and Create* factories
          OutSystems.Model.Data.IEntity, OutSystems.Model.Data.IStaticEntity, OutSystems.Model.Data.IStructure
          OutSystems.Model.Types.IAttribute
          OutSystems.Model.Logic.IServerAction, OutSystems.Model.Logic.IClientAction, OutSystems.Model.Logic.IServiceAction
          OutSystems.Model.Logic.IRole
          OutSystems.Model.Logic.Nodes.I*Node           every flow node (IAssignNode, IIfNode, ...)
          OutSystems.Model.Logic.Aggregates.IAggregate
          OutSystems.Model.UI.Mobile.IMobileFlow, OutSystems.Model.UI.Mobile.IMobileScreen, OutSystems.Model.UI.Mobile.IMobileBlock
          OutSystems.Model.UI.Mobile.IScreenAction, OutSystems.Model.UI.Mobile.IScreenAggregate, OutSystems.Model.UI.Mobile.IDataAction
          OutSystems.Model.UI.Mobile.Widgets.I*         Reactive and Mobile widgets
          OutSystems.Model.UI.Web.IWebScreen, OutSystems.Model.UI.Web.IWebBlock
          ServiceStudio.Plugin.REST.IRestClient, ServiceStudio.Plugin.RESTService.IRestService
        Example: oml api OutSystems.Model.Logic.IServerAction "OutSystems.Model.Logic.Nodes.I*Node"

        """;

    // Each name matches a type's short or full name, in any case; '*' is a wildcard. A name that matches nothing is
    // tried again with an 'I' before its last part (ServerAction -> IServerAction). Still nothing is an error (exit 2)
    // that lists the closest type names: the result is never silently empty.
    public static object Types(IEnumerable<string> names)
    {
        Model.Services();
        var all = AllTypes().ToList();
        static System.Text.RegularExpressions.Regex Pattern(string n) => new("^" + System.Text.RegularExpressions.Regex.Escape(n).Replace(@"\*", ".*") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        bool Matches(System.Text.RegularExpressions.Regex p) => all.Any(x => p.IsMatch(x.type.Name) || p.IsMatch(x.type.FullName!));
        var patterns = new List<System.Text.RegularExpressions.Regex>();
        var unknown = new List<string>();
        foreach (var n in names)
        {
            var dot = n.LastIndexOf('.');
            var prefixed = n[..(dot + 1)] + "I" + n[(dot + 1)..];
            if (Matches(Pattern(n))) patterns.Add(Pattern(n));
            else if (!n[(dot + 1)..].StartsWith('I') && Matches(Pattern(prefixed))) patterns.Add(Pattern(prefixed));
            else unknown.Add(n);
        }
        if (unknown.Count > 0)
            throw new CliError(string.Join("\n", unknown.Select(n => $"No type matches '{n}'. "
                + (Closest(n, all.Select(x => x.type)).ToList() is { Count: > 0 } close ? $"Closest: {string.Join(", ", close)}. " : "No close names. ")
                + "Use '*' as a wildcard, for example 'oml api \"*ServerAction*\"', or run 'oml api' for the useful types.")), 2);
        return all.Where(x => patterns.Any(p => p.IsMatch(x.type.Name) || p.IsMatch(x.type.FullName!))).Select(x => (object)new
        {
            type = x.type.FullName,
            assembly = x.assembly,
            implements = x.type.GetInterfaces().Select(i => i.Name),
            members = x.type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m is not MethodInfo { IsSpecialName: true })
                .Select(Describe),
        }).ToList();
    }

    static IEnumerable<(Type type, string assembly)> AllTypes()
    {
        var context = AssemblyLoadContext.GetLoadContext(typeof(Api).Assembly)!;
        foreach (var asmName in Assemblies)
        {
            Assembly asm;
            try { asm = context.LoadFromAssemblyName(new AssemblyName(asmName)); }
            catch (FileNotFoundException) { continue; }
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.OfType<Type>().ToArray(); }
            foreach (var t in types) yield return (t, asmName);
        }
    }

    // Public types whose short name contains the word (also with an 'I' before it), or is a small edit away from it;
    // the typed model API (OutSystems.Model.*) and the plugins (ServiceStudio.Plugin.*) first, then interfaces,
    // then the shortest or nearest. Five full names at most; none when nothing is close.
    static IEnumerable<string> Closest(string name, IEnumerable<Type> types)
    {
        var word = name.Replace("*", "").Split('.')[^1].ToLowerInvariant();
        if (word.Length == 0) return [];
        var limit = Math.Max(2, word.Length / 3);
        return types.Where(t => t.IsPublic && !t.FullName!.Contains('+'))
            .Select(t => (name: t.FullName!, s: t.Name.ToLowerInvariant(), contract: t.IsInterface,
                main: t.FullName!.StartsWith("OutSystems.Model.") || t.FullName!.StartsWith("ServiceStudio.Plugin.")))
            .Select(t => (t.name, t.contract, t.main, contains: t.s.Contains(word),
                d: Math.Min(SpecHelp.Distance(word, t.s), SpecHelp.Distance("i" + word, t.s))))
            .Where(t => t.contains || t.d <= limit)
            .OrderByDescending(t => t.contains).ThenByDescending(t => t.main).ThenBy(t => t.contains ? t.name.Length : t.d)
            .ThenByDescending(t => t.contract).ThenBy(t => t.name, StringComparer.Ordinal)
            .Select(t => t.name).Distinct().Take(5);
    }

    static string Describe(MemberInfo m)
    {
        try { return m is FieldInfo { IsLiteral: true } field
            ? $"{m} = {field.GetRawConstantValue()}" : m.ToString() ?? m.Name; }
        catch (Exception) { return m.Name; } // a signature type that cannot load on this build
    }

    public static object Widgets()
    {
        Model.Services();
        return PluginProvider.Implementations
            .Select(d => new { name = d.GetType().Name.Replace("Descriptor", ""), plugin = d.GetType().Assembly.GetName().Name })
            .OrderBy(d => d.name);
    }
}
