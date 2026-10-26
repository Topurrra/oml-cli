using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// `oml publish --in <folder> --order <order.json>`: publish many modules in the order of `oml architecture --publish-order`.
// The loop takes the publisher as a parameter, so the self-test runs it with a fake and never touches Service Studio.
static class PublishBatch
{
    internal sealed record Item(string module, string file);
    internal sealed record PublishResult(string module, bool published, string? warning, JsonNode? result);

    const string Manual = "Publish the module in stoppedAt and then the modules in notRun by hand: open each one in Service Studio, in that order, and use 1-Click Publish. " +
        "oml leaves the modules it opened open in Service Studio. Close them there before you run this batch again: a module that is already open is refused.";

    // Reads { "order": [ { "module", "file"? } ] }, the output of `oml architecture --publish-order`. Exit 2 for anything else.
    internal static List<Item> ReadOrder(string json)
    {
        try
        {
            var order = (JsonNode.Parse(json) as JsonObject)?["order"] as JsonArray
                ?? throw new CliError("The order file must be a JSON object with an 'order' array (the output of 'oml architecture --publish-order').", 2);
            var items = new List<Item>();
            foreach (var entry in order)
            {
                var module = (entry as JsonObject)?["module"] is JsonValue v && v.TryGetValue<string>(out var m) && m.Length > 0 ? m
                    : throw new CliError("Every entry of 'order' needs a 'module' name.", 2);
                var file = (entry as JsonObject)?["file"] is JsonValue f && f.TryGetValue<string>(out var path) ? path : "";
                if (items.Any(i => i.module.Equals(module, StringComparison.OrdinalIgnoreCase))) throw new CliError($"Module {module} is listed twice in the order file.", 2);
                items.Add(new(module, file));
            }
            return items.Count > 0 ? items : throw new CliError("The order file lists no module.", 2);
        }
        catch (JsonException e) { throw new CliError("The order file is not valid JSON: " + e.Message, 2); }
    }

    // Finds the file of every module in the folder's files. The order file's file name is used (its folder is not),
    // else <module>.oml. Exit 2, naming every problem, before anything is published.
    internal static List<Item> Plan(List<Item> order, IReadOnlyList<string> files, Func<string, string> moduleNameOf)
    {
        var problems = new List<string>();
        var plan = new List<Item>();
        foreach (var item in order)
        {
            var leaf = item.file.Length > 0 ? Path.GetFileName(item.file.Replace('\\', '/')) : item.module + ".oml";
            var found = files.Where(f => Path.GetFileName(f).Equals(leaf, StringComparison.OrdinalIgnoreCase)).ToList();
            if (leaf.Contains('!')) { problems.Add($"module {item.module}: {leaf} is inside a package; use an .oml file of the module in the folder"); continue; }
            if (found.Count != 1) { problems.Add(found.Count == 0 ? $"module {item.module}: no file {leaf} in the folder" : $"module {item.module}: {found.Count} files named {leaf} in the folder"); continue; }
            string actual;
            try { actual = moduleNameOf(found[0]); }
            catch (Exception e) when (e is not CliError) { problems.Add($"module {item.module}: cannot read {found[0]} ({(e.InnerException ?? e).Message})"); continue; }
            if (actual != item.module) { problems.Add($"module {item.module}: {found[0]} holds module {actual}"); continue; }
            plan.Add(new(item.module, found[0]));
        }
        return problems.Count == 0 ? plan : throw new CliError("Nothing was published. " + string.Join("; ", problems) + ".", 2);
    }

    // A module that is already open in Service Studio is refused by the single publish (the file would not be the one used).
    // Checking all of them first keeps a half-done batch from this cause. Exit 2.
    internal static void RefuseOpen(IReadOnlyList<Item> plan, IEnumerable<string> open)
    {
        var clash = plan.Where(i => open.Contains(i.module)).Select(i => i.module).ToList();
        if (clash.Count > 0) throw new CliError($"Nothing was published. Already open in Service Studio: {string.Join(", ", clash)}. Close them there and run again, or publish them one by one with 'oml publish <Module>' to use the open copy.", 2);
    }

    // Publishes in order and stops at the first module that does not publish. Any exception from the publisher counts as a failure,
    // so the report of the modules already published is never lost.
    // Exit code: 0 all published, 1 stopped on a failure.
    internal static (int code, object report) Run(IReadOnlyList<Item> plan, Func<string, PublishResult> publish)
    {
        var results = new List<object>();
        string? stoppedAt = null;
        foreach (var item in plan)
        {
            try
            {
                var r = publish(item.file);
                if (r.published) { results.Add(new { module = item.module, file = item.file, published = true, warning = r.warning }); continue; }
                results.Add(new { module = item.module, file = item.file, published = false, warning = r.warning, result = r.result });
            }
            catch (Exception e) { results.Add(new { module = item.module, file = item.file, published = false, error = e.Message }); }
            stoppedAt = item.module;
            break;
        }
        var done = results.Count - (stoppedAt is null ? 0 : 1);
        var notRun = plan.Skip(results.Count).Select(i => i.module).ToArray();
        return (stoppedAt is null ? 0 : 1, new { total = plan.Count, published = done, stoppedAt, results, notRun, manualStep = stoppedAt is null ? null : Manual });
    }

    public static int Execute(string folder, string orderFile)
    {
        var dir = Path.GetFullPath(folder);
        if (!Directory.Exists(dir)) throw new CliError($"Folder not found: {dir}", 2);
        if (!File.Exists(orderFile)) throw new CliError($"Order file not found: {Path.GetFullPath(orderFile)}", 2);
        var order = ReadOrder(File.ReadAllText(orderFile));
        var files = Directory.EnumerateFiles(dir, "*.oml", SearchOption.AllDirectories).ToList();
        var plan = Plan(order, files, path => { using var m = Model.Load(path); return ((OutSystems.Model.Applications.IModule)m).Name; });
        RefuseOpen(plan, Studio.OpenModuleNames());
        var (code, report) = Run(plan, file => Studio.PublishOne(file));
        TerminalOutput.PrintNode(JsonSerializer.SerializeToNode(report)!); // as a built tree: empty lists and a null stoppedAt stay in the output
        return code;
    }
}
