using System.Text.Json;
using System.Text.RegularExpressions;
using OutSystems.Model;
using OutSystems.Model.Data;
using OutSystems.Model.Types;

namespace OmlCli;

// Capture before any spec work. Global keys survive the data/logic scratch reload.
sealed class ProductionRules
{
    readonly HashSet<string> source;
    readonly HashSet<string> referenced;
    readonly Dictionary<string, string> sourceSql;
    readonly Dictionary<string, (IObjectSignature element, string name, string? shape, bool? isPublic)> definitions;
    readonly HashSet<string> wasPublic, wasPrivate;
    public ProductionRules(IESpace module)
    {
        var elements = Walk(module).ToList();
        source = elements.Select(Key).ToHashSet();
        // Elements of producer modules hold no data here; "Remove Unused Dependencies" may drop them.
        var self = ((OutSystems.Model.Applications.IModule)module).Name;
        referenced = Refs.All(module).Where(x => x.owner != self).Select(x => x.element).OfType<IObjectSignature>()
            .SelectMany(Walk).Select(Key).ToHashSet();
        consumed = ConsumedState(module);
        sourceSql = elements.OfType<OutSystems.Model.Logic.Nodes.ISQLNode>().GroupBy(Key).ToDictionary(g => g.Key, g => g.First().Statement ?? "");
        definitions = elements.Where(Protected).ToDictionary(Key, o => (o, Name(o), Shape(o), Public(o)));
        // Every own element that is public, of any kind (images, scripts, resources, themes and process
        // activities included): rule 4 refuses making it private.
        wasPublic = elements.Where(o => Public(o) == true && !referenced.Contains(Key(o))).Select(Key).ToHashSet();
        wasPrivate = elements.Where(o => Public(o) == false && !referenced.Contains(Key(o))).Select(Key).ToHashSet();
        var traditional = WebUi.IsTraditional(module);
        signatures = definitions.Values.Where(d => d.isPublic == true && SignatureKind(d.element, traditional) is not null)
            .ToDictionary(d => Key(d.element), d => Signature(module, d.element, SignatureKind(d.element, traditional)!));
    }

    // The catch-all for referenced modules: what each referenced element looked like when the module was loaded. Validate
    // refuses the save when any of it differs. Not covered on purpose: the reference object itself (a `references` refresh or an
    // alias rename is oml's own change) and referenced elements that appear or go (a refresh adds them; Remove Unused Dependencies drops them).
    static readonly string[] ConsumedFields = { "Description", "Public", "DataType", "Length", "Decimals", "IsMandatory", "IsAutoNumber" };
    readonly Dictionary<string, (IObjectSignature element, Dictionary<string, string?> fields)> consumed;
    static Dictionary<string, (IObjectSignature element, Dictionary<string, string?> fields)> ConsumedState(IESpace module)
    {
        var self = ((OutSystems.Model.Applications.IModule)module).Name;
        var state = new Dictionary<string, (IObjectSignature, Dictionary<string, string?>)>();
        foreach (var o in Refs.All(module).Where(x => x.owner != self).Select(x => x.element).OfType<IObjectSignature>().SelectMany(Walk))
        {
            if (state.ContainsKey(Key(o))) continue;
            var fields = new Dictionary<string, string?> { ["kind"] = ElementPaths.Kind(o), ["name"] = Model.NameOf(o) };
            foreach (var f in ConsumedFields)
            {
                try { fields[f] = Reflect.Get(o, f) is { } v ? (v is IObjectSignature t ? Key(t) : v.ToString()) : null; }
                catch (System.Reflection.TargetInvocationException) { fields[f] = null; } // a proxy that cannot answer
            }
            state[Key(o)] = (o, fields);
        }
        return state;
    }
    // Test hook: the referenced state is compared again, with the snapshot of a module that was loaded before.
    internal void ValidateConsumed(IESpace module, List<string> problems)
    {
        var now = ConsumedState(module);
        foreach (var (key, before) in consumed)
            if (now.TryGetValue(key, out var after) && before.fields.Where(f => after.fields[f.Key] != f.Value).Select(f => f.Key).ToList() is { Count: > 0 } changed)
                problems.Add($"{Consumed(before.element).Message} Changed: {string.Join(", ", changed)}.");
    }

