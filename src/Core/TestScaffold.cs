using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OutSystems.Model;
using OutSystems.Model.Applications;
using OutSystems.Model.Data;
using OutSystems.Model.Logic;
using OutSystems.Model.Types;
using OutSystems.Model.UI;

namespace OmlCli;

// `oml test scaffold`: reads a module's public server actions and a BDDFramework module, and writes a spec that
// builds BDD test screens in a separate Reactive test module. Read-only: it never saves either module.
// Every block, input, placeholder, event and assert name comes from the BDDFramework file itself, so the spec
// matches the module the user gives. oml's own tests use a stand-in (tests/bdd-standin.json), not the Forge component.
static class TestScaffold
{
    const string Todo = " /* TODO */";
    const string Unverified = "oml's own tests use a stand-in BDDFramework module (block names from the OutSystems docs; inputs, placeholder, step event and assert action assumed). The real BDDFramework Client Side was not available, so a test module built from this spec is unverified against it until it applies cleanly to a module that references the real component.";

    public static int Run(List<string> args)
    {
        string? output = null, moduleName = null, bdd = null, producerPath = null;
        var actions = new List<string>();
        for (var i = 3; i < args.Count; i++)
        {
            var option = args[i];
            if (!option.StartsWith("--")) { actions.Add(option); continue; }
            if (option is not ("--out" or "--module-name" or "--bdd" or "--producer") || i + 1 >= args.Count || args[i + 1].StartsWith("--"))
                throw new CliError("test scaffold accepts --out <spec.json>, --module-name <TestModule>, --bdd <BDDFramework.oml> and --producer <path>.", 2);
            var value = args[++i];
            ref var slot = ref option == "--out" ? ref output : ref option == "--module-name" ? ref moduleName : ref option == "--bdd" ? ref bdd : ref producerPath;
            if (slot is not null) throw new CliError($"Duplicate option {option}.", 2);
            slot = value;
        }
        return Cli.Emit(Write(args[2], actions, bdd, output, moduleName, producerPath));
    }

