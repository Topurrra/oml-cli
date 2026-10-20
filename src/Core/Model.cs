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

    public static IModelServices Services()
    {
        if (services is not null) return services;

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

    public static IESpace Load(string path)
    {
        if (!File.Exists(path)) throw new CliError($"File not found: {path}", 2);
        return Services().LoadESpace(File.ReadAllBytes(path), null, null, null);
    }

    // `oml run` keeps the modules it only reads, keyed by full path, file write time and length, so the next
    // command on the same file skips the load. A file that changed on disk (an `apply` output is a new file) reloads.
    // Code that changes a module must use Load, never Borrow.
    public static bool ShareModules;
    const int SharedKeep = 8;
    static readonly List<(string path, long ticks, long length, IESpace module)> shared = new();

    public static Borrowed Borrow(string path) => new(LoadShared(path));

    public static IESpace LoadShared(string path)
    {
        if (!ShareModules) return Load(path);
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
        var module = Load(path);
        shared.Add((info.FullName, info.LastWriteTimeUtc.Ticks, info.Length, module));
        return module;
    }

    // A shared module stays open for the next command; any other module is closed now.
    public static void Release(IESpace module)
    {
        if (!shared.Any(s => ReferenceEquals(s.module, module))) module.Dispose();
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

    public static void Save(IESpace module, string path) => ((IModule)module).Save(path, false);

    public static IValidationMessage[] Errors(IESpace module) =>
        ((IObjectSignature)module).GetValidationMessages(true)
            .Where(m => m.Type == ValidationMessageType.Error).ToArray();

    public static IValidationMessage[] Warnings(IESpace module) =>
        ((IObjectSignature)module).GetValidationMessages(true)
            .Where(m => m.Type == ValidationMessageType.Warning).ToArray();

    public static object[] Grouped(IEnumerable<IValidationMessage> errors) =>
        errors.GroupBy(e => e.Id)
            .Select(g => (object)new { id = g.Key, count = g.Count(), samples = g.Take(12).Select(e => new { message = e.Message, detail = e.Detail, owner = OwnerName(e.Owner) }) })
            .ToArray();

    static string? OwnerName(IObjectSignature? o)
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
        var interfaces = o.GetType().GetInterfaces().Where(i => i.Namespace is { } ns
            && (ns.StartsWith("OutSystems.Model") || ns.StartsWith("ServiceStudio.Plugin"))).ToList();
        foreach (var property in new[] { "Name", "Label" })
            foreach (var p in interfaces.Select(i => i.GetProperty(property)).Where(p => p?.PropertyType == typeof(string)))
            {
                try { if (p!.GetValue(o) is string { Length: > 0 } name) return name; }
                catch (TargetInvocationException) { }
            }
        return o is OutSystems.Model.Logic.Nodes.IStartNode ? "Start" : o is OutSystems.Model.Logic.Nodes.IEndNode ? "End" : null;
    }

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
