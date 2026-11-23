using System.Text.Json;
using System.Text.RegularExpressions;
using OutSystems.Model;
using OutSystems.Model.Data;
using OutSystems.Model.Types;

namespace OmlCli;

// The production rules of skill/rules.md that `oml apply` enforces, and the risks `oml review` lists. Capture before any
// spec work; global keys survive the data/logic scratch reload, so the change is found by key, not by name.
//   forbidden (rule 2): an existing entity or attribute deleted, an attribute retyped or renamed, an entity made again
//     with a new key. Always refused.
//   delete (rule 3): any other existing element deleted. Saved only with the user's yes (--user-approved, autoApprove.deletes).
//   data (rules 5 and 6): a change that can lose production data. Saved only with the user's yes (--user-approved, autoApprove.dataDeletion).
//   breaking (rule 4): a renamed public element or a changed public signature. Allowed; reported with what consumers must do.
//   security (rule 7): a warning.
sealed class ProductionRules
{
    readonly HashSet<string> source;
    readonly HashSet<string> referenced;
    readonly Dictionary<string, string> sourceSql;
    readonly Dictionary<string, (IObjectSignature element, string name, string? shape, bool? isPublic)> definitions;
    readonly HashSet<string> wasPublic, wasPrivate;
    readonly IESpace origin; // the module before: it stays open to the end, for the names of its types
    public ProductionRules(IESpace module)
    {
        origin = module;
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
        // activities included): making it private breaks its consumers (rule 4).
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

    // Rule 4: the inputs, outputs and events of each existing public action, block and screen, by key (a renamed
    // parameter is reported as a rename, not as one removed and one added).
    sealed record Param(string name, string type, bool mandatory);
    sealed record Contract(string kind, Dictionary<string, Param> inputs, Dictionary<string, Param> outputs,
        Dictionary<string, (string name, bool mandatory, Dictionary<string, Param> inputs)> events);
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
        Dictionary<string, Param> Of(object owner, string collection) => Parameters(owner, collection).GroupBy(Key)
            .ToDictionary(g => g.Key, g => new Param(Name(g.First()), Type(g.First()), Reflect.Get(g.First(), "IsMandatory") is true));
        return new(kind, Of(o, "InputParameters"), Of(o, "OutputParameters"),
            Parameters(o, "Events").GroupBy(Key).ToDictionary(g => g.Key, g => (Name(g.First()), Reflect.Get(g.First(), "IsMandatory") is true, Of(g.First(), "InputParameters"))));
    }
    static string What(string kind) => kind switch
    {
        "server" => "public server or service action", "client" => "public client action",
        "reactive" => "public Reactive or Mobile block or screen", _ => "public Traditional Web block or screen",
    };

