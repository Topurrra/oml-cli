using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OutSystems.Model;
using OutSystems.Model.Logic;
using OutSystems.Model.Types;

namespace OmlCli;

record EmailSpec(string Name, string Subject, string Flow = "Emails", List<VarSpec>? Inputs = null, List<VarSpec>? Locals = null,
    List<JsonElement>? Widgets = null, string? StyleSheet = null, string? Description = null);

// Reactive and Mobile emails: a UI flow element built from a small set of widgets, sent by a Send Email node
// in a server action. Its content sees only its own inputs and locals.
partial class Builder
{
    static readonly HashSet<string> EmailWidgets = new(StringComparer.OrdinalIgnoreCase)
        { "Container", "Expression", "If", "Image", "Link", "List", "TableRecords", "HeaderCell", "RowCell", "Text" };

    public void Emails(List<EmailSpec>? emails)
    {
        foreach (var e in emails ?? new()) Try($"email {e.Name}", () => CreateEmail(e));
    }

    void CreateEmail(EmailSpec e)
    {
        var label = $"email {e.Name}";
        if (WebUi.IsTraditional(m) || m.Kind.ToString() == "Service")
            throw new ModelError("emails builds Reactive and Mobile emails; in Traditional Web, create the Email screen in Service Studio and send it with sendEmail");
        if (string.IsNullOrWhiteSpace(e.Subject)) throw new ModelError("subject is required: a Text expression; a literal keeps its quotes inside the JSON string");
        foreach (var w in e.Widgets ?? new()) EmailWidget(w);
        var flow = m.MobileFlows.FirstOrDefault(f => f.Name == e.Flow) ?? NewMobileFlow(e.Flow);
        if (ElementPaths.Children(flow).FirstOrDefault(n => Model.NameOf(n) == e.Name) is { } found)
        {
            // A style sheet is UI (rule 3): a different one replaces the email's.
            if (e.StyleSheet is not null && found is OutSystems.Model.UI.IEmail && Reuse.Css(Reflect.Get(found, "StyleSheet") as string) != Reuse.Css(e.StyleSheet))
            {
                Queue(label, m, found, "StyleSheet", JsonSerializer.SerializeToElement(e.StyleSheet));
                Change($"styleSheet of {label}");
            }
            NotChanged(label, "subject, inputs, locals and widgets", $"edits: set, replaceWidgets with \"at\": \"{ElementPaths.PathTo(m, (IObjectSignature)found) ?? e.Name}\", or addWidgets");
            existing.Add(label);
            return;
        }
        var email = flow.CreateEmail(e.Name, services.NewKey());
        if (!Fresh(email, e.Name, label)) return;
        created.Add(label);
        if (e.Description is not null) email.Description = e.Description;
        Variables(email, "CreateInputParameter", e.Inputs, label);
        Variables(email, "CreateLocalVariable", e.Locals, label);
        Defer(email, ($"{label}.subject", () => email.SetSubject(e.Subject)));
        if (e.StyleSheet is not null) Queue(label, m, email, "StyleSheet", JsonSerializer.SerializeToElement(e.StyleSheet));
        widgets.Add(($"{label}.widgets", () =>
        {
            foreach (var w in e.Widgets ?? new()) Widget(email, w, label, new());
        }));
    }

