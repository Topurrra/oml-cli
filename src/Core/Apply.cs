using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Data;
using OutSystems.Model.Enumerations;
using OutSystems.Model.Logic;
using OutSystems.Model.Types;

namespace OmlCli;

record AttrSpec(string Name, string Type = "Text", int? Length = null, int? Decimals = null,
    bool Mandatory = false, string? Default = null, string? Description = null, string? DeleteRule = null);
record RecordSpec(string Name, List<AttrSpec>? Attributes = null, bool Public = false, string? Description = null);
record Spec(List<RecordSpec>? Entities = null, List<RecordSpec>? Structures = null, List<string>? Exceptions = null,
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
    List<FolderSpec>? Folders = null, List<string>? TidyFlows = null);

/// `--describe` names and `--describe-created` for `oml apply`: the report then carries describe output.
record ApplyOptions(List<string> Describe, bool DescribeCreated);

static class Apply
{
    const int DescribeCreatedCap = 10;
    static readonly string[] DescribableKinds = ["entity", "structure", "static entity", "serverAction", "serviceAction", "clientAction", "screen", "block"];

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

        var many = specPaths.Count > 1;
        var parts = specPaths.Select(p =>
        {
            try
            {
                return (file: p, node: System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(p),
                    documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }));
            }
            catch (JsonException e) { throw new CliError($"Invalid spec JSON{(many ? $" in {p}" : "")}: {e.Message}", 2); }
        }).ToList();
        System.Text.Json.Nodes.JsonNode? json;
        try { json = many ? SpecMerge.Merge(parts.Select(p => (Path.GetFileName(p.file), p.node)).ToList()) : parts[0].node; }
        catch (SpecMerge.Conflict c) { return (1, new { saved = false, specs = specPaths, problems = new[] { c.Message } }); }

        if (SpecHelp.UnknownKeys(json) is { Count: > 0 } unknown) return (1, new { saved = false, phase = "spec", problems = unknown });
        Spec spec;
        var commentsRemoved = 0;
        try
        {
            var keep = json?["keepComments"]?.GetValueKind() == JsonValueKind.True
                       || json?["KeepComments"]?.GetValueKind() == JsonValueKind.True;
            if (!keep) commentsRemoved = Minify.Spec(json);
            spec = json?.Deserialize<Spec>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new CliError("Empty spec.", 2);
        }
        catch (JsonException e) { return (1, new { saved = false, phase = "spec", problems = new[] { SpecHelp.TypeProblem(e) } }); }

        var m = Model.Load(source);
        try { return Execute(spec, m, output, commentsRemoved, many ? specPaths : null, options); }
        finally { m.Dispose(); }
    }

    // Describes the named elements, then the created ones (capped), of the saved module.
    static (Dictionary<string, object>? described, object[]? errors, int? more) DescribeResults(IESpace saved, ApplyOptions? options, List<string> created)
    {
        if (options is null) return (null, null, null);
        var names = options.Describe.Distinct().ToList();
        int? more = null;
        if (options.DescribeCreated)
        {
            var all = created.Select(c => DescribableKinds.FirstOrDefault(k => c.StartsWith(k + " ")) is { } k ? c[(k.Length + 1)..] : null)
                .OfType<string>().Except(names).Distinct().ToList();
            names.AddRange(all.Take(DescribeCreatedCap));
            if (all.Count > DescribeCreatedCap) more = all.Count - DescribeCreatedCap;
        }
        if (names.Count == 0) return (null, null, null);
        var results = Describe.Many(saved, names, 0, [], collectErrors: true);
        var described = names.Zip(results).Where(x => x.Second is not Describe.DescribeError).ToDictionary(x => x.First, x => x.Second);
        return (described, results.OfType<Describe.DescribeError>().Cast<object>().ToArray(), more);
    }

    static (int code, object report) Execute(Spec spec, IESpace m, string output, int commentsRemoved, IReadOnlyList<string>? specs, ApplyOptions? options)
    {
        var rules = new ProductionRules(m);
        var flowLayoutBefore = FlowLayout.Capture(m);
        var services = Model.Services();
        var baseline = Model.Errors(m);
        var baselineWarnings = Model.Warnings(m);
        var created = new List<string>();
        var existing = new List<string>();
        var problems = new List<string>();
        var warnings = new List<string>();

        ModuleReferences.Add(m, spec.References, created, existing, problems);
        ModuleReferences.Alias(m, spec.ReferenceAliases, problems, rules);

        ModuleSettings.Apply(m, spec.Module, problems);
        Content.Before(m, spec, created, existing, problems);
        var staticEntities = StaticEntities.Create(m, spec.StaticEntities, created, existing, problems);

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
                if (e.Name != s.Name) problems.Add($"entity {s.Name}: name taken; the model renamed it {e.Name}");
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
            }
            else existing.Add($"entity {s.Name}");
            return (s, e);
        }).ToList();
        var structures = (spec.Structures ?? new()).Select(s =>
        {
            var st = m.Structures.SingleOrDefault(x => x.Name == s.Name);
            if (st is null)
            {
                st = m.CreateStructure(s.Name, services.NewKey());
                if (st.Name != s.Name) problems.Add($"structure {s.Name}: name taken; the model renamed it {st.Name}");
                created.Add($"structure {s.Name}");
            }
            else existing.Add($"structure {s.Name}");
            return (s, st);
        }).ToList();

        // Pass 2: attributes. Existing attributes are never retyped: changing a published column can lose data.
        foreach (var (s, e) in entities)
        {
            if (s.Description is not null) e.Description = s.Description;
            e.Public = s.Public;
            foreach (var a in s.Attributes ?? new())
            {
                if (e.Attributes.Any(x => x.Name == a.Name)) { existing.Add($"attribute {s.Name}.{a.Name}"); continue; }
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
        foreach (var (s, st) in structures)
        {
            if (s.Description is not null) st.Description = s.Description;
            st.Public = s.Public;
            foreach (var a in s.Attributes ?? new())
            {
                if (st.Attributes.Any(x => x.Name == a.Name)) { existing.Add($"attribute {s.Name}.{a.Name}"); continue; }
                if (Model.Resolve(m, a.Type, problems, $"{s.Name}.{a.Name}") is not { } type) continue;
                var attr = st.CreateAttribute(a.Name, services.NewKey());
                attr.DataType = type;
                SetCommon(attr, a);
                created.Add($"attribute {s.Name}.{a.Name}");
            }
        }

        StaticEntities.Populate(m, staticEntities, created, existing, problems);
        new Builder(m, services, created, existing, problems, rules).PlatformData(spec);

        // Phase 2: logic. Entity CRUD actions (CreateOrder, GetOrder, ...) exist only after a save and reopen
        // (skill §8.2), so the data model is saved to a scratch file and reloaded before any flow is built.
        var logic = new[] { spec.ServerActions, spec.ServiceActions, spec.ClientActions }.Any(l => l is { Count: > 0 })
                    || spec.Blocks is { Count: > 0 } || spec.Screens is { Count: > 0 } || spec.Edits is { Count: > 0 }
                    || spec.RestClients is { Count: > 0 } || spec.RestServices is { Count: > 0 }
                    || spec.Timers is { Count: > 0 } || spec.Processes is { Count: > 0 }
                    || spec.ClientVariables is not null || spec.SessionVariables is not null || spec.SystemEvents is not null
                    || spec.OnException is not null || spec.GlobalExceptionHandler is not null;
        if (logic)
        {
            var dataErrors = Subtract(Model.Errors(m), baseline);
            if (problems.Count > 0 || dataErrors.Length > 0)
                return (1, new { saved = false, phase = "data model", problems, newErrors = Model.Grouped(dataErrors), created, existing });
            var scratch = Path.Combine(Path.GetTempPath(), $"oml-{Guid.NewGuid():N}.oml");
            try
            {
                Model.Save(m, scratch);
                m = Model.Load(scratch);
            }
            finally { File.Delete(scratch); }

            var builder = new Builder(m, services, created, existing, problems, rules);
            builder.Rest(spec.RestClients, spec.RestServices);
            foreach (var a in spec.ServerActions ?? new()) builder.Action("serverAction", a);
            foreach (var a in spec.ServiceActions ?? new()) builder.Action("serviceAction", a);
            foreach (var a in spec.ClientActions ?? new()) builder.Action("clientAction", a);
            foreach (var b in spec.Blocks ?? new()) builder.Screen(b, block: true);
            foreach (var sc in Scaffolding.Expand(m, spec.Screens ?? new(), problems)) builder.Screen(sc, block: false);
            builder.PlatformLogic(spec);
            builder.LogicExtensions(spec);
            builder.Run(spec.Edits);
            warnings.AddRange(builder.Warnings);
        }

        Content.After(m, spec, created, existing, problems);
        FlowLayout.Apply(m, flowLayoutBefore, spec.TidyFlows, problems);

        // Service Studio's "Remove Unused Dependencies" (module context menu); runs last so new usages count.
        var removedDependencies = spec.RemoveUnusedDependencies ? RemoveUnused(m) : null;

        rules.Validate(m, problems);
        // Include copied SQL nodes and statements written by edits; unchanged existing SQL is left alone.
        foreach (var sql in ProductionRules.Walk(m).OfType<OutSystems.Model.Logic.Nodes.ISQLNode>().Where(s => !rules.UnchangedSql(s)))
            try { ProductionRules.Sql(sql, sql.Statement); }
            catch (ModelError e) { problems.Add(e.Message); }

        var after = Model.Errors(m);
        var newErrors = Subtract(after, baseline);
        if (problems.Count > 0 || newErrors.Length > 0)
        {
            return (1, new { saved = false, problems, warnings, newErrors = Model.Grouped(newErrors), created, existing });
        }

        Model.Save(m, output);
        using var open = Model.Borrow(output); var reopened = open.Module; // an `oml run` keeps it for the next command
        var reopenNew = Subtract(Model.Errors(reopened), baseline);
        // Warnings do not block a save, but a type mismatch warning can still mean a wrong model: report them.
        var newWarnings = Subtract(Model.Warnings(reopened), baselineWarnings);
        var (described, describeErrors, describeMore) = DescribeResults(reopened, options, created);
        return (reopenNew.Length == 0 ? 0 : 1, new
        {
            saved = true,
            specs,
            staged = output,
            commentsRemoved = commentsRemoved > 0 ? commentsRemoved : (int?)null, // characters stripped from styleSheet/javaScript
            created,
            removedDependencies,
            existing,
            warnings,
            baselineErrors = baseline.Length,
            reopenNewErrors = Model.Grouped(reopenNew),
            newWarnings = Model.Grouped(newWarnings),
            describe = described,
            describeErrors,
            describeCreatedMore = describeMore,
            next = "Review, then 'oml install' the staged file over the source and publish it from Service Studio.",
        });
    }

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

    // Multiset difference keyed by (Id, Message): pre-existing errors are never blamed on this change.
    internal static IValidationMessage[] Subtract(IValidationMessage[] after, IValidationMessage[] baseline)
    {
        var remaining = baseline.GroupBy(e => (e.Id, e.Message)).ToDictionary(g => g.Key, g => g.Count());
        return after.Where(e =>
        {
            var key = (e.Id, e.Message);
            if (remaining.GetValueOrDefault(key) == 0) return true;
            remaining[key]--;
            return false;
        }).ToArray();
    }
}
