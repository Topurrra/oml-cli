using System.Text.Json;
using OutSystems.Model;

namespace OmlCli;

static partial class SelfTest
{
    // A block event fired by a Trigger Event node and handled on the block instance, plus a Reactive email sent
    // from a server action. The exception handler keeps the build free of the "no exception handling" warning.
    const string EventsSpec = """
        { "onException": { "name": "OnException", "nodes": [{ "kind": "exceptionHandler", "name": "OnAll", "exception": "AllExceptions" }] },
          "globalExceptionHandler": "MainFlow/OnException",
          "blocks": [{ "name": "Picker", "inputs": [{ "name": "ItemId", "type": "Integer" }], "locals": [{ "name": "Count", "type": "Integer" }],
            "events": [{ "name": "OnPick", "inputs": [{ "name": "Id", "type": "Integer", "mandatory": true }] }, { "name": "OnClose", "mandatory": false }, { "name": "OnSpare", "mandatory": false }],
            "actions": [{ "name": "Pick", "flow": [{ "kind": "triggerEvent", "name": "FirePick", "event": "OnPick", "args": { "Id": "ItemId" } }] },
                        { "name": "Close", "flow": [{ "kind": "triggerEvent", "name": "FireClose", "event": "OnClose" }] },
                        { "name": "Reset", "flow": [{ "kind": "assign", "name": "Clear", "assign": { "Count": "0" } }] }],
            "onParametersChanged": "Reset",
            "widgets": [{ "type": "Button", "name": "PickButton", "onClick": "Pick", "children": [{ "type": "Text", "text": "Pick" }] },
                        { "type": "Button", "name": "CloseButton", "onClick": "Close", "children": [{ "type": "Text", "text": "Close" }] }] }],
          "screens": [{ "name": "PickHost", "locals": [{ "name": "Picked", "type": "Integer" }],
            "actions": [{ "name": "Handle", "inputs": [{ "name": "Id", "type": "Integer" }], "flow": [{ "kind": "assign", "name": "Keep", "assign": { "Picked": "Id" } }] },
                        { "name": "HandleTwice", "inputs": [{ "name": "Value", "type": "Integer" }], "flow": [{ "kind": "assign", "name": "Keep", "assign": { "Picked": "Value" } }] }],
            "widgets": [{ "type": "BlockInstance", "name": "First", "sourceBlock": "Picker", "args": { "ItemId": "1" }, "events": { "OnPick": "Handle" } },
                        { "type": "BlockInstance", "name": "Second", "sourceBlock": "Picker", "args": { "ItemId": "2" },
                          "events": { "OnPick": { "action": "HandleTwice", "args": { "Value": "Id * 2" } }, "OnClose": "Handle2" } }] }],
          "emails": [{ "name": "Notice", "subject": "\"Notice \" + Caption", "inputs": [{ "name": "Caption", "type": "Text" }],
            "widgets": [{ "type": "Container", "children": [{ "type": "Expression", "value": "Caption" }] }] }],
          "serverActions": [{ "name": "NotifyReader", "public": true, "inputs": [{ "name": "To", "type": "Email" }], "flow": [
            { "kind": "sendEmail", "name": "Send", "email": "Notice", "from": "\"sender@example.com\"", "to": "To", "bcc": "\"audit@example.com\"", "args": { "Caption": "\"Hi\"" } }] }] }
        """;

    static bool Says(object report, string text) => JsonSerializer.Serialize(report).Contains(JsonSerializer.Serialize(text)[1..^1]);

