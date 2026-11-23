using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Enumerations;

namespace OmlCli;

static partial class SelfTest
{
    static void LogicTest(string blank, string dir, List<string> failures)
    {
        const string spec = """
            {
              "clientVariables":[{"name":"Status","default":"\"Ready\""}],
              "clientActions":[{"name":"Notify","flow":[
                {"kind":"comment","text":"Report status"},
                {"kind":"message","name":"ShowStatus","message":"Client.Status","type":"info"},
                {"kind":"exceptionHandler","name":"OnError","exception":"AllExceptions","abortTransaction":true,"logError":false,"next":"End"}
              ]}],
              "globalExceptionHandler":"Notify",
              "systemEvents":{"onApplicationReady":"Notify","onApplicationResume":"Notify"},
              "edits":[{"op":"addExceptionHandler","action":"Notify","flow":[
                {"kind":"exceptionHandler","name":"OnDatabase","exception":"DatabaseException","next":"End"}
              ]}]
            }
            """;
        var path = Path.Combine(dir, "logic-spec.json");
        var output = Path.Combine(dir, "logic.oml");
        File.WriteAllText(path, spec);
        var (code, report) = Apply.Execute(blank, path, output);
        if (code != 0) { failures.Add("logic: " + JsonSerializer.Serialize(report)); return; }
        using (var built = Model.Load(output))
        {
            if (built.GlobalExceptionHandler is null) failures.Add("global handler did not round-trip");
            if (built.ClientVariables.SingleOrDefault(v => v.Name == "Status")?.DefaultValue.Text != "\"Ready\"")
                failures.Add("client variable default did not round-trip");
            foreach (var ev in new[] { built.OnApplicationReady, built.OnApplicationResume })
                if (ev is null || !ev.Nodes.Any(n => Reflect.Get(n, "Action") is { } action && Model.NameOf(action) == "Notify"))
                    failures.Add("system event lost its action call");
            var notify = built.ClientActions.Single(a => a.Name == "Notify");
            var handlers = notify.Nodes.OfType<OutSystems.Model.Logic.Nodes.IExceptionHandlerNode>().ToList();
            if (handlers.Count != 2 || handlers.Any(h => h.IncomingConnectors.Any()))
                failures.Add("exception handlers missing or connected to main flow");
            if (handlers.Single(h => h.Name == "OnError") is { } error
                && (!error.AbortTransaction || error.LogError || !ReferenceEquals(error.Exception, built.AllExceptions)))
                failures.Add("exception handler settings did not round-trip");
        }

        var webSource = Path.Combine(dir, "logic-web-source.oml");
        using (var web = Model.Services().CreateESpace(SegmentationKind.WebResponsive, "LogicEmail", "", false, Model.Services().NewKey()))
        {
            // Adapted from OutSystems/outsystems11-mcp examples/AddServerActionSendEmail.cs (MIT).
            var flow = web.CreateWebFlow("Emails", Model.Services().NewKey());
            var email = flow.CreateEmail("Notice", Model.Services().NewKey());
            email.CreateInputParameter("Caption", Model.Services().NewKey()).DataType = web.TextType;
            email.SetSubject("Caption");
            Model.Save(web, webSource);
        }
        File.WriteAllText(path, """
            {"sessionVariables":[{"name":"Count","type":"Integer","default":"0"}],
             "serverActions":[{"name":"SendNotice","flow":[
               {"kind":"assign","name":"CountRequest","assign":{"Session.Count":"Session.Count + 1"}},
               {"kind":"sendEmail","name":"Send","email":"Notice","from":"\"sender@example.com\"","to":"\"reader@example.com\"","args":{"Caption":"\"Notice\""}}
             ]}],"systemEvents":{"onSessionStart":"SendNotice","onBeginWebRequest":"SendNotice"}}
            """);
        output = Path.Combine(dir, "logic-web.oml");
        (code, report) = Apply.Execute(webSource, path, output);
        if (code != 0) failures.Add("logic web/email: " + JsonSerializer.Serialize(report));
        else
        {
            using var web = Model.Load(output);
            if (web.OnSessionStart is null || web.OnBeginWebRequest is null || !web.SessionVariables.Any(v => v.Name == "Count"))
                failures.Add("web system events or session variable missing");
            if (!web.ServerActions.Single(a => a.Name == "SendNotice").Nodes.Any(n => Reflect.Get(n, "Email") is { } email && Model.NameOf(email) == "Notice"))
                failures.Add("sendEmail lost its Email reference");
        }

        // Traditional Web: an email with a Preparation that attaches a file, a process that sends it next to a comment, Ajax Refresh
        // of a named container, UI flow comments and an entry, module settings.
        File.WriteAllText(path, """
            {"module":{"useCookies":false,"invalidEmail":"Bad email address"},
             "emails":[{"name":"Invoice","subject":"\"Your invoice\"","inputs":[{"name":"Id","type":"Text"}],"locals":[{"name":"Doc","type":"Binary Data"}],
               "preparation":[{"kind":"attachFile","fileName":"\"invoice.pdf\"","fileContent":"Doc","mimeType":"\"application/pdf\""}],
               "widgets":[{"type":"Expression","value":"Id"}]}],
             "serverActions":[{"name":"Prepare","flow":[]}],
             "processes":[{"name":"Notify","flow":[{"kind":"comment","text":"Sends the invoice"},{"kind":"automaticActivity","name":"Work","action":"Prepare"},
               {"kind":"sendEmail","name":"SendIt","email":"Invoice","from":"\"a@example.com\"","to":"\"b@example.com\"","args":{"Id":"\"1\""}}]}],
             "screens":[{"name":"Home","actions":[{"name":"Again","flow":[{"kind":"ajaxRefresh","widget":"Panel","animationEffect":"Highlight"}]}],
               "widgets":[{"type":"Container","name":"Panel","children":[{"type":"Expression","value":"CurrDateTime()"}]},{"type":"Button","onClick":"Again","children":[{"type":"Text","text":"Go"}]}]},
               {"name":"Other"}],
             "uiFlows":[{"name":"MainFlow","comments":["Public pages"],"entries":[{"name":"Start","screen":"Other"}]}]}
            """);
        var gaps = Path.Combine(dir, "logic-web-gaps.oml");
        (code, report) = Apply.Execute(webSource, path, gaps);
        if (code != 0) failures.Add("traditional gaps: " + JsonSerializer.Serialize(report));
        else
        {
            File.WriteAllText(path, """{"processes":[{"name":"Notify","flow":[{"kind":"comment","text":"Sends the invoice"}]}],"uiFlows":[{"name":"MainFlow","comments":["Public pages"],"entries":[{"name":"Start","screen":"Home"}]}]}""");
            var again = Path.Combine(dir, "logic-web-gaps-again.oml");
            (code, report) = Apply.Execute(gaps, path, again);
            if (code != 0) failures.Add("traditional gaps listed again: " + JsonSerializer.Serialize(report));
            else
            {
                using var web = Model.Load(again);
                var ajax = ElementPaths.Children(ElementPaths.Resolve(web, "MainFlow/Home/Again")).OfType<OutSystems.Model.Logic.Nodes.IAjaxRefreshNode>().SingleOrDefault();
                if (ajax is null || Model.NameOf(ajax.WidgetOrWebBlock) != "Panel") failures.Add("ajaxRefresh does not refresh Panel");
                var email = web.WebFlows.SelectMany(f => f.Nodes).OfType<OutSystems.Model.UI.Web.IWebEmail>().Single(e => e.Name == "Invoice");
                if (email.Preparation?.Nodes.OfType<OutSystems.Model.Logic.Nodes.IAttachFileNode>().Count() != 1) failures.Add("the email preparation has no attachFile");
                var process = web.Processes.Single(p => p.Name == "Notify");
                if (process.Nodes.OfType<OutSystems.Model.Processes.Nodes.ICommentNode>().Count() != 1) failures.Add("process comment missing or doubled");
                if (process.Nodes.OfType<OutSystems.Model.Processes.Nodes.ISendEmailNode>().SingleOrDefault() is not { Email.Name: "Invoice" }) failures.Add("process sendEmail lost its email");
                var main = web.WebFlows.Single(f => f.Name == "MainFlow");
                if (main.Nodes.OfType<OutSystems.Model.UI.Web.IComment>().Count() != 1) failures.Add("UI flow comment missing or doubled");
                if (main.Nodes.OfType<OutSystems.Model.UI.Web.IEntry>().SingleOrDefault() is not { } entry || Model.NameOf(entry.Target) != "Home") failures.Add("entry Start does not go to Home after it was listed again");
                if (web.UseCookies || web.InvalidEmail != "Bad email address") failures.Add("module settings useCookies or invalidEmail not set");
            }
            // "isDefault": true on a Traditional screen: the default Entry, never DefaultScreen (the Traditional compiler stops
            // on a module with one). Listed again, the same Entry; no second one.
            if (code == 0)
            {
                File.WriteAllText(path, """{"screens":[{"name":"Landing","isDefault":true}]}""");
                var startHere = Path.Combine(dir, "logic-web-default.oml");
                var startAgain = Path.Combine(dir, "logic-web-default-again.oml");
                (code, report) = Apply.Execute(again, path, startHere);
                if (code == 0) (code, report) = Apply.Execute(startHere, path, startAgain);
                if (code != 0) failures.Add("traditional isDefault: " + JsonSerializer.Serialize(report));
                else
                {
                    using var start = Model.Load(startAgain);
                    var entries = start.WebFlows.SelectMany(f => f.Nodes).OfType<OutSystems.Model.UI.Web.IEntry>().ToList();
                    var defaults = entries.Where(e => e.IsDefault).ToList();
                    if (Reflect.Get(start, "DefaultScreen") is not null) failures.Add("traditional isDefault set DefaultScreen (the compiler fails on it)");
                    if (defaults.Count != 1 || Model.NameOf(defaults[0].Target) != "Landing") failures.Add($"traditional isDefault: {defaults.Count} default entries, to {string.Join(",", defaults.Select(e => Model.NameOf(e.Target)))}");
                    if (entries.Count(e => Model.NameOf(e.Target) == "Landing") != 1) failures.Add("traditional isDefault listed again made a second entry");
                }
            }
        }

        // Traditional widgets without a public model interface (Image, List Box, Label, Editable Table, Upload) and an image per locale.
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        File.WriteAllBytes(Path.Combine(dir, "pic.png"), png);
        File.WriteAllBytes(Path.Combine(dir, "pic-pt.png"), png);
        File.WriteAllText(path, """
            {"entities":[{"name":"Item","attributes":[{"name":"Title"},{"name":"Done","type":"Boolean"}]}],
             "images":[{"name":"Pic","file":PIC,"localized":{"pt-PT":PICPT}}],
             "screens":[{"name":"Widgets","locals":[{"name":"Name","type":"Text"}],"preparation":[{"kind":"aggregate","name":"GetItems","sources":["Item"]}],
               "widgets":[{"type":"Image","name":"Logo","image":"Pic"},
                 {"type":"ListBox","name":"Box","source":"GetItems.List","sourceAttribute":"Item.Title","selectionAttribute":"Item.Done"},
                 {"type":"Label","name":"Lbl","value":"\"Name\"","targetWidget":"NameIn"},{"type":"Input","name":"NameIn","variable":"Name"},
                 {"type":"Upload","name":"Up"},
                 {"type":"EditableTable","name":"Grid","source":"GetItems.List",
                  "headerCells":[{"type":"EditableTableCell","children":[{"type":"Text","text":"Title"}]}],
                  "bodyCells":[{"type":"EditableTableCell","children":[{"type":"Input","name":"TitleIn","variable":"Grid.List.Current.Item.Title"}]}]}]}]}
            """.Replace("PICPT", JsonSerializer.Serialize(Path.Combine(dir, "pic-pt.png"))).Replace("PIC", JsonSerializer.Serialize(Path.Combine(dir, "pic.png"))));
        var widgetsOut = Path.Combine(dir, "logic-web-widgets.oml");
        (code, report) = Apply.Execute(webSource, path, widgetsOut);
        if (code != 0) failures.Add("traditional widgets: " + JsonSerializer.Serialize(report));
        else
        {
            using var web = Model.Load(widgetsOut);
            var screen = ElementPaths.Resolve(web, "MainFlow/Widgets");
            var parts = ProductionRules.Walk(screen).ToList();
            object Named(string n) => parts.First(o => Model.NameOf(o) == n);
            if (Model.NameOf(Reflect.Get(Named("Logo"), "ActualImage") ?? "") != "Pic") failures.Add("Traditional Image does not show Pic");
            if (Model.NameOf(Reflect.Get(Named("Box"), "SourceAttribute") ?? "") != "Title" || Model.NameOf(Reflect.Get(Named("Box"), "SelectionAttribute") ?? "") != "Done")
                failures.Add("Traditional List Box attributes not set");
            if (Model.NameOf(Reflect.Get(Named("Lbl"), "TargetWidget") ?? "") != "NameIn") failures.Add("Traditional Label does not target NameIn");
            if (!parts.Any(o => Model.NameOf(o) == "TitleIn")) failures.Add("Editable Table body cell lost its input");
            if (web.Images.Single(i => i.Name == "Pic").LocalizedImages.Count() != 1) failures.Add("image Pic has no pt-PT version");
        }

        // repoint: a call node runs another action with the same input and output names. It keeps its key, its argument
        // is bound again by name, and the Assign that reads Run.Id still validates after a reopen.
        File.WriteAllText(path, """
            {"serverActions":[
              {"name":"MakeA","inputs":[{"name":"Source","type":"Text"}],"outputs":[{"name":"Id","type":"Integer"}],"flow":[{"kind":"assign","name":"SetA","assign":{"Id":"Length(Source)"}}]},
              {"name":"MakeB","inputs":[{"name":"Source","type":"Text"}],"outputs":[{"name":"Id","type":"Integer"}],"flow":[{"kind":"assign","name":"SetB","assign":{"Id":"Length(Source) + 1"}}]},
              {"name":"Caller","outputs":[{"name":"Result","type":"Integer"}],"flow":[
                {"kind":"execute","name":"Run","action":"MakeA","args":{"Source":"\"abc\""}},
                {"kind":"assign","name":"Keep","assign":{"Result":"Run.Id"}}]}]}
            """);
        var calls = Path.Combine(dir, "logic-repoint-source.oml");
        (code, report) = Apply.Execute(blank, path, calls);
        if (code != 0) { failures.Add("repoint fixture: " + JsonSerializer.Serialize(report)); return; }
        string key;
        using (var before = Model.Load(calls)) key = ElementPaths.Resolve(before, "Caller/Run").GlobalKey.ToString()!;
        File.WriteAllText(path, """{"edits":[{"op":"repoint","element":"Caller/Run","action":"MakeB"}]}""");
        output = Path.Combine(dir, "logic-repoint.oml");
        (code, report) = Apply.Execute(calls, path, output);
        if (code != 0 || !Says(report, "node Caller/Run now calls MakeB")) { failures.Add("repoint: " + JsonSerializer.Serialize(report)); return; }
        using (var repointed = Model.Load(output))
        {
            var run = ElementPaths.Resolve(repointed, "Caller/Run");
            if (run.GlobalKey.ToString() != key) failures.Add("repoint: the node got a new key");
            if (Model.NameOf(Reflect.Get(run, "Action") ?? "") != "MakeB") failures.Add("repoint: the node does not call MakeB");
            var source = ((System.Collections.IEnumerable)Reflect.Get(run, "Arguments")!).Cast<object>()
                .FirstOrDefault(a => Reflect.Get(a, "Parameter") is { } p && Model.NameOf(p) == "Source");
            if (source is null || (Reflect.Get(source, "Value") as IExpression)?.Text != "\"abc\"") failures.Add("repoint: the argument Source lost its value");
            if (Model.Errors(repointed).Length > 0) failures.Add("repoint: errors after reopen (the Assign reads Run.Id)");
        }
        // The same edit again changes nothing.
        (code, report) = Apply.Execute(output, path, Path.Combine(dir, "logic-repoint-again.oml"));
        if (code != 0 || !Says(report, "already calls MakeB")) failures.Add("repoint again: " + JsonSerializer.Serialize(report));

        // rewrite: an identifier path becomes another expression in every expression of an element; literals and longer names stay.
        var sample = ExpressionText.ReplacePath("Session.A + \"Session.A\" + x.Session.A + Session.AB + Session.A.B", "Session.A", "A");
        if (sample != "A + \"Session.A\" + x.Session.A + Session.AB + A.B") failures.Add("rewrite text: " + sample);
        if (ExpressionText.ReplacePath("N * 2", "N", "M + 1") != "(M + 1) * 2") failures.Add("rewrite keeps precedence: " + ExpressionText.ReplacePath("N * 2", "N", "M + 1"));
        File.WriteAllText(path, """{"edits":[{"op":"addVariables","action":"Caller","inputs":[{"name":"Bonus","type":"Integer","mandatory":false}]},{"op":"rewrite","element":"Caller","from":"Run.Id","to":"Bonus"}]}""");
        var rewritten = Path.Combine(dir, "logic-rewrite.oml");
        (code, report) = Apply.Execute(output, path, rewritten);
        if (code != 0 || !Says(report, "Run.Id -> Bonus")) { failures.Add("rewrite: " + JsonSerializer.Serialize(report)); return; }
        using (var rw = Model.Load(rewritten))
        {
            var keep = ElementPaths.Resolve(rw, "Caller/Keep");
            var value = ((System.Collections.IEnumerable)Reflect.Get(keep, "Assignments")!).Cast<object>().Select(x => (Reflect.Get(x, "Value") as IExpression)?.Text).FirstOrDefault();
            if (value != "Bonus") failures.Add("rewrite: the assign reads " + value);
            if (Model.Errors(rw).Length > 0) failures.Add("rewrite: errors after reopen");
        }
    }
}
