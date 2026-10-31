using System.Text.Json;
using System.Text.RegularExpressions;

namespace OmlCli.Qa;

// The expectations a step can hold. Each check returns null when it holds, or what is wrong in plain words.
// The runner retries them all until they hold or the step's timeout passes.
static class QaExpect
{
    public static readonly string[] Keys = ["screen", "url", "text", "noText", "visible", "hidden", "value", "feedback", "call", "table", "count", "invalid"];

    public static async Task<string?> Check(IQaEngine b, string key, JsonElement v, int seq) => key switch
    {
        "screen" => await Screen(b, v.GetString()!),
        "url" => (await b.Url()).Contains(v.GetString()!, StringComparison.OrdinalIgnoreCase) ? null : $"the URL is {await b.Url()}, not one with {v.GetString()}",
        "text" => await Texts(b, v, present: true),
        "noText" => await Texts(b, v, present: false),
        "visible" => await Shown(b, v, shown: true),
        "hidden" => await Shown(b, v, shown: false),
        "value" => await Values(b, v),
        "feedback" => await Feedback(b, v),
        "call" => await Call(b, v, seq),
        "table" => await Table(b, v),
        "count" => await Count(b, v),
        "invalid" => await Invalid(b, v),
        _ => $"unknown expectation {key}",
    };

    static async Task<string?> Screen(IQaEngine b, string screen)
    {
        var path = new Uri(await b.Url()).AbsolutePath.TrimEnd('/');
        var now = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
        return now.Equals(screen, StringComparison.OrdinalIgnoreCase) ? null : $"the screen is {(now.Length == 0 ? "the home page" : now)}, not {screen}";
    }

