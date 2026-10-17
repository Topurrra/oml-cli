using System.Security.Cryptography;
using OutSystems.Model;
using OutSystems.Model.Applications;
using OutSystems.Model.Enumerations;

namespace OmlCli;

static class Packages
{
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static void Fresh(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) throw new ModelError($"Output already exists: {path}");
    }
    internal static object App(IApplication app) => new {
        name = app.Name, key = app.ApplicationKey.ToString(), kind = app.Kind.ToString(), description = app.Description,
        modules = app.Modules.Select(Module).ToArray(),
        dependencies = app.Dependencies.Select(d => new { name = d.Name, key = d.Key.ToString(), kind = d.Kind.ToString() }).ToArray()
    };
    static object Module(IModuleBytes m) => new { name = m.Name, key = m.Key.ToString(), kind = m.Kind.ToString(), size = m.Content.Length };
    internal static T Read<T>(string path, Func<IApplication[], IModuleBytes[], T> use)
    {
        var s = Model.Services();
        if (Path.GetExtension(path).Equals(".oap", StringComparison.OrdinalIgnoreCase))
        {
            using var app = (IApplication)Reflect.Call(s, "LoadApplication", File.ReadAllBytes(path), null, null)!;
            return use(new[] { app }, app.Modules.ToArray());
        }
        if (!Path.GetExtension(path).Equals(".osp", StringComparison.OrdinalIgnoreCase)) throw new ModelError("Expected .oap or .osp package");
        using var solution = (ISolution)Reflect.Call(s, "LoadSolution", File.ReadAllBytes(path), null, null)!;
        return use(solution.Applications.ToArray(), solution.Modules.Concat(solution.Applications.SelectMany(a => a.Modules)).GroupBy(m => m.Key.ToString()).Select(g => g.First()).ToArray());
    }
    internal static object List(string path) => Read(path, (apps, modules) => (object)new { applications = apps.Select(App).ToArray(), modules = modules.Select(Module).ToArray() });
    internal static object[] Extract(string path, string output) => Read(path, (_, modules) => {
        output = Path.GetFullPath(output);
        var files = modules.Select(m => {
            if (string.IsNullOrWhiteSpace(m.Name) || m.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || m.Name is "." or "..")
                throw new ModelError($"Unsafe module filename: {m.Name}");
            var file = Path.Combine(output, m.Name + (m.Kind.ToString() == "Extension" ? ".xif" : ".oml"));
            return (m, file);
        }).ToArray();
        if (files.Select(f => f.file).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length) throw new ModelError("Duplicate module filenames in package");
        foreach (var f in files) Fresh(f.file);
        Directory.CreateDirectory(output);
        return files.Select(f => {
            var bytes = f.m.Content;
            using (var stream = new FileStream(f.file, FileMode.CreateNew)) stream.Write(bytes);
            return (object)new { name = f.m.Name, path = f.file, size = bytes.Length, sha256 = Hash(bytes) };
        }).ToArray();
    });
    internal static void Pack(string output, string name, IEnumerable<string> paths)
    {
        Fresh(output);
        if (!Path.GetExtension(output).Equals(".oap", StringComparison.OrdinalIgnoreCase)) throw new ModelError("Pack output must be .oap");
        var services = Model.Services();
        var modules = new List<IModule>();
        try {
            foreach (var path in paths) modules.Add(Path.GetExtension(path).Equals(".xif", StringComparison.OrdinalIgnoreCase)
                ? (IModule)Reflect.Call(services, "LoadExtension", File.ReadAllBytes(path))! : Model.Load(path));
            if (modules.Count == 0) throw new ModelError("Pack requires at least one module");
            if (modules.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != modules.Count || modules.Select(m => m.Key.ToString()).Distinct().Count() != modules.Count)
                throw new ModelError("Duplicate module name or key");
            var kind = modules.OfType<IESpace>().FirstOrDefault()?.Kind ?? SegmentationKind.Service;
            using var app = (IApplication)Reflect.Call(services, "CreateApplication", kind, name, "", "", services.NewKey())!;
            if (app.Name != name) throw new ModelError($"Application renamed to {app.Name}");
            foreach (var module in modules) Reflect.Call(app, "Add", module);
            var bytes = (byte[])Reflect.Call(app, "Save")!;
            using var stream = new FileStream(output, FileMode.CreateNew);
            stream.Write(bytes);
        } finally { foreach (var module in modules) module.Dispose(); }
    }
    public static int Run(List<string> args) => Guard(() => {
        if (args.Count == 3 && args[1] == "list") return Cli.Emit(List(args[2]));
        if (args.Count == 4 && args[1] == "extract") return Cli.Emit(new { extracted = Extract(args[2], args[3]) });
        if (args.Count >= 6 && args[1] == "pack" && args[3] == "--name") {
            Pack(args[2], args[4], args.Skip(5)); return Cli.Emit(new { saved = true, path = Path.GetFullPath(args[2]) });
        }
        throw new CliError("Usage: oml app list <package> | extract <package> <outdir> | pack <out.oap> --name <Name> <module>...", 2);
    });
    internal static int Guard(Func<int> action)
    {
        try { return action(); }
        catch (Exception e) when (e is not CliError) { Cli.Emit(new { saved = false, problems = new[] { (e.InnerException ?? e).Message } }); return 1; }
    }
}
