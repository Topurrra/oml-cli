using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Data;
using OutSystems.Model.Enumerations;
using OutSystems.Model.Logic;
using OutSystems.Model.Types;

namespace OmlCli;

record AttrSpec(string Name, string Type = "Text", int? Length = null, int? Decimals = null,
    bool Mandatory = false, string? Default = null, string? Description = null, string? DeleteRule = null);
// Public is null when the spec leaves it out: a reused element keeps its own value.
record RecordSpec(string Name, List<AttrSpec>? Attributes = null, bool? Public = null, string? Description = null);
record Spec(List<EntitySpec>? Entities = null, List<RecordSpec>? Structures = null, List<string>? Exceptions = null,
    List<ActionSpec>? ServerActions = null, List<ActionSpec>? ServiceActions = null, List<ActionSpec>? ClientActions = null,
    List<ScreenSpec>? Blocks = null, List<ScreenSpec>? Screens = null, List<JsonElement>? Edits = null,
    List<RestSpec>? RestClients = null, List<RestSpec>? RestServices = null,
    List<JsonElement>? Timers = null,
    List<JsonElement>? Roles = null,
    List<JsonElement>? Processes = null,
    List<JsonElement>? SiteProperties = null,
    List<ReferenceSpec>? References = null, List<StaticEntitySpec>? StaticEntities = null,
    Dictionary<string, string>? ReferenceAliases = null,
    bool RemoveUnusedDependencies = false,
    bool KeepComments = false,
    JsonElement? Module = null,
    List<ModuleVariableSpec>? ClientVariables = null,
    List<ModuleVariableSpec>? SessionVariables = null,
    Dictionary<string, string>? SystemEvents = null,
    OnExceptionSpec? OnException = null,
    string? GlobalExceptionHandler = null,
    List<CopySpec>? Copy = null,
    List<string>? Locales = null, TranslationImportSpec? Translations = null, List<TranslationSpec>? Translate = null,
    List<ContentFileSpec>? Images = null, List<ContentFileSpec>? Scripts = null,
    List<ThemeSpec>? Themes = null, List<ContentFileSpec>? Resources = null,
    List<FolderSpec>? Folders = null, List<string>? TidyFlows = null,
    List<EmailSpec>? Emails = null);

/// `--describe` names and `--describe-created` for `oml apply`: the report then carries describe output.
record ApplyOptions(List<string> Describe, bool DescribeCreated);

static class Apply
{
    const int DescribeCreatedCap = 10;
    static readonly string[] DescribableKinds = ["entity", "structure", "static entity", "serverAction", "serviceAction", "clientAction", "screen", "block"];
    static readonly string[] PartKinds = ["aggregate", "dataAction", "screenAction"];

    public static int Run(string source, IReadOnlyList<string> specPaths, string output, ApplyOptions? options = null)
    {
        var (code, report) = Execute(source, specPaths, output, options);
        Cli.Emit(report);
        return code;
    }

    public static (int code, object report) Execute(string source, string specPath, string output) =>
        Execute(source, [specPath], output, null);

