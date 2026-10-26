using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// `oml apply --batch <index.json>` and `oml install --batch <index.json>`: many modules in one process.
public static partial class Cli
{
    // The index is {"items": [ {...}, ... ]} or a bare list. Paths are relative to the index file's folder.
    // Any fault here is exit 2, before the first module is touched.
    static List<JsonObject> ReadBatchIndex(string indexPath, string command, string[] keys)
    {
        string text;
        try { text = File.ReadAllText(indexPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new CliError($"Cannot read the {command} index: {e.Message}", 2); }
        JsonNode? document;
        try { document = JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException e) { throw new CliError($"Invalid {command} index JSON: {e.Message}", 2); }
        var list = document as JsonArray ?? (document as JsonObject)?["items"] as JsonArray;
        var sample = string.Join(", ", keys.Select(k => $"\"{k}\": \"...\""));
        if (list is null || list.Count == 0) throw new CliError($"The {command} index must be {{\"items\": [{{{sample}}}, ...]}}: a non-empty list.", 2);
        var items = new List<JsonObject>();
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not JsonObject item) throw new CliError($"Item {i + 1} of the {command} index must be an object with {string.Join(", ", keys)}.", 2);
            if (item.Select(p => p.Key).FirstOrDefault(k => !keys.Contains(k)) is { } unknown)
                throw new CliError($"Item {i + 1} of the {command} index has the unknown key '{unknown}'. Keys: {string.Join(", ", keys)}.", 2);
            foreach (var key in keys)
                if (item[key] is null) throw new CliError($"Item {i + 1} of the {command} index has no '{key}'.", 2);
            items.Add(item);
        }
        return items;
    }

    // One path or (for a spec) a list of paths, as full paths against the index folder.
    static List<string> BatchPaths(JsonObject item, string key, string folder, int number, bool many = false)
    {
        var node = item[key];
        var values = node is JsonArray a && many ? a.ToList() : [node];
        var paths = values.Select(v => v is JsonValue j && j.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? Path.GetFullPath(s, folder) : null).ToList();
        if (paths.Count == 0 || paths.Any(p => p is null)) throw new CliError($"Item {number} of the index: '{key}' must be a path{(many ? " or a list of paths" : "")}.", 2);
        return paths!.ToList()!;
    }

    static void BatchRefuseDuplicates(IEnumerable<string> paths, string what)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
            if (!seen.Add(p)) throw new CliError($"The {what} {p} appears twice in the index. Give each item its own path.", 2);
    }

    static int? CmdApplyBatch(List<string> args)
    {
        if (args.Count != 3 || args[1] != "--batch") return null;
        var index = Path.GetFullPath(args[2]);
        var folder = Path.GetDirectoryName(index)!;
        var plan = ReadBatchIndex(index, "apply", ["source", "spec", "staged"]).Select((item, i) =>
            (source: BatchPaths(item, "source", folder, i + 1)[0], specs: BatchPaths(item, "spec", folder, i + 1, many: true), staged: BatchPaths(item, "staged", folder, i + 1)[0])).ToList();
        BatchRefuseDuplicates(plan.Select(p => p.staged), "staged file");
        // A staged file that is also a source or a spec would be read after another item wrote it, or hide a clash.
        var inputs = new HashSet<string>(plan.SelectMany(p => p.specs.Append(p.source)), StringComparer.OrdinalIgnoreCase);
        if (plan.Select(p => p.staged).FirstOrDefault(inputs.Contains) is { } clash)
            throw new CliError($"The staged file {clash} is also a source or a spec in the index. Give it its own path.", 2);

        var results = new JsonArray();
        int saved = 0, failed = 0, newErrors = 0, newWarnings = 0, created = 0, changed = 0;
        // Inside `oml run` the sharing is already on and belongs to the caller: leave it on (as inspect --in does).
        var nested = Model.ShareModules;
        Model.ShareModules = true;
        try
        {
            foreach (var (source, specs, staged) in plan)
            {
                Model.TrimShared();
                var entry = new JsonObject { ["source"] = source, ["staged"] = staged };
                try
                {
                    var (code, report) = Apply.Execute(source, specs, staged, null);
                    var shaped = Apply.Shape((JsonObject)JsonSerializer.SerializeToNode(report)!);
                    var summary = shaped["summary"]!.AsObject();
                    if (summary["saved"]!.GetValue<bool>()) saved++;
                    if (code != 0) failed++;
                    newErrors += summary["newErrors"]!.GetValue<int>() + summary["reopenNewErrors"]!.GetValue<int>();
                    newWarnings += summary["newWarnings"]!.GetValue<int>();
                    created += (shaped["created"] as JsonArray)?.Count ?? 0;
                    changed += (shaped["changed"] as JsonArray)?.Count ?? 0;
                    // `next` is said once for the batch: a per-item copy costs about 60 tokens per module.
                    foreach (var (k, v) in shaped.ToArray()) { shaped.Remove(k); if (k is not ("staged" or "next")) entry[k] = v; }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    failed++;
                    entry["error"] = e is CliError or ModelError ? e.Message : $"{e.GetType().Name}: {e.Message}";
                }
                results.Add(entry);
            }
        }
        finally { if (!nested) Model.EndSharing(); }
        Emit(new
        {
            items = results, summary = new { items = plan.Count, saved, failed, newErrors, newWarnings, created, changed },
            next = saved > 0 ? "Run oml review <source> <staged> --md <new .md> for each saved item and show it; ask before oml install --batch." : null,
        });
        return failed > 0 ? 1 : 0;
    }

    // All or nothing on the checks: every item is checked first, and one refusal stops the whole batch before any write.
    static int? InstallBatch(string indexPath, bool allowExisting)
    {
        var index = Path.GetFullPath(indexPath);
        var folder = Path.GetDirectoryName(index)!;
        var items = ReadBatchIndex(index, "install", ["staged", "target"]).Select((item, i) =>
            (staged: BatchPaths(item, "staged", folder, i + 1)[0], target: BatchPaths(item, "target", folder, i + 1)[0])).ToList();
        BatchRefuseDuplicates(items.Select(i => i.target), "target");
        BatchRefuseDuplicates(items.Select(i => i.staged), "staged file");
        var staged = new HashSet<string>(items.Select(i => i.staged), StringComparer.OrdinalIgnoreCase);
        if (items.Select(i => i.target).FirstOrDefault(staged.Contains) is { } clash)
            throw new CliError($"The target {clash} is also a staged file in the index. Give it its own path.", 2);

        var plans = new List<InstallPlan>();
        var refused = new JsonArray();
        foreach (var (s, t) in items)
        {
            try { plans.Add(InstallCheck(s, t, allowExisting) with { Bytes = [] }); } // keep the hash; the bytes are read again at the write
            catch (Exception e) when (e is CliError or ModelError) { refused.Add(new JsonObject { ["staged"] = s, ["target"] = t, ["error"] = e.Message }); }
        }
        if (refused.Count > 0)
        {
            Emit(new { error = $"Nothing installed: {refused.Count} of {items.Count} items would be refused.", items = refused,
                summary = new { items = items.Count, installed = 0, refused = refused.Count } });
            return 1;
        }

        var results = new JsonArray();
        var installed = 0;
        var failure = false;
        foreach (var plan in plans)
        {
            var entry = new JsonObject { ["staged"] = plan.Staged, ["target"] = plan.Target };
            results.Add(entry);
            if (failure) { entry["skipped"] = true; continue; } // after a failed write, touch nothing else
            try
            {
                var bytes = File.ReadAllBytes(plan.Staged);
                if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) != plan.Hash)
                    throw new CliError($"The staged file changed after the checks: {plan.Staged}. Nothing installed for this item.", 2);
                foreach (var (k, v) in JsonSerializer.SerializeToNode(InstallWrite(plan, bytes))!.AsObject().ToArray()) entry[k] = v?.DeepClone();
                installed++;
            }
            catch (Exception e) when (e is not OutOfMemoryException) // earlier items are already installed: always print the summary
            {
                failure = true;
                entry["error"] = e is CliError or ModelError or IOException or UnauthorizedAccessException ? e.Message : $"{e.GetType().Name}: {e.Message}";
            }
        }
        Emit(new { items = results, summary = new { items = plans.Count, installed, failed = failure ? 1 : 0, skipped = results.Count(r => r!["skipped"] is not null) } });
        return failure ? 1 : 0;
    }
}
