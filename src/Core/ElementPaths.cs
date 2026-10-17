using System.Text.RegularExpressions;
using OutSystems.Model;

namespace OmlCli;

// Paths search one breadth-first level at a time within each selected scope.
static class ElementPaths
{
    public static string Kind(object o) => Reflect.Get(o, "CustomWidgetDescriptor") is { } descriptor
        ? descriptor.GetType().Name.Replace("Descriptor", "") : o.GetType().Name;

    public static IEnumerable<IObjectSignature> Children(IObjectSignature o) => o.Children.Distinct();

    public static string Segment(IObjectSignature o, IObjectSignature parent)
    {
        if (!string.IsNullOrEmpty(Model.NameOf(o)) && Children(parent).Count(c => Model.NameOf(c) == Model.NameOf(o)) == 1)
            return Model.NameOf(o)!;
        var kind = Kind(o);
        var siblings = Children(parent).Where(c => Kind(c) == kind).ToList();
        return $"{kind}[{siblings.IndexOf(o)}]";
    }

    public static IObjectSignature Resolve(IObjectSignature root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ModelError("element path is required");
        var scope = root;
        foreach (var part in path.Split('/'))
        {
            var match = Regex.Match(part, @"^(.+)\[(\d+)\]$");
            var level = Children(scope).ToList();
            var visited = new HashSet<IObjectSignature> { scope };
            IObjectSignature? found = null;
            while (level.Count > 0)
            {
                level = level.Where(visited.Add).ToList();
                var hits = level.Where(o => match.Success
                    ? Kind(o).Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase)
                      && o.Parent is { } parent && Children(parent).Where(c => Kind(c) == Kind(o)).ToList().IndexOf(o) == int.Parse(match.Groups[2].Value)
                    : Model.NameOf(o) == part).Distinct().ToList();
                if (hits.Count > 1) throw new ModelError($"ambiguous path '{path}' at '{part}'; qualify the parent or use the index path from tree");
                if (hits.Count == 1) { found = hits[0]; break; }
                level = level.SelectMany(Children).Distinct().ToList();
            }
            scope = found ?? throw new ModelError($"no element '{part}' in path '{path}'");
        }
        return scope;
    }
}