    // The report of one run; the spec file is written before it returns. The self-test calls this directly.
    internal static object Write(string module, List<string> actions, string? bdd, string? output, string? moduleName, string? producerPath)
    {
        if (actions.Count == 0) throw new CliError("test scaffold needs at least one <ServerAction> after <module.oml>.", 2);
        if (bdd is null)
            throw new CliError("test scaffold needs --bdd <BDDFramework.oml>: the framework module the test module references. oml cannot download it. "
                + "Install BDDFramework Client Side from Forge in Service Studio, open it, and save it with 'oml pull --out BDDFramework.oml'.", 2);
        var source = Path.GetFullPath(module);
        var framework = Path.GetFullPath(bdd);
        foreach (var file in new[] { source, framework })
            if (!File.Exists(file)) throw new CliError($"File not found: {file}.", 2);
        if (actions.GroupBy(a => a, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new CliError($"Action '{twice.Key}' is listed twice.", 2);

        using var openProducer = Model.Borrow(source); var producer = openProducer.Module;
        using var openBdd = Model.Borrow(framework); var bddModule = openBdd.Module;
        var producerName = ((IModule)producer).Name;
        moduleName ??= producerName + "_Tests";
        if (!Regex.IsMatch(moduleName, @"^[A-Za-z_][A-Za-z0-9_]*$")) throw new CliError($"--module-name '{moduleName}' must be a plain name: letters, digits and _.", 2);
        var path = Path.GetFullPath(output ?? moduleName + ".bdd-spec.json");
        if (File.Exists(path)) throw new CliError($"Output already exists: {path}. Use a new output path, or delete that file first.", 2);

        var kit = Framework.Read(bddModule, framework);
        var targets = actions.Select(a => Target.Read(producer, a)).ToList();
        const string flow = "TestFlow"; // the flow of a module made from the BDD Framework Client Side app type
        var notes = new List<string>();
        var screens = targets.Select(t => Screen(t, kit, flow, producerName, notes)).ToList();

        var producerElements = targets.Select(t => t.Name)
            .Concat(targets.SelectMany(t => t.Parameters).Select(p => p.OwnedRecord).OfType<string>()).Distinct(StringComparer.Ordinal).ToList();
        var spec = new JsonObject
        {
            ["references"] = new JsonArray(
                Node(new { producer = framework, elements = kit.Elements }),
                Node(new { producer = producerPath ?? source, elements = producerElements })),
            // A UI flow without exception handling warns; the handler shows the error instead of hiding it.
            ["onException"] = Node(new { name = "OnException", flow, nodes = new object[] {
                new { kind = "exceptionHandler", name = "OnAllErrors", exception = "AllExceptions" },
                new { kind = "message", message = "OnAllErrors.ExceptionMessage", type = "error" } } }),
            ["screens"] = new JsonArray(screens.Select(s => s.spec).ToArray()),
        };
        var text = spec.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
            .Replace("\r\n", "\n") + "\n";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // CreateNew also refuses a file created after the check above. UTF-8 without BOM, LF.
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(text);
        }
        catch (IOException) when (File.Exists(path))
        { throw new CliError($"Output already exists: {path}. Use a new output path, or delete that file first.", 2); }

        notes.Add("Exceptions are found in each action's own flow only (Raise Exception nodes); exceptions that its called actions raise are not listed.");
        notes.Add("A user exception of another module cannot be referenced, so exception scenarios catch AllExceptions and check the message where the raise uses a text literal.");
        notes.Add($"Skip 'oml new' when the test module exists. Prefer a module the user creates from the BDD Framework Client Side app type, saved with 'oml pull --out {moduleName}.oml'.");
        notes.Add(Unverified);
        var staged = moduleName + ".staged.oml";
        return new
        {
            spec = path,
            verified = "stand-in only",
            module = producerName,
            testModule = moduleName,
            flow,
            screens = screens.Select(s => new { screen = s.name, action = s.action, scenarios = s.scenarios }),
            framework = kit.Report,
            todo = Regex.Matches(spec["screens"]!.ToJsonString(), "TODO").Count,
            notes,
            next = new[]
            {
                $"oml new {moduleName}.oml --name {moduleName} --kind CrossDevice",
                $"oml spec check {Quote(path)}",
                $"oml apply {moduleName}.oml {Quote(path)} {staged}",
                $"oml preview {staged} {screens[0].name} --screenshot {screens[0].name}.png",
                $"oml review {moduleName}.oml {staged} --md {moduleName}.review.md",
            },
        };
    }

    static JsonNode Node(object value) => JsonSerializer.SerializeToNode(value)!;

    static IEnumerable<object> List(object o, string property) => Reflect.Get(o, property) is System.Collections.IEnumerable items ? items.Cast<object>() : [];

    static IEnumerable<IObjectSignature> Walk(IObjectSignature o) => o.Children.SelectMany(c => new[] { c }.Concat(Walk(c)));