    static void EventsTest(string blank, string dir, List<string> failures)
    {
        try
        {
            var spec = Path.Combine(dir, "events.json");
            var output = Path.Combine(dir, "events.oml");
            // Handle2 does not exist yet: the first apply must be refused, naming it; the fixed spec must pass clean.
            File.WriteAllText(spec, EventsSpec);
            var (code, report) = Apply.Execute(blank, spec, output);
            if (code == 0 || !Says(report, "no screen action 'Handle2'")) failures.Add("events: an unknown handler was not refused: " + JsonSerializer.Serialize(report));
            File.WriteAllText(spec, EventsSpec.Replace(", \"OnClose\": \"Handle2\"", ""));
            (code, report) = Apply.Execute(blank, spec, output);
            var json = JsonSerializer.SerializeToElement(report);
            if (code != 0) { failures.Add("events: " + json); return; }
            // NotifyReader is a public server action: its PublicServerAction warning (rule 7) is expected.
            if (json.TryGetProperty("newWarnings", out var warnings) && warnings.EnumerateArray().Any(w => w.GetProperty("id").GetString() != "PublicServerAction")) failures.Add("events: new warnings " + warnings);
            using (var built = Model.Load(output))
            {
                if (Model.Errors(built).Length > 0) failures.Add("events: errors after reopen");
                var picker = ElementPaths.Resolve(built, "MainFlow/Picker");
                var events = ((System.Collections.IEnumerable)Reflect.Get(picker, "Events")!).Cast<OutSystems.Model.UI.IBlockEvent>().ToList();
                var pick = events.SingleOrDefault(e => e.Name == "OnPick");
                if (pick is null || !pick.IsMandatory || pick.InputParameters.SingleOrDefault()?.Name != "Id"
                    || events.SingleOrDefault(e => e.Name == "OnClose") is not { IsMandatory: false })
                    failures.Add("events: block events did not round-trip");
                var trigger = Descendants(picker).OfType<OutSystems.Model.Logic.Nodes.ITriggerNode>().SingleOrDefault(n => n.Name == "FirePick");
                if (trigger is null || !ReferenceEquals(trigger.Event, pick) || trigger.Arguments.Single().Value.Text != "ItemId")
                    failures.Add("events: Trigger Event node lost its event or argument");
                if (Model.NameOf(Reflect.Get(Reflect.Get(picker, "OnParametersChanged")!, "Destination") ?? "") != "Reset")
                    failures.Add("events: OnParametersChanged lost its action");
                var host = ElementPaths.Resolve(built, "MainFlow/PickHost");
                var second = Descendants(host).OfType<OutSystems.Model.UI.Mobile.Widgets.IMobileBlockInstanceWidget>().Single(w => w.Name == "Second");
                var handler = second.EventHandlers.Single(h => h.Event.Name == "OnPick");
                if (Model.NameOf(handler.Handler) != "HandleTwice" || handler.Arguments.Single().Value.Text != "Id * 2")
                    failures.Add("events: block instance handler or argument did not round-trip");
                var text = JsonSerializer.SerializeToElement(Describe.Run(built, "MainFlow/Picker", 0, [])).GetProperty("text").GetString()!
                           + JsonSerializer.SerializeToElement(Describe.Run(built, "MainFlow/PickHost", 0, [])).GetProperty("text").GetString()!
                           + JsonSerializer.SerializeToElement(Describe.Run(built, "NotifyReader", 1, [])).GetProperty("text").GetString()!;
                foreach (var line in new[] { "event OnPick(Id: Integer) [mandatory]", "OnParametersChanged -> Reset", "trigger OnPick(Id = ItemId)",
                             "event OnPick=Handle(Id = Id)", "event OnPick=HandleTwice(Value = Id * 2)",
                             "call OmlSelfTest.SendNotice(BCC = \"audit@example.com\", Caption = \"Hi\", From = \"sender@example.com\", To = To)",
                             "send email Notice from From to To(Caption = Caption)" }.Where(l => !text.Contains(l, StringComparison.Ordinal)))
                    failures.Add("events: describe missing: " + line);
            }
            // Refusals: a mandatory event without a handler, a Trigger Event outside a block, and a widget an email cannot hold.
            foreach (var (bad, expected) in new[]
            {
                (EventsSpec.Replace("\"events\": { \"OnPick\": \"Handle\" }", "\"events\": {}"), "event 'OnPick' of block 'Picker' is mandatory"),
                (EventsSpec.Replace("\"flow\": [{ \"kind\": \"assign\", \"name\": \"Keep\", \"assign\": { \"Picked\": \"Id\" } }]", "\"flow\": [{ \"kind\": \"triggerEvent\", \"event\": \"OnPick\" }]"), "only allowed in a client action of a block"),
                (EventsSpec.Replace("\"type\": \"Container\", \"children\"", "\"type\": \"Button\", \"children\""), "widget 'Button' is not allowed in an email"),
            })
            {
                File.WriteAllText(spec, bad.Replace(", \"OnClose\": \"Handle2\"", ""));
                var refused = Apply.Execute(blank, spec, Path.Combine(dir, $"events-bad-{Guid.NewGuid():N}.oml"));
                if (refused.code == 0 || !Says(refused.report, expected))
                    failures.Add($"events: not refused with '{expected}': {JsonSerializer.Serialize(refused.report)}");
            }
        }
        catch (Exception e) { failures.Add("events: " + (e.InnerException ?? e).Message); }
    }

