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

    public static object Types(IEnumerable<string> names)
    {
        Model.Services();
        // Each name matches a type's short or full name; '*' is a wildcard.
        var patterns = names.Select(n => new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(n).Replace(@"\*", ".*") + "$")).ToArray();
        var context = AssemblyLoadContext.GetLoadContext(typeof(Api).Assembly)!;
        var result = new List<object>();
        foreach (var asmName in Assemblies)
        {
            Assembly asm;
            try { asm = context.LoadFromAssemblyName(new AssemblyName(asmName)); }
            catch (FileNotFoundException) { continue; }
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.OfType<Type>().ToArray(); }
            foreach (var t in types.Where(t => patterns.Any(p => p.IsMatch(t.Name) || p.IsMatch(t.FullName!))))
                result.Add(new
                {
                    type = t.FullName,
                    assembly = asmName,
                    implements = t.GetInterfaces().Select(i => i.Name),
                    members = t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                        .Where(m => m is not MethodInfo { IsSpecialName: true })
                        .Select(Describe),
                });
        }
        return result;
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
