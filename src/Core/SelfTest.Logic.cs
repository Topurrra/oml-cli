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
    }
}