    // Each change of a public element's signature that breaks the modules using it (rule 4), with what they must do.
    // Allowed: the report lists it. A new optional input of a server or service action or a Traditional Web block or
    // screen, and a new output of a server or service action, break nothing.
    static IEnumerable<string> BreakingChanges(string name, Contract before, Contract after)
    {
        var start = $"{What(before.kind)} '{name}'";
        var fixedInputs = before.kind is "client" or "reactive"; // a Reactive caller breaks on any input change
        foreach (var (key, input) in after.inputs)
        {
            if (!before.inputs.TryGetValue(key, out var old))
            {
                if (input.mandatory) yield return $"{start}: new mandatory input '{input.name}'. Callers in other modules must pass it.";
                else if (fixedInputs) yield return $"{start}: new input '{input.name}'. Modules that use it must refresh references.";
                continue;
            }
            if (old.type != input.type) yield return $"{start}: input '{input.name}' changed type ({old.type} to {input.type}). Callers must pass the new type.";
            if (!old.mandatory && input.mandatory) yield return $"{start}: input '{input.name}' is mandatory now. Callers that leave it out must pass it.";
            if (old.mandatory && !input.mandatory && fixedInputs) yield return $"{start}: input '{input.name}' is optional now. Modules that use it must refresh references.";
        }
        foreach (var (key, input) in before.inputs.Where(p => !after.inputs.ContainsKey(p.Key)))
            yield return $"{start}: input '{input.name}' was removed. Callers must stop passing it.";
        foreach (var (key, output) in after.outputs)
        {
            if (!before.outputs.TryGetValue(key, out var old))
            { if (before.kind != "server") yield return $"{start}: new output '{output.name}'. Modules that use it must refresh references."; }
            else if (old.type != output.type) yield return $"{start}: output '{output.name}' changed type ({old.type} to {output.type}). Callers that read it must handle the new type.";
        }
        foreach (var (key, output) in before.outputs.Where(p => !after.outputs.ContainsKey(p.Key)))
            yield return $"{start}: output '{output.name}' was removed. Callers that read it must stop.";
        // An event and its inputs are part of a block's contract: instances in other modules handle it.
        foreach (var (key, evt) in after.events)
        {
            if (!before.events.TryGetValue(key, out var old))
            {
                yield return evt.mandatory ? $"{start}: new mandatory event '{evt.name}'. Every instance in other modules must get a handler."
                    : $"{start}: new optional event '{evt.name}'. Modules that use the block must refresh references.";
                continue;
            }
            if (evt.mandatory && !old.mandatory) yield return $"{start}: event '{evt.name}' is mandatory now. Instances in other modules without a handler must get one.";
            foreach (var (inputKey, input) in evt.inputs)
                if (old.inputs.TryGetValue(inputKey, out var oldInput) && oldInput.type != input.type)
                    yield return $"{start}: input '{input.name}' of event '{evt.name}' changed type ({oldInput.type} to {input.type}). Handlers in other modules must read the new type.";
        }
        foreach (var (key, evt) in before.events.Where(e => !after.events.ContainsKey(e.Key)))
            yield return $"{start}: event '{evt.name}' was removed. Handlers in other modules must be removed.";
    }
    static readonly string[] AttributeProperties = { "DataType", "Length", "Decimals", "IsMandatory", "IsAutoNumber" };
    // `oml model-coverage` reads the same tables this class enforces.
    internal static IReadOnlyList<string> AttributeShape => AttributeProperties;
    internal static IReadOnlyList<(string property, string what)> EntityShape => EntitySettings;
    internal static IReadOnlyList<string> EntityDataRisk => ["IdentifierAttribute", "IsMultiTenant"];
    internal static IEnumerable<string> ContractTypes => Contracts.Order(StringComparer.Ordinal);
    // Entity settings that change how published rows are keyed, read or written. Only the identifier and the
    // multi-tenant setting need the user's yes (rule 5); the others may change.
    static readonly (string property, string what)[] EntitySettings =
    {
        ("IdentifierAttribute", "identifier"), ("IsActiveAttribute", "is active attribute"), ("LabelAttribute", "label attribute"),
        ("OrderByAttribute", "order by attribute"), ("ExposeReadOnly", "expose read only setting"), ("UpdateBehavior", "update behavior"),
        ("IsMultiTenant", "multi-tenant setting"),
    };
    // An attribute's column shape, or an entity's settings.
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
        && (o is not IParameterSignature || o.Parent is not OutSystems.Model.Logic.Nodes.IActionNode)
        // A widget's event handler parameter (a table's OnSort ClickedColumn, added by Service Studio on save) is part
        // of the widget: replaceWidgets may remove it with the widget.
        && (o is not IParameterSignature || o.Parent?.Parent is not OutSystems.Model.UI.IWidgetSignature)
        // What a process node owns (the Close or Start activity action Service Studio makes for it, its parameters) goes with the node.
        && o.Parent is not OutSystems.Model.Processes.Nodes.IProcessNode && o.Parent?.Parent is not OutSystems.Model.Processes.Nodes.IProcessNode;
    // An entity attribute: a column with production data.
    static bool IsColumn(IObjectSignature o) => o is IAttributeSignature && o.Parent is IEntitySignature;
    bool Own(IObjectSignature o) => source.Contains(Key(o)) && !referenced.Contains(Key(o));
    // An entity's generated actions (CreateOrder, GetOrder) follow the entity.
    static bool EntityAction(IObjectSignature o) => o.GetType().GetInterfaces().Any(i => i.Name.Contains("EntityAction"));
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