    static IEnumerable<string> Strings(JsonElement v) => v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.ToString()) : [v.ToString()];

    static async Task<string?> Texts(IQaEngine b, JsonElement v, bool present)
    {
        var page = (await b.Helper("text")).GetString() ?? "";
        var wrong = Strings(v).Where(t => page.Contains(t, StringComparison.OrdinalIgnoreCase) != present).ToList();
        return wrong.Count == 0 ? null : (present ? "not on the screen: " : "on the screen but should not be: ") + string.Join(", ", wrong.Select(QaRunner.Quote));
    }

    static async Task<string?> Shown(IQaEngine b, JsonElement v, bool shown)
    {
        foreach (var t in Strings(v))
        {
            var at = await b.Helper("locate", t, null, false);
            var visible = at.GetProperty("found").GetInt32() > 0 && !at.TryGetProperty("ambiguous", out _) && at.TryGetProperty("visible", out var vis) && vis.GetBoolean()
                || at.TryGetProperty("ambiguous", out _);
            if (visible != shown) return shown ? $"{t} is not visible" : $"{t} is visible";
        }
        return null;
    }

    static async Task<string?> Values(IQaEngine b, JsonElement v)
    {
        foreach (var p in v.EnumerateObject())
        {
            var at = await b.Helper("locate", p.Name, null, true);
            if (at.GetProperty("found").GetInt32() == 0) return $"no field {p.Name}";
            var now = (await b.Helper("valueOfLast")).ToString();
            if (now != p.Value.ToString()) return $"{p.Name} is {QaRunner.Quote(now)}, not {QaRunner.Quote(p.Value.ToString())}";
        }
        return null;
    }

    // "success" or {"type": "error", "text": "part of the message"}.
    static async Task<string?> Feedback(IQaEngine b, JsonElement v)
    {
        var type = v.ValueKind == JsonValueKind.String ? v.GetString() : v.TryGetProperty("type", out var t) ? t.GetString() : null;
        var text = v.ValueKind == JsonValueKind.Object && v.TryGetProperty("text", out var x) ? x.GetString() : null;
        var shown = (await b.Helper("feedback")).EnumerateArray().Select(m => (Type: m.GetProperty("type").GetString()!, Text: m.GetProperty("text").GetString()!)).ToList();
        if (shown.Any(m => (type is null || m.Type.Equals(type, StringComparison.OrdinalIgnoreCase)) && (text is null || m.Text.Contains(text, StringComparison.OrdinalIgnoreCase)))) return null;
        return shown.Count == 0 ? $"no {type} feedback message" : $"no {type} feedback message{(text is null ? "" : " with " + QaRunner.Quote(text))}; shown: {string.Join(", ", shown.Select(m => m.Type + " " + QaRunner.Quote(m.Text)))}";
    }

    // "FetchUsers" or {"name": "FetchUsers", "rows": ">0"}: an OutSystems call made during this step, by its model name.
    static async Task<string?> Call(IQaEngine b, JsonElement v, int seq)
    {
        var name = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetProperty("name").GetString()!;
        var rows = v.ValueKind == JsonValueKind.Object && v.TryGetProperty("rows", out var r) ? r.ToString() : null;
        var calls = b.Network.CallsSince(seq).Where(c => c.Call!.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (calls.Count == 0)
        {
            var made = b.Network.CallsSince(seq).Select(c => c.Call).Distinct().ToList();
            return $"no call to {name} in this step{(made.Count > 0 ? "; calls made: " + string.Join(", ", made) : "")}";
        }
        var last = calls[^1];
        if (!last.Done) return $"{last.Call} has not finished";
        if (last.Status >= 400 || last.Failed is not null) return $"{last.Call} failed: {last.Failed ?? "HTTP " + last.Status}";
        var read = await b.ReadCall(last);
        if (read.Exception is not null) return $"{last.Call} raised {read.Exception}";
        if (rows is not null && !Compare(read.Rows, rows)) return $"{last.Call} returned {read.Rows?.ToString() ?? "no list"} rows, expected {rows}";
        return null;
    }

    // {"target": "UsersTable", "rows": ">0", "column": "Role", "match": "^[A-Za-z]", "notMatch": "^\\d+$"}
    static async Task<string?> Table(IQaEngine b, JsonElement v)
    {
        var target = v.GetProperty("target").GetString()!;
        var t = await b.Helper("table", target, null);
        if (t.TryGetProperty("found", out var f) && f.GetInt32() == 0) return $"no table {target}";
        if (t.TryGetProperty("ambiguous", out _)) return $"{target} matches several elements";
        if (t.TryGetProperty("error", out var e)) return $"{target}: {e.GetString()}";
        var rows = t.GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().Select(c => c.GetString() ?? "").ToList()).ToList();
        if (v.TryGetProperty("rows", out var want) && !Compare(rows.Count, want.ToString())) return $"{target} has {rows.Count} rows, expected {want}";
        if (!v.TryGetProperty("column", out var col)) return null;
        var columns = t.GetProperty("columns").EnumerateArray().Select(c => c.GetString() ?? "").ToList();
        var i = columns.FindIndex(c => c.Equals(col.GetString(), StringComparison.OrdinalIgnoreCase));
        if (i < 0) return $"{target} has no column {col.GetString()} (columns: {string.Join(", ", columns)})";
        var cells = rows.Where(r => r.Count > i).Select((r, n) => (Row: n + 1, Text: r[i])).ToList();
        if (v.TryGetProperty("match", out var m) && cells.FirstOrDefault(c => !Regex.IsMatch(c.Text, m.GetString()!)) is { Text: not null } bad)
            return $"{target} row {bad.Row} {col.GetString()} is {QaRunner.Quote(bad.Text)}, which does not match {m.GetString()}";
        if (v.TryGetProperty("notMatch", out var nm) && cells.FirstOrDefault(c => Regex.IsMatch(c.Text, nm.GetString()!)) is { Text: not null } hit)
            return $"{target} row {hit.Row} {col.GetString()} is {QaRunner.Quote(hit.Text)}, which matches {nm.GetString()}";
        return null;
    }

    // {"target": "css:.list-item", "is": ">0"}
    static async Task<string?> Count(IQaEngine b, JsonElement v)
    {
        var target = v.GetProperty("target").GetString()!;
        var at = await b.Helper("locate", target, null, false);
        var n = at.GetProperty("found").GetInt32();
        return Compare(n, v.GetProperty("is").ToString()) ? null : $"{n} elements match {target}, expected {v.GetProperty("is")}";
    }

    // true: a validation message shows; false: none shows; a field name: that field shows one.
    static async Task<string?> Invalid(IQaEngine b, JsonElement v)
    {
        var shown = (await b.Helper("invalid")).EnumerateArray().Select(m => (Target: m.TryGetProperty("target", out var t) ? t.GetString() : null, Text: m.GetProperty("text").GetString())).ToList();
        if (v.ValueKind == JsonValueKind.True) return shown.Count > 0 ? null : "no validation message on the screen";
        if (v.ValueKind == JsonValueKind.False) return shown.Count == 0 ? null : "validation messages: " + string.Join(", ", shown.Select(s => $"{s.Target} {QaRunner.Quote(s.Text ?? "")}"));
        var field = v.GetString()!;
        return shown.Any(s => s.Target is not null && s.Target.Split('#')[0].Equals(field, StringComparison.OrdinalIgnoreCase)) ? null : $"{field} shows no validation message";
    }

    // ">0", ">=2", "<5", "<=5", "=3", "!=0" or "3".
    internal static bool Compare(int? actual, string rule)
    {
        if (actual is not { } a) return false;
        var m = Regex.Match(rule.Trim(), @"^(>=|<=|!=|>|<|=)?\s*(\d+)$");
        if (!m.Success) throw new QaUsage($"A count rule is a number with an optional >, >=, <, <=, = or != (not {QaRunner.Quote(rule)}).");
        var n = int.Parse(m.Groups[2].Value);
        return m.Groups[1].Value switch { ">" => a > n, ">=" => a >= n, "<" => a < n, "<=" => a <= n, "!=" => a != n, _ => a == n };
    }
}