    public static (int code, object report) Execute(string source, IReadOnlyList<string> specPaths, string output, ApplyOptions? options)
    {
        source = Path.GetFullPath(source);
        output = Path.GetFullPath(output);
        if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
            throw new CliError("Output must differ from source; install with 'oml install' after review.", 2);
        if (File.Exists(output)) throw new CliError($"Staged output already exists: {output}. Use a new output path, or delete that file first.", 2);

        using var specPhase = Timings.Phase("spec");
        var many = specPaths.Count > 1;
        var parts = specPaths.Select(p =>
        {
            try
            {
                return (file: p, node: System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(p),
                    documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }));
            }
            catch (JsonException e) { throw new CliError($"Invalid spec JSON{(many ? $" in {p}" : "")}: {e.Message}", 2); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new CliError($"Cannot read the spec: {e.Message}", 2); }
        }).ToList();
        foreach (var (file, node) in parts)
            if (JsonOutput.UnpairedSurrogatePath(node) is { } bad)
                return (1, new { saved = false, phase = "spec", problems = new[] { $"{(many ? Path.GetFileName(file) + ": " : "")}{bad}: {JsonOutput.UnpairedSurrogate}" } });
        // Per spec file: comments go from inline code first, then {"file": ...} values are read relative to that
        // file, so file text is stored as written.
        var keep = parts.Any(p => p.node is System.Text.Json.Nodes.JsonObject o && (o["keepComments"]?.GetValueKind() == JsonValueKind.True
                                  || o["KeepComments"]?.GetValueKind() == JsonValueKind.True));
        var commentsRemoved = keep ? 0 : parts.Sum(p => Minify.Spec(p.node));
        var pathWarnings = parts.SelectMany(p => SpecFiles.Rebase(p.node, p.file)).ToList();
        var fileProblems = parts.SelectMany(p => SpecFiles.Resolve(p.node, p.file)
            .Select(x => $"{(many ? Path.GetFileName(p.file) + ": " : "")}{x.path}: {x.message}")).ToList();
        if (fileProblems.Count > 0) return (1, new { saved = false, phase = "spec", problems = fileProblems });
        System.Text.Json.Nodes.JsonNode? json;
        try { json = many ? SpecMerge.Merge(parts.Select(p => (Path.GetFileName(p.file), p.node)).ToList()) : parts[0].node; }
        catch (SpecMerge.Conflict c) { return (1, new { saved = false, specs = specPaths, problems = new[] { c.Message } }); }

        if (SpecHelp.UnknownKeys(json) is { Count: > 0 } unknown) return (1, new { saved = false, phase = "spec", problems = unknown });
        Spec spec;
        try
        {
            spec = json?.Deserialize<Spec>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new CliError("Empty spec.", 2);
        }
        catch (JsonException e) { return (1, new { saved = false, phase = "spec", problems = new[] { SpecHelp.TypeProblem(e) } }); }
        specPhase?.Dispose();

        var m = Model.Take(source);
        // Every module this apply opens: the source and, after the scratch reload, the reloaded one. The source stays
        // open to the end, because the production rules name its elements in their messages.
        var opened = new List<IESpace> { m };
        try { return Execute(spec, json, m, source, output, commentsRemoved, many ? specPaths : null, options, opened, pathWarnings); }
        finally { foreach (var module in opened) Timings.Measure("close", module.Dispose); }
    }

    // Describes the named elements, then the created ones (capped), of the saved module.
    static (Dictionary<string, object>? described, object[]? errors, int? more) DescribeResults(IESpace saved, ApplyOptions? options, List<string> created)
    {
        if (options is null) return (null, null, null);
        var names = options.Describe.Distinct().ToList();
        int? more = null;
        if (options.DescribeCreated)
        {
            // A part added to an existing screen or block ("aggregate Home/GetMore") is described through its owner.
            var all = created.Select(c => DescribableKinds.FirstOrDefault(k => c.StartsWith(k + " ")) is { } k ? c[(k.Length + 1)..]
                    : PartKinds.FirstOrDefault(k => c.StartsWith(k + " ")) is { } part ? c[(part.Length + 1)..].Split('/')[0] : null)
                .OfType<string>().Except(names).Distinct().ToList();
            names.AddRange(all.Take(DescribeCreatedCap));
            if (all.Count > DescribeCreatedCap) more = all.Count - DescribeCreatedCap;
        }
        if (names.Count == 0) return (null, null, null);
        var results = Describe.Many(saved, names, 0, [], collectErrors: true);
        var described = names.Zip(results).Where(x => x.Second is not Describe.DescribeError).ToDictionary(x => x.First, x => x.Second);
        return (described, results.OfType<Describe.DescribeError>().Cast<object>().ToArray(), more);
    }

    // raw: the spec's JSON, to tell keys the spec sets on an existing element from record defaults (Reuse).
    static (int code, object report) Execute(Spec spec, System.Text.Json.Nodes.JsonNode? raw, IESpace m, string source, string output, int commentsRemoved, IReadOnlyList<string>? specs, ApplyOptions? options, List<IESpace> opened, List<string>? pathWarnings = null)
    {
        var rules = Timings.Measure("rules snapshot", () => new ProductionRules(m));
        var flowLayoutBefore = Timings.Measure("flow snapshot", () => FlowLayout.Capture(m));
        var services = Model.Services();
        var baseline = Model.Errors(m, "validate source");
        var baselineWarnings = Model.Warnings(m, "validate source");
        using var build = Timings.Phase("build");
        var created = new List<string>();
        var existing = new List<string>();
        var problems = new List<string>();
        var warnings = new List<string>(pathWarnings ?? []);
        var changed = new List<string>(); // UI settings replaced on existing elements (rule 3)
        var sheetsBefore = Timings.Measure("style sheet snapshot", () => StyleSheets(m));

        ModuleReferences.Add(m, spec.References, created, existing, problems);
        ModuleReferences.Alias(m, spec.ReferenceAliases, problems, rules);

        ModuleSettings.Apply(m, spec.Module, problems);
        Timings.Measure("content", () => Content.Before(m, spec, created, existing, changed, problems));
        var staticEntities = StaticEntities.Create(m, spec.StaticEntities, created, existing, problems);
        // Warnings oml itself raises; the report lists them in newWarnings, next to the model's, so they are explained too.
        var notices = new List<(string id, string owner, string message, int count)>();

        foreach (var name in spec.Exceptions ?? new())
            if (m.UserExceptions.Any(e => e.Name == name)) existing.Add($"exception {name}");
            else { m.CreateUserException(name, services.NewKey()); created.Add($"exception {name}"); }

        // Pass 1: every record type exists before any attribute is typed, so specs can reference each other.
        var entities = (spec.Entities ?? new()).Select(s =>
        {
            var e = m.Entities.SingleOrDefault(x => x.Name == s.Name);
            if (e is null)
            {
                e = m.CreateServerEntity(s.Name, services.NewKey());
                if (e.Name != s.Name) problems.Add($"entity {s.Name}: {NameClash.Taken(e, s.Name)}; the model renamed it {e.Name}");
                // This factory adds no Id; without one the entity has no Identifier type to reference.
                if (e.IdentifierAttribute is null)
                {
                    var id = e.CreateAttribute("Id", services.NewKey());
                    id.DataType = m.LongIntegerType; // as Service Studio and OutSystems examples do; the generic identifier type validates but is not a real column type
                    id.IsAutoNumber = AutoNumber.Yes;
                    id.IsMandatory = true;
                    e.IdentifierAttribute = id;
                }
                created.Add($"entity {s.Name}");
                return (s, e, isNew: true);
            }
            existing.Add($"entity {s.Name}");
            return (s, e, isNew: false);
        }).ToList();
        var structures = (spec.Structures ?? new()).Select(s =>
        {
            var st = m.Structures.SingleOrDefault(x => x.Name == s.Name);
            if (st is null)
            {
                st = m.CreateStructure(s.Name, services.NewKey());
                if (st.Name != s.Name) problems.Add($"structure {s.Name}: {NameClash.Taken(st, s.Name)}; the model renamed it {st.Name}");
                created.Add($"structure {s.Name}");
            }
            else existing.Add($"structure {s.Name}");
            return (s, st);
        }).ToList();

        // Pass 2: attributes. Existing attributes are never retyped: changing a published column can lose data.
        foreach (var (s, e, isNew) in entities)
        {
            if (s.Description is not null) e.Description = s.Description;
            if (s.Public is { } entityPublic) e.Public = entityPublic;
            foreach (var a in s.Attributes ?? new())
            {
                var old = e.Attributes.FirstOrDefault(x => x.Name == a.Name);
                // The Id seeded on a new entity takes the spec's shape: nothing is published yet.
                if (old is not null && isNew && a.Name == "Id" && ReferenceEquals(old, e.IdentifierAttribute))
                {
                    if (Model.Resolve(m, a.Type, problems, $"{s.Name}.Id") is not IBasicType idType)
                    { problems.Add($"{s.Name}.Id: an identifier needs a basic or Identifier type, got '{a.Type}'"); continue; }
                    old.DataType = idType;
                    SetCommon(old, a);
                    old.IsMandatory = true;
                    if (!ReferenceEquals(idType, m.IntegerType) && !ReferenceEquals(idType, m.LongIntegerType)) old.IsAutoNumber = AutoNumber.No;
                    created.Add($"attribute {s.Name}.Id");
                    continue;
                }
                if (old is not null)
                {
                    if (!isNew) Reuse.Attribute(m, old, a, Reuse.Item(Reuse.Item(raw, "entities", s.Name), "attributes", a.Name), $"{s.Name}.{a.Name}", problems);
                    existing.Add($"attribute {s.Name}.{a.Name}");
                    continue;
                }
                var resolved = Model.Resolve(m, a.Type, problems, $"{s.Name}.{a.Name}");
                if (resolved is null) continue;
                if (resolved is not IBasicType type)
                {
                    problems.Add($"{s.Name}.{a.Name}: entity attributes need a basic or Identifier type, got '{a.Type}'");
                    continue;
                }
                var attr = e.CreateAttribute(a.Name, services.NewKey());
                attr.DataType = type;
                if (type is IIdentifierType) attr.DeleteRule = Enum.Parse<DeleteRule>(a.DeleteRule ?? "Protect", ignoreCase: true);
                SetCommon(attr, a);
                created.Add($"attribute {s.Name}.{a.Name}");
            }
        }
        // Identifier, options and indexes need every attribute of the entity.
        foreach (var (s, e, isNew) in entities)
        {
            var uniqueIndexWarnings = new List<string>();
            EntityOptions.Apply(m, e, s, isNew, created, existing, problems, uniqueIndexWarnings);
            notices.AddRange(uniqueIndexWarnings.Select(w => ("UniqueIndexOnExistingEntity", s.Name, w, 1)));
        }
        foreach (var (s, st) in structures)
        {
            if (s.Description is not null) st.Description = s.Description;
            if (s.Public is { } structurePublic) st.Public = structurePublic;
            foreach (var a in s.Attributes ?? new())
            {
                if (st.Attributes.FirstOrDefault(x => x.Name == a.Name) is { } old)
                {
                    if (!rules.Existed(st)) { existing.Add($"attribute {s.Name}.{a.Name}"); continue; }
                    Reuse.Attribute(m, old, a, Reuse.Item(Reuse.Item(raw, "structures", s.Name), "attributes", a.Name), $"{s.Name}.{a.Name}", problems);
                    existing.Add($"attribute {s.Name}.{a.Name}");
                    continue;
                }
                if (Model.Resolve(m, a.Type, problems, $"{s.Name}.{a.Name}") is not { } type) continue;
                var attr = st.CreateAttribute(a.Name, services.NewKey());
                attr.DataType = type;
                SetCommon(attr, a);
                created.Add($"attribute {s.Name}.{a.Name}");
            }
        }

        StaticEntities.Populate(m, staticEntities, created, existing, problems, raw, rules);
        var platform = new Builder(m, services, created, existing, problems, rules) { Raw = raw };
        platform.PlatformData(spec);
        warnings.AddRange(platform.Warnings);
        changed.AddRange(platform.Changed);

        // Phase 2: logic. Entity CRUD actions (CreateOrder, GetOrder, ...) exist only after a save and reopen
        // (skill §8.2), so the data model is saved to a scratch file and reloaded before any flow is built.
        var logic = new[] { spec.ServerActions, spec.ServiceActions, spec.ClientActions }.Any(l => l is { Count: > 0 })
                    || spec.Blocks is { Count: > 0 } || spec.Screens is { Count: > 0 } || spec.Edits is { Count: > 0 }
                    || spec.RestClients is { Count: > 0 } || spec.RestServices is { Count: > 0 }
                    || spec.Timers is { Count: > 0 } || spec.Processes is { Count: > 0 }
                    || spec.ClientVariables is not null || spec.SessionVariables is not null || spec.SystemEvents is not null
                    || spec.OnException is not null || spec.GlobalExceptionHandler is not null
                    || spec.Emails is { Count: > 0 };
        if (logic)
        {
            var dataErrors = Subtract(Model.Errors(m, "validate data"), baseline);
            if (problems.Count > 0 || dataErrors.Length > 0)
                return (1, new { saved = false, phase = "data model", problems, newErrors = Model.Grouped(dataErrors), created, existing });
            // A spec with only logic keys left the data phase nothing to do: the module is still as it was loaded,
            // so the reload would give the same model. On a big module that skips seconds.
            if (!LogicOnly(raw))
            {
                var scratch = Path.Combine(Path.GetTempPath(), $"oml-{Guid.NewGuid():N}.oml");
                try
                {
                    Model.Save(m, scratch, "save scratch");
                    m = Model.Load(scratch, "reload scratch");
                    opened.Add(m);
                }
                finally { File.Delete(scratch); }
            }

            using var logicPhase = Timings.Phase("build logic");
            var builder = new Builder(m, services, created, existing, problems, rules) { Raw = raw };
            builder.Rest(spec.RestClients, spec.RestServices);
            builder.Emails(spec.Emails);
            foreach (var a in spec.ServerActions ?? new()) builder.Action("serverAction", a);
            foreach (var a in spec.ServiceActions ?? new()) builder.Action("serviceAction", a);
            foreach (var a in spec.ClientActions ?? new()) builder.Action("clientAction", a);
            foreach (var b in spec.Blocks ?? new()) builder.Screen(b, block: true);
            foreach (var sc in Scaffolding.Expand(m, spec.Screens ?? new(), problems)) builder.Screen(sc, block: false);
            builder.PlatformLogic(spec);
            builder.LogicExtensions(spec);
            builder.Run(spec.Edits);
            warnings.AddRange(builder.Warnings);
            changed.AddRange(builder.Changed);
        }

        Timings.Measure("content", () => Content.After(m, spec, created, existing, problems));
        Timings.Measure("flow layout", () => FlowLayout.Apply(m, flowLayoutBefore, spec.TidyFlows, problems));

        // Service Studio's "Remove Unused Dependencies" (module context menu); runs last so new usages count.
        var removedDependencies = spec.RemoveUnusedDependencies ? RemoveUnused(m) : null;

        warnings.AddRange(ShrunkStyleSheets(sheetsBefore, m));
        Timings.Measure("rules check", () =>
        {
            rules.Validate(m, problems);
            changed.AddRange(rules.MadePublic.Where(c => !changed.Contains(c)));
            var newPublic = rules.NewPublicServerActions(m).ToList();
            // More than the 12 samples a report keeps: one message names them all, so the agent can ask about each.
            if (newPublic.Count > 12) notices.Add(("PublicServerAction", "", $"server actions {string.Join(", ", newPublic)} are public: other modules can call them (rule 7).", newPublic.Count));
            else notices.AddRange(newPublic.Select(a => ("PublicServerAction", a, $"server action {a} is public: other modules can call it (rule 7).", 1)));
            // Include copied SQL nodes and statements written by edits; unchanged existing SQL is left alone.
            foreach (var sql in ProductionRules.Walk(m).OfType<OutSystems.Model.Logic.Nodes.ISQLNode>().Where(s => !rules.UnchangedSql(s)))
                try { ProductionRules.Sql(sql, sql.Statement); }
                catch (ModelError e) { problems.Add(e.Message); }
        });

        build?.Dispose();
        var after = Model.Errors(m, "validate built");
        var newErrors = Subtract(after, baseline);
        if (problems.Count > 0 || newErrors.Length > 0)
        {
            return (1, new { saved = false, problems, warnings, newErrors = Model.Grouped(newErrors), created, changed, existing });
        }

        Model.Save(m, output);
        using var open = Model.Borrow(output, "reopen"); var reopened = open.Module; // an `oml run` keeps it for the next command
        var reopenNew = Subtract(Model.Errors(reopened, "validate reopened"), baseline);
        // Warnings do not block a save, but a type mismatch warning can still mean a wrong model: report them.
        var newWarnings = Subtract(Model.Warnings(reopened, "validate reopened"), baselineWarnings);
        var (described, describeErrors, describeMore) = Timings.Measure("describe", () => DescribeResults(reopened, options, created));
        return (reopenNew.Length == 0 ? 0 : 1, new
        {
            saved = true,
            specs,
            staged = output,
            commentsRemoved = commentsRemoved > 0 ? commentsRemoved : (int?)null, // characters stripped from styleSheet/javaScript
            created,
            changed,
            removedDependencies,
            existing,
            warnings,
            baselineErrors = baseline.Length,
            reopenNewErrors = Model.Grouped(reopenNew),
            newWarnings = Model.Grouped(newWarnings).Concat(Notices(notices)).ToArray(),
            describe = described,
            describeErrors,
            describeCreatedMore = describeMore,
            next = $"Run oml review {Arg(source)} {Arg(output)} --md <new .md> and show it; ask before oml install.",
        });
    }

    // Keys the data phase never reads (they are built after the scratch reload, or need no reload). Any other key,
    // even an empty list or one added to the spec later, keeps the reload.
    static readonly HashSet<string> LogicKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "serverActions", "serviceActions", "clientActions", "blocks", "screens", "edits", "restClients", "restServices",
        "timers", "processes", "clientVariables", "sessionVariables", "systemEvents", "onException", "globalExceptionHandler",
        "emails", "locales", "translations", "translate", "folders", "tidyFlows", "removeUnusedDependencies", "keepComments",
    };

    static bool LogicOnly(System.Text.Json.Nodes.JsonNode? raw) =>
        raw is System.Text.Json.Nodes.JsonObject spec && spec.All(p => LogicKeys.Contains(p.Key));

    // Same shape as Model.Grouped, so agents read oml's own warnings like the model's.
    // Non-empty style sheets of the source's screens, blocks, emails and themes, by key: a styleSheet in a spec replaces
    // the whole sheet, so a much smaller new sheet usually means the agent sent only its new rules.
    static Dictionary<string, (string label, string css)> StyleSheets(IESpace m) =>
        ProductionRules.Walk(m).Where(o => Reflect.Property(o, "StyleSheet") is not null)
            .Select(o => (o, css: Sheet(o)))
            .Where(x => !string.IsNullOrWhiteSpace(x.css))
            .GroupBy(x => x.o.GlobalKey.ToString()!).ToDictionary(g => g.Key, g => (SheetLabel(g.First().o), g.First().css!));

    static string Arg(string path) => path.Contains(' ') ? "\"" + path + "\"" : path;

    static string? Sheet(IObjectSignature o)
    {
        try { return Reflect.Get(o, "StyleSheet") as string; }
        catch (System.Reflection.TargetInvocationException) { return null; } // a platform part that cannot be read has no sheet to protect
    }

    static string SheetLabel(IObjectSignature o) => (o is OutSystems.Model.UI.IEmail ? "email" : Inspect.IsBlock(o) ? "block"
        : o is OutSystems.Model.UI.IScreenSignature ? "screen" : ElementPaths.Kind(o).Contains("Theme") ? "theme" : ElementPaths.Kind(o)) + " " + Model.NameOf(o);

    internal static IEnumerable<string> ShrunkStyleSheets(Dictionary<string, (string label, string css)> before, IESpace m)
    {
        foreach (var o in ProductionRules.Walk(m))
        {
            if (!before.TryGetValue(o.GlobalKey.ToString()!, out var old) || Sheet(o) is not string now || Reuse.Css(now) == Reuse.Css(old.css)) continue;
            if (ShrinkNote(old.css, now) is { } note)
                yield return $"styleSheet of {old.label} replaced: {old.css.Length} -> {now.Length} characters{note}; the whole sheet is replaced, not merged. "
                    + $"Read the current one with 'oml describe <module> {Model.NameOf(o)} --css' and edit it.";
        }
    }

    // Null when the new sheet is at least half the old one and keeps every selector it had.
    internal static string? ShrinkNote(string old, string now)
    {
        var gone = Selectors(old).Except(Selectors(now)).ToList();
        if (Reuse.Css(now).Length * 2 >= Reuse.Css(old).Length && gone.Count == 0) return null;
        return gone.Count == 0 ? "" : $", {gone.Count} selector{(gone.Count == 1 ? "" : "s")} gone ({string.Join(", ", gone.Take(5))}{(gone.Count > 5 ? ", ..." : "")})";
    }

    // The selectors of each rule (not @media and other at-rule headers), as Reuse.Css normalizes them.
    static HashSet<string> Selectors(string css)
    {
        var text = Reuse.Css(css);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is ';' or '}') start = i + 1;
            else if (text[i] == '{')
            {
                var head = text[start..i].Trim();
                if (head.Length > 0 && head[0] != '@') foreach (var s in head.Split(',')) found.Add(s.Trim());
                start = i + 1;
            }
        }
        return found;
    }

    static IEnumerable<object> Notices(List<(string id, string owner, string message, int count)> notices) =>
        notices.GroupBy(n => n.id).Select(g => (object)new
        {
            id = g.Key, count = g.Sum(n => n.count), hint = WarningTable.Hint(g.Key), // rows in warnings.md, so `oml warn` finds them too
            samples = g.Take(12).Select(n => new { message = n.message, detail = (string?)null, owner = n.owner == "" ? null : n.owner }),
        });

    internal static void SetCommon(IAttribute attr, AttrSpec a)
    {
        if (a.Length is { } length) attr.Length = length;
        if (a.Decimals is { } decimals) attr.Decimals = decimals;
        attr.IsMandatory = a.Mandatory;
        if (a.Default is not null) attr.SetDefaultValue(a.Default);
        if (a.Description is not null) attr.Description = a.Description;
    }

    // Reports what went, per producer module: whole modules, or single elements of modules still referenced.
    static object RemoveUnused(IESpace m)
    {
        var own = ((OutSystems.Model.Applications.IModule)m).Name;
        List<(string module, string name)> Referenced() => Refs.All(m).Where(c => c.owner != own)
            .Select(c => (c.owner, Model.NameOf(c.element) ?? "")).Distinct().ToList();
        var before = Referenced();
        var modulesBefore = m.References.Select(r => r.Name).ToList();
        m.RemoveUnusedDependencies();
        var after = Referenced().ToHashSet();
        var modulesAfter = m.References.Select(r => r.Name).ToHashSet();
        return new
        {
            modules = modulesBefore.Where(n => !modulesAfter.Contains(n)).ToArray(),
            elements = before.Where(e => !after.Contains(e) && modulesAfter.Contains(e.module))
                .GroupBy(e => e.module).ToDictionary(g => g.Key, g => g.Select(e => e.name).OrderBy(n => n).ToArray()),
        };
    }

    // Multiset difference keyed by (Id, Message, Detail): pre-existing errors are never blamed on this change, and the
    // samples of a new one show the new occurrence (a warning id and message can repeat with another element in Detail).
    internal static IValidationMessage[] Subtract(IValidationMessage[] after, IValidationMessage[] baseline)
    {
        var remaining = baseline.GroupBy(e => (e.Id, e.Message, e.Detail)).ToDictionary(g => g.Key, g => g.Count());
        return after.Where(e =>
        {
            var key = (e.Id, e.Message, e.Detail);
            if (remaining.GetValueOrDefault(key) == 0) return true;
            remaining[key]--;
            return false;
        }).ToArray();
    }
}
