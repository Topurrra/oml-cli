using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// Compares validation errors of a file with the errors Service Studio lists for the open module (its MCP tool
// getValidationMessages). Pure functions: no Service Studio call happens here.
static class StudioCompare
{
    internal readonly record struct Message(string Id, string Text);

    // Reads the tool result: { validationMessages: [{ id, type, message, detail, ownerKey, ownerPath, ownerType }] }.
    // The shape comes from the tool's schema, not from a recorded reply, so this reader is tolerant: a bare array or a
    // "messages" field is accepted and names are case-insensitive. The request already asks for errors only, so an
    // entry is skipped only when its type says warning or info; any other type (a number, "ValidationMessageType.Error") stays.
    internal static List<Message> ParseErrors(JsonNode? result)
    {
        var items = result as JsonArray
            ?? (result as JsonObject)?.FirstOrDefault(p => p.Key.Equals("validationMessages", StringComparison.OrdinalIgnoreCase)
                                                          || p.Key.Equals("messages", StringComparison.OrdinalIgnoreCase)).Value as JsonArray
            ?? throw new CliError($"getValidationMessages returned no message list (got: {(result?.ToJsonString() is { Length: > 200 } s ? s[..200] : result?.ToJsonString())}).", 2);
        var errors = new List<Message>();
        foreach (var item in items.OfType<JsonObject>())
        {
            string? Field(string name) => item.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value?.ToString();
            var type = Field("type");
            if (type is not null && (type.Contains("warn", StringComparison.OrdinalIgnoreCase) || type.Contains("info", StringComparison.OrdinalIgnoreCase))) continue;
            errors.Add(new Message(Field("id") ?? "", Field("message") ?? ""));
        }
        return errors;
    }

    static string Normalize(string text) => Regex.Replace(text.Trim(), @"\s+", " ").TrimEnd('.').TrimEnd();

    // One flag per copy error, in order. Pass 1: same id and same normalized text (each Studio error used once).
    // Pass 2: a leftover copy error matches a leftover Studio error with the same id, whatever the text.
    // The owner is not compared: its path format in Service Studio is not recorded.
    internal static bool[] InStudio(IReadOnlyList<Message> copy, IReadOnlyList<Message> studio)
    {
        var flags = new bool[copy.Count];
        var left = studio.Select(m => (m.Id, Text: Normalize(m.Text))).ToList();
        for (var i = 0; i < copy.Count; i++)
        {
            var at = left.IndexOf((copy[i].Id, Normalize(copy[i].Text)));
            if (at < 0) continue;
            flags[i] = true; left.RemoveAt(at);
        }
        for (var i = 0; i < copy.Count; i++)
        {
            if (flags[i]) continue;
            var at = left.FindIndex(m => m.Id == copy[i].Id);
            if (at < 0) continue;
            flags[i] = true; left.RemoveAt(at);
        }
        return flags;
    }

    // The text after a pull whose copy has errors. studioErrors is null only when the comparison failed.
    internal static string Attention(string module, int copyErrors, int? studioErrors, int inStudio, string? failure)
    {
        var head = $"The pulled copy of '{module}' has {copyErrors} error(s).";
        if (studioErrors is null)
            return $"{head} The comparison with Service Studio was not possible ({failure}). Ask the user whether Service Studio shows the same " +
                   "errors for the open module (TrueChange). If yes, they are real errors in the module. If not, the pull copy is wrong: " +
                   "ask the user to save the module by hand (File > Save As) and use that file instead.";
        head += $" Service Studio lists {studioErrors}.";
        if (studioErrors == 0)
            return $"{head} Service Studio shows no such errors. Save the module by hand (File > Save As) and use that file.";
        if (inStudio == 0)
            return $"Service Studio lists {studioErrors} error(s), the copy has {copyErrors}, and they do not match. Compare them by hand in Service Studio. ({head})";
        if (inStudio == copyErrors)
            return $"{head} Service Studio shows the same errors (inStudio: true), so they are real errors in the module.";
        return $"{head} {inStudio} of the copy's errors show in Service Studio (inStudio: true); the others (inStudio: false) may come from the pull copy. " +
               "Fix the real errors in the module, or save the module by hand (File > Save As) and use that file.";
    }
}