    // Rule 2: what oml never does, with any flag or setting.
    internal static string Forbidden(string text, string fix = "Undo that part of the spec; to change an attribute, add a new one.") =>
        $"refused: {text} oml never does this, with any flag or setting (rule 2). {fix} If the user wants it anyway, they do it by hand in Service Studio.";
    static ModelError Refuse(IObjectSignature o, string what) => new(Forbidden(o is IEntitySignature
        ? $"'{Name(o)}' is an existing entity with production data; {what} it loses that data."
        : $"'{Name(o.Parent!)}/{Name(o)}' is an existing attribute (a column with production data); {what} it can lose that data."));
    // Renames are allowed (a public element's rename is reported as breaking), except an existing attribute's.
    public void Rename(IObjectSignature o)
    {
        if (IsColumn(o) && Own(o)) throw Refuse(o, "renaming");
    }
    // Deletes of existing elements need the user's yes (found by Validate); an existing entity or attribute is never deleted.
    public void Delete(IObjectSignature o)
    {
        // Deleting a containing element must not bypass the protection of its members.
        foreach (var child in Walk(o))
            if (child is IEntitySignature && Own(child) || IsColumn(child) && Own(child)) throw Refuse(child, "deleting");
    }
    public void Set(object target, string property, JsonElement value)
    {
        if (target is not IObjectSignature o) return;
        if (property.Equals("Name", StringComparison.OrdinalIgnoreCase)
            || (property.Equals("Identifier", StringComparison.OrdinalIgnoreCase) && o is OutSystems.Model.Data.IRecord)) Rename(o);
        if (IsColumn(o) && Own(o) && new[] { "DataType", "Type" }.Contains(property, StringComparer.OrdinalIgnoreCase))
            throw Refuse(o, "changing the data type of");
    }

    // What Validate found that needs the user's yes: kind "delete" or "data", the element, and what changes, in plain words.
    internal sealed record Approval(string Kind, string Element, string What);
    public List<Approval> NeedsApproval { get; } = new();
    // Breaking changes to public elements (rule 4), each with what the consumer modules must do.
    public List<string> Breaking { get; } = new();
    // Security warnings found by key (rule 7): an existing public entity made writable.
    public List<string> Warnings { get; } = new();

    // One finding of the comparison by key. Kind: forbidden, delete, data, breaking, security. Breaks: a delete that also
    // breaks the modules that use the element (it was public, or its owner was).
    internal sealed record Finding(string Kind, int Rule, string Element, string Text, bool Breaks = false);

    public void Validate(IESpace module, List<string> problems)
    {
        // Catch indirect SDK mutations too: dependency cleanup, clipboard operations, and property side effects.
        // Static record value slots can share an attribute's key; compare definition objects only.
        ValidateConsumed(module, problems);
        var all = Walk(module).Where(o => !o.WasDeleted).ToList();
        // A UI flow reads as public when it holds a public screen or block; the screen or block is the change.
        MadePublic = all.Where(o => wasPrivate.Contains(Key(o)) && Public(o) == true && !o.GetType().GetInterfaces().Any(i => i.Name == "IUIFlowSignature")).GroupBy(Key).Select(g => g.First())
            .Select(o => $"public of {NameClash.Kind(o)} {Name(o)}").Order(StringComparer.Ordinal).ToList();
        NeedsApproval.Clear(); Breaking.Clear(); Warnings.Clear();
        foreach (var f in Compare(module, all))
            switch (f.Kind)
            {
                case "forbidden": problems.Add(Forbidden(f.Text)); break;
                case "delete" or "data":
                    NeedsApproval.Add(new(f.Kind, f.Element, f.Text));
                    if (f.Breaks) Breaking.Add(f.Text);
                    break;
                case "breaking": Breaking.Add(f.Text); break;
                case "security": Warnings.Add($"rule {f.Rule}: {f.Text}"); break;
            }
    }