    // Rule 4: the inputs and outputs of each existing public action, block and screen, by name.
    // events: a block's events and the types of their inputs.
    sealed record Contract(string kind, Dictionary<string, (string type, bool mandatory)> inputs, Dictionary<string, string> outputs,
        Dictionary<string, Dictionary<string, string>> events);
    readonly Dictionary<string, Contract> signatures;
    // server: server and service actions; client: client actions; reactive and web: blocks and screens.
    static string? SignatureKind(IObjectSignature o, bool traditional) =>
        o is OutSystems.Model.Logic.IServerActionSignature or OutSystems.Model.Logic.IServiceActionSignature ? "server"
        : o is OutSystems.Model.Logic.IClientActionSignature ? "client"
        : o is OutSystems.Model.UI.IScreenSignature or OutSystems.Model.UI.IBlockSignature ? (traditional ? "web" : "reactive") : null;
    static Contract Signature(IESpace module, IObjectSignature o, string kind)
    {
        static IEnumerable<IObjectSignature> Parameters(object owner, string collection) => Reflect.Get(owner, collection) is System.Collections.IEnumerable list
            ? list.OfType<IObjectSignature>() : Enumerable.Empty<IObjectSignature>();
        string Type(IObjectSignature p) => Model.Describe(module, Reflect.Get(p, "DataType") as ITypeSignature);
        return new(kind,
            Parameters(o, "InputParameters").GroupBy(p => Name(p)).ToDictionary(g => g.Key, g => (Type(g.First()), Reflect.Get(g.First(), "IsMandatory") is true)),
            Parameters(o, "OutputParameters").GroupBy(p => Name(p)).ToDictionary(g => g.Key, g => Type(g.First())),
            Parameters(o, "Events").GroupBy(e => Name(e)).ToDictionary(g => g.Key,
                g => Parameters(g.First(), "InputParameters").GroupBy(p => Name(p)).ToDictionary(p => p.Key, p => Type(p.First()))));
    }
    static string What(string kind) => kind switch
    {
        "server" => "public server or service action", "client" => "public client action",
        "reactive" => "public Reactive or Mobile block or screen", _ => "public Traditional Web block or screen",
    };
    // Each change that breaks the modules using a public element (rule 4), worded by its direction.
    static IEnumerable<string> SignatureProblems(string name, Contract before, Contract after, string? start = null)
    {
        start ??= $"refused: '{name}' is an existing {What(before.kind)}.";
        var fixedInputs = before.kind is "client" or "reactive"; // no input change at all, not even an optional one
        foreach (var (input, (type, mandatory)) in after.inputs)
        {
            if (!before.inputs.TryGetValue(input, out var old))
            {
                if (mandatory) yield return $"{start} A new mandatory input '{input}' breaks every module that uses it (rule 4). {(fixedInputs ? "Create a new element instead." : "Add it as optional (\"mandatory\": false), or create a new element.")}";
                else if (fixedInputs) yield return $"{start} Any new input, even an optional one ('{input}'), breaks the modules that use it (rule 4). Create a new element instead.";
                continue;
            }
            if (old.type != type) yield return $"{start} Changing the type of input '{input}' ({old.type} to {type}) breaks the modules that use it (rule 4). Add a new input or element instead.";
            if (!old.mandatory && mandatory) yield return $"{start} Making input '{input}' mandatory breaks the modules that leave it out (rule 4). Keep it optional.";
            if (old.mandatory && !mandatory && fixedInputs) yield return $"{start} Making input '{input}' optional changes its signature and breaks the modules that use it (rule 4); that is allowed only on server and service actions and Traditional Web blocks and screens.";
        }
        // An event input's type is part of the block's contract: handlers in other modules read it.
        foreach (var (evt, inputs) in after.events)
            if (before.events.TryGetValue(evt, out var oldInputs))
                foreach (var (input, type) in inputs)
                    if (oldInputs.TryGetValue(input, out var oldType) && oldType != type)
                        yield return $"{start} Changing the type of input '{input}' of event '{evt}' ({oldType} to {type}) breaks the handlers in the modules that use it (rule 4). Add a new event instead.";
        foreach (var (output, type) in after.outputs)
        {
            if (!before.outputs.TryGetValue(output, out var old))
            { if (before.kind != "server") yield return $"{start} A new output '{output}' breaks the modules that use it (rule 4); only public server and service actions may get new outputs."; }
            else if (old != type) yield return $"{start} Changing the type of output '{output}' ({old} to {type}) breaks the modules that use it (rule 4). Add a new output or element instead.";
        }
    }
    static readonly string[] AttributeProperties = { "DataType", "Length", "Decimals", "IsMandatory", "IsAutoNumber" };
    // Entity settings that change how published rows are keyed, read or written (EntityOptions sets them on new entities).
    static readonly (string property, string what)[] EntitySettings =
    {
        ("IdentifierAttribute", "identifier"), ("IsActiveAttribute", "is active attribute"), ("LabelAttribute", "label attribute"),
        ("OrderByAttribute", "order by attribute"), ("ExposeReadOnly", "expose read only setting"), ("UpdateBehavior", "update behavior"),
        ("IsMultiTenant", "multi-tenant setting"),
    };
    // What rule 5 protects: an attribute's column shape, or an entity's settings.
    static string? Shape(IObjectSignature o) =>
        (o is IAttributeSignature && o.Parent is IEntitySignature or IStructureSignature ? AttributeProperties
            : o is IEntitySignature ? EntitySettings.Select(x => x.property).ToArray() : null) is { } properties
        ? string.Join("|", properties.Select(p => Reflect.Get(o, p) is IObjectSignature type ? Key(type) : Reflect.Get(o, p)?.ToString())) : null;
    static string Key(IObjectSignature o) => o.GlobalKey.ToString()!;
    internal static IEnumerable<IObjectSignature> Walk(IObjectSignature root)
    {
        var seen = new HashSet<IObjectSignature>();
        var pending = new Stack<IObjectSignature>();
        pending.Push(root);
        while (pending.TryPop(out var o))
        {
            if (!seen.Add(o)) continue;
            yield return o;
            foreach (var child in ElementPaths.Children(o)) pending.Push(child);
        }
    }
    static readonly HashSet<string> Contracts = new(StringComparer.OrdinalIgnoreCase)
    {
        "IEntitySignature", "IAttributeSignature", "IStructureSignature", "IRecord", "IRecordSignature",
        "IServerActionSignature", "IServiceActionSignature", "IClientActionSignature", "IActionSignature", "IScreenAction",
        "IInputParameterSignature", "IOutputParameterSignature", "IScreenSignature", "IBlockSignature",
        "IRoleSignature", "IUserException", "IUserExceptionSignature", "ISiteProperty", "ISitePropertySignature",
        "ITimer", "ITimerSignature", "IRestClient", "IRestService", "IRestAction", "IRestServiceAction", "IException", "IProcess", "IProcessSignature",
        "IBlockEvent", "IBlockEventSignature"
    };
    public bool Existed(object o) => o is IObjectSignature s && source.Contains(Key(s));
    // SQL that was already in the module is production code the user owns; only new or changed statements are checked.
    public bool UnchangedSql(OutSystems.Model.Logic.Nodes.ISQLNode node) => sourceSql.TryGetValue(Key(node), out var s) && s == (node.Statement ?? "");
    bool Protected(IObjectSignature o) => source.Contains(Key(o)) && !referenced.Contains(Key(o)) && o is not OutSystems.Model.Logic.Nodes.IActionNode &&
        o.GetType().GetInterfaces().Any(i => (i.Namespace?.StartsWith("OutSystems.Model") == true
            || i.Namespace?.StartsWith("ServiceStudio.Plugin.") == true) && Contracts.Contains(i.Name))
        && (o is not IAttributeSignature || o.Parent is IEntitySignature or IStructureSignature)
        && (o is not IParameterSignature || o.Parent is not OutSystems.Model.Logic.Nodes.IActionNode);
    // Any change to an element of a referenced module (a proxy) is refused with this one message.
    internal static ModelError Consumed(IObjectSignature o)
    {
        var path = new List<string>();
        var top = o;
        for (; top.Parent is { } parent && parent is not IReference; top = parent) path.Insert(0, Name(top));
        path.Insert(0, Name(top));
        var module = top.Parent is IReference r ? Name(r) : "?";
        return new($"'{module}.{string.Join("/", path)}' belongs to the referenced module {module}; it cannot be changed here. Change it in the producer module.");
    }
    // The same message when a referenced element holds the name (the model renamed the new element), else null.
    internal static string? ConsumedClash(object created, string name) =>
        NameClash.HolderElement(created, name) is { } held && ElementPaths.Referenced(held) ? Consumed(held).Message : null;
    static string Name(IObjectSignature o) => Model.NameOf(o) ?? Reflect.Get(o, "Identifier")?.ToString() ?? ElementPaths.Kind(o);
    static ModelError Refuse(IObjectSignature o, string operation) => new(
        $"refused: '{Name(o)}' is an existing {(o is IEntitySignature ? "entity" : "element")}. oml never {operation} existing {(o is IEntitySignature ? "entities" : "elements")}; {(operation == "deletes" ? "delete" : "rename")} it manually in Service Studio if you really want to.");
    public void Rename(IObjectSignature o)
    {
        if (Protected(o)) throw Refuse(o, "renames");
    }
    // References are not protected against removal, but an existing local name is still an existing name.
    public void RenameReference(IObjectSignature o)
    {
        if (source.Contains(Key(o))) throw Refuse(o, "renames");
    }
    public void Delete(IObjectSignature o)
    {
        // Deleting a containing flow/folder must not bypass protection of its members.
        foreach (var child in Walk(o)) if (Protected(child)) throw Refuse(child, "deletes");
    }
    public void Validate(IESpace module, List<string> problems)
    {
        // Catch indirect SDK mutations too: dependency cleanup, clipboard operations, and property side effects.
        // Static record value slots can share an attribute's key; compare definition objects only.
        ValidateConsumed(module, problems);
        var all = Walk(module).Where(o => !o.WasDeleted).ToList();
        var current = all.Where(Protected).GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        foreach (var o in all.Where(o => wasPublic.Contains(Key(o)) && Public(o) == false).GroupBy(Key).Select(g => g.First()))
            problems.Add(PublicRefusal(o).Message);
        // A UI flow reads as public when it holds a public screen or block; the screen or block is the change.
        MadePublic = all.Where(o => wasPrivate.Contains(Key(o)) && Public(o) == true && !o.GetType().GetInterfaces().Any(i => i.Name == "IUIFlowSignature")).GroupBy(Key).Select(g => g.First())
            .Select(o => $"public of {NameClash.Kind(o)} {Name(o)}").Order(StringComparer.Ordinal).ToList();
        foreach (var (key, before) in definitions)
        {
            if (!current.TryGetValue(key, out var after)) problems.Add(Refuse(before.element, "deletes").Message);
            else if (Name(after) != before.name) problems.Add(Refuse(before.element, "renames").Message);
            else if (signatures.TryGetValue(key, out var contract))
                problems.AddRange(SignatureProblems(before.name, contract, Signature(module, after, contract.kind)));
            else if (Shape(after) != before.shape) problems.Add(before.element is IEntitySignature entity
                ? EntityOptions.Refusal(entity, string.Join(", ", EntitySettings.Where(x => Value(before.shape, x.property) != Value(Shape(after), x.property)).Select(x => x.what)))
                : AttributeRefusal(before.element).Message);
        }
    }
    static bool? Public(IObjectSignature o) => Reflect.Get(o, "Public") as bool?;
    // Rule 4 checks events and signatures only on elements that were public before this apply.
    public bool WasPublic(object o) => o is IObjectSignature s && wasPublic.Contains(Key(s));
    // Public to private breaks the consumers of the element (rule 4). Private to public is allowed (rule 7).
    internal static ModelError PublicRefusal(IObjectSignature o) => new(
        $"refused: '{Name(o)}' is an existing public {(o is IEntitySignature ? "entity" : "element")}. oml never makes it private: that breaks the modules that use it (rule 4); change it manually in Service Studio if you really want to.");
    // Rule 7: every server action of the module that is public now and was not before (new or made public).
    // Service actions and exposed REST methods are made to be called from outside, so they are not listed.
    public IEnumerable<string> NewPublicServerActions(IESpace module) => module.ServerActions
        .Where(a => !a.WasDeleted && a.Public && !WasPublic(a) && !referenced.Contains(Key(a))).Select(a => a.Name).Order(StringComparer.Ordinal);
    // Existing own elements that this apply made public, by any path (creation lists, content, edits.set), for the
    // report's `changed` ("public of entity Sector"). Set by Validate.
    public List<string> MadePublic { get; private set; } = new();
    static string? Value(string? shape, string property) =>
        shape?.Split('|').ElementAtOrDefault(Array.FindIndex(EntitySettings, x => x.property == property));
    static ModelError AttributeRefusal(IObjectSignature o) => new(
        $"refused: '{Name(o.Parent!)}/{Name(o)}' is an existing attribute. oml never changes its type, length, decimals, mandatory or auto number setting; change it manually in Service Studio if you really want to.");
    public void Set(object target, string property, JsonElement value)
    {
        if (target is not IObjectSignature o) return;
        if (property.Equals("Name", StringComparison.OrdinalIgnoreCase)
            || (property.Equals("Identifier", StringComparison.OrdinalIgnoreCase) && o is OutSystems.Model.Data.IRecord)) Rename(o);
        if (property.Equals("Public", StringComparison.OrdinalIgnoreCase) && WasPublic(o) && value.ValueKind == JsonValueKind.False) throw PublicRefusal(o);
        if (source.Contains(Key(o)) && o is IAttributeSignature && (o.Parent is IEntitySignature or IStructureSignature)
            && new[] { "DataType", "Type", "Length", "Decimals", "IsMandatory", "Mandatory", "IsAutoNumber", "AutoNumber" }.Contains(property, StringComparer.OrdinalIgnoreCase))
            throw AttributeRefusal(o);
        // Rule 4: making an existing event mandatory breaks every instance that has no handler.
        if (source.Contains(Key(o)) && o is OutSystems.Model.UI.IBlockEvent { IsMandatory: false } && property.Equals("IsMandatory", StringComparison.OrdinalIgnoreCase)
            && value.ValueKind == JsonValueKind.True)
            throw new ModelError($"refused: '{Name(o)}' is an existing optional event. oml never makes an existing event mandatory: block instances without a handler would break. Add a new event instead.");
        if (source.Contains(Key(o)) && o is IEntitySignature entity
            && EntitySettings.FirstOrDefault(x => x.property.Equals(property, StringComparison.OrdinalIgnoreCase)) is { property: not null } setting)
            throw new ModelError(EntityOptions.Refusal(entity, setting.what));
        if (o is OutSystems.Model.Logic.Nodes.ISQLNode && property.Equals("Statement", StringComparison.OrdinalIgnoreCase))
            Sql(o, value.GetString() ?? "");
    }
    public static void Sql(object node, string statement)
    {
        // SQL strings/quoted identifiers and comments are not keywords. Support nested block comments.
        var tokens = new System.Text.StringBuilder();
        for (var i = 0; i < statement.Length;)
        {
            var c = statement[i];
            if (c == '-' && i + 1 < statement.Length && statement[i + 1] == '-')
            { while (i < statement.Length && statement[i] is not ('\n' or '\r')) i++; tokens.Append(' '); }
            else if (c == '/' && i + 1 < statement.Length && statement[i + 1] == '*')
            {
                i += 2; var depth = 1;
                while (i < statement.Length && depth > 0)
                    if (i + 1 < statement.Length && statement[i] == '/' && statement[i + 1] == '*') { depth++; i += 2; }
                    else if (i + 1 < statement.Length && statement[i] == '*' && statement[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                tokens.Append(' ');
            }
            else if (c is '\'' or '"' or '[' or '{')
            {
                var end = c == '[' ? ']' : c == '{' ? '}' : c; i++;
                while (i < statement.Length)
                    if (statement[i++] == end) { if (i < statement.Length && statement[i] == end) i++; else break; }
                tokens.Append(' ');
            }
            else if (c == '@')
            {
                i++;
                while (i < statement.Length && (char.IsLetterOrDigit(statement[i]) || statement[i] is '_' or '@' or '$' or '#')) i++;
                tokens.Append(' ');
            }
            else { tokens.Append(c); i++; }
        }
        var match = Regex.Match(tokens.ToString(), @"\b(DELETE|UPDATE|TRUNCATE|DROP|ALTER|MERGE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success) throw new ModelError($"refused: '{Model.NameOf(node)}' SQL contains {match.Value.ToUpperInvariant()}. oml never applies data-changing SQL; change it manually in Service Studio if you really want to.");
    }

    // The data-changing keyword in a statement (rule 6), or null: the same tokenizer as Sql.
    public static string? DataChangingKeyword(string statement)
    {
        try { Sql(new object(), statement); return null; }
        catch (ModelError e) { return Regex.Match(e.Message, @"SQL contains (\w+)\.").Groups[1].Value; }
    }

    // `oml review`: one risk of a change. Info: worth a look, not a broken rule.
    internal sealed record Risk(int Rule, string Element, string Text, bool Info = false);

    // `oml review`: what the production rules say about the change from this module (the one before) to `after`, a
    // second module file of the same module. Keys are GlobalKeys, which stay the same across saves. Each entry names
    // its rule (rules.md) and says what changed; nothing is refused here.
    internal List<Risk> Risks(IESpace after)
    {
        var risks = new List<Risk>();
        var all = Walk(after).Where(o => !o.WasDeleted).ToList();
        var current = all.Where(Protected).GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        var recreated = new Lazy<HashSet<string>>(() => all.Where(o => !source.Contains(Key(o)) && Model.NameOf(o) is not null)
            .Select(o => NameClash.Kind(o) + "\n" + Name(o)).ToHashSet());
        foreach (var o in all.Where(o => wasPublic.Contains(Key(o)) && Public(o) == false).GroupBy(Key).Select(g => g.First()))
            risks.Add(new(4, Name(o), $"{NameClash.Kind(o)} '{Name(o)}' was public and is now private: the modules that use it break."));
        foreach (var (key, before) in definitions)
        {
            // An entity's generated actions follow the entity: its own rename or delete is the risk.
            if (before.element.GetType().GetInterfaces().Any(i => i.Name.Contains("EntityAction"))) continue;
            var kind = NameClash.Kind(before.element);
            var path = PathOf(before.element, before.name);
            if (!current.TryGetValue(key, out var now))
            {
                // A deleted entity or action is one risk, not one more per attribute or parameter.
                if (before.element.Parent is { } owner && definitions.ContainsKey(Key(owner)) && !current.ContainsKey(Key(owner))) continue;
                // Created again with the same name, it is still a new element: references hold the old key.
                var again = recreated.Value.Contains(kind + "\n" + before.name) ? " and created again with the same name, which makes a new element" : "";
                risks.Add(new(before.element is IEntitySignature ? 2 : 3, path, $"{kind} '{path}' was deleted{again}: {Loss(before.element)}"));
                continue;
            }
            if (Name(now) != before.name)
                risks.Add(new(3, PathOf(now, Name(now)), $"{kind} '{path}' was renamed to '{Name(now)}': the modules that use the old name break."));
            if (signatures.TryGetValue(key, out var contract))
                foreach (var text in SignatureProblems(before.name, contract, Signature(after, now, contract.kind), $"'{Name(now)}' is a {What(contract.kind)}."))
                    risks.Add(new(4, Name(now), text));
            else if (Shape(now) is var shape && shape != before.shape)
            {
                var entity = now is IEntitySignature;
                var names = entity ? EntitySettings.Select(x => (x.property, x.what)).ToArray() : AttributeProperties.Select(p => (p, Words(p))).ToArray();
                var changed = names.Where(x => Value(before.shape, x.Item1, names) != Value(shape, x.Item1, names)).ToList();
                // A public entity that other modules may now write to weakens security (rule 7), not the data shape.
                if (entity && before.isPublic == true && Value(before.shape, "ExposeReadOnly", names) == "True" && Value(shape, "ExposeReadOnly", names) == "False")
                {
                    risks.Add(new(7, Name(now), $"entity '{Name(now)}' is public and Expose Read Only is No now: other modules can now write to this public entity."));
                    changed.RemoveAll(x => x.Item1 == "ExposeReadOnly");
                }
                if (changed.Count == 0) continue;
                var text = string.Join(", ", changed.Select(x => $"{x.Item2} {Value(before.shape, x.Item1, names, reviewed)} -> {Value(shape, x.Item1, names, after)}"));
                var element = PathOf(now, Name(now));
                risks.Add(new(5, element, $"{(entity ? "entity" : "attribute")} '{element}' changed: {text}. " + (entity ? "That changes how existing rows are keyed, read or written."
                    : now.Parent is IEntitySignature ? "Production data can be cut off or fail to convert."
                    : "Values passed through this structure can be cut off or fail to convert, and the modules that use it may break.")));
            }
        }
        foreach (var name in NewPublicServerActions(after))
            risks.Add(new(7, name, $"server action '{name}' is public now: other modules (and their users) can call it. Make sure that is wanted."));
        foreach (var node in all.OfType<OutSystems.Model.Logic.Nodes.ISQLNode>())
            if (!UnchangedSql(node) && DataChangingKeyword(node.Statement ?? "") is { } keyword)
                risks.Add(new(6, Model.OwnerName(node) ?? Name(node), $"SQL '{Name(node)}' contains {keyword}: it changes data in production."));
        Access(after, all, risks);
        Events(all, risks);
        foreach (var entity in all.OfType<IEntitySignature>().Where(e => source.Contains(Key(e)) && !referenced.Contains(Key(e))))
        {
            foreach (var index in EntityOptions.Indexes(entity).Where(i => Reflect.Get(i, "Unique") is true))
                if (!uniqueIndexes.Contains(Key(entity) + "/" + EntityOptions.IndexName(index)))
                    risks.Add(new(5, Name(entity), $"unique index {EntityOptions.IndexName(index)} ({string.Join(", ", EntityOptions.Columns(index))}) on the existing entity '{Name(entity)}': publish fails if production has duplicate values."));
            // Rule 1: existing rows are production data. With Delete, deleting a referenced record deletes these rows too.
            foreach (var attribute in entity.Attributes.Where(a => deleteRules.TryGetValue(Key(a), out var rule) && rule != "Delete"
                && Reflect.Get(a, "DeleteRule")?.ToString() == "Delete"))
                risks.Add(new(1, Name(entity) + "/" + Name(attribute), $"attribute '{Name(entity)}/{Name(attribute)}': delete rule {deleteRules[Key(attribute)]} -> Delete: it can cascade deletes in production.", Info: true));
        }
        return risks;
    }

    // Owner/Name for attributes and parameters, so the line says whose they are.
    static string PathOf(IObjectSignature o, string name) =>
        o is IAttributeSignature or IParameterSignature && o.Parent is { } owner ? Name(owner) + "/" + name : name;

    // What a delete takes with it, by kind.
    static string Loss(IObjectSignature o) => o switch
    {
        IEntitySignature => "its production data is deleted with it, and the modules that use it break.",
        IAttributeSignature when o.Parent is IEntitySignature => "its column and production data are deleted, and the modules that use it break.",
        IAttributeSignature => "the modules and APIs that use the structure break.",
        IParameterSignature => "the modules that use its owner break.",
        _ => "the modules that use it break.",
    };

    // Rule 7: who may open a screen. Anonymous lets everyone in; Registered lets every logged-in user in.
    void Access(IESpace after, List<IObjectSignature> all, List<Risk> risks)
    {
        var anonymous = Reflect.Get(after, "AnonymousRole") is { } a ? Model.NameOf(a) : null;
        var registered = Reflect.Get(after, "RegisteredRole") is { } r ? Model.NameOf(r) : null;
        foreach (var screen in all.OfType<OutSystems.Model.UI.IScreenSignature>())
        {
            var roles = Roles(screen);
            var key = Key(screen);
            var where = Model.OwnerName(screen) ?? Name(screen);
            var open = anonymous is not null && roles.Contains(anonymous);
            screenRoles.TryGetValue(key, out var old);
            if (open)
            {
                if (!anonymousScreens.Contains(key))
                    risks.Add(new(7, where, $"screen '{Name(screen)}' allows Anonymous now: anyone can open it without logging in."));
                continue; // Anonymous lets everyone in: other role changes do not matter then.
            }
            if (old is null) continue; // a new screen
            if (old.Except(roles).ToList() is { Count: > 0 } removed)
                risks.Add(new(7, where, $"screen '{Name(screen)}' lost the role{(removed.Count > 1 ? "s" : "")} {string.Join(", ", removed)}."));
            if (registered is not null && roles.Contains(registered) && !old.Contains(registered))
                risks.Add(new(7, where, $"screen '{Name(screen)}' allows Registered now: every logged-in user can open it, not only its roles."));
            else if (roles.Except(old).ToList() is { Count: > 0 } added)
                risks.Add(new(7, where, $"screen '{Name(screen)}' can now be opened by the role{(added.Count > 1 ? "s" : "")} {string.Join(", ", added)}.", Info: true));
        }
    }

    // Rule 4: an existing public block gets no new event, and an optional event stays optional.
    void Events(List<IObjectSignature> all, List<Risk> risks)
    {
        foreach (var block in all.Where(o => Inspect.IsBlock(o) && blockEvents.ContainsKey(Key(o))))
        {
            var old = blockEvents[Key(block)];
            foreach (var evt in (Reflect.Get(block, "Events") as System.Collections.IEnumerable)?.OfType<IObjectSignature>() ?? [])
            {
                var mandatory = Reflect.Get(evt, "IsMandatory") is true;
                if (!old.TryGetValue(Name(evt), out var was))
                    risks.Add(new(4, Name(block) + "/" + Name(evt), $"public block '{Name(block)}' has a new {(mandatory ? "mandatory" : "optional")} event '{Name(evt)}': "
                        + (mandatory ? "every instance in other modules now needs a handler, so they break." : "the modules that use the block must be refreshed to handle it.")));
                else if (mandatory && !was)
                    risks.Add(new(4, Name(block) + "/" + Name(evt), $"event '{Name(evt)}' of public block '{Name(block)}' is mandatory now: instances in other modules without a handler break."));
            }
        }
    }

    static string Words(string property) => Regex.Replace(property.StartsWith("Is") ? property[2..] : property, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
    // One property of a shape string. A type is stored by key; `module` turns it back into its name.
    static string Value(string? shape, string property, (string property, string what)[] names, IESpace? module = null)
    {
        var value = shape?.Split('|').ElementAtOrDefault(Array.FindIndex(names, x => x.property == property)) ?? "";
        if (module is null || value.Length == 0) return value.Length == 0 ? "(none)" : value;
        // Types (basic, identifier and list types included) and attributes are elements of the module.
        return Walk(module).FirstOrDefault(o => Key(o) == value) is not { } element ? value
            : element is ITypeSignature type ? Model.Describe(module, type) : Name(element);
    }
    static List<string> Roles(object screen) => (Reflect.Get(screen, "Roles") as System.Collections.IEnumerable)?.Cast<object>().Select(r => Model.NameOf(r) ?? "").ToList() ?? new();
    // Review only, from the module before: screens that allowed Anonymous, the roles of each screen, the unique
    // indexes, each entity attribute's delete rule, and each public block's events (name -> mandatory).
    readonly HashSet<string> anonymousScreens = new(), uniqueIndexes = new();
    readonly Dictionary<string, List<string>> screenRoles = new();
    readonly Dictionary<string, string> deleteRules = new();
    readonly Dictionary<string, Dictionary<string, bool>> blockEvents = new();
    IESpace? reviewed; // the module before, for the names of its types
    public static ProductionRules ForReview(IESpace before)
    {
        var rules = new ProductionRules(before) { reviewed = before };
        var anonymous = Reflect.Get(before, "AnonymousRole") is { } a ? Model.NameOf(a) : null;
        var elements = Walk(before).ToList();
        foreach (var screen in elements.OfType<OutSystems.Model.UI.IScreenSignature>())
        {
            rules.screenRoles[Key(screen)] = Roles(screen);
            if (anonymous is not null && rules.screenRoles[Key(screen)].Contains(anonymous)) rules.anonymousScreens.Add(Key(screen));
        }
        foreach (var entity in elements.OfType<IEntitySignature>())
        {
            foreach (var index in EntityOptions.Indexes(entity).Where(i => Reflect.Get(i, "Unique") is true))
                rules.uniqueIndexes.Add(Key(entity) + "/" + EntityOptions.IndexName(index));
            foreach (var attribute in entity.Attributes)
                if (Reflect.Get(attribute, "DeleteRule")?.ToString() is { } rule) rules.deleteRules[Key(attribute)] = rule;
        }
        foreach (var block in elements.Where(o => Inspect.IsBlock(o) && rules.WasPublic(o)))
            rules.blockEvents[Key(block)] = ((Reflect.Get(block, "Events") as System.Collections.IEnumerable)?.OfType<IObjectSignature>() ?? [])
                .GroupBy(Name).ToDictionary(g => g.Key, g => Reflect.Get(g.First(), "IsMandatory") is true);
        return rules;
    }
}
