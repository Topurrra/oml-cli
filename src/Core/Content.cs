using System.Collections;
using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Enumerations;

namespace OmlCli;

record CopySpec(string From, List<string> Elements, string? Into = null);
record ContentFileSpec(string Name, string File, bool? Public = null);
record ThemeSpec(string Name, string? StyleSheet = null, string? BaseTheme = null);
record FolderSpec(string Name, string Area, List<string> Elements);
record TranslationImportSpec(string Import);
record TranslationSpec(string Locale, string Text, string Translation);

static class Content
{
    internal static void Attempt(string label, List<string> problems, Action action)
    {
        try { action(); }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            while (e.InnerException is { } inner) e = inner;
            problems.Add($"{label}: {e.GetType().Name}: {e.Message}");
        }
    }

    static T NamedEnum<T>(string name) where T : struct, Enum =>
        Enum.GetNames<T>().FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } found
            ? Enum.Parse<T>(found) : throw new ModelError($"unknown {typeof(T).Name} '{name}'; supported: {(Enum.GetNames<T>().Length > 30 ? "see oml api " + typeof(T).Name : string.Join(", ", Enum.GetNames<T>()))}");

    static Culture CultureOf(string name) => NamedEnum<Culture>(name.Replace('-', '_'));
    internal static IEnumerable<object> Items(object owner, string property) =>
        Reflect.Get(owner, property) is IEnumerable items ? items.Cast<object>() : Enumerable.Empty<object>();
    static void Set(object owner, string property, object? value) =>
        (Reflect.Property(owner, property, true) ?? throw new ModelError($"no writable {property} on {owner.GetType().Name}")).SetValue(owner, value);

    // changed: UI settings replaced on existing elements (an existing theme's styleSheet).
    public static void Before(IESpace m, Spec spec, List<string> created, List<string> existing, List<string> changed, List<string> problems)
    {
        foreach (var copy in spec.Copy ?? new()) Attempt($"copy from {copy.From}", problems, () =>
        {
            using var source = Model.Load(Path.GetFullPath(copy.From));
            var selected = copy.Elements.Select(p => ElementPaths.Resolve(source, p) is var e && ElementPaths.Referenced(e)
                ? throw new ModelError($"'{p}' in {copy.From} is an element of a module that it references; copy it from that producer module") : e).ToArray();
            if (selected.Length == 0) throw new ModelError("elements must not be empty");
            foreach (var item in selected)
                if (Refs.All(m).FirstOrDefault(x => x.owner == ((OutSystems.Model.Applications.IModule)m).Name
                    && item.GetInterface().IsInstanceOfType(x.element) && Model.NameOf(x.element) == Model.NameOf(item)).element is IObjectSignature held)
                    throw new ModelError($"copy {Model.NameOf(item)}: the name is taken by {NameClash.Describe(held)}; oml does not replace or rename it. Rename or remove one of them first.");
            var missing = MissingReferences(source, m, selected);
            if (missing.Length > 0) throw new ModelError($"copy needs missing references: {string.Join(", ", missing)}; add them explicitly with references");
            var referencesBefore = m.References.SelectMany(r => ModuleReferences.Elements(r))
                .Select(e => e.GlobalKey).ToHashSet();
            IObjectSignature destination = m;
            if (copy.Into is not null || selected.Any(IsScreen))
            {
                var into = copy.Into ?? "MainFlow";
                destination = (IObjectSignature)(Items(m, WebUi.IsTraditional(m) ? "WebFlows" : "MobileFlows").FirstOrDefault(f => Model.NameOf(f) == into)
                    ?? Reflect.Call(m, WebUi.IsTraditional(m) ? "CreateWebFlow" : "CreateMobileFlow", into, Model.Services().NewKey())!);
                CheckName(destination, into, problems);
            }
            try
            {
                var pasted = new List<IObjectSignature>();
                foreach (var group in selected.GroupBy(IsScreen).OrderBy(g => g.Key))
                {
                    var payload = (byte[])Reflect.Call(source, "Serialize", (object)group.ToArray())!;
                    pasted.AddRange((IEnumerable<IObjectSignature>)Reflect.Call(m, "DeserializeInto", payload, group.Key ? destination : m)!);
                }

                foreach (var item in pasted) created.Add($"copied {ElementPaths.Kind(item)} {Model.NameOf(item)}");
                foreach (var group in selected.GroupBy(Model.NameOf))
                    if (pasted.Count(p => Model.NameOf(p) == group.Key) != group.Count())
                        problems.Add($"copy {group.Key}: model renamed or did not paste requested element; pasted: {string.Join(", ", pasted.Select(Model.NameOf))}");
                foreach (var reference in m.References)
                {
                    var added = ModuleReferences.Elements(reference).Where(e => !referencesBefore.Contains(e.GlobalKey)).ToList();
                    if (added.Count > 0) problems.Add($"copy needs missing references from {reference.Name}: {string.Join(", ", added.Select(Model.NameOf))}; add them explicitly with references");
                }
            }
            catch (Exception e) when (e is NotImplementedException || e.Message.Contains("Not implemented", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("IPP", StringComparison.OrdinalIgnoreCase))
            {
                throw new ModelError("Clipboard refused or requires a headless dialog (possible IPPErrorNotConnected / different activation codes). Use an authorized export and connect to the source infrastructure in Service Studio. " + e.Message);
            }
        });
        foreach (var group in new[] { (spec.Images, "Images", "image"), (spec.Scripts, "Scripts", "script"), (spec.Resources, "Resources", "resource") })
            foreach (var file in group.Item1 ?? new()) Attempt($"{group.Item3} {file.Name}", problems, () =>
            {
                if (Items(m, group.Item2).FirstOrDefault(x => Model.NameOf(x) == file.Name) is { } found)
                {
                    // As for other elements: public may be given; ProductionRules refuses public to private (rule 4).
                    if (file.Public is { } makePublic) Set(found, "Public", makePublic);
                    existing.Add($"{group.Item3} {file.Name}");
                    return;
                }
                object item = group.Item3 switch
                {
                    "image" => Reflect.Call(m, "CreateImage", System.IO.File.ReadAllBytes(file.File), Path.GetFileName(file.File), file.Name, Model.Services().NewKey())!,
                    "resource" => Reflect.Call(m, "CreateResource", System.IO.File.ReadAllBytes(file.File), file.Name, Model.Services().NewKey())!,
                    _ => Reflect.Call(m, "CreateScript", file.Name, Model.Services().NewKey())!,
                };
                if (group.Item3 == "script") Set(item, "JavaScript", System.IO.File.ReadAllText(file.File));
                if (file.Public is { } isPublic) Set(item, "Public", isPublic);
                CheckName(item, file.Name, problems);
                created.Add($"{group.Item3} {Model.NameOf(item)}");
            });
        foreach (var theme in spec.Themes ?? new()) Attempt($"theme {theme.Name}", problems, () =>
        {
            var web = WebUi.IsTraditional(m);
            var item = Items(m, web ? "WebThemes" : "MobileThemes").FirstOrDefault(x => Model.NameOf(x) == theme.Name);
            // A styleSheet is CSS text or the path of a file (relative to the spec's folder, or else the working folder).
            // A value that looks like a path but names no file would be stored as the CSS itself.
            if (theme.StyleSheet is { } given && !File.Exists(given) && given.IndexOfAny(['{', '}', ';', ' ', '\n']) < 0 && (given.EndsWith(".css", StringComparison.OrdinalIgnoreCase) || given.IndexOfAny(['/', '\\']) >= 0))
                throw new ModelError($"styleSheet '{given}' looks like a file path, but there is no such file in the spec's folder or the working folder; give CSS text, or {{ \"file\": \"relative/path.css\" }}");
            var css = theme.StyleSheet is null ? null : File.Exists(theme.StyleSheet) ? File.ReadAllText(theme.StyleSheet) : theme.StyleSheet;
            if (item is not null)
            {
                // A style sheet and a base theme are UI (rule 3): what the spec gives replaces the theme's; what it leaves out stays.
                if (css is not null && Reuse.Css(Reflect.Get(item, "StyleSheet") as string) != Reuse.Css(css)) { Set(item, "StyleSheet", css); changed.Add($"styleSheet of theme {theme.Name}"); }
                if (theme.BaseTheme is { } newBase && Reflect.Property(item, "BaseTheme", true) is { } baseProperty
                    && Refs.Find(m, newBase, baseProperty.PropertyType) is var target && !ReferenceEquals(target, baseProperty.GetValue(item)))
                { baseProperty.SetValue(item, target); changed.Add($"baseTheme of theme {theme.Name}"); }
                existing.Add($"theme {theme.Name}");
                return;
            }
            item = Reflect.Call(m, web ? "CreateWebTheme" : "CreateMobileTheme", theme.Name, Model.Services().NewKey())!;
            Set(item, "StyleSheet", css ?? "");
            if (theme.BaseTheme is { } parent)
            {
                var prop = Reflect.Property(item, "BaseTheme", true)!;
                prop.SetValue(item, Refs.Find(m, parent, prop.PropertyType));
            }
            CheckName(item, theme.Name, problems);
            created.Add($"theme {Model.NameOf(item)}");
        });
    }

    static bool IsScreen(IObjectSignature item) => item is OutSystems.Model.UI.IScreenSignature or OutSystems.Model.UI.IBlockSignature;

    // Clipboard drops unresolved producer pointers. Inspect the source's typed references before paste.
    static string[] MissingReferences(IESpace source, IESpace target, IEnumerable<IObjectSignature> selected)
    {
        var available = target.References.SelectMany(ModuleReferences.Elements).Select(e => e.GlobalKey).ToHashSet();
        var owners = new Dictionary<IGlobalKey, (IGlobalKey key, string name)>();
        foreach (var reference in source.References)
            foreach (var element in ModuleReferences.Elements(reference))
                foreach (var child in new[] { element }.Concat(ModuleReferences.Descendants(element)))
                    owners.TryAdd(child.GlobalKey, (element.GlobalKey, reference.Name + "." + Model.NameOf(element)));
        var missing = new HashSet<string>();
        var visitedReferences = new HashSet<IObjectSignature>();
        void Reference(object? value)
        {
            if (value is not IObjectSignature item || !visitedReferences.Add(item)) return;
            if (owners.TryGetValue(item.GlobalKey, out var owner) && !available.Contains(owner.key)) missing.Add(owner.name);
            if (item is OutSystems.Model.Types.IIdentifierType identifier) Reference(identifier.Entity);
            if (item is OutSystems.Model.Types.IListType list) Reference(list.ElementType);
        }
        foreach (var node in selected.SelectMany(s => new[] { s }.Concat(ModuleReferences.Descendants(s))).Distinct())
        {
            var contract = node.GetInterface();
            foreach (var property in contract.GetInterfaces().Append(contract)
                .Concat(node.GetType().GetInterfaces().Where(t => t.Namespace?.StartsWith("ServiceStudio.Plugin.") == true))
                .SelectMany(t => t.GetProperties()).Where(p => p.GetIndexParameters().Length == 0 && p.Name is not ("Parent" or "Children")))
            {
                if (typeof(IObjectSignature).IsAssignableFrom(property.PropertyType)) Reference(property.GetValue(node));
                else if (property.PropertyType.IsGenericType && typeof(IEnumerable).IsAssignableFrom(property.PropertyType)
                    && property.PropertyType.GetGenericArguments().Any(t => typeof(IObjectSignature).IsAssignableFrom(t))
                    && property.GetValue(node) is IEnumerable values)
                    foreach (var value in values) Reference(value);
            }
            if (node is IExpression expression && Reflect.Get(expression, "Root") is { } root)
            {
                var pending = new Stack<object>();
                var seen = new HashSet<object>();
                pending.Push(root);
                while (pending.TryPop(out var part))
                    if (seen.Add(part))
                    {
                        if (Reflect.Get(part, "Binding") is { } binding) Reference(Reflect.Get(binding, "Object"));
                        foreach (var child in Items(part, "Children")) pending.Push(child);
                    }
            }
        }
        return missing.OrderBy(x => x).ToArray();
    }

    static void CheckName(object item, string requested, List<string> problems)
    {
        if (Model.NameOf(item) != requested) problems.Add($"{requested}: {NameClash.Taken(item, requested)}; the model renamed it {Model.NameOf(item)}");
    }

    public static void After(IESpace m, Spec spec, List<string> created, List<string> existing, List<string> problems)
    {
        foreach (var name in spec.Locales ?? new()) Attempt($"locale {name}", problems, () =>
        {
            Reflect.Call(m, "GetOrCreateLocale", CultureOf(name));
            created.Add($"locale {name}");
        });
        if (spec.Translations is { } import) Attempt("translations import", problems, () => Reflect.Call(m, "LoadTextResources", Path.GetFullPath(import.Import)));
        foreach (var t in spec.Translate ?? new()) Attempt($"translate {t.Locale}: {t.Text}", problems, () =>
        {
            var culture = CultureOf(t.Locale);
            Reflect.Call(m, "GetOrCreateLocale", culture);
            Reflect.Call(m, "CreateOrUpdateTranslation", culture, t.Text, t.Translation);
            created.Add($"translation {t.Locale}: {t.Text}");
        });
        foreach (var folder in spec.Folders ?? new()) Attempt($"folder {folder.Area}/{folder.Name}", problems, () =>
        {
            var area = NamedEnum<ESpaceTreeFolder>(folder.Area.Equals("entities", StringComparison.OrdinalIgnoreCase) ? "ServerEntities" : folder.Area);
            var item = m.Folders.FirstOrDefault(f => Model.NameOf(f) == folder.Name && Equals(Reflect.Get(f, "ParentFolder"), area));
            if (item is null)
            {
                item = (IFolder)Reflect.Call(m, "CreateFolder", area, folder.Name, Model.Services().NewKey())!;
                CheckName(item, folder.Name, problems);
                created.Add($"folder {folder.Area}/{Model.NameOf(item)}");
            }
            else existing.Add($"folder {folder.Area}/{folder.Name}");
            foreach (var path in folder.Elements)
            {
                var element = ElementPaths.Resolve(m, path, write: true);
                var collection = area.ToString() switch { "ServerEntities" or "ClientEntities" => "Entities", "Themes" => WebUi.IsTraditional(m) ? "WebThemes" : "MobileThemes", var value => value };
                if (!Items(m, collection).Contains(element)) throw new ModelError($"'{path}' is a {Describe.Kind(element)}; a folder in {area} holds only those elements");
                Set(element, "Folder", item);
            }
        });
    }

    public static int Export(string source, string path, string? format)
    {
        var problems = new List<string>();
        Attempt("translations export", problems, () =>
        {
            if (File.Exists(path) || Directory.Exists(path)) throw new ModelError($"Output already exists: {path}");
            var name = format ?? (Path.GetExtension(path).Equals(".resx", StringComparison.OrdinalIgnoreCase) ? "ResX" : "Excel");
            if (name.Equals("xlsx", StringComparison.OrdinalIgnoreCase)) name = "Excel";
            var kind = NamedEnum<TextResourcesFileFormat>(name);
            // Both formats take an output folder. Stage it so a model refusal leaves no empty files.
            var scratch = Path.Combine(Path.GetTempPath(), "oml-translations-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                using var m = Model.Load(source);
                Reflect.Call(m, "SaveTextResources", scratch, kind);
                var files = Directory.GetFiles(scratch);
                if (files.Length == 0 || files.Any(f => new FileInfo(f).Length == 0)) throw new ModelError("API produced empty text resource files");
                if (kind == TextResourcesFileFormat.ResX) Directory.Move(scratch, Path.GetFullPath(path));
                else if (files.Length == 1) File.Move(files[0], Path.GetFullPath(path));
                else throw new ModelError("Excel export returned more than one file");
            }
            catch (ModelError e) when (e.Message.Contains("AbortCommandException") || e.Message.Contains("not implemented", StringComparison.OrdinalIgnoreCase))
            {
                throw new ModelError("SaveTextResources is unavailable in this headless build (11.55.92 ResX writer throws NotImplementedException; Excel also refuses). Use translate to store values or export in Service Studio. " + e.Message);
            }
            finally
            {
                if (Directory.Exists(scratch))
                    Attempt($"translations scratch cleanup {scratch}", problems, () => Directory.Delete(scratch, true));
            }
        });
        Cli.Emit(new { exported = problems.Count == 0, file = Path.GetFullPath(path), problems });
        return problems.Count == 0 ? 0 : 1;
    }

    // Adds the scripts the owner does not require yet; returns how many it added.
    public static int RequireScripts(IESpace m, object owner, List<string>? names)
    {
        if (names is null) return 0;
        var collection = Reflect.Get(owner, "RequiredScripts") ?? throw new ModelError("RequiredScripts is unavailable on this build");
        var added = 0;
        foreach (var name in names)
        {
            var script = Refs.Find(m, name, typeof(OutSystems.Model.UI.IScriptSignature));
            if (!((IEnumerable)collection).Cast<object>().Contains(script)) { Reflect.Call(collection, "Add", script); added++; }
        }
        return added;
    }

    public static object Inspect(IESpace m) => new
    {
        images = OmlCli.Inspect.ByName(Items(m, "Images")).Select(x => new { name = Model.NameOf(x), format = Reflect.Get(x, "OriginalFormat")?.ToString(), bytes = (Reflect.Get(x, "ImageContent") as byte[])?.Length }),
        scripts = OmlCli.Inspect.ByName(Items(m, "Scripts")).Select(x => new { name = Model.NameOf(x), javaScript = Reflect.Get(x, "JavaScript") }),
        resources = OmlCli.Inspect.ByName(Items(m, "Resources")).Select(x => new { name = Model.NameOf(x), isPublic = Reflect.Get(x, "Public"), bytes = (Reflect.Get(x, "ResourceContent") as byte[])?.Length }),
        themes = OmlCli.Inspect.ByName(Items(m, "MobileThemes").Concat(Items(m, "WebThemes"))).Select(x => new { name = Model.NameOf(x), styleSheet = Reflect.Get(x, "StyleSheet"), baseTheme = Reflect.Get(x, "BaseTheme") is { } b ? Model.NameOf(b) : null }),
        locales = Items(m, "Locales").Select(x => Reflect.Get(x, "Culture")?.ToString()?.Replace('_', '-')).Order(StringComparer.Ordinal),
        translations = m.TextResources.OrderBy(x => x.Id, StringComparer.Ordinal).Select(x => new { text = x.Id,
            values = x.Translations.Select(t => new { locale = t.Culture.ToString().Replace('_', '-'), translation = t.Value }).OrderBy(t => t.locale, StringComparer.Ordinal) }),
        folders = OmlCli.Inspect.ByName(m.Folders).Select(f => new { name = Model.NameOf(f), area = Reflect.Get(f, "ParentFolder")?.ToString(), elements = OmlCli.Inspect.ByName(Items(f, "Entries")).Select(Model.NameOf) }),
    };
}
