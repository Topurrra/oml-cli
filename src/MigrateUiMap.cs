using System.Text.Json.Nodes;

namespace OmlCli;

// The Traditional UI libraries (OutSystemsUIWeb, RichWidgets, OutSystemsChartsWeb) and what each of their blocks, actions and static
// entities becomes in Reactive: data/ui-library-map.json, checked against real signatures where its confidence says high. The screen
// translator uses the block rows; `oml migrate plan` writes the rows a module uses into ui-libraries.md of the plan folder.
static class UiLibraryMap
{
    internal sealed record Row(string Traditional, string ElementKind, string? Reactive, string ReactiveKind, JsonObject Inputs,
        JsonObject Placeholders, string Rules, string Notes, string Confidence, JsonObject? Values = null, JsonObject? Defaults = null, string? Replace = null)
    {
        // Replace: what a call of this server action with one argument becomes in a client expression, {0} being the argument (DateToText({0})).
        // Values: Traditional input -> {exact old expression -> Reactive expression} (an Orientation record that is a Boolean now).
        // Defaults: Reactive input -> the value it gets when the old instance set none (a mandatory input the Traditional block did not have).
        // A block translated by code: a Reactive block that was checked against its signature or documented.
        public bool ByCode => ElementKind == "block" && ReactiveKind == "block" && Reactive is { Length: > 0 } && Confidence is "high" or "medium";
        // A block that Reactive does not need at all (a loader, a wait box, the message area).
        public bool Dropped => ElementKind == "block" && Reactive is null && ReactiveKind == "none";
    }

    internal static readonly string[] Libraries = ["OutSystemsUIWeb", "RichWidgets", "OutSystemsChartsWeb"];

    static readonly Lazy<Dictionary<string, Row>> Rows = new(Load);

    static Dictionary<string, Row> Load()
    {
        using var stream = typeof(UiLibraryMap).Assembly.GetManifestResourceStream("OmlCli.ui-library-map.json")
            ?? throw new InvalidOperationException("ui-library-map.json is not embedded in this build.");
        var rows = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in (JsonNode.Parse(stream) as JsonArray ?? []).OfType<JsonObject>())
        {
            string S(string k) => r[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
            var row = new Row(S("traditional"), S("elementKind"), S("reactive") is { Length: > 0 } re ? re : null, S("reactiveKind"),
                r["inputs"] as JsonObject ?? [], r["placeholders"] as JsonObject ?? [], S("inputValueRules"), S("notes") is { Length: > 0 } n ? n : S("logicRewrite"), S("confidence"),
                r["values"] as JsonObject, r["defaults"] as JsonObject, S("replace") is { Length: > 0 } rp ? rp : null);
            rows.TryAdd(row.ElementKind + ":" + row.Traditional, row);
        }
        return rows;
    }

    internal static bool IsLibrary(string qualified) => qualified.IndexOf('.') is > 0 and var dot && Libraries.Contains(qualified[..dot], StringComparer.OrdinalIgnoreCase);

    internal static Row? Block(string qualified) => Rows.Value.GetValueOrDefault("block:" + qualified);

    internal static Row? Action(string qualified) => Rows.Value.GetValueOrDefault("serverAction:" + qualified);

    internal static IEnumerable<Row> All => Rows.Value.Values;

    // The rows for the library elements a module references, as Markdown for the plan folder: what each becomes and what to check.
    internal static string Markdown(IEnumerable<(string module, string kind, string name)> used)
    {
        static string Cell(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Replace("|", "/").Trim();
        var lines = new List<string> { "# UI libraries used by the old module", "",
            "What each Traditional library element becomes in Reactive. Blocks marked `code` are translated in 4-screens.json (Reactive block, inputs and placeholders renamed).",
            "The others are yours: follow the note. Confidence: high (both signatures checked), medium (OutSystems docs), low (inferred).", "",
            "| Element | Becomes | How | Note | Confidence |", "|---|---|---|---|---|" };
        var count = 0;
        foreach (var (module, kind, name) in used.Distinct().OrderBy(u => u.module).ThenBy(u => u.kind).ThenBy(u => u.name))
        {
            var row = Rows.Value.GetValueOrDefault(kind + ":" + module + "." + name);
            if (row is null) continue;
            var how = row.ByCode ? "code" : row.Dropped ? "dropped" : "by hand";
            var renamed = string.Join(", ", row.Inputs.Where(kv => kv.Value is JsonValue v && v.ToString() is { Length: > 0 } t && t != kv.Key).Select(kv => $"{kv.Key} -> {kv.Value}"));
            var note = string.Join(" ", new[] { renamed.Length > 0 ? "Inputs: " + renamed + "." : "", row.Rules, row.Notes }.Where(x => x.Length > 0));
            lines.Add($"| {module}.{name} ({kind}) | {row.Reactive ?? "nothing"} | {how} | {Cell(note)} | {row.Confidence} |");
            count++;
        }
        return count == 0 ? "" : string.Join("\n", lines) + "\n";
    }
}