    // The module before (this snapshot) against `after`, by key: what the production rules say about each change.
    List<Finding> Compare(IESpace after, List<IObjectSignature> all)
    {
        var findings = new List<Finding>();
        var current = all.Where(Protected).GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        var made = new Lazy<HashSet<string>>(() => all.Where(o => !source.Contains(Key(o)) && Model.NameOf(o) is not null)
            .Select(o => NameClash.Kind(o) + "\n" + PathOf(o, Name(o))).ToHashSet());
        foreach (var o in all.Where(o => wasPublic.Contains(Key(o)) && Public(o) == false).GroupBy(Key).Select(g => g.First()))
            findings.Add(new("breaking", 4, Name(o), $"{NameClash.Kind(o)} '{Name(o)}' was public and is now private: the modules that use it break. They must stop using it."));
        // Public for consumers: the element itself, or its owner (a parameter, a structure attribute, an event).
        bool PublicFor(IObjectSignature o) => wasPublic.Contains(Key(o)) || o.Parent is { } owner && wasPublic.Contains(Key(owner));
        bool Gone(IObjectSignature o) => definitions.ContainsKey(Key(o)) && !current.ContainsKey(Key(o));
        foreach (var (key, before) in definitions)
        {
            if (EntityAction(before.element)) continue;
            var kind = NameClash.Kind(before.element);
            var path = PathOf(before.element, before.name);
            if (!current.TryGetValue(key, out var now))
            {
                // One finding per deleted element, not one more per attribute, parameter or record inside it.
                var owner = before.element.Parent;
                for (; owner is not null && owner is not IESpace; owner = owner.Parent) if (Gone(owner)) break;
                if (owner is not null && owner is not IESpace) continue;
                // Created again with the same name, it is still a new element: references hold the old key.
                var again = made.Value.Contains(kind + "\n" + path);
                if (before.element is IEntitySignature)
                    findings.Add(new("forbidden", 2, path, again
                        ? $"{kind} '{path}' was deleted and created again with the same name: that makes a new, empty table, and the app loses the production data of the old one."
                        : $"{kind} '{path}' was deleted: its production data is lost, and the modules that use it break."));
                else if (IsColumn(before.element))
                    findings.Add(new("forbidden", 2, path, $"attribute '{path}' was deleted{(again ? " and created again with the same name, which makes a new, empty column" : "")}: its production data is lost."));
                else
                {
                    // A parameter or event of a public action, block or screen: its signature line says what breaks.
                    var breaks = wasPublic.Contains(key) || before.element.Parent is { } p && wasPublic.Contains(Key(p)) && !signatures.ContainsKey(Key(p));
                    findings.Add(new("delete", 3, path, $"{kind} '{path}' was deleted{(again ? " and created again with the same name, which makes a new element" : "")}."
                        + (breaks ? " The modules that use it break; they must stop using it." : ""), breaks));
                }
                continue;
            }
            if (Name(now) != before.name)
            {
                if (IsColumn(before.element)) findings.Add(new("forbidden", 2, path, $"attribute '{path}' was renamed to '{Name(now)}'."));
                else if (PublicFor(before.element))
                    findings.Add(new("breaking", 4, PathOf(now, Name(now)), $"{kind} '{path}' was renamed to '{Name(now)}': the modules that use it must refresh references."));
            }
            if (signatures.TryGetValue(key, out var contract))
                findings.AddRange(BreakingChanges(Name(now), contract, Signature(after, now, contract.kind)).Select(t => new Finding("breaking", 4, Name(now), t)));
            else if (Shape(now) is var shape && shape != before.shape) findings.AddRange(ShapeFindings(before.element, before.shape, now, shape, before.isPublic, after));
        }
        foreach (var node in all.OfType<OutSystems.Model.Logic.Nodes.ISQLNode>())
            if (!UnchangedSql(node) && DataChangingKeyword(node.Statement ?? "") is { } keyword)
                findings.Add(new("data", 6, Model.OwnerName(node) ?? Name(node), $"SQL '{Name(node)}' contains {keyword}: it changes data in production."));
        return findings;
    }

