using System.Text.Json;

namespace OmlCli;

// (System) server actions a module does not reference yet (ListAppend, ListClear, ListInsert, ListRemove, ListAppendAll,
// ListDuplicate, LogMessage, CommitTransaction, AbortTransaction, ...). Service Studio adds them with Manage Dependencies,
// from the platform server. Offline, oml copies them from the (System) reference of the tutorial module every Service
// Studio install has (TutorialResources/NoLockIn.oml), the same way `references` with "module" copies a reference.
// That module is Traditional Web: it has the server actions only. A client flow needs (System)'s client actions, which
// only Service Studio can add; calling the server one there would change a copy of the list on the server.
static class SystemActions
{
    internal static string Tutorial => Path.Combine(Model.ServiceStudioDir, "TutorialResources", "NoLockIn.oml");

    static HashSet<string>? names;

    // The (System) server actions the tutorial module references; empty when the file is not there.
    internal static HashSet<string> Known()
    {
        if (names is not null) return names;
        names = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(Tutorial)) return names;
        using var tutorial = Model.Borrow(Tutorial, "load system actions");
        foreach (var r in tutorial.Module.References.Where(r => r.Name == "(System)"))
            foreach (var a in r.ServerActions) names.Add(a.Name);
        return names;
    }

    // Adds (System).<name> to the module's (System) reference. False when it is not a known system action.
    internal static bool TryImport(OutSystems.Model.IESpace m, string name, bool clientFlow, List<string> created, List<string> existing, List<string> problems, List<string> warnings)
    {
        if (name.Contains('.') && !name.StartsWith("(System).", StringComparison.Ordinal)) return false;
        var bare = name.StartsWith("(System).", StringComparison.Ordinal) ? name[9..] : name;
        if (!Known().Contains(bare)) return false;
        if (clientFlow)
            throw new ModelError($"'{bare}' in a client flow needs the (System) client action {bare}, and this module does not reference it. "
                + $"Only Service Studio can add it: Manage Dependencies > (System) > {bare}, save, and apply again (or pass a module that has it: "
                + $"\"references\": [{{ \"producer\": \"<that module>.oml\", \"module\": \"(System)\", \"elements\": [\"{bare}\"] }}]). "
                + "A server action can call it now: oml adds the server action from Service Studio's tutorial module.");
        var before = problems.Count;
        ModuleReferences.Add(m, [new ReferenceSpec(Tutorial, JsonSerializer.SerializeToElement(new[] { bare }), Module: "(System)")], created, existing, problems, warnings);
        if (problems.Count > before) return false;
        warnings.Add($"added (System).{bare} to the module's (System) reference, from Service Studio's tutorial module; Service Studio refreshes it from the platform server on the next Manage Dependencies");
        return true;
    }
}
