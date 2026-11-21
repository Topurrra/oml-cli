using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// `oml migrate check <old.oml> <new.oml>`: is the new Reactive module a correct copy of the old one, beyond the names that
// `migrate status` compares? Each check is pass, warn or fail; exit 1 when one fails.
//   validation   the new module's errors (fail) and warnings (warn)
//   signatures   a server or service action in both modules keeps every input and output with its type (a removed or retyped
//                one fails: callers break); an added input warns (expected for session variables the plan threads through)
//   screens      a screen in both keeps its inputs (links and destinations pass them). Its roles say who may open it: a new screen
//                that lets in someone the old one did not (Anonymous added; Registered where only custom roles were; a new custom role)
//                fails, as a security change; a role no longer there warns (those users cannot open it now)
//   blocks       a block in both keeps its inputs (warn when one is missing)
//   todos        the "TODO migrate" stubs still in the new module's screens and blocks, per element (warn)
static partial class Migrate
{
    static int CheckCommand(string[] a)
    {
        var files = a.Where(x => !x.StartsWith("--")).ToList();
        if (a.FirstOrDefault(x => x.StartsWith("--")) is { } option) throw new Refusal($"Unknown option {option}.", "Usage: oml migrate check <old.oml> <new.oml>");
        if (files.Count != 2) throw new Refusal("migrate check takes two modules: oml migrate check <old.oml> <new.oml>.");
        foreach (var f in files) if (!File.Exists(f)) throw new Refusal($"{Path.GetFullPath(f)} does not exist.");
        var (oldPath, newPath) = (Path.GetFullPath(files[0]), Path.GetFullPath(files[1]));
        var oldI = Child("inspect", oldPath);
        var newI = Child("inspect", newPath);
        var validation = Child(["validate", newPath, "--warnings"], tolerateFailure: true);
        // The describe text of every screen and block of the new module, for the stubs the translator left.
        var notes = new List<string>();
        var elements = Names(newI, "screens").Select(n => ($"Screen:{n}", n)).Concat(Names(newI, "blocks").Select(n => ($"Block:{n}", n))).ToList();
        var texts = elements.Count == 0 ? new Dictionary<string, string>() : Describe((args, stdin) => Child(args, tolerateFailure: true, stdin: stdin), newPath, elements, notes, "--depth", "0");
        var result = Check(oldI, newI, validation, texts);
        if (notes.Count > 0) result["notes"] = Arr(notes);
        TerminalOutput.PrintNode(result);
        return result["ok"]!.GetValue<bool>() ? 0 : 1;
    }

    // "OrdersWeb.Order Identifier" and "Order Identifier" are the same type: the new module references the old one's entities.
    static string TypeText(string? t) => Regex.Replace(t ?? "", @"\b\w+\.(?=\w)", "").Trim();

    static Dictionary<string, string> Params(JsonObject action, string key) =>
        Rows(action, key).Where(p => S(p, "name") is not null).GroupBy(p => S(p, "name")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => TypeText(S(g.First(), "type")), StringComparer.OrdinalIgnoreCase);