    // A changed attribute or entity: what each changed setting means for production data.
    IEnumerable<Finding> ShapeFindings(IObjectSignature before, string? old, IObjectSignature now, string? shape, bool? wasPublicEntity, IESpace after)
    {
        var element = PathOf(now, Name(now));
        if (now is IEntitySignature)
        {
            var names = EntitySettings.Select(x => (x.property, x.what)).ToArray();
            string Text(string property) => $"{Value(old, property, names, origin)} -> {Value(shape, property, names, after)}";
            bool Changed(string property) => Value(old, property, names) != Value(shape, property, names);
            if (Changed("IdentifierAttribute")) yield return new("data", 5, element, $"entity '{element}': identifier {Text("IdentifierAttribute")}. That changes how existing rows are keyed.");
            if (Changed("IsMultiTenant")) yield return new("data", 5, element, $"entity '{element}': multi-tenant setting {Text("IsMultiTenant")}. That changes which existing rows each tenant sees.");
            // A public entity that other modules may now write to weakens security (rule 7).
            if (wasPublicEntity == true && Value(old, "ExposeReadOnly", names) == "True" && Value(shape, "ExposeReadOnly", names) == "False")
                yield return new("security", 7, Name(now), $"entity '{Name(now)}' is public and Expose Read Only is No now: other modules can now write to this public entity.");
            yield break;
        }
        var properties = AttributeProperties.Select(p => (p, Words(p))).ToArray();
        var changed = properties.Where(x => Value(old, x.Item1, properties) != Value(shape, x.Item1, properties)).ToList();
        string Change((string property, string what) x) => $"{x.what} {Value(old, x.property, properties, origin)} -> {Value(shape, x.property, properties, after)}";
        if (!IsColumn(now))
        {
            // A structure attribute holds no data; the modules that use a public structure must refresh references.
            if (before.Parent is { } owner && wasPublic.Contains(Key(owner)) && changed.Count > 0)
                yield return new("breaking", 4, element, $"attribute '{element}' of a public structure changed: {string.Join(", ", changed.Select(Change))}. The modules that use the structure must refresh references.");
            yield break;
        }
        int? Number(string? s, string property) => int.TryParse(Value(s, property, properties), out var n) ? n : null;
        foreach (var x in changed)
        {
            switch (x.Item1)
            {
                case "DataType":
                    yield return new("forbidden", 2, element, $"attribute '{element}' changed its data type ({Change(x)}): production data can fail to convert."); break;
                case "Length" when Number(shape, "Length") < Number(old, "Length"):
                    yield return new("data", 5, element, $"attribute '{element}': shorter {Change(x)}. Longer values in production are cut."); break;
                case "Decimals" when Number(shape, "Decimals") < Number(old, "Decimals"):
                    yield return new("data", 5, element, $"attribute '{element}': fewer {Change(x)}. Values in production are rounded."); break;
                case "IsMandatory" when Value(shape, "IsMandatory", properties) == "True":
                    yield return new("data", 5, element, $"attribute '{element}' is mandatory now. Existing rows without a value break."); break;
                case "IsAutoNumber":
                    yield return new("data", 5, element, $"attribute '{element}': {Change(x)}. That changes how new rows get their identifier."); break;
            }
        }
    }

    static bool? Public(IObjectSignature o) => Reflect.Get(o, "Public") as bool?;
    // Rule 4 lists events and signatures only on elements that were public before this apply.
    public bool WasPublic(object o) => o is IObjectSignature s && wasPublic.Contains(Key(s));
    // Rule 7: every server action of the module that is public now and was not before (new or made public).
    // Service actions and exposed REST methods are made to be called from outside, so they are not listed.
    public IEnumerable<string> NewPublicServerActions(IESpace module) => module.ServerActions
        .Where(a => !a.WasDeleted && a.Public && !WasPublic(a) && !referenced.Contains(Key(a))).Select(a => a.Name).Order(StringComparer.Ordinal);
    // Existing own elements that this apply made public, by any path (creation lists, content, edits.set), for the
    // report's `changed` ("public of entity Sector"). Set by Validate.
    public List<string> MadePublic { get; private set; } = new();

    // The data-changing keyword in a statement (rule 6), or null. SQL strings, quoted identifiers, parameters and
    // comments are not keywords; nested block comments are supported.
    public static string? DataChangingKeyword(string statement)
    {
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
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    // `oml review`: one risk of a change. Info: worth a look, not a broken rule.
    internal sealed record Risk(int Rule, string Element, string Text, bool Info = false);

    // `oml review`: what the production rules say about the change from this module (the one before) to `after`, a
    // second module file of the same module. Keys are GlobalKeys, which stay the same across saves. Each entry names
    // its rule (rules.md) and says what changed; nothing is refused here.
    internal List<Risk> Risks(IESpace after)
    {
        var all = Walk(after).Where(o => !o.WasDeleted).ToList();
        var risks = Compare(after, all).Select(f => new Risk(f.Rule, f.Element, f.Text)).ToList();
        foreach (var name in NewPublicServerActions(after))
            risks.Add(new(7, name, $"server action '{name}' is public now: other modules (and their users) can call it. Make sure that is wanted."));
        Access(after, all, risks);
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
    // indexes and each entity attribute's delete rule.
    readonly HashSet<string> anonymousScreens = new(), uniqueIndexes = new();
    readonly Dictionary<string, List<string>> screenRoles = new();
    readonly Dictionary<string, string> deleteRules = new();
    public static ProductionRules ForReview(IESpace before)
    {
        var rules = new ProductionRules(before);
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
        return rules;
    }
}
