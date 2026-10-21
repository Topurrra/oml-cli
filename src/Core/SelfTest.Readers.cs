using System.Reflection;
using OutSystems.Model;

namespace OmlCli;

static partial class SelfTest
{
    // Readers: while one runs, a getter that throws is recorded in `unreadable` and read as null; outside a reader it
    // still throws, so code that changes a module sees every error. On this build the (System) Attachment structure
    // declares history fields whose getters throw "Operation is not valid due to the current state of the object".
    static void ReadersTest(string blank, List<string> failures)
    {
        var part = new ThrowingPart();
        using (var unreadable = new Unreadable())
        {
            if (Reflect.Get(part, "Value") is not null || unreadable.Items() is not [{ reason: var reason }] || !reason.StartsWith("InvalidOperationException", StringComparison.Ordinal))
                failures.Add("readers: a getter that throws was not recorded as unreadable");
            if (Unreadable.Try("part", () => throw new InvalidOperationException(), "fallback") != "fallback")
                failures.Add("readers: Unreadable.Try did not return its fallback");
            try { Unreadable.Try("bug", () => throw new ArgumentException("an oml bug"), ""); failures.Add("readers: a non-model error was swallowed"); }
            catch (ArgumentException) { }
        }
        try { Reflect.Get(part, "Value"); failures.Add("readers: a getter error was swallowed outside a reader"); }
        catch (TargetInvocationException) { }

        // The cap: 50 distinct parts, then one entry that counts every other failure, repeats included, per kept section.
        using (var unreadable = new Unreadable())
        {
            using (Unreadable.In("entities"))
                for (var i = 0; i < 60; i++) Unreadable.Try($"entities[{i}]", () => throw new InvalidOperationException(), 0);
            using (Unreadable.In("screens"))
                for (var i = 0; i < 3; i++) Unreadable.Try("screens[0]", () => throw new InvalidOperationException(), 0); // the same part 3 times
            var all = unreadable.Items()!;
            if (all.Length != 51 || all[^1].part != "13 more") failures.Add($"readers: cap gave {all.Length} entries, last '{all[^1].part}'");
            var screens = unreadable.Items(s => s == "screens")!;
            if (screens.Length != 2 || screens[0].part != "screens[0]" || screens[1].part != "2 more")
                failures.Add($"readers: section filter gave {string.Join(", ", screens.Select(x => x.part))}");
            if (unreadable.Items(s => s == "roles") is not null) failures.Add("readers: a removed section kept its entries");
        }

        using var m = Model.Load(blank);
        if (ElementPaths.Children(m).FirstOrDefault(c => Model.NameOf(c) == "Attachment") is not { } attachment)
        { failures.Add("readers: a new module has no (System) Attachment structure"); return; }
        if (History.Of(attachment) is not null) failures.Add("readers: (System) Attachment has a history");
        try { Describe.Run(m, "Attachment", 0, [], history: true); }
        catch (Exception e) { failures.Add("readers: describe Attachment --history failed: " + e.Message); }

        // find --text walks the module's own elements and gives each hit a path that describe and edits accept.
        var (docs, elements) = ModuleSearch.Read(m);
        var hit = new SearchIndex(docs, ModuleSearch.Options).Search("attachment", 1).FirstOrDefault();
        if (hit is null || hit.Doc.Title != "Attachment" || ElementPaths.PathTo(m, elements[hit.Doc.Id]) != "Attachment")
            failures.Add($"readers: find --text 'attachment' on a new module gave {hit?.Doc.Title ?? "nothing"} ({docs.Count} elements read)");
    }

    sealed class ThrowingPart { public string Value => throw new InvalidOperationException("Operation is not valid due to the current state of the object."); }
}
