using System.Reflection;
using OutSystems.Model;

namespace OmlCli;

// Readers (describe, inspect, tree, find, census) print what they can read. While a collector is open, a part that throws
// (a widget, node, property, reference) is recorded as {"part", "reason"} and skipped, instead of ending the command.
// Code that changes a module never opens one, so writers still see every error.
sealed class Unreadable : IDisposable
{
    // Section: the output section the part belongs to (inspect), so a filtered section drops its entries too.
    public sealed record Item(string part, string reason)
    { [System.Text.Json.Serialization.JsonIgnore] public string? Section { get; init; } }
    const int Keep = 50;

    [ThreadStatic] static Unreadable? current;
    [ThreadStatic] static string? section;
    [ThreadStatic] static bool labeling;
    readonly Unreadable? previous;
    readonly List<Item> all = new(); // every failure, repeats included

    public Unreadable() { previous = current; current = this; }
    public void Dispose() => current = previous;

    public static bool Active => current is not null;

    // Null when everything was read, so the field is left out. At most 50 distinct parts, then one "N more" entry that
    // counts every other failure. keep: which sections are still in the output (inspect --only, --changed-since).
    public Item[]? Items(Func<string, bool>? keep = null) => Cap(all.Where(i => i.Section is null || keep?.Invoke(i.Section) != false).ToList());

    // The same cap for a list kept elsewhere (the review report).
    public static Item[]? Cap(List<Item> kept)
    {
        if (kept.Count == 0) return null;
        var listed = kept.Distinct().Take(Keep).ToList();
        if (kept.Count > listed.Count)
            listed.Add(new Item($"{kept.Count - listed.Count} more", "not listed: repeats of the parts above or other parts"));
        return listed.ToArray();
    }

    // The failures a reader skips: what the model's getters throw. Anything else (a bug in oml) still ends the command.
    static bool ModelFailure(Exception e) => e is TargetInvocationException or InvalidOperationException or NotSupportedException
        or NotImplementedException or NullReferenceException or InvalidCastException;

    public static void Add(Func<string> part, Exception e)
    {
        if (current is not { } c || labeling) return;
        while (e is TargetInvocationException { InnerException: { } inner }) e = inner;
        string label;
        labeling = true; // a getter that throws while the label is built is not recorded again
        try { label = part(); }
        catch (Exception) { label = "?"; }
        finally { labeling = false; }
        c.all.Add(new Item(label, $"{e.GetType().Name}: {e.Message}") { Section = section });
    }

    // Where an element is: its path from the module (tree's form, Kind[i] for unnamed parts).
    public static string Where(object o)
    {
        if (o is not IObjectSignature s) return o.GetType().Name;
        var parts = new List<string>();
        for (; s.Parent is { } parent && s is not IESpace; s = parent) parts.Insert(0, ElementPaths.Segment(s, parent));
        return parts.Count == 0 ? o.GetType().Name : string.Join("/", parts);
    }

    // Parts read until the result is disposed belong to this output section.
    public static IDisposable In(string name)
    {
        var outer = section;
        section = name;
        return new Restore(() => section = outer);
    }
    sealed class Restore(Action undo) : IDisposable { public void Dispose() => undo(); }

    public static T Try<T>(Func<string> part, Func<T> read, T fallback)
    {
        if (current is null) return read();
        try { return read(); }
        catch (Exception e) when (ModelFailure(e)) { Add(part, e); return fallback; }
    }

    public static T Try<T>(string part, Func<T> read, T fallback) => Try(() => part, read, fallback);

    // False when the part could not be read.
    public static bool Try(Func<string> part, Action read) => Try(part, () => { read(); return true; }, false);
    public static bool Try(string part, Action read) => Try(() => part, read);
}