    // The BDDFramework parts the spec uses, read from the module: no name in here is assumed except the documented block names.
    sealed record Framework(string Module, string ScenarioInput, string Placeholder, string StepInput, string StepEvent,
        string Assert, string Condition, string? Message, List<string> Elements, object Report)
    {
        public static Framework Read(IESpace m, string file)
        {
            var module = ((IModule)m).Name;
            if (WebUi.IsTraditional(m))
                throw new CliError($"{file} is a Traditional Web module. oml binds a step block to a screen action through a block event, which only Reactive and Mobile blocks have. "
                    + "Use BDDFramework Client Side (a Reactive module): its client screen actions can call the public server actions under test.", 1);
            var blocks = m.MobileFlows.SelectMany(f => f.Nodes).Where(n => Inspect.IsBlock(n)).Cast<IObjectSignature>().ToList();
            IObjectSignature Block(string name) =>
                blocks.FirstOrDefault(b => Model.NameOf(b) == name && Reflect.Get(b, "Public") is true)
                ?? throw new CliError($"{module} has no public block '{name}'. Public blocks: {string.Join(", ", blocks.Where(b => Reflect.Get(b, "Public") is true).Select(Model.NameOf).DefaultIfEmpty("(none)"))}. "
                    + "Give --bdd the BDDFramework Client Side module.", 1);
            var scenario = Block("BDDScenario");
            var step = Block("BDDStep");
            if (Mandatory(Block("FinalResult"), null) is { } finalInput)
                throw new CliError($"{module}.FinalResult has a mandatory input '{finalInput}'; oml test scaffold cannot fill it.", 1);

            var placeholders = Walk(scenario).Where(w => w is OutSystems.Model.UI.Mobile.Widgets.IPlaceholderWidgetSignature).Select(w => Model.NameOf(w)!).ToList();
            var placeholder = placeholders.Count == 1 ? placeholders[0]
                : placeholders.Where(p => p.Contains("Step", StringComparison.OrdinalIgnoreCase)).ToList() is [var one] ? one
                : throw new CliError($"{module}.BDDScenario has placeholders {string.Join(", ", placeholders.DefaultIfEmpty("(none)"))}; oml test scaffold needs one placeholder, or one whose name has 'Step', for the steps.", 1);

            var events = List(step, "Events").Cast<IObjectSignature>().ToList();
            var stepEvent = events.Count == 1 ? events[0]
                : events.Where(e => Reflect.Get(e, "IsMandatory") is true).ToList() is [var mandatory] ? mandatory
                : throw new CliError($"{module}.BDDStep has events {string.Join(", ", events.Select(Model.NameOf).DefaultIfEmpty("(none)"))}; oml test scaffold needs one event, or one mandatory event, that runs the step's screen action.", 1);

            var assert = m.ClientActions.FirstOrDefault(a => a.Name == "AssertTrue" && a.Public)
                ?? throw new CliError($"{module} has no public client action 'AssertTrue'. Public client actions named Assert*: {string.Join(", ", m.ClientActions.Where(a => a.Public && a.Name.StartsWith("Assert")).Select(a => a.Name).DefaultIfEmpty("(none)"))}.", 1);
            var assertInputs = List(assert, "InputParameters").Cast<IObjectSignature>().ToList();
            var condition = assertInputs.Where(p => ReferenceEquals(Reflect.Get(p, "DataType"), m.BooleanType)).Select(Model.NameOf).ToList() is [var c] ? c!
                : throw new CliError($"{module}.AssertTrue needs exactly one Boolean input for the condition; it has {string.Join(", ", assertInputs.Select(Model.NameOf))}.", 1);
            var message = TextInput(m, assert, "Message");
            if (Mandatory(assert, [condition, message]) is { } other)
                throw new CliError($"{module}.AssertTrue has a mandatory input '{other}' that oml test scaffold cannot fill.", 1);

            var scenarioInput = TextInput(m, scenario, "Description") ?? throw new CliError($"{module}.BDDScenario has no Text input for the scenario description.", 1);
            var stepInput = TextInput(m, step, "Description") ?? throw new CliError($"{module}.BDDStep has no Text input for the step description.", 1);
            foreach (var (block, input) in new[] { (scenario, scenarioInput), (step, stepInput) })
                if (Mandatory(block, [input]) is { } extra)
                    throw new CliError($"{module}.{Model.NameOf(block)} has a mandatory input '{extra}' that oml test scaffold cannot fill.", 1);
            var eventName = Model.NameOf(stepEvent)!;
            return new(module, scenarioInput, placeholder, stepInput, eventName, assert.Name, condition, message,
                ["BDDScenario", "BDDStep", "FinalResult", assert.Name],
                new
                {
                    file, module,
                    scenario = $"BDDScenario({scenarioInput}), placeholder {placeholder}",
                    step = $"BDDStep({stepInput}), event {eventName}",
                    assert = $"{assert.Name}({string.Join(", ", new[] { condition, message }.OfType<string>())})",
                });
        }

        // The Text input that carries a description or message: the one whose name has the hint, else the only Text input.
        static string? TextInput(IESpace m, object owner, string hint)
        {
            var texts = List(owner, "InputParameters").Where(p => ReferenceEquals(Reflect.Get(p, "DataType"), m.TextType)).Select(p => Model.NameOf(p)!).ToList();
            return texts.FirstOrDefault(t => t.Contains(hint, StringComparison.OrdinalIgnoreCase)) ?? (texts.Count == 1 ? texts[0] : null);
        }

        static string? Mandatory(object owner, string?[]? filled) =>
            List(owner, "InputParameters").Where(p => Reflect.Get(p, "IsMandatory") is true).Select(Model.NameOf).FirstOrDefault(n => filled is null || !filled.Contains(n));
    }

