using System.Text.Json.Nodes;

namespace OmlCli;

// Pure decisions for install, publish and references. No Service Studio call and no model load happens here,
// so the self-test can run them on fake data.
static class StudioGuards
{
    internal readonly record struct ErrorKey(string Id, string Owner);

    // Multiset split: a staged error is "existing" when the target has an unused error with the same id and owner path.
    internal static (List<ErrorKey> added, List<ErrorKey> existing) SplitErrors(IEnumerable<ErrorKey> staged, IEnumerable<ErrorKey> target)
    {
        var left = target.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        List<ErrorKey> added = [], existing = [];
        foreach (var k in staged)
            if (left.GetValueOrDefault(k) > 0) { left[k]--; existing.Add(k); } else added.Add(k);
        return (added, existing);
    }

    // The refusal text of install for a staged file with errors; null means install may go on.
    internal static string? InstallRefusal(int stagedErrors, int newErrors, bool allowExisting) =>
        stagedErrors == 0 ? null
        : newErrors > 0 && allowExisting ? $"Staged module has {newErrors} validation error(s) that the target does not have; refusing to install, also with --allow-existing-errors."
        : newErrors > 0 ? $"Staged module has {stagedErrors} validation errors; refusing to install."
        : allowExisting ? null
        : $"Staged module has {stagedErrors} validation errors; refusing to install. All of them also exist in the target: use --allow-existing-errors to install anyway.";

    // A module extracted from an .oap has version "0" in Service Studio, and its publish fails with a "concurrent
    // publication" error. A new module that was never published also has version "0", so this only warns. Only an app
    // with a "version" of exactly 0 (text or number) warns; any other shape returns null.
    internal static string? PublishWarning(string module, JsonNode? app)
    {
        var zero = app is JsonObject o && o["version"] is JsonValue v
            && (v.TryGetValue<string>(out var text) ? text.Trim() == "0" : v.TryGetValue<double>(out var number) && number == 0);
        return zero
            ? $"Module '{module}' has version 0 in Service Studio. This is normal for a new module. If it is a copy extracted from a package, Service Studio rejects the publish with 'A concurrent publication was detected': " +
              "open the server version of the module (or run oml pull on it), apply the spec again to that file, then publish."
            : null;
    }

    // The listApps entry for a module; null for any other reply shape. Never throws.
    internal static JsonNode? FindApp(JsonNode? reply, string module) =>
        reply is JsonObject o && o["apps"] is JsonArray apps
            ? apps.FirstOrDefault(a => a is JsonObject x && x["name"] is JsonValue n && n.TryGetValue<string>(out var s) && s == module)
            : null;

    // Service Studio answers "Service Studio is busy..." in a normal reply when it refreshed nothing. Only a text field at
    // the top of the reply counts: a deeper text (an element or message name) must not turn a real refresh into a failure.
    internal static (bool refreshed, string? message) RefreshOutcome(JsonNode? result)
    {
        var busy = result switch
        {
            JsonObject o => o.Select(p => p.Value).OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).FirstOrDefault(IsBusy),
            JsonValue v when v.TryGetValue<string>(out var s) && IsBusy(s) => s,
            _ => null,
        };
        return (busy is null, busy);
    }

    static bool IsBusy(string? s) => s is not null && s.Contains("is busy", StringComparison.OrdinalIgnoreCase);
}
