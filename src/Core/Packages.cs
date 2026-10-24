using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
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

    // ---- fast manifest path (no model) ----

    static bool IsPackage(string path) => Path.GetExtension(path).Equals(".oap", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".osp", StringComparison.OrdinalIgnoreCase);

    // The pack parsed from its manifest, or null when there is no readable manifest (then the model path is used).
    static PackageManifest.Pack? ManifestOf(string path) => IsPackage(path) ? PackageManifest.Read(path) : null;

    static object FastList(ZipArchive zip, PackageManifest.Pack pack)
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
            if (!e.FullName.EndsWith(PackageManifest.ManifestEntry, StringComparison.OrdinalIgnoreCase))
                sizes[Path.GetFileName(e.FullName)] = e.Length;
        var modules = pack.AllModules
            .GroupBy(mm => mm.Name + "\u0000" + mm.Kind)
            .Select(g => g.First())
            .Select(mm => new
            {
                name = mm.Name, key = ModuleHeader.NormalizeKey(mm.UID), kind = mm.Kind,
                size = sizes.GetValueOrDefault(mm.Name + (mm.Kind == "Extension" ? ".xif" : ".oml")),
            }).ToArray();
        var applications = pack.Apps.Select(a => new
        {
            name = a.Name, key = ModuleHeader.NormalizeKey(a.Key),
            modules = a.Modules.Select(mm => new { name = mm.Name, key = ModuleHeader.NormalizeKey(mm.UID), kind = mm.Kind }).ToArray(),
            dependencies = a.Dependencies.Select(d => new { name = d.Name, key = ModuleHeader.NormalizeKey(d.Key), kind = "application" }).ToArray(),
        }).ToArray();
        return new { applications, modules };
    }

    // Null when a module of the manifest has no file in the archive: the model path then extracts, so none is left out.
    static object[]? FastExtract(ZipArchive zip, PackageManifest.Pack pack, string output)
    {
        output = Path.GetFullPath(output);
        var wanted = new List<(string name, string ext, string entry)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mm in pack.AllModules)
        {
            var key = ModuleHeader.NormalizeKey(mm.UID);
            if (key.Length > 0 && !seen.Add(key)) continue;
            var ext = mm.Kind == "Extension" ? ".xif" : ".oml";
            var entry = zip.Entries.FirstOrDefault(e =>
                Path.GetFileName(e.FullName).Equals(mm.Name + ext, StringComparison.OrdinalIgnoreCase))?.FullName;
            if (entry is null) return null;
            wanted.Add((mm.Name, ext, entry));
        }
        if (wanted.Count == 0)
            foreach (var e in zip.Entries)
                if (!e.FullName.EndsWith(PackageManifest.ManifestEntry, StringComparison.OrdinalIgnoreCase))
                    wanted.Add((Path.GetFileNameWithoutExtension(e.FullName), Path.GetExtension(e.FullName), e.FullName));
        if (wanted.Select(w => w.name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != wanted.Count)
            throw new ModelError("Duplicate module filenames in package");
        foreach (var w in wanted)
            if (string.IsNullOrWhiteSpace(w.name) || w.name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || w.name is "." or "..")
                throw new ModelError($"Unsafe module filename: {w.name}");
        foreach (var w in wanted) Fresh(Path.Combine(output, w.name + w.ext));
        Directory.CreateDirectory(output);
        var files = new List<object>();
        foreach (var w in wanted)
        {
            var entry = zip.GetEntry(w.entry)!;
            using var stream = entry.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            var file = Path.Combine(output, w.name + w.ext);
            using (var fileStream = new FileStream(file, FileMode.CreateNew)) fileStream.Write(bytes);
            files.Add((object)new { name = w.name, path = file, size = bytes.Length, sha256 = Hash(bytes) });
        }
        return files.ToArray();
    }

    // ---- app check ----

    internal static object Check(string path, List<string> options)
    {
        var pack = ManifestOf(path) ?? throw new ModelError($"Cannot read the package manifest: {path}");
        var producers = new ProducerIndex(options);
        var checks = new List<object>();
        int missing = 0, typeChanged = 0, incompatible = 0, ok = 0;
        // --deep loads producer modules; cache them by path so several references to the same producer load it once.
        var loadedProducers = new Dictionary<string, IESpace>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var app in pack.Apps)
                foreach (var dep in app.Dependencies)
                    foreach (var rm in dep.Modules)
                    {
                        var key = ModuleHeader.NormalizeKey(rm.UID);
                        var producer = producers.Find(key);
                        var kind = "unknown";
                        string? detail = null;
                        if (producer is null)
                        {
                            kind = "missing producer"; missing++;
                            detail = $"module '{rm.UID}' is referenced by '{dep.Name}' but no producer module with that key was found";
                        }
                        else if (!TypesCompatible(rm.ESpaceType, producer.ESpaceType))
                        {
                            kind = "module type changed"; typeChanged++;
                            detail = $"the producer '{producer.Name}' is a {producer.ESpaceType} module, the reference expects {rm.ESpaceType}";
                        }
                        else if (producers.Deep && producer.Path is not null)
                        {
                            var missingElements = DeepMissing(rm, producer, loadedProducers);
                            if (missingElements is null) { kind = "not checked"; detail = $"cannot load the producer {producer.Path}"; }
                            else if (missingElements.Count > 0)
                            {
                                kind = "incompatible element"; incompatible++;
                                detail = "consumed elements not found in the producer: " + string.Join(", ", missingElements);
                            }
                            else { kind = "ok"; ok++; }
                        }
                        else
                        {
                            kind = "ok"; ok++;
                            if (producers.Deep) detail = "key and module type only: the producer is inside a package, so its elements were not compared";
                        }
                        checks.Add(new { application = app.Name, dependency = dep.Name, module = rm.UID, producer = producer?.Name, status = kind, detail });
                    }
        }
        finally
        {
            foreach (var m in loadedProducers.Values) m.Dispose();
        }
        return new
        {
            package = new { schema = pack.Schema, isApplicationPack = pack.IsApplicationPack, applications = pack.Apps.Count(), modules = pack.AllModules.Select(mm => mm.Name + "\u0000" + mm.Kind).Distinct().Count() },
            checks,
            summary = new { missingProducers = missing, typeMismatches = typeChanged, incompatibleElements = incompatible, ok },
            note = checks.Count == 0 ? "The package references no other application, so there is nothing to check." : null,
        };
    }

    // The segmentation kind strings in manifests are "WebResponsive", "Mobile", "Service"...; headers store the same enum names.
    static bool TypesCompatible(string reference, string producer)
    {
        if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(producer)) return true;
        return reference.Equals(producer, StringComparison.OrdinalIgnoreCase);
    }

    // The consumed public elements the producer lacks, or null when the producer cannot be loaded.
    static List<string>? DeepMissing(PackageManifest.ReferencedModule rm, ProducerIndex.Producer producer,
        Dictionary<string, IESpace> cache)
    {
        var result = new List<string>();
        if (!cache.TryGetValue(producer.Path!, out var module))
        {
            try { module = Model.Load(producer.Path!); }
            catch (Exception e) when (e is CliError or ModelError or BadImageFormatException) { return null; }
            cache[producer.Path!] = module;
        }
        var present = PublicKeys(module);
        foreach (var pe in rm.Api)
        {
            var key = ModuleHeader.NormalizeKey(pe.Key);
            if (key.Length > 0 && !present.Contains(key)) result.Add(pe.Name.Length > 0 ? $"{pe.Name} ({pe.Kind})" : key);
        }
        return result;
    }

    // The normal keys of the elements a module exposes publicly. Service Center's API list holds the shareable elements
    // marked Public; the named collections cover actions, entities, structures and screens/blocks.
    static HashSet<string> PublicKeys(IESpace m)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        void Add(object? o)
        {
            if (o is IObjectSignature s && Reflect.Get(s, "Public") is true) keys.Add(ModuleHeader.NormalizeKey(s.GlobalKey.ToString()));
        }
        foreach (var a in m.ServerActions) Add(a);
        foreach (var a in m.ClientActions) Add(a);
        foreach (var a in m.ServiceActions) Add(a);
        foreach (var e in m.Entities) Add(e);
        foreach (var s in m.Structures) Add(s);
        foreach (var f in m.MobileFlows) foreach (var n in f.Nodes) Add(n);
        foreach (var f in m.WebFlows) foreach (var n in f.Nodes) Add(n);
        return keys;
    }

    // Producers for app check: a folder of .oml/.xif files (headers are read, nothing is loaded) or another package.
    sealed class ProducerIndex
    {
        public bool Deep { get; }
        readonly Dictionary<string, Producer> byKey = new(StringComparer.Ordinal);

        public sealed class Producer
        {
            public required string Name;
            public required string ESpaceType;
            public string? Path;
            public string? Entry; // within a producer package
        }

        public ProducerIndex(List<string> options)
        {
            var folder = Option(options, "--producers");
            Deep = options.Contains("--deep");
            if (folder is null) throw new CliError("app check needs --producers <folder|package>: the producer modules to check against.", 2);
            if (Directory.Exists(folder)) AddFolder(folder);
            else if (File.Exists(folder) && IsPackage(folder)) AddPackage(folder);
            else if (folder is not null) throw new ModelError($"--producers must be a folder of .oml files or another .oap/.osp package: {folder}");
        }

        static string? Option(List<string> options, string name)
        {
            var i = options.IndexOf(name);
            return i >= 0 && i + 1 < options.Count ? options[i + 1] : null;
        }

        void AddFolder(string dir)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.oml", SearchOption.AllDirectories))
            {
                var info = ModuleHeader.Read(file);
                if (info is not null && info.Key.Length > 0)
                    byKey.TryAdd(ModuleHeader.NormalizeKey(info.Key), new Producer { Name = info.Name, ESpaceType = info.ESpaceType, Path = file });
            }
            foreach (var file in Directory.EnumerateFiles(dir, "*.xif", SearchOption.AllDirectories))
            {
                var key = XifKey(file);
                if (key is not null)
                    byKey.TryAdd(ModuleHeader.NormalizeKey(key), new Producer { Name = Path.GetFileNameWithoutExtension(file), ESpaceType = "Extension", Path = file });
            }
        }

        void AddPackage(string path)
        {
            var pack = ManifestOf(path);
            if (pack is null) return;
            foreach (var mm in pack.AllModules)
                byKey.TryAdd(ModuleHeader.NormalizeKey(mm.UID),
                    new Producer { Name = mm.Name, ESpaceType = mm.ESpaceType.Length > 0 ? mm.ESpaceType : mm.Kind, Entry = mm.Name });
        }

        public Producer? Find(string key) => key.Length > 0 && byKey.TryGetValue(key, out var p) ? p : null;
    }

    static string? XifKey(string path)
    {
        try
        {
            var doc = XDocument.Load(path);
            return doc.Root?.Attribute("Key")?.Value;
        }
        catch (System.Xml.XmlException) { return null; }
    }

    internal static object List(string path)
    {
        if (IsPackage(path))
        {
            using var zip = ZipFile.OpenRead(path);
            if (PackageManifest.Read(zip) is { } pack) return FastList(zip, pack);
        }
        return Read(path, (apps, modules) => (object)new { applications = apps.Select(App).ToArray(), modules = modules.Select(Module).ToArray() });
    }
    internal static object[] Extract(string path, string output)
    {
        if (IsPackage(path))
        {
            using var zip = ZipFile.OpenRead(path);
            if (PackageManifest.Read(zip) is { } pack && FastExtract(zip, pack, output) is { } extracted) return extracted;
        }
        return Read(path, (_, modules) =>
        {
            output = Path.GetFullPath(output);
            var files = modules.Select(m =>
            {
                if (string.IsNullOrWhiteSpace(m.Name) || m.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || m.Name is "." or "..")
                    throw new ModelError($"Unsafe module filename: {m.Name}");
                var file = Path.Combine(output, m.Name + (m.Kind.ToString() == "Extension" ? ".xif" : ".oml"));
                return (m, file);
            }).ToArray();
            if (files.Select(f => f.file).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length) throw new ModelError("Duplicate module filenames in package");
            foreach (var f in files) Fresh(f.file);
            Directory.CreateDirectory(output);
            return files.Select(f =>
            {
                var bytes = f.m.Content;
                using (var stream = new FileStream(f.file, FileMode.CreateNew)) stream.Write(bytes);
                return (object)new { name = f.m.Name, path = f.file, size = bytes.Length, sha256 = Hash(bytes) };
            }).ToArray();
        });
    }
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
        if (args.Count >= 4 && args[1] == "check") return Cli.Emit(Check(args[2], args.Skip(3).ToList()));
        if (args.Count >= 6 && args[1] == "pack" && args[3] == "--name") {
            Pack(args[2], args[4], args.Skip(5)); return Cli.Emit(new { saved = true, path = Path.GetFullPath(args[2]) });
        }
        throw new CliError("Usage: oml app list <package> | extract <package> <outdir> | check <package> --producers <folder|package> [--deep] | pack <out.oap> --name <Name> <module>...", 2);
    });
    internal static int Guard(Func<int> action)
    {
        try { return action(); }
        catch (Exception e) when (e is not CliError) { Cli.Emit(new { saved = false, problems = new[] { (e.InnerException ?? e).Message } }); return 1; }
    }
}