    // A parameter of the action under test, with the names and expressions the test screen uses.
    sealed record Parameter(string Name, string Type, string? Todo, string? Empty, string Check, string? OwnedRecord, string? Note);

    sealed record Target(string Name, List<Parameter> Inputs, List<Parameter> Outputs, List<(string exception, string? message)> Raises)
    {
        public IEnumerable<Parameter> Parameters => Inputs.Concat(Outputs);

        public static Target Read(IESpace m, string name)
        {
            var action = m.ServerActions.FirstOrDefault(a => a.Name == name);
            if (action is null || !action.Public)
            {
                var publicOnes = m.ServerActions.Where(a => a.Public).Select(a => a.Name).Order(StringComparer.Ordinal).ToList();
                var why = action is null ? m.ClientActions.Any(a => a.Name == name) ? $"'{name}' is a client action; oml test scaffold tests public server actions" : $"no server action '{name}'"
                    : $"server action '{name}' is not public; a test module can only call public actions";
                throw new CliError($"{((IModule)m).Name}: {why}. Public server actions: {string.Join(", ", publicOnes.DefaultIfEmpty("(none)"))}.", 1);
            }
            Parameter Read(object p) => Describe(m, Model.NameOf(p)!, Reflect.Get(p, "DataType") as ITypeSignature, Reflect.Get(p, "IsMandatory") is true);
            var raises = Walk(action).OfType<OutSystems.Model.Logic.Nodes.IRaiseExceptionNode>()
                .Where(n => n.Exception is IUserException).GroupBy(n => Model.NameOf(n.Exception)!, StringComparer.Ordinal)
                .Select(g => (g.Key, g.Select(n => n.ExceptionMessage?.Text).Distinct().ToList() is [var only] && only is not null && Regex.IsMatch(only, "^\"([^\"]|\"\")*\"$") ? only : null))
                .OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
            return new(name, List(action, "InputParameters").Select(Read).ToList(), List(action, "OutputParameters").Select(Read).ToList(), raises);
        }

        // Type name as the test module writes it, a TODO value that compiles, an empty value, and a "was set" check.
        static Parameter Describe(IESpace m, string name, ITypeSignature? type, bool mandatory)
        {
            var record = type switch { IIdentifierType id => id.Entity as IObjectSignature, IListType { ElementType: IEntitySignature or IStructureSignature } list => (IObjectSignature)list.ElementType, IEntitySignature or IStructureSignature => (IObjectSignature)type, _ => null };
            var owned = record is not null && ReferenceEquals(record.GetESpace(), m) ? Model.NameOf(record) : null;
            var typeName = Model.Describe(m, type);
            if (owned is not null) typeName = typeName.StartsWith("List of ") ? "List of " + ((IModule)m).Name + "." + typeName[8..] : ((IModule)m).Name + "." + typeName;
            var note = record is not null && owned is null ? $"type '{typeName}' of {name} comes from another module; the test module must reference it" : null;
            var nullId = type is IIdentifierType idType && (idType.Entity?.IdentifierAttribute?.DataType ?? type) is var key && (key == m.TextType || key == m.TextIdentifierType) ? "NullTextIdentifier()" : "NullIdentifier()";
            var (todo, empty, check) = type switch
            {
                IIdentifierType => (nullId + Todo, nullId, " <> " + nullId),
                IListType => (null, null, ".Empty = False"),
                _ when record is not null => (null, null, null),
                _ => Model.Describe(m, type) switch
                {
                    "Text" or "Email" or "Phone Number" => ("\"TODO\"", "\"\"", " <> \"\""),
                    "Integer" or "Long Integer" or "Decimal" or "Currency" => ("0" + Todo, null, " <> 0"),
                    "Boolean" => ("False" + Todo, null, " = True"),
                    "Date" => ("#1900-01-01#" + Todo, null, " <> #1900-01-01#"),
                    "Time" => ("#00:00:00#" + Todo, null, " <> #00:00:00#"),
                    "Date Time" => ("#1900-01-01 00:00:00#" + Todo, null, " <> #1900-01-01 00:00:00#"),
                    _ => ((string?)null, (string?)null, (string?)null),
                },
            };
            return new(name, typeName, todo, mandatory ? empty : null, check ?? "", owned, note);
        }
    }