    // Refuses widgets an email cannot hold, at any depth, before anything is created.
    static void EmailWidget(JsonElement w)
    {
        var type = w.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        if (!EmailWidgets.Contains(type))
            throw new ModelError($"widget '{type}' is not allowed in an email; use {string.Join(", ", EmailWidgets)}");
        foreach (var p in w.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array))
            foreach (var child in p.Value.EnumerateArray()) EmailWidget(child);
    }

    // Reactive: the model API cannot place a Send Email node in any server action. It can create Service Studio's own
    // send action for an email (Send<Email>, as the email's "Create Send Email Action" command does). oml gives that
    // action the inputs From, To, CC, BCC and the email's inputs once, and turns each sendEmail node into a call to it.
    static readonly string[] AddressKeys = { "from", "to", "cc", "bcc" };

    JsonElement ReactiveSendEmail(object action, JsonElement n)
    {
        if (action is not IServerAction) throw new ModelError("sendEmail runs on the server; use it in a server action");
        if (!n.TryGetProperty("email", out var e) || e.ValueKind != JsonValueKind.String)
            throw new ModelError("sendEmail requires email: the name of an email (create one with \"emails\")");
        if (n.TryGetProperty("logContent", out _)) throw new ModelError("logContent is not supported for Reactive emails; remove it");
        var emails = m.MobileFlows.SelectMany(ElementPaths.Children).OfType<OutSystems.Model.UI.IEmail>().ToList();
        var send = SendAction(emails.FirstOrDefault(x => x.Name == e.GetString())
            ?? throw new ModelError($"no email '{e.GetString()}' in this module (emails: {(emails.Count == 0 ? "none; create one with \"emails\"" : string.Join(", ", emails.Select(x => x.Name)))})"));
        var call = (JsonObject)JsonNode.Parse(n.GetRawText())!;
        var args = new JsonObject();
        foreach (var key in AddressKeys)
        {
            if (call[key] is { } value) args[key == "from" || key == "to" ? char.ToUpperInvariant(key[0]) + key[1..] : key.ToUpperInvariant()] = value.DeepClone();
            call.Remove(key);
        }
        if (call["args"] is JsonObject given) foreach (var (k, v) in given) args[k] = v?.DeepClone();
        call.Remove("email");
        call["kind"] = "execute";
        call["name"] ??= "sendEmail";
        call["action"] = Model.NameOf(send);
        call["args"] = args;
        return JsonSerializer.SerializeToElement(call);
    }

    object SendAction(object email)
    {
        var type = email.GetType();
        var name = type.GetMethod("GetSendEmailActionName", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new[] { email }) as string
                   ?? "Send" + Model.NameOf(email);
        if (m.ServerActions.FirstOrDefault(a => a.Name == name) is { } found)
            return ElementPaths.Children(found).Any(x => ReferenceEquals(Reflect.Get(x, "EmailScreen"), email))
                   && new[] { "From", "To" }.All(i => found.InputParameters.Any(p => p.Name == i)) ? found
                : throw new ModelError($"server action '{name}' exists but is not oml's send action for email '{Model.NameOf(email)}' (one that sends it, with inputs From and To)."
                    + " A send action made by Service Studio's own command has no From and To inputs. Rename that action in Service Studio, or give the email another name.");
        var inputs = Elements(Reflect.Get(email, "InputParameters")).ToList();
        if (inputs.Select(Model.NameOf).FirstOrDefault(i => new[] { "From", "To", "CC", "BCC" }.Contains(i, StringComparer.OrdinalIgnoreCase)) is { } clash)
            throw new ModelError($"email input '{clash}' clashes with the send action input of the same name; rename the email input");
        var create = type.GetMethod("CreateSendEmailServerAction", BindingFlags.Public | BindingFlags.Static, new[] { type })
                     ?? throw new ModelError("this Service Studio build cannot add a Send Email node to a Reactive module");
        IServerAction action;
        try { action = (IServerAction)create.Invoke(null, new[] { email })!; }
        catch (TargetInvocationException x) { throw new ModelError($"create send action {name}: {x.InnerException?.Message}"); }
        if (action.Name != name)
        {
            var given = action.Name;
            var taken = ProductionRules.ConsumedClash(action, name) ?? $"{NameClash.Taken(action, name)}; the model would rename it {given}";
            action.Delete();
            throw new ModelError($"server action name '{name}': {taken}");
        }
        action.Public = false; // module-internal helper; public only when the user asks (rule 7)
        created.Add($"serverAction {name}");
        var node = ElementPaths.Children(action).Single(x => ReferenceEquals(Reflect.Get(x, "EmailScreen"), email));
        void Input(string input, ITypeSignature dataType, bool mandatory)
        {
            var p = action.CreateInputParameter(input, services.NewKey());
            p.DataType = dataType;
            p.IsMandatory = mandatory;
        }
        Input("From", m.TextType, true);
        Input("To", m.TextType, true);
        Input("CC", m.TextType, false);
        Input("BCC", m.TextType, false);
        foreach (var i in inputs) Input(Model.NameOf(i)!, (ITypeSignature)Reflect.Get(i, "DataType")!, Reflect.Get(i, "IsMandatory") is true);
        Defer(node, ($"serverAction {name}/send", () =>
        {
            Reflect.Call(node, "SetFrom", "From");
            Reflect.Call(node, "SetTo", "To");
            Reflect.Call(node, "SetCC", "CC");
            Reflect.Call(node, "SetBCC", "BCC");
            Arguments(node, JsonSerializer.SerializeToElement(inputs.ToDictionary(i => Model.NameOf(i)!, i => Model.NameOf(i))));
        }));
        return action;
    }
}
