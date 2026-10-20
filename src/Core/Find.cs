using System.Text.RegularExpressions;
using OutSystems.Model;

namespace OmlCli;

// "Which module has GetCustomer?": lists every match with its module, so the developer picks the right one
// instead of the agent guessing between modules that use the same name.
static class Finder
{
    public static object Find(string name, string? modulePath, List<string> searchIn)
    {
        var pattern = new Regex("^" + Regex.Escape(name).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase);
        var matches = new List<object>();
        var problems = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modules = new HashSet<string>();

        if (modulePath is not null)
        {
            var path = Path.GetFullPath(modulePath);
            seen.Add(path);
            using var borrowed = Model.Borrow(path); var m = borrowed.Module;
            var own = ((OutSystems.Model.Applications.IModule)m).Name;
            foreach (var (owner, element) in Refs.All(m))
                if (Model.NameOf(element) is { } n && pattern.IsMatch(n))
                {
                    matches.Add(Row(m, owner, element, owner == own ? "this module" : "referenced", path));
                    modules.Add(owner);
                }
        }

        foreach (var file in searchIn.SelectMany(Files))
        {
            if (!seen.Add(file)) continue;
            try
            {
                using var m = Model.Load(file); // a folder scan: do not keep every file open
                var own = ((OutSystems.Model.Applications.IModule)m).Name;
                foreach (var (owner, element) in Refs.All(m).Where(c => c.owner == own))
                    if (Model.NameOf(element) is { } n && pattern.IsMatch(n))
                    {
                        matches.Add(Row(m, owner, element, "producer file", file));
                        modules.Add(owner);
                    }
            }
            catch (Exception e) when (e is CliError or ModelError or InvalidOperationException or IOException or NotSupportedException)
            {
                problems.Add(new { file, error = e.Message });
            }
        }

        return new
        {
            name,
            matches,
            hint = modules.Count > 1 ? $"Found in {modules.Count} modules: use the qualified Module.Name in the spec." : null,
            problems,
        };
    }

    static IEnumerable<string> Files(string path)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full)) return new[] { full };
        if (Directory.Exists(full)) return Directory.EnumerateFiles(full, "*.oml", SearchOption.AllDirectories);
        throw new CliError($"Not found: {full}", 2);
    }

    static object Row(IESpace m, string owner, object element, string source, string file)
    {
        var kind = element is IObjectSignature o ? o.GetInterface().Name.TrimStart('I').Replace("Signature", "") : element.GetType().Name;
        object? Params(string property) => Reflect.Get(element, property) is System.Collections.IEnumerable list
            ? list.Cast<object>().Select(p => new { name = Model.NameOf(p), type = Model.Describe(m, Reflect.Get(p, "DataType") as OutSystems.Model.Types.ITypeSignature) }).ToArray()
            : null;
        return new
        {
            module = owner,
            name = Model.NameOf(element),
            qualified = $"{owner}.{Model.NameOf(element)}",
            kind,
            isPublic = source == "referenced" ? null : Reflect.Get(element, "Public") as bool?, // a reference proxy is never public itself
            inputs = Params("InputParameters"),
            outputs = Params("OutputParameters"),
            source,
            file,
        };
    }
}
