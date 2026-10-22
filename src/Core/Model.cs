using System.Reflection;
using System.Runtime.Loader;
using OutSystems.Model;
using OutSystems.Model.Applications;
using OutSystems.Model.Data;
using OutSystems.Model.Types;
using ServiceStudio.PluginAPI;

namespace OmlCli;

static class Model
{
    public static string ServiceStudioDir = "";

    public static string InstalledBuild() =>
        File.ReadAllText(Path.Combine(ServiceStudioDir, "Version.txt")).Split(' ', '\r', '\n')[0];

    static IModelServices? services;

    public static IModelServices Services() => services ?? Timings.Measure("serviceStudioInit", InitServices);

    static IModelServices InitServices()
    {
        // Load through our own context so each name resolves from the installed Service Studio.
        var context = AssemblyLoadContext.GetLoadContext(typeof(Model).Assembly)!;
        foreach (var name in new[] { "ServiceStudio.BuiltInBehaviors", "ServiceStudio.Presenter",
                     "OutSystems.Model.Implementation.Applications", "OutSystems.Model.Implementation.Keys",
                     "OutSystems.Model.Implementation.Extensions" })
            context.LoadFromAssemblyName(new AssemblyName(name));

        // Init logs before any default logger exists, so register no-op providers under the default id first.
        var providers = OutSystems.Model.DependenciesProviders.Providers.Instance;
        var defaultId = typeof(OutSystems.Model.DependenciesProviders.Providers).GetMethod("Get")!.GetParameters()[0].DefaultValue as string;
        providers.Register((OutSystems.Model.DependenciesProviders.ILoggersProvider)NoOp.For(typeof(OutSystems.Model.DependenciesProviders.ILoggersProvider)), defaultId!);
        providers.Register((OutSystems.Model.DependenciesProviders.ITracerProvider)NoOp.For(typeof(OutSystems.Model.DependenciesProviders.ITracerProvider)), defaultId!);

        var framework = context.LoadFromAssemblyName(new AssemblyName("ServiceStudio.Framework"));
        framework.GetType("ServiceStudio.Model.API.V1.ModelAPIRuntimeImplementation", throwOnError: true)!
            .GetMethod("Init", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { new Configuration { DeveloperId = "oml-cli" } });

        services = PluginProvider.ModelServices ?? throw new CliError("ModelServices was not initialized.", 2);

        // The headless runtime registers no plugins: load the Reactive widget and integration plugins ourselves.
        var plugins = new[] { "Widgets", "NRWidgets", "REST", "RESTService", "SOAP" }
            .Where(p => File.Exists(Path.Combine(ServiceStudioDir, "Plugins", "ServiceStudio", $"ServiceStudio.Plugin.{p}.dll")))
            .Select(p => context.LoadFromAssemblyName(new AssemblyName($"ServiceStudio.Plugin.{p}"))).ToArray();
        using (context.EnterContextualReflection())
        {
            // Each plugin widget class registers its concrete type through a nested Kind singleton when that
            // class is initialised; Service Studio's start-up does this, headless nothing does.
            foreach (var kind in plugins.SelectMany(LoadableTypes).Where(t => t.Name == "Kind" && t.DeclaringType is not null))
            {
                try { _ = kind.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                          ?? kind.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null); }
                catch (Exception e) { Console.Error.WriteLine($"skipped widget kind {kind.DeclaringType!.Name}: {(e.InnerException ?? e).Message}"); }
            }
            // Service Studio discovers descriptors at start-up; headless nothing does, so register them here.
            foreach (var type in plugins.SelectMany(LoadableTypes)
                         .Where(t => !t.IsAbstract && typeof(ServiceStudio.PluginAPI.Descriptors.CustomObjectDescriptor).IsAssignableFrom(t)
                                     && t.GetConstructor(Type.EmptyTypes) is not null))
            {
                try
                {
                    // A descriptor's constructor registers it; register explicitly only if it did not.
                    var descriptor = (ServiceStudio.PluginAPI.Descriptors.CustomObjectDescriptor)Activator.CreateInstance(type)!;
                    if (PluginProvider.Implementations.All(d => d.GetType() != type)) PluginProvider.RegisterImplementation(descriptor);
                }
                catch (TargetInvocationException e) when (e.InnerException is ArgumentException) { } // same key shipped by two plugins
                catch (Exception e) { Console.Error.WriteLine($"skipped widget descriptor {type.Name}: {(e.InnerException ?? e).Message}"); }
            }
        }
        return services;
    }

    static IEnumerable<Type> LoadableTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.OfType<Type>(); }
    }

    // phase: the --timings name for this load.
    public static IESpace Load(string path, string phase = "load")
    {
        if (!File.Exists(path)) throw new CliError($"File not found: {path}", 2);
        return Timings.Measure(phase, () => Load(File.ReadAllBytes(path), phase, path));
    }

    // path: the file the bytes came from, for the error when they are not a module.
    public static IESpace Load(byte[] bytes, string phase = "load", string? path = null)
    {
        try { return Timings.Measure(phase, () => Services().LoadESpace(bytes, null, null, null)); }
        catch (InvalidDataException e) { throw new CliError($"Cannot read {path ?? "the module"}: it is not a valid .oml module ({e.Message}).", 2); }
    }

    // `oml run` keeps the modules it only reads, keyed by full path, file write time and length, so the next
    // command on the same file skips the load. A file that changed on disk (an `apply` output is a new file) reloads.
    // Code that changes a module must use Load or Take, never Borrow.
    public static bool ShareModules;
    const int SharedKeep = 8;
    // sha256: of the bytes the module was loaded from; Take checks it before it hands the module to a change.
    static readonly List<(string path, long ticks, long length, string sha256, IESpace module)> shared = new();

    public static Borrowed Borrow(string path, string phase = "load") => new(LoadShared(path, phase));

    public static IESpace LoadShared(string path, string phase = "load")
    {
        if (!ShareModules) return Load(path, phase);
        var info = new FileInfo(path);
        if (!info.Exists) return Load(path); // throws the usual "File not found"
        var i = shared.FindIndex(s => string.Equals(s.path, info.FullName, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
        {
            var hit = shared[i];
            if (hit.ticks == info.LastWriteTimeUtc.Ticks && hit.length == info.Length) return hit.module;
            hit.module.Dispose();
            shared.RemoveAt(i);
        }
        var bytes = Timings.Measure(phase, () => File.ReadAllBytes(info.FullName));
        var module = Load(bytes, phase, info.FullName);
        shared.Add((info.FullName, info.LastWriteTimeUtc.Ticks, info.Length, Sha256(bytes), module));
        return module;
    }

    // For a command that changes the module (apply). In an `oml run`, an open shared copy that still matches the
    // file (write time, length and SHA-256 of its bytes) is handed over and leaves the cache, so no later command
    // sees the changes and a later read loads the file again. The commands that share modules only read them, so
    // the copy is the same as a fresh load.
    public static IESpace Take(string path, string phase = "load")
    {
        var info = new FileInfo(path);
        var i = ShareModules && info.Exists
            ? shared.FindIndex(s => string.Equals(s.path, info.FullName, StringComparison.OrdinalIgnoreCase)
                                    && s.ticks == info.LastWriteTimeUtc.Ticks && s.length == info.Length)
            : -1;
        if (i < 0) return Load(path, phase);
        if (Sha256(File.ReadAllBytes(info.FullName)) != shared[i].sha256)
        {
            // Same write time and length, other bytes: the open copy is stale for readers too.
            shared[i].module.Dispose();
            shared.RemoveAt(i);
            return Load(path, phase);
        }
        var module = shared[i].module;
        shared.RemoveAt(i);
        return module;
    }

    static string Sha256(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    // A shared module stays open for the next command; any other module is closed now.
    public static void Release(IESpace module)
    {
        if (!shared.Any(s => ReferenceEquals(s.module, module))) Timings.Measure("close", module.Dispose);
    }

    // Between commands of one `oml run`: close the oldest shared modules beyond the keep limit.
    public static void TrimShared()
    {
        while (shared.Count > SharedKeep) { shared[0].module.Dispose(); shared.RemoveAt(0); }
    }

    public static void EndSharing()
    {
        ShareModules = false;
        foreach (var s in shared) s.module.Dispose();
        shared.Clear();
    }

    public static void Save(IESpace module, string path, string phase = "save") =>
        Timings.Measure(phase, () => ((IModule)module).Save(path, false));

    // The only overload in OutSystems.Model.V1 is GetValidationMessages(bool includeMessagesFromDescendants):
    // true is the whole module tree, which is what Service Studio's own getValidationMessages tool lists.
    public static IValidationMessage[] All(IESpace module, string phase = "validate") =>
        Timings.Measure(phase, () => ((IObjectSignature)module).GetValidationMessages(true).ToArray());

    public static IValidationMessage[] Errors(IESpace module, string phase = "validate") =>
        All(module, phase).Where(m => m.Type == ValidationMessageType.Error).ToArray();

    public static IValidationMessage[] Warnings(IESpace module, string phase = "validate") =>
        All(module, phase).Where(m => m.Type == ValidationMessageType.Warning).ToArray();

    // inStudio (optional): whether Service Studio shows the same message; null entries are left out of the output.
    public static object[] Grouped(IEnumerable<IValidationMessage> errors, Func<IValidationMessage, bool?>? inStudio = null, string? module = null) =>
        errors.GroupBy(e => e.Id)
            // `hint` (the fix from warnings.md) only when known, so --pretty shows no "hint": null.
            .Select(g => WarningTable.Hint(g.Key) is { } hint
                ? (object)new { id = g.Key, count = g.Count(), hint, samples = Samples(g, inStudio, module) }
                : new { id = g.Key, count = g.Count(), samples = Samples(g, inStudio, module) })
            .ToArray();

    // Read now: apply closes the reopened module before its report is written, and a closed module has no owner names.
    static object[] Samples(IEnumerable<IValidationMessage> group, Func<IValidationMessage, bool?>? inStudio, string? module) =>
        group.Take(12).Select(e =>
        {
            // module and inStudio are left out when unknown, also with --pretty (an anonymous type would print null).
            var sample = new Dictionary<string, object?>();
            if ((ModuleName(e.Owner) ?? module) is { } name) sample["module"] = name;
            sample["owner"] = OwnerName(e.Owner); sample["message"] = e.Message; sample["detail"] = e.Detail;
            if (inStudio?.Invoke(e) is { } flag) sample["inStudio"] = flag;
            return (object)sample;
        }).ToArray();

    internal static string? ModuleName(IObjectSignature? o)
    {
        for (; o is not null && o is not IESpace; o = o.Parent) { }
        return (o as IModule)?.Name;
    }

    internal static string? OwnerName(IObjectSignature? o)
    {
        var parts = new List<string>();
        for (; o is not null && o is not IESpace; o = o.Parent)
            if (NameOf(o) is { } n) parts.Insert(0, n);
        return parts.Count == 0 ? null : string.Join("/", parts);
    }

    // Many V1 interfaces each declare their own Name; read through whichever one the object implements.
    // Flow nodes such as Assign carry a Label instead. Only string properties count, and a getter that
    // throws (some interfaces on referenced-module proxies) is skipped rather than failing the lookup.
    public static string? NameOf(object o)
    {
        foreach (var p in NameProperties.GetOrAdd(o.GetType(), _ => new[] { "Name", "Label" }
                     .SelectMany(property => Reflect.Interfaces(o).Select(i => i.GetProperty(property)))
                     .Where(p => p?.PropertyType == typeof(string)).ToArray()!))
        {
            try { if (p.GetValue(o) is string { Length: > 0 } name) return name; }
            catch (TargetInvocationException) { }
        }
        return o is OutSystems.Model.Logic.Nodes.IStartNode ? "Start" : o is OutSystems.Model.Logic.Nodes.IEndNode ? "End" : null;
    }

    // The Name and Label properties NameOf reads, per concrete type, in the order it reads them.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo[]> NameProperties = new();

    static readonly (string name, Func<IESpace, ITypeSignature> get)[] BasicTypes =
    {
        ("Text", m => m.TextType), ("Integer", m => m.IntegerType), ("Long Integer", m => m.LongIntegerType),
        ("Decimal", m => m.DecimalType), ("Boolean", m => m.BooleanType), ("Date Time", m => m.DateTimeType),
        ("Date", m => m.DateType), ("Time", m => m.TimeType), ("Phone Number", m => m.PhoneNumberType),
        ("Email", m => m.EmailType), ("Binary Data", m => m.BinaryDataType), ("Currency", m => m.CurrencyType),
        ("Object", m => m.ObjectType),
        ("Long Integer Identifier", m => m.LongIntegerIdentifierType), ("Integer Identifier", m => m.IntegerIdentifierType),
        ("Text Identifier", m => m.TextIdentifierType),
    };

    public static string Describe(IESpace module, ITypeSignature? type) => type switch
    {
        null => "(none)",
        IIdentifierType id => $"{id.Entity?.Name} Identifier",
        IListType list => "List of " + Describe(module, list.ElementType),
        IEntitySignature e => e.Name,
        IStructureSignature s => s.Name,
        _ => BasicTypes.FirstOrDefault(b => ReferenceEquals(b.get(module), type)).name ?? NameOf(type) ?? type.GetType().Name,
    };

    // Accepts the names Service Studio shows: "Text", "Long Integer", "Order Identifier", "List of OrderDto", "Order".
    public static ITypeSignature? Resolve(IESpace module, string spec)
    {
        spec = spec.Trim();
        if (spec.StartsWith("List of ", StringComparison.OrdinalIgnoreCase))
            return Resolve(module, spec[8..]) is { } element ? module.GetOrCreateListType(element) : null;

        var basic = BasicTypes.FirstOrDefault(b =>
            string.Equals(b.name.Replace(" ", ""), spec.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
        if (basic.name is not null) return basic.get(module);

        var isId = spec.EndsWith(" Identifier", StringComparison.OrdinalIgnoreCase);
        var name = isId ? spec[..^" Identifier".Length] : spec;
        // Same rules as element names: "Module.Entity" qualifies, ambiguity across references is refused.
        var records = Refs.All(module).Where(c => c.element is IEntitySignature || (!isId && c.element is IStructureSignature))
            .Select(c => (c.owner, element: (ITypeSignature)c.element)).ToList();
        var record = Refs.Pick(module, name, isId ? "entity" : "entity or structure", records, missingIsNull: true);
        // A freshly created entity has no IdentifierType yet; its Id attribute already carries the type.
        if (isId) return record is IEntitySignature entity ? (ITypeSignature?)entity.IdentifierType ?? entity.IdentifierAttribute?.DataType : null;
        return record;
    }

    // Resolve for callers that report problems instead of throwing: ambiguity and unknown types become a problem.
    public static ITypeSignature? Resolve(IESpace module, string spec, List<string> problems, string where)
    {
        try { return Resolve(module, spec) ?? Fail(UnknownType(spec)); }
        catch (ModelError e) { return Fail(e.Message); }
        ITypeSignature? Fail(string message) { problems.Add($"{where}: {message}"); return null; }
    }

    // An unknown type gets the same hint as an unknown element.
    public static string UnknownType(string type) => $"unknown type '{type}'. {Refs.ReferenceHint}";
}

class CliError(string message, int exitCode = 1) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

// A module that may be shared with the next command of an `oml run`; disposing it closes it only when it is not shared.
readonly struct Borrowed(IESpace module) : IDisposable
{
    public IESpace Module => module;
    public void Dispose() => Model.Release(module);
}
