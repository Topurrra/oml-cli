using System.Text.Json;

namespace OmlCli;

static partial class SelfTest
{
    // The author (OML_AUTHOR) goes on what an apply created (CreatedBy and LastModifiedBy) and changed (LastModifiedBy),
    // and on nothing else. A build whose history fields are read-only passes with a note: the apply then says so itself.
    static void AuthorTest(string builtPath, string dir, List<string> failures)
    {
        const string Author = "Ana Silva";
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var spec = Path.Combine(dir, "author.json");
        File.WriteAllText(spec, """
            { "entities": [ { "name": "Invoice", "attributes": [ { "name": "Total", "type": "Decimal" } ] },
                            { "name": "Order", "attributes": [ { "name": "Note", "type": "Text" } ] } ],
              "serverActions": [ { "name": "SendInvoice" } ] }
            """);
        var output = Path.Combine(dir, "author.oml");
        Environment.SetEnvironmentVariable("OML_AUTHOR", Author);
        (int code, object report) result;
        try { result = Apply.Execute(builtPath, spec, output); }
        finally { Environment.SetEnvironmentVariable("OML_AUTHOR", null); }
        var json = JsonSerializer.SerializeToElement(result.report);
        if (result.code != 0) { failures.Add("author: apply failed: " + json); return; }
        if (!json.TryGetProperty("author", out var named) || named.GetString() != Author) failures.Add("author: the apply report does not name the author: " + json);
        if (json.TryGetProperty("next", out var next) && next.GetString()!.Contains("No author is set", StringComparison.Ordinal))
            failures.Add("author: an apply with an author still asks for one");
        if (json.GetProperty("warnings").EnumerateArray().Any(w => w.GetString() == Authorship.NotWritable))
        {
            Console.Error.WriteLine("self-test note: this Service Studio build does not let oml write CreatedBy or LastModifiedBy; Service Studio shows Unknown for what oml creates.");
            return;
        }

        using var m = Model.Load(output);
        // Only a field this build lets a caller write is expected; History reads what the saved module holds.
        void Expect(object element, string label, bool created, bool changed)
        {
            var h = History.Of(element);
            if (Reflect.Property(element, "LastModifiedBy", writable: true) is not null && (h?.lastModifiedBy == Author) != (created || changed))
                failures.Add($"author: {label} has lastModifiedBy '{h?.lastModifiedBy}'");
            if (Reflect.Property(element, "CreatedBy", writable: true) is not null && (h?.createdBy == Author) != created)
                failures.Add($"author: {label} has createdBy '{h?.createdBy}'");
            // The date goes with the name, as History prints it (yyyy-MM-ddTHH:mm:ss, local time, from the day this test started).
            if (Reflect.Property(element, "LastModifiedBy", writable: true) is not null && Reflect.Property(element, "LastModifiedDate", writable: true) is not null
                && (h?.lastModifiedDate is { } date && string.CompareOrdinal(date, 0, today, 0, 10) >= 0) != (created || changed))
                failures.Add($"author: {label} has lastModifiedDate '{h?.lastModifiedDate}'");
        }
        Expect(m.Entities.Single(e => e.Name == "Invoice"), "the created entity Invoice", created: true, changed: false);
        Expect(m.ServerActions.Single(a => a.Name == "SendInvoice"), "the created action SendInvoice", created: true, changed: false);
        Expect(m.Entities.Single(e => e.Name == "Order"), "the entity Order (an attribute added)", created: false, changed: true);
        Expect(m.Entities.Single(e => e.Name == "Customer"), "the untouched entity Customer", created: false, changed: false);
        Expect(m.ServerActions.Single(a => a.Name == "FetchOrder"), "the untouched action FetchOrder", created: false, changed: false);
    }
}
