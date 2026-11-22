using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Enumerations;

namespace OmlCli;

static partial class SelfTest
{

    // ListFilter, ListIndexOf, ListAny, ListAll and ListSort get their Condition or By the way Service Studio writes it: a
    // lambda over the element (`ThreadId = Wanted`), not the outer list's current row (`Mails.Current.ThreadId`), which
    // reads the wrong rows at run time (kanzo oml#257).
    static void ListLambdasTest(string dir, List<string> failures)
    {
        void Check(bool condition, string detail) { if (!condition) failures.Add("list lambdas: " + detail); }
        foreach (var (written, list, expected) in new[]
        {
            ("Mails.Current.ThreadId = Wanted", "Mails", "ThreadId = Wanted"),
            ("mails.current . Rank > 1 and Mails.CurrentRowNumber > 0", "Mails", "Rank > 1 and Mails.CurrentRowNumber > 0"),
            ("GetMails.List.Current.Mail.ThreadId = \"Mails.Current.x\"\"\" + GetMails.List.Current.Mail.Subject", "GetMails.List", "Mail.ThreadId = \"Mails.Current.x\"\"\" + Mail.Subject"),
            ("Tags.Current = Wanted", "Tags", "Value = Wanted"),
            ("Mails.Current.ThreadId = Others.Current.ThreadId and OtherMails.Current.Rank = 1", "Mails", "ThreadId = Others.Current.ThreadId and OtherMails.Current.Rank = 1"),
            ("ThreadId = Wanted", "Mails", "ThreadId = Wanted"),
            ("Mails.Current.ThreadId = Wanted", "", "Mails.Current.ThreadId = Wanted"),
        })
            Check(ListLambdas.Rewrite(written, list) == expected, $"'{written}' over '{list}' gave '{ListLambdas.Rewrite(written, list)}', expected '{expected}'");

        // A new module has no System list actions until Service Studio registers it: add them as Studio does while it loads one.
        var services = Model.Services();
        var source = Path.Combine(dir, "list-lambdas-source.oml");
        using (var module = services.CreateESpace(SegmentationKind.CrossDevice, "ListLambdas", "", false, services.NewKey()))
        {
            var create = module.GetType().Assembly.GetType("ServiceStudio.Model.Signatures.References")?.GetMethod("Create", [module.GetType()]);
            var loading = module.GetType().GetProperty("IsLoading");
            if (create is null || loading?.SetMethod is null) { Check(false, "Service Studio's System references builder not found"); return; }
            loading.SetValue(module, true);
            try { create.Invoke(null, [module]); }
            finally { loading.SetValue(module, false); }
            Model.Save(module, source);
        }
        var spec = Path.Combine(dir, "list-lambdas-spec.json");
        var output = Path.Combine(dir, "list-lambdas.oml");
        File.WriteAllText(spec, """
            { "structures": [ { "name": "Mail", "attributes": [ { "name": "ThreadId" }, { "name": "Rank", "type": "Integer" } ] } ],
              "clientActions": [ { "name": "PickThread", "inputs": [ { "name": "Wanted" } ], "outputs": [ { "name": "Count", "type": "Integer" } ],
                "locals": [ { "name": "Mails", "type": "List of Mail" }, { "name": "Tags", "type": "List of Text" }, { "name": "Rank", "type": "Boolean" } ],
                "flow": [
                  { "kind": "execute", "name": "Filter", "action": "ListFilter", "args": { "Condition": "Mails.Current.ThreadId = Wanted", "SourceList": "Mails" } },
                  { "kind": "execute", "name": "Find", "action": "ListIndexOf", "args": { "List": "Mails", "Condition": "Mails.Current.Rank > 1" } },
                  { "kind": "execute", "name": "Sort", "action": "ListSort", "args": { "List": "Mails", "By": "Mails.Current.Rank" } },
                  { "kind": "execute", "name": "AnyTag", "action": "ListAny", "args": { "List": "Tags", "Condition": "Tags.Current = Wanted" } },
                  { "kind": "execute", "name": "All", "action": "ListAll", "args": { "List": "Mails", "Condition": "Mails.Current.Rank > 0" } },
                  { "kind": "assign", "name": "Out", "assign": { "Count": "Filter.FilteredList.Length + Find.Position + If(AnyTag.Result and All.Result, 1, 0)" } } ] } ] }
            """);
        var (code, report) = Apply.Execute(source, spec, output);
        if (code != 0) { Check(false, "apply: " + JsonSerializer.Serialize(report)); return; }
        using var built = Model.Load(output);
        string? Argument(string node, string parameter) =>
            (ElementPaths.Resolve(built, $"PickThread/{node}") as IObjectSignature) is { } n && Reflect.Get(n, "Arguments") is System.Collections.IEnumerable arguments
                ? arguments.Cast<object>().OfType<IArgument>().FirstOrDefault(a => Reflect.Get(a, "Parameter") is { } p && Model.NameOf(p) == parameter)?.Value?.Text : null;
        Check(Argument("Filter", "Condition") == "ThreadId = Wanted", "ListFilter condition: " + Argument("Filter", "Condition"));
        Check(Argument("Find", "Condition") == "Rank > 1", "ListIndexOf condition: " + Argument("Find", "Condition"));
        Check(Argument("Sort", "By") == "Rank", "ListSort by: " + Argument("Sort", "By"));
        Check(Argument("AnyTag", "Condition") == "Value = Wanted", "ListAny condition: " + Argument("AnyTag", "Condition"));
        Check(Argument("All", "Condition") == "Rank > 0", "ListAll condition: " + Argument("All", "Condition"));
        // The element's attribute, not the Boolean local of the same name, is what `Rank` means there: the local stays unused,
        // and the condition argument knows its element type, as in a node Service Studio builds.
        var condition = (ElementPaths.Resolve(built, "PickThread/Find") as IObjectSignature) is { } find && Reflect.Get(find, "Arguments") is System.Collections.IEnumerable findArguments
            ? findArguments.Cast<object>().FirstOrDefault(ListLambdas.IsLambda) : null;
        Check(condition is not null && Reflect.Get(condition, "ConcreteLambdaParameterType") is { } element && Model.NameOf(element) == "Mail",
            "ListIndexOf condition has no element type Mail");
        var warnings = JsonSerializer.Serialize(report);
        Check(warnings.Contains("UnusedElement", StringComparison.Ordinal) && warnings.Contains("PickThread/Rank", StringComparison.Ordinal),
            "the Boolean local Rank was read instead of the element's Rank");
    }
}
