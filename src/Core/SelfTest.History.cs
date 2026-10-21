using System.Text.Json;
using OutSystems.Model;

namespace OmlCli;

static partial class SelfTest
{
    // Change history: describe --history and inspect --history / --changed-since read what the model stores.
    // A module built here carries no history (checked); the values are written through the typed properties,
    // saved, reopened and read back.
    static void HistoryTest(string builtPath, string dir, List<string> failures)
    {
        static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
        static bool Has(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;
        static string[] Names(JsonElement e, string section) => !e.TryGetProperty(section, out var list) ? []
            : list.EnumerateArray().Select(x => x.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToArray();

        // A headless build records no author and no date: no history object, and no extra text line.
        using (var fresh = Model.Load(builtPath))
        {
            var plain = Json(Describe.Run(fresh, "FetchOrder", 0, []));
            var asked = Json(Describe.Run(fresh, "FetchOrder", 0, [], history: true));
            if (Has(plain, "history") || Has(asked, "history") || plain.GetProperty("text").GetString() != asked.GetProperty("text").GetString())
                failures.Add("history: a module without history gained a history object or text line");
            var empty = Json(Inspect.Filter(fresh, true, "entities", history: true));
            if (empty.GetProperty("entities").EnumerateArray().Any(e => Has(e, "lastModifiedBy") || Has(e, "lastModifiedDate")))
                failures.Add("history: inspect --history invented a change date");
        }

        var stamped = Path.Combine(dir, "history.oml");
        using (var m = Model.Load(builtPath))
        {
            var order = m.Entities.Single(e => e.Name == "Order");
            order.CreatedBy = "Ana"; order.LastModifiedBy = "Ben"; order.LastModifiedDate = new DateTime(2024, 5, 6, 7, 8, 9);
            order.LastMergedBy = "Cleo"; order.LastMergedDate = new DateTime(2024, 5, 7, 1, 2, 3);
            var fetch = m.ServerActions.Single(a => a.Name == "FetchOrder");
            // IFlow and IServerActionSignature both declare the properties; the cast picks one.
            var tracked = (OutSystems.Model.IFlow)fetch;
            tracked.LastModifiedBy = "Dev"; tracked.LastModifiedDate = new DateTime(2023, 1, 2, 3, 4, 5);
            // 1900-01-01 is OutSystems' empty date and must count as empty, like the CLR default.
            var customer = m.Entities.Single(e => e.Name == "Customer");
            customer.LastModifiedBy = "Eve"; customer.LastModifiedDate = new DateTime(1900, 1, 1);
            // A screen: its history line must come right after its own text, before the bodies --depth adds.
            var screen = (OutSystems.Model.UI.IScreen)ElementPaths.Resolve(m, "MainFlow/Orders");
            screen.LastModifiedBy = "Fay"; screen.LastModifiedDate = new DateTime(2022, 3, 4, 5, 6, 7);
            Model.Save(m, stamped);
        }

        using var built = Model.Load(stamped);
        var entity = Json(Describe.Run(built, "Order", 0, [], history: true));
        var h = entity.TryGetProperty("history", out var found) ? found : default;
        string Field(string name) => h.ValueKind == JsonValueKind.Object && h.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        if (Field("createdBy") != "Ana" || Field("lastModifiedBy") != "Ben" || Field("lastModifiedDate") != "2024-05-06T07:08:09"
            || Field("lastMergedBy") != "Cleo" || Field("lastMergedDate") != "2024-05-07T01:02:03")
            failures.Add("history: describe --history lost a value after save and reopen: " + h);
        if (!entity.GetProperty("text").GetString()!.TrimEnd().EndsWith("Changed by Ben on 2024-05-06T07:08:09; created by Ana", StringComparison.Ordinal))
            failures.Add("history: the last line of the describe text is wrong");
        var eve = Json(Describe.Run(built, "Customer", 0, [], history: true)).GetProperty("history");
        if (eve.GetProperty("lastModifiedBy").GetString() != "Eve" || Has(eve, "lastModifiedDate"))
            failures.Add("history: a 1900-01-01 date was not treated as empty: " + eve);
        var deep = Json(Describe.Run(built, "MainFlow/Orders", 1, [], history: true)).GetProperty("text").GetString()!.Split(Environment.NewLine + Environment.NewLine);
        if (deep.Length < 2 || !deep[1].StartsWith("Changed by Fay on 2022-03-04T05:06:07", StringComparison.Ordinal))
            failures.Add("history: with --depth the line should follow the element's own text: " + string.Join(" | ", deep.Select(x => x[..Math.Min(40, x.Length)])));

        // An element with only a modified date lists only that; without the flag the output is as before.
        var action = Json(Describe.Run(built, "FetchOrder", 0, [], history: true));
        if (!Has(action, "history") || action.GetProperty("history").GetProperty("lastModifiedBy").GetString() != "Dev" || Has(action.GetProperty("history"), "createdBy"))
            failures.Add("history: an element with only a modified date should list only that: " + action);
        var before = Json(Describe.Run(built, "Order", 0, []));
        if (Has(before, "history") || before.GetProperty("text").GetString()!.Contains("Changed by"))
            failures.Add("history: describe without --history changed its output");

        var rows = Json(Inspect.Filter(built, true, null, history: true));
        var order2 = rows.GetProperty("entities").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "Order");
        if (order2.GetProperty("lastModifiedBy").GetString() != "Ben" || order2.GetProperty("lastModifiedDate").GetString() != "2024-05-06T07:08:09")
            failures.Add("history: inspect --history row is wrong: " + order2);
        if (JsonOutput.Serialize(Inspect.Filter(built, true, null)).Contains("lastModified", StringComparison.Ordinal))
            failures.Add("history: inspect without --history printed change dates");

        // --changed-since keeps the date and later ones, only elements with a date, and drops what has no change date.
        var since = Json(Inspect.Filter(built, true, "entities,actions,screens", changedSince: "2024-05-06"));
        if (string.Join(",", Names(since, "entities")) != "Order" || Names(since, "serverActions").Length != 0 || Names(since, "screens").Length != 0)
            failures.Add("history: --changed-since 2024-05-06 should keep only Order: " + since);
        if (Has(since.GetProperty("entities")[0], "lastModifiedBy"))
            failures.Add("history: --changed-since alone should not add --history fields");
        var wide = Json(Inspect.Filter(built, true, "entities,actions", changedSince: "2023-01-02"));
        if (string.Join(",", Names(wide, "entities")) != "Order" || string.Join(",", Names(wide, "serverActions")) != "FetchOrder")
            failures.Add("history: --changed-since 2023-01-02 should keep Order and FetchOrder (inclusive): " + wide);
        // Exactly one day after the newest change (Order, 2024-05-06): nothing passes. A day-only compare, so 07:08:09 on the day itself counts.
        var none = Json(Inspect.Filter(built, true, "entities,actions", changedSince: "2024-05-07"));
        if (Names(none, "entities").Length != 0 || Names(none, "serverActions").Length != 0)
            failures.Add("history: --changed-since 2024-05-07 should keep nothing");
        if (Json(Inspect.Filter(built, false, null, changedSince: "2023-01-01")).TryGetProperty("references", out _))
            failures.Add("history: --changed-since should drop the references section");
        if (Names(Json(Inspect.Filter(built, true, "entities", changedSince: "2000-01-01")), "entities").Contains("Customer"))
            failures.Add("history: an element with the empty date 1900-01-01 passed --changed-since");
        if (Json(Inspect.Filter(built, true, "screens", changedSince: "2022-03-04")).GetProperty("screens").GetArrayLength() != 1
            || Names(Json(Inspect.Filter(built, true, "screens", changedSince: "2022-03-05")), "screens").Length != 0)
            failures.Add("history: --changed-since is not inclusive on the screen's own day, or keeps it a day later");
        if (Json(Inspect.Filter(built, true, "entities", changedSince: "2024-05-06")).GetProperty("changedSince").GetString() != "2024-05-06")
            failures.Add("history: a filtered inspect does not say it is filtered");
        try { Inspect.Filter(built, false, "references", changedSince: "2024-05-06"); failures.Add("history: --only references with --changed-since was accepted"); }
        catch (CliError) { }
        try { Inspect.Filter(built, true, null, changedSince: "06/05/2024"); failures.Add("history: a bad --changed-since date was accepted"); }
        catch (CliError) { }
    }
}
