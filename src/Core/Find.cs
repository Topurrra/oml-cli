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
        using var unreadable = new Unreadable();
        void Add(Func<object> row, string owner, object element)
        {
            if (!Unreadable.Try(owner + "." + Model.NameOf(element), () => matches.Add(row()))) return;
            modules.Add(owner);
        }

        if (modulePath is not null)
        {
            var path = Path.GetFullPath(modulePath);
            seen.Add(path);
            using var borrowed = Model.Borrow(path); var m = borrowed.Module;
            var own = ((OutSystems.Model.Applications.IModule)m).Name;
            foreach (var (owner, element) in Unreadable.Try("elements of " + own, () => Refs.All(m).ToArray(), []))
                if (Model.NameOf(element) is { } n && pattern.IsMatch(n))
                    Add(() => Row(m, owner, element, owner == own ? "this module" : "referenced", path), owner, element);
        }

        foreach (var file in searchIn.SelectMany(Files))
        {
            if (!seen.Add(file)) continue;
            try
            {
                using var m = Model.Load(file); // a folder scan: do not keep every file open
                var own = ((OutSystems.Model.Applications.IModule)m).Name;
                foreach (var (owner, element) in Unreadable.Try("elements of " + own, () => Refs.All(m).Where(c => c.owner == own).ToArray(), []))
                    if (Model.NameOf(element) is { } n && pattern.IsMatch(n))
                        Add(() => Row(m, owner, element, "producer file", file), owner, element);
            }
            catch (Exception e) when (e is CliError or ModelError or InvalidOperationException or IOException or NotSupportedException)
            {
                problems.Add(new { file, error = e.Message });
            }
        }

        var result = System.Text.Json.JsonSerializer.SerializeToNode(new
        {
            name,
            matches,
            hint = modules.Count > 1 ? $"Found in {modules.Count} modules: use the qualified Module.Name in the spec." : null,
            problems,
        })!.AsObject();
        if (unreadable.Items() is { } items) result["unreadable"] = System.Text.Json.JsonSerializer.SerializeToNode(items); // absent, not null, also with --pretty
        return result;
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
        var kind = element is IObjectSignature o ? o.GetInterface().Name[1..].Replace("Signature", "") : element.GetType().Name;
        object? Params(string property) => Reflect.Get(element, property) is System.Collections.IEnumerable list
            ? list.Cast<object>().Select(p => new { name = Model.NameOf(p), type = Model.Describe(m, Reflect.Get(p, "DataType") as OutSystems.Model.Types.ITypeSignature) }).ToArray()
            : null;
        return new
        {
            module = owner,
            name = Model.NameOf(element),
            qualified = $"{owner}.{Model.NameOf(element)}",
            path = element is IObjectSignature target ? ElementPaths.PathTo(m, target) : null, // what describe, tree and edits accept
            kind,
            isPublic = source == "referenced" ? null : Reflect.Get(element, "Public") as bool?, // a reference proxy is never public itself
            inputs = Params("InputParameters"),
            outputs = Params("OutputParameters"),
            source,
            file,
        };
    }
}