    // Follow-up checks on the module EventsTest built: optional inputs, existing-block rules, idempotent re-apply,
    // the send action's shape, widget events, and a flow name taken by a referenced module.
    static void EventsFollowUpTest(string blank, string dir, List<string> failures)
    {
        var built = Path.Combine(dir, "events.oml");
        if (!File.Exists(built)) return; // EventsTest already reported why
        var spec = Path.Combine(dir, "events-more.json");
        (int code, object report) Run(string source, string json)
        {
            File.WriteAllText(spec, json);
            return Apply.Execute(source, spec, Path.Combine(dir, $"events-more-{Guid.NewGuid():N}.oml"));
        }
        void Accept(string what, string source, string json)
        {
            var (code, report) = Run(source, json);
            if (code != 0) failures.Add($"events: {what} was refused: {JsonSerializer.Serialize(report)}");
        }
        void Refuse(string what, string source, string json, string expected)
        {
            var (code, report) = Run(source, json);
            if (code == 0 || !Says(report, expected)) failures.Add($"events: {what} not refused with '{expected}': {JsonSerializer.Serialize(report)}");
        }
        try
        {
            // "mandatory": false makes an input optional: calls, instances, triggers and sends without that argument pass.
            Accept("optional inputs", blank, """
                { "serverActions": [{ "name": "Callee", "public": true, "inputs": [{ "name": "A", "type": "Integer", "mandatory": false }] },
                    { "name": "Caller", "public": true, "flow": [{ "kind": "execute", "action": "Callee" }] },
                    { "name": "Mail", "public": true, "flow": [{ "kind": "sendEmail", "email": "E", "from": "\"a@example.com\"", "to": "\"b@example.com\"" }] }],
                  "blocks": [{ "name": "B", "inputs": [{ "name": "X", "type": "Integer", "mandatory": false }],
                    "events": [{ "name": "OnE", "mandatory": false, "inputs": [{ "name": "Id", "type": "Integer", "mandatory": false }] }],
                    "actions": [{ "name": "Fire", "flow": [{ "kind": "triggerEvent", "event": "OnE" }] }],
                    "widgets": [{ "type": "Button", "onClick": "Fire", "children": [{ "type": "Text", "text": "x" }] }] }],
                  "screens": [{ "name": "S", "widgets": [{ "type": "BlockInstance", "sourceBlock": "B" }] }],
                  "emails": [{ "name": "E", "subject": "\"s\"", "inputs": [{ "name": "N", "type": "Text", "mandatory": false }] }] }
                """);
            // Without the key an input keeps the model default, mandatory: the same call is refused.
            Refuse("omitted mandatory", blank, """
                { "serverActions": [{ "name": "Callee", "public": true, "inputs": [{ "name": "A", "type": "Integer" }] },
                    { "name": "Caller", "public": true, "flow": [{ "kind": "execute", "action": "Callee" }] }] }
                """, "A valid expression must be set for parameter 'A'");

            // Existing blocks: rule 3 (no rename or delete of an event), rule 4 (no new mandatory event, no event on a public block).
            Refuse("rename of an existing event", built, """{ "edits": [{ "op": "rename", "element": "MainFlow/Picker/OnPick", "to": "OnChoose" }] }""", "refused: 'OnPick'");
            Refuse("delete of an existing event", built, """{ "edits": [{ "op": "delete", "element": "MainFlow/Picker/OnSpare" }] }""", "refused: 'OnSpare'");
            Refuse("existing event made mandatory", built, """{ "edits": [{ "op": "set", "element": "MainFlow/Picker/OnClose", "properties": { "IsMandatory": true } }] }""", "existing optional event");
            Refuse("new mandatory event on an existing block", built, """{ "edits": [{ "op": "addParts", "screen": "MainFlow/Picker", "events": [{ "name": "OnLater" }] }] }""", "existing instances have no handler");
            Accept("new optional event on an existing block", built, """{ "edits": [{ "op": "addParts", "screen": "MainFlow/Picker", "events": [{ "name": "OnLater", "mandatory": false }] }] }""");
            var publicBlock = Path.Combine(dir, "events-public.oml");
            // A new block made public in its own spec, for the rule 4 check of a new event on an existing public block.
            File.WriteAllText(spec, """{ "blocks": [{ "name": "SharedPicker" }], "edits": [{ "op": "set", "element": "MainFlow/SharedPicker", "properties": { "Public": true } }] }""");
            if (Apply.Execute(built, spec, publicBlock).code != 0) failures.Add("events: could not create the public block SharedPicker for the rule 4 check");
            else Refuse("new event on an existing public block", publicBlock, """{ "edits": [{ "op": "addParts", "screen": "MainFlow/SharedPicker", "events": [{ "name": "OnLater", "mandatory": false }] }] }""", "existing public block");

            // Handlers through addWidgets: a handled instance is accepted, an unhandled one names the mandatory event.
            Accept("addWidgets instance with handler", built, """{ "edits": [{ "op": "addWidgets", "screen": "MainFlow/PickHost", "widgets": [{ "type": "BlockInstance", "name": "Third", "sourceBlock": "Picker", "args": { "ItemId": "3" }, "events": { "OnPick": "Handle" } }] }] }""");
            Refuse("addWidgets instance without handler", built, """{ "edits": [{ "op": "addWidgets", "screen": "MainFlow/PickHost", "widgets": [{ "type": "BlockInstance", "sourceBlock": "Picker", "args": { "ItemId": "3" } }] }] }""", "event 'OnPick' of block 'Picker' is mandatory");
            // events on other widgets, an argument that is not a string, number or true/false, and a duplicate event name are refused.
            // events on other widgets are JavaScript events (onblur, onmouseover, ...): a block event name there is refused, a JavaScript one accepted.
            Refuse("a block event on a Button", built, """{ "edits": [{ "op": "addWidgets", "screen": "MainFlow/PickHost", "widgets": [{ "type": "Button", "onClick": "Handle", "args": { "Id": "1" }, "events": { "OnPick": "Handle" } }] }] }""", "not a JavaScript event name");
            Accept("a JavaScript event on a Container, with an argument", built, """{ "edits": [{ "op": "addWidgets", "screen": "MainFlow/PickHost", "widgets": [{ "type": "Container", "events": { "onmouseover": { "action": "Handle", "args": { "Id": "1" } } }, "children": [{ "type": "Text", "text": "x" }] }] }] }""");
            // The other events of the model: OnSort hands ClickedColumn to its handler; process and node callbacks with Create and Update
            // triggers; OnRequest and OnResponse of an exposed REST API.
            Accept("OnSort binds ClickedColumn", blank, """{ "entities": [{ "name": "Row1", "attributes": [{ "name": "Name" }] }], "screens": [{ "name": "Sorted", "aggregates": [{ "name": "GetRows", "sources": ["Row1"] }], "actions": [{ "name": "SortIt", "inputs": [{ "name": "ClickedColumn", "type": "Text" }], "flow": [] }], "widgets": [{ "type": "TableRecords", "source": "GetRows.List", "onSort": "SortIt", "headerRow": [{ "type": "HeaderCell", "children": [{ "type": "Text", "text": "Name" }] }], "row": [{ "type": "RowCell", "children": [{ "type": "Expression", "value": "GetRows.List.Current.Row1.Name" }] }] }] }] }""");
            Accept("process and node callbacks, Create and Update triggers", blank, """{ "entities": [{ "name": "Ticket", "attributes": [{ "name": "Title" }] }], "serverActions": [{ "name": "Noop", "flow": [] }], "processes": [{ "name": "TicketFlow", "launchOn": { "entity": "Ticket", "event": "Create" }, "onProcessStart": [{ "kind": "execute", "name": "Log", "action": "Noop" }], "onActivityClose": { "flow": [] }, "flow": [{ "kind": "wait", "name": "Updated", "closeOn": { "entity": "Ticket", "event": "Update" }, "onClose": [{ "kind": "execute", "name": "Log2", "action": "Noop" }] }, { "kind": "automaticActivity", "name": "Do", "action": "Noop" }] }] }""");
            Accept("REST service OnRequest and OnResponse", blank, """{ "restServices": [{ "name": "Api", "authentication": "custom", "onAuthentication": { "flow": [] }, "onRequest": { "flow": [{ "kind": "assign", "name": "Same", "assign": { "CustomizedRequest": "Request" } }] }, "onResponse": { "flow": [{ "kind": "assign", "name": "Same2", "assign": { "CustomizedResponse": "Response" } }] }, "actions": [{ "name": "Ping", "method": "GET", "flow": [] }] }] }""");
            // Process flows change and stay connected: insert after a node and on a Decision branch, connect, replace in place and with
            // another kind, delete with reconnect; a Decision with two exits is not deleted.
            Accept("process edits", blank, """{ "entities": [{ "name": "Case1", "attributes": [{ "name": "Title" }] }], "serverActions": [{ "name": "Noop", "flow": [] }, { "name": "Other", "flow": [] }], "processes": [{ "name": "CaseFlow", "launchOn": "Case1", "flow": [{ "kind": "decision", "name": "Check", "condition": "True", "true": "Hold", "false": "End" }, { "kind": "wait", "name": "Hold", "closeOn": { "entity": "Case1", "event": "Update" } }, { "kind": "automaticActivity", "name": "Work", "action": "Noop" }] }], "edits": [{ "op": "insertNodes", "action": "Process:CaseFlow", "after": "Hold", "flow": [{ "kind": "automaticActivity", "name": "Extra", "action": "Noop" }] }, { "op": "insertNodes", "action": "Process:CaseFlow", "after": "Check", "connector": "false", "flow": [{ "kind": "automaticActivity", "name": "OnNo", "action": "Noop" }] }, { "op": "replaceNode", "element": "Process:CaseFlow/Work", "node": { "kind": "automaticActivity", "action": "Other" } }, { "op": "connect", "element": "Process:CaseFlow/OnNo", "to": "Work" }, { "op": "delete", "element": "Process:CaseFlow/Extra" }] }""");
            Accept("parallel branches and child processes", blank, """{ "serverActions": [{ "name": "Noop3", "flow": [] }], "processes": [{ "name": "ChildOne", "inputs": [{ "name": "Note", "type": "Text" }], "flow": [{ "kind": "automaticActivity", "name": "WorkOne", "action": "Noop3" }] }, { "name": "ChildTwo", "flow": [{ "kind": "automaticActivity", "name": "WorkTwo", "action": "Noop3" }] }, { "name": "Parent", "flow": [{ "kind": "start", "name": "Start", "next": ["RunOne", "RunTwo"] }, { "kind": "executeProcess", "name": "RunOne", "process": "ChildOne", "args": { "Note": "\"a\"" }, "next": "End" }, { "kind": "executeProcess", "name": "RunTwo", "process": "ChildTwo", "next": "End" }] }], "edits": [{ "op": "insertNodes", "action": "Process:Parent", "after": "Start", "branch": "RunTwo", "flow": [{ "kind": "automaticActivity", "name": "Before2", "action": "Noop3" }] }, { "op": "connect", "element": "Process:Parent/Start", "to": ["RunOne", "Before2", "End"] }] }""");
            Refuse("delete of a Decision", blank, """{ "entities": [{ "name": "Case2", "attributes": [{ "name": "Title" }] }], "serverActions": [{ "name": "Noop2", "flow": [] }], "processes": [{ "name": "CaseFlow2", "launchOn": "Case2", "flow": [{ "kind": "decision", "name": "Check2", "condition": "True", "true": "Work2", "false": "End" }, { "kind": "automaticActivity", "name": "Work2", "action": "Noop2" }] }], "edits": [{ "op": "delete", "element": "Process:CaseFlow2/Check2" }] }""", "Decision with two exits");
            Accept("test case, entity diagram and email attachments", blank, """{ "entities": [{ "name": "Note3", "attributes": [{ "name": "Body" }] }], "serverActions": [{ "name": "Twice", "inputs": [{ "name": "N", "type": "Integer" }], "outputs": [{ "name": "R", "type": "Integer" }], "flow": [{ "kind": "assign", "assign": { "R": "N * 2" } }] }, { "name": "MailIt", "locals": [{ "name": "Files", "type": "List of Attachment" }], "flow": [{ "kind": "sendEmail", "email": "Report3", "from": "\"a@example.com\"", "to": "\"b@example.com\"", "attachments": "Files" }] }], "emails": [{ "name": "Report3", "subject": "\"Report\"", "widgets": [{ "type": "Text", "text": "Attached" }] }], "tests": [{ "name": "DoublesTwo", "flow": [{ "kind": "execute", "name": "Run", "action": "Twice", "args": { "N": "2" } }, { "kind": "assert", "name": "IsFour", "condition": "Run.R = 4" }] }], "entityDiagrams": [{ "name": "Notes", "entities": ["Note3"] }] }""");
            Accept("external site from a link and a destination node", blank, """{ "externalSites": [{ "name": "HelpSite", "url": "https://help.example.com", "inputs": [{ "name": "Topic", "type": "Text" }] }], "screens": [{ "name": "GoesOut", "actions": [{ "name": "ToHelp", "flow": [{ "kind": "destination", "name": "Out", "screen": "HelpSite", "args": { "Topic": "\"b\"" } }] }], "widgets": [{ "type": "Link", "onClick": { "destination": "HelpSite", "args": { "Topic": "\"a\"" } }, "children": [{ "type": "Text", "text": "Help" }] }] }] }""");
            Refuse("array handler argument", built, """{ "edits": [{ "op": "addWidgets", "screen": "MainFlow/PickHost", "widgets": [{ "type": "BlockInstance", "sourceBlock": "Picker", "args": { "ItemId": "3" }, "events": { "OnPick": { "action": "HandleTwice", "args": { "Value": [5] } } } }] }] }""", "argument 'Value' must be an expression string, a number or true/false");
            Refuse("duplicate event name", blank, """{ "blocks": [{ "name": "Twice", "events": [{ "name": "OnX", "mandatory": false }, { "name": "OnX", "mandatory": false }] }] }""", "event 'OnX' is listed more than once");

            // Re-applying the spec changes nothing, and a second sender reuses the one send action.
            var again = Path.Combine(dir, "events-again.oml");
            File.WriteAllText(spec, EventsSpec.Replace(", \"OnClose\": \"Handle2\"", "").Replace("\"name\": \"NotifyReader\"", "\"name\": \"NotifyAgain\""));
            var (code, report) = Apply.Execute(built, spec, again);
            if (code != 0) failures.Add("events: re-apply failed: " + JsonSerializer.Serialize(report));
            else
            {
                using var m = Model.Load(again);
                var picker = ElementPaths.Resolve(m, "MainFlow/Picker");
                var instances = Descendants(ElementPaths.Resolve(m, "MainFlow/PickHost")).OfType<OutSystems.Model.UI.Mobile.Widgets.IMobileBlockInstanceWidget>().ToList();
                if (Describe.List(picker, "Events").Count() != 3 || instances.Count != 2
                    || instances.Sum(i => i.EventHandlers.Count(h => h.Handler is not null)) != 2)
                    failures.Add("events: re-apply duplicated events, instances or handlers");
                var sends = m.ServerActions.Where(a => a.Name.StartsWith("SendNotice")).ToList();
                if (sends.Count != 1 || sends[0].Name != "SendNotice") failures.Add("events: a second sender did not reuse SendNotice");
                else
                {
                    if (sends[0].Public) failures.Add("events: SendNotice is public");
                    var node = ElementPaths.Children(sends[0]).Single(x => Reflect.Get(x, "EmailScreen") is not null);
                    foreach (var key in new[] { "From", "To", "CC", "BCC" })
                        if ((Reflect.Get(node, key) as IExpression)?.Text != key) failures.Add($"events: SendNotice {key} is not wired to its input");
                }
                if (!m.ServerActions.Any(a => a.Name == "NotifyAgain")) failures.Add("events: second sender missing");
            }

            // A producer's MainFlow takes the name: a consumer screen in MainFlow is refused, another flow name works,
            // and the referenced block's event is handled like a local one.
            var producerSource = Path.Combine(dir, "events-producer-blank.oml");
            var producer = Path.Combine(dir, "events-producer.oml");
            using (var module = Model.Services().CreateESpace(OutSystems.Model.Enumerations.SegmentationKind.CrossDevice, "EventsProducer", "", false, Model.Services().NewKey()))
                Model.Save(module, producerSource);
            File.WriteAllText(spec, """
                { "blocks": [{ "name": "Chooser", "events": [{ "name": "OnChoose", "inputs": [{ "name": "Id", "type": "Integer" }] }],
                    "actions": [{ "name": "Choose", "flow": [{ "kind": "triggerEvent", "event": "OnChoose", "args": { "Id": "1" } }] }],
                    "widgets": [{ "type": "Button", "onClick": "Choose", "children": [{ "type": "Text", "text": "Choose" }] }] }],
                  "edits": [{ "op": "set", "element": "MainFlow/Chooser", "properties": { "Public": true } }] }
                """);
            if (Apply.Execute(producerSource, spec, producer).code != 0) { failures.Add("events: producer setup failed"); return; }
            var consumer = """
                { "references": [{ "producer": PRODUCER, "elements": ["Chooser"] }],
                  "screens": [{ "name": "Home", "flow": "FLOW", "actions": [{ "name": "Chosen", "inputs": [{ "name": "Id", "type": "Integer" }] }],
                    "widgets": [{ "type": "BlockInstance", "sourceBlock": "EventsProducer.Chooser", "events": { "OnChoose": "Chosen" } }] }] }
                """.Replace("PRODUCER", JsonSerializer.Serialize(producer));
            Refuse("screen in a flow name a reference takes", blank, consumer.Replace("FLOW", "MainFlow"), "UI flow name 'MainFlow' is taken");
            Accept("referenced block event handled", blank, consumer.Replace("FLOW", "AppFlow"));
        }
        catch (Exception e) { failures.Add("events follow-up: " + (e.InnerException ?? e).Message); }
    }
}
