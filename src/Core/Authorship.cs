using OutSystems.Model;

namespace OmlCli;

// The author oml writes on what it creates or changes, so Service Studio shows a name instead of "Unknown".
// The launcher passes it in OML_AUTHOR (the variable a person set, else `author` of the project profile, see
// ProjectConfig.ApplyEnvironment). What a run created or changed is found as `oml review` finds it: two ReviewModel
// snapshots, compared by GlobalKey. Only those elements are stamped, and only the history fields the model lets a
// caller write (CreatedBy on created ones, LastModifiedBy and LastModifiedDate on created and changed ones).
static class Authorship
{
    // One line, at most 100 characters, no control characters (the launcher refuses the same in `oml config set author`).
    public static string? Name => Environment.GetEnvironmentVariable("OML_AUTHOR")?.Trim() is { Length: > 0 and <= 100 } name && !name.Any(char.IsControl) ? name : null;

    // The `next` text of a change made without an author.
    // A variable that is set but not a valid name is named as the cause: it wins over the profile, so setting the profile alone does not help.
    public static string Ask => Environment.GetEnvironmentVariable("OML_AUTHOR") is { } raw && raw.Trim().Length > 0
        ? "OML_AUTHOR is not a valid name (one line, at most 100 characters), so Service Studio will show Unknown for what this change created. Ask the user which name to use, correct or remove OML_AUTHOR, run: oml config set author \"<name>\" and apply again to a new staged file."
        : "No author is set, so Service Studio will show Unknown for what this change created. Ask the user which name to use, then run: oml config set author \"<name>\" and apply again to a new staged file.";

    // The report line when no element took the name.
    public const string NotWritable = "The author could not be written on this Service Studio build (its history fields are read-only), so Service Studio shows Unknown for what this change created.";

    // Keys of the elements `after` has that `before` lacks (created), and of the elements whose own text differs or that
    // hold a created, changed or removed element (changed). The elements of another module (inside a reference) are
    // never listed; the reference that holds them is.
    public static (HashSet<string> created, HashSet<string> changed) Touched(ReviewModel.Snapshot before, ReviewModel.Snapshot after)
    {
        var created = new HashSet<string>(StringComparer.Ordinal);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        static bool Foreign(ReviewModel.Snapshot s, ReviewModel.Item i) => i.Unit != i.Key && s.Items.TryGetValue(i.Unit, out var unit) && unit.Kind == "Reference";
        static bool Same(ReviewModel.Item a, ReviewModel.Item b) => a.Name == b.Name && a.Kind == b.Kind && a.Parent == b.Parent
            && a.Props.Count == b.Props.Count && a.Props.All(p => b.Props.TryGetValue(p.Key, out var v) && v == p.Value) && a.Next.SequenceEqual(b.Next);
        // The element and every element that holds it, up to its top element.
        void Up(string? key)
        {
            while (key is not null && after.Items.TryGetValue(key, out var item))
            {
                if (!created.Contains(key)) changed.Add(key);
                key = item.Parent;
            }
        }
        foreach (var key in after.Order)
        {
            var now = after.Items[key];
            if (!before.Items.TryGetValue(key, out var old))
            {
                if (!Foreign(after, now)) created.Add(key);
                Up(now.Parent);
            }
            else if (!Foreign(after, now) && !Same(old, now))
            {
                Up(key);
                if (old.Parent != now.Parent) Up(old.Parent);
            }
        }
        foreach (var key in before.Order)
            if (!after.Items.ContainsKey(key)) Up(before.Items[key].Parent);
        return (created, changed);
    }

    // Every own element of a module the run made from nothing (`oml new`): all of it is created.
    public static ReviewModel.Snapshot Nothing() => new() { Module = "", Key = "" };

    // Writes the author on the created and changed elements that keep history. Returns how many took it and whether any
    // could not (an element keeps history, but this build gives no writable CreatedBy or LastModifiedBy).
    public static (int written, bool refused) Stamp(IESpace m, string author, HashSet<string> created, HashSet<string> changed)
    {
        int written = 0, refused = 0;
        var done = new HashSet<string>(StringComparer.Ordinal);
        // As SelfTest.History writes it: a DateTime without a kind (the model stores no time zone), here the local time of this computer.
        var now = new DateTime(DateTime.Now.Ticks, DateTimeKind.Unspecified);
        static bool Set(object o, string field, object value)
        {
            if (Reflect.Property(o, field, writable: true) is not { } property) return false;
            try { property.SetValue(o, value); return true; }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException is InvalidOperationException or NotImplementedException or NotSupportedException) { return false; }
        }
        foreach (var o in ProductionRules.Walk(m))
        {
            var key = o.GlobalKey.ToString()!;
            var isNew = created.Contains(key);
            if (!isNew && !changed.Contains(key) || !done.Add(key) || !KeepsHistory(o)) continue;
            var wrote = isNew && Set(o, "CreatedBy", author);
            // The date goes with the name: a name without a date looks wrong in Service Studio and misses inspect --changed-since.
            if (Set(o, "LastModifiedBy", author)) { wrote = true; Set(o, "LastModifiedDate", now); }
            if (wrote) written++; else refused++;
        }
        return (written, refused > 0 && written == 0);
    }

    // As History.Of: the element declares the fields, and its getter answers (system elements declare them and throw).
    static bool KeepsHistory(object o)
    {
        if (Reflect.Property(o, "LastModifiedDate") is null || Reflect.Property(o, "LastModifiedBy") is not { } by) return false;
        try { _ = by.GetValue(o); return true; }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException is InvalidOperationException or NotImplementedException) { return false; }
    }
}