    // The pure part: inspect of both modules, the validate output of the new one, and describe texts by element name.
    internal static JsonObject Check(JsonNode oldI, JsonNode newI, JsonNode validation, IReadOnlyDictionary<string, string> texts)
    {
        var checks = new JsonArray();
        void Add(string name, string status, IEnumerable<string> details, string summary)
        {
            var list = details.Take(30).ToList();
            checks.Add(new JsonObject { ["check"] = name, ["status"] = status, ["summary"] = summary, ["details"] = list.Count > 0 ? Arr(list) : null });
        }

        // validation
        var errors = P(validation, "errorCount")?.GetValue<int>() ?? Rows(validation, "errors").Length;
        var warnings = P(validation, "warningCount")?.GetValue<int>() ?? 0;
        var errorLines = Rows(validation, "errors").SelectMany(e => Rows(e, "samples").Take(2).Select(s => $"{S(e, "id")}: {S(s, "message")} {S(s, "detail")}".Trim()));
        Add("validation", errors > 0 ? "fail" : warnings > 0 ? "warn" : "pass", errorLines, $"{errors} errors, {warnings} warnings in the new module");

        // signatures of actions in both
        var wrappers = Set(WrappersIn(oldI));
        var sigFails = new List<string>(); var sigWarns = new List<string>(); var compared = 0;
        foreach (var kind in new[] { "serverActions", "serviceActions" })
        {
            var now = Rows(newI, kind).Where(r => S(r, "name") is not null).GroupBy(r => S(r, "name")!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var old in Rows(oldI, kind))
            {
                if (S(old, "name") is not { } n || wrappers.Contains(n) || !now.TryGetValue(n, out var copy)) continue;
                compared++;
                foreach (var key in new[] { "inputs", "outputs" })
                {
                    var (before, after) = (Params(old, key), Params(copy, key));
                    foreach (var (p, type) in before)
                        if (!after.TryGetValue(p, out var t)) sigFails.Add($"{n}: {key[..^1]} {p} is missing");
                        else if (!string.Equals(t, type, StringComparison.OrdinalIgnoreCase)) sigFails.Add($"{n}: {key[..^1]} {p} was {type}, is {t}");
                    foreach (var p in after.Keys.Where(p => !before.ContainsKey(p)))
                        (key == "inputs" ? sigWarns : sigFails).Add($"{n}: new {key[..^1]} {p} ({after[p]}){(key == "inputs" ? ", every caller must pass it" : "")}");
                }
            }
        }
        Add("signatures", sigFails.Count > 0 ? "fail" : sigWarns.Count > 0 ? "warn" : "pass", sigFails.Concat(sigWarns),
            $"{compared} actions in both modules: {sigFails.Count} broken, {sigWarns.Count} with new inputs");

        // screens: inputs and roles
        var screenFails = new List<string>(); var screenWarns = new List<string>(); var screensCompared = 0;
        var newScreens = Rows(newI, "screens").Where(r => S(r, "name") is not null).GroupBy(r => S(r, "name")!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var old in Rows(oldI, "screens"))
        {
            if (S(old, "name") is not { } n || !newScreens.TryGetValue(n, out var now)) continue;
            screensCompared++;
            var inputs = Set(Strings(now, "inputs"));
            foreach (var i in Strings(old, "inputs").Where(i => !inputs.Contains(i))) screenFails.Add($"{n}: input {i} is missing (links and destinations pass it)");
            var (rolesBefore, rolesNow) = (Set(Strings(old, "roles")), Set(Strings(now, "roles")));
            // Anonymous lets everyone in, Registered every signed-in user; a custom role only its members.
            bool Open(HashSet<string> roles) => roles.Contains("Anonymous");
            bool AnyUser(HashSet<string> roles) => Open(roles) || roles.Contains("Registered");
            if (Open(rolesNow) && !Open(rolesBefore)) screenFails.Add($"{n}: Anonymous can open it now; the old screen needed a login");
            else if (AnyUser(rolesNow) && !AnyUser(rolesBefore)) screenFails.Add($"{n}: every signed-in user can open it now (Registered); the old screen needed {string.Join(" or ", rolesBefore.Order(StringComparer.Ordinal))}");
            else if (!AnyUser(rolesBefore))
                foreach (var r in rolesNow.Where(r => !rolesBefore.Contains(r)).Order(StringComparer.Ordinal)) screenFails.Add($"{n}: role {r} can open it now; it could not before");
            foreach (var r in rolesBefore.Where(r => !rolesNow.Contains(r)).Order(StringComparer.Ordinal))
                if (!(r == "Registered" && Open(rolesNow)) && !(r != "Anonymous" && r != "Registered" && AnyUser(rolesNow)))
                    screenWarns.Add($"{n}: role {r} can no longer open it");
        }
        Add("screens", screenFails.Count > 0 ? "fail" : screenWarns.Count > 0 ? "warn" : "pass", screenFails.Concat(screenWarns),
            $"{screensCompared} screens in both modules: {screenFails.Count} problems, {screenWarns.Count} narrower");

        // blocks: inputs
        var blockWarns = new List<string>(); var blocksCompared = 0;
        var newBlocks = Rows(newI, "blocks").Where(r => S(r, "name") is not null).GroupBy(r => S(r, "name")!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var old in Rows(oldI, "blocks"))
        {
            if (S(old, "name") is not { } n || !newBlocks.TryGetValue(n, out var now)) continue;
            blocksCompared++;
            var inputs = Set(Strings(now, "inputs"));
            foreach (var i in Strings(old, "inputs").Where(i => !inputs.Contains(i))) blockWarns.Add($"{n}: input {i} is missing");
        }
        Add("blocks", blockWarns.Count > 0 ? "warn" : "pass", blockWarns, $"{blocksCompared} blocks in both modules: {blockWarns.Count} missing inputs");

        // todos left by the translator
        var todos = texts.Select(kv => (name: kv.Key, count: Regex.Matches(kv.Value, @"TODO migrate", RegexOptions.IgnoreCase).Count)).Where(x => x.count > 0)
            .OrderByDescending(x => x.count).ThenBy(x => x.name, StringComparer.Ordinal).ToList();
        Add("todos", todos.Count > 0 ? "warn" : "pass", todos.Select(t => $"{t.name}: {t.count}"),
            $"{todos.Sum(t => t.count)} TODO migrate stubs in {todos.Count} of {texts.Count} screens and blocks");

        var statuses = checks.Select(c => c!["status"]!.GetValue<string>()).ToList();
        return new JsonObject
        {
            ["old"] = S(oldI, "name"), ["new"] = S(newI, "name"),
            ["ok"] = !statuses.Contains("fail"),
            ["line"] = string.Join("; ", checks.Select(c => $"{c!["check"]} {c["status"]}")),
            ["checks"] = checks,
            ["next"] = statuses.Contains("fail") ? "Fix every failed check in a new staged copy of the new module, then run oml migrate check again."
                : statuses.Contains("warn") ? "Review each warning; the todos are listed in 4-screens.md of the plan." : "The new module matches the old one on every check. Test it in the browser (oml browser sweep, oml test scenarios).",
        };
    }
}