    // One screen per action: a happy path, one scenario per raised user exception, one per mandatory input that can be empty.
    static (string name, string action, List<string> scenarios, JsonNode spec) Screen(Target t, Framework kit, string flow, string producer, List<string> notes)
    {
        var qualified = producer + "." + t.Name;
        notes.AddRange(t.Parameters.Select(p => p.Note).OfType<string>().Select(n => $"{t.Name}: {n}."));
        var locals = new List<object>();
        var actions = new List<object>();
        // A heading: blocks of another module have no look of their own in oml preview, so the screen shows what it tests.
        var widgets = new List<object> { new { type = "Text", text = t.Name + " tests" } };
        var scenarios = new List<string>();

        void Local(string name, string type) => locals.Add(new { name, type });
        object Assert(string name, string condition, string message) => new
        {
            kind = "execute", name, action = kit.Module + "." + kit.Assert,
            args = kit.Message is null ? new Dictionary<string, string> { [kit.Condition] = condition }
                : new Dictionary<string, string> { [kit.Condition] = condition, [kit.Message] = Quote(message) },
        };
        // Given sets every input of the case: TODO values, or a comment for a type with no literal.
        object[] Given(string c, string? emptyInput)
        {
            var assign = new Dictionary<string, string>();
            var unset = new List<string>();
            foreach (var p in t.Inputs)
            {
                Local($"{c}_{p.Name}", p.Type);
                if (p.Name == emptyInput) assign[$"{c}_{p.Name}"] = p.Empty!;
                else if (p.Todo is not null) assign[$"{c}_{p.Name}"] = p.Todo;
                else unset.Add($"{c}_{p.Name} ({p.Type})");
            }
            var flowNodes = new List<object>();
            if (assign.Count > 0) flowNodes.Add(new { kind = "assign", name = "SetInputs", assign });
            // One comment for all of them: two comment nodes without names clash.
            if (unset.Count > 0) flowNodes.Add(new { kind = "comment", text = "TODO: set " + string.Join(", ", unset) + " to test values." });
            return flowNodes.ToArray();
        }
        Dictionary<string, string> Args(string c) => t.Inputs.ToDictionary(p => p.Name, p => $"{c}_{p.Name}");
        void Scenario(string c, string description, string given, string when, string then, object[] givenFlow, object[] whenFlow, object[] thenFlow)
        {
            scenarios.Add(c);
            actions.Add(new { name = c + "_Given", flow = givenFlow });
            actions.Add(new { name = c + "_When", flow = whenFlow });
            actions.Add(new { name = c + "_Then", flow = thenFlow });
            object Step(string text, string action) => new
            {
                type = "BlockInstance", sourceBlock = kit.Module + ".BDDStep",
                args = new Dictionary<string, string> { [kit.StepInput] = Quote(text) }, events = new Dictionary<string, string> { [kit.StepEvent] = action },
            };
            widgets.Add(new
            {
                type = "BlockInstance", name = c + "Scenario", sourceBlock = kit.Module + ".BDDScenario",
                args = new Dictionary<string, string> { [kit.ScenarioInput] = Quote(description) },
                placeholders = new Dictionary<string, object[]> { [kit.Placeholder] = [Step(given, c + "_Given"), Step(when, c + "_When"), Step(then, c + "_Then")] },
            });
        }
        // When for a case that expects a refusal: the handler keeps the exception for Then (no checks in When).
        object[] CatchingWhen(string c) =>
        [
            new { kind = "execute", name = "Call" + t.Name, action = qualified, args = Args(c), next = "End" },
            new { kind = "exceptionHandler", name = "OnError", exception = "AllExceptions" },
            new { kind = "assign", name = "KeepError", assign = new Dictionary<string, string> { [c + "_Raised"] = "True", [c + "_Message"] = "OnError.ExceptionMessage" }, next = "End" },
        ];

        // Happy path: Then checks that each output was set; the checks are TODO until they compare expected values.
        const string happy = "HappyPath";
        var given = Given(happy, null);
        foreach (var o in t.Outputs) Local($"{happy}_{o.Name}", o.Type);
        var when = new List<object> { new { kind = "execute", name = "Call" + t.Name, action = qualified, args = Args(happy) } };
        if (t.Outputs.Count > 0)
            when.Add(new { kind = "assign", name = "KeepResults", assign = t.Outputs.ToDictionary(o => $"{happy}_{o.Name}", o => $"Call{t.Name}.{o.Name}") });
        var checks = t.Outputs.Select(o => Assert("Check" + o.Name, o.Check == "" ? "False /* TODO: check " + o.Name + " */" : $"{happy}_{o.Name}{o.Check} /* TODO: compare with the expected value */", o.Name + " was returned")).ToList();
        if (checks.Count == 0) checks.Add(Assert("CheckEffect", $"False /* TODO: check what {t.Name} changed */", t.Name + " ran without an error"));
        Scenario(happy, $"{t.Name}: happy path", "Given valid inputs (TODO: real test values)", $"When {t.Name} is called",
            t.Outputs.Count > 0 ? "Then " + string.Join(", ", t.Outputs.Select(o => o.Name)) + " are returned" : $"Then {t.Name} has its effect",
            given, when.ToArray(), checks.ToArray());

        foreach (var (exception, message) in t.Raises)
        {
            var c = "Raises" + exception;
            given = Given(c, null);
            Local(c + "_Raised", "Boolean"); Local(c + "_Message", "Text");
            var then = new List<object> { Assert("CheckRaised", c + "_Raised", t.Name + " raised an exception") };
            if (message is not null) then.Add(Assert("CheckMessage", $"{c}_Message = {message}", $"The message is {message}"));
            Scenario(c, $"{t.Name} raises {exception}", $"Given inputs that make {t.Name} raise {exception} (TODO: values)", $"When {t.Name} is called",
                $"Then {exception} is raised", given, CatchingWhen(c), then.ToArray());
        }

        foreach (var input in t.Inputs.Where(p => p.Empty is not null))
        {
            var c = "Empty" + input.Name;
            given = Given(c, input.Name);
            Local(c + "_Raised", "Boolean"); Local(c + "_Message", "Text");
            Scenario(c, $"{t.Name} refuses an empty {input.Name}", $"Given an empty {input.Name}", $"When {t.Name} is called", $"Then {t.Name} refuses it",
                given, CatchingWhen(c),
                [Assert("CheckRefused", $"{c}_Raised /* TODO: or check the outputs, if {t.Name} reports this without an exception */", $"{t.Name} refused an empty {input.Name}")]);
        }

        var clash = locals.Select(l => (string)l.GetType().GetProperty("name")!.GetValue(l)!).GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (clash is not null) throw new CliError($"{t.Name}: two test variables would be named '{clash.Key}' (an input or output has the same name as a generated one). Rename it, or write that scenario by hand.", 1);
        widgets.Add(new { type = "BlockInstance", sourceBlock = kit.Module + ".FinalResult" });
        var screen = t.Name + "_Tests";
        return (screen, t.Name, scenarios, Node(new { name = screen, flow, title = Quote(t.Name + " tests"), locals, actions, widgets }));
    }

    static string Quote(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
}
