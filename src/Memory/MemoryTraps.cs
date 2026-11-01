namespace OmlCli;

// `oml memory traps --command "<command>"`: the live trap records that a shell command may run into, so a pre-command hook
// or an agent can read them before it runs the command. Lexical, like the rest of oml memory: no pattern is stored, so the
// record format does not change. A trap matches when the command names one of its elements, or holds two or more of the
// words of its text (after the normalization of the search engine; numbers, "oml" and the module name do not count).
// It only warns: it never blocks a command, and it may also name a trap whose fix the command already applies.
static class MemoryTraps
{
    public sealed record Match(MemRecord Trap, string[] Matched, int Score);

    // The module a command works on, from its first module file (Orders.oml -> Orders), or null.
    public static string? ModuleOf(string command) =>
        SearchText.Words(command).Where(w => w.EndsWith(".oml", StringComparison.OrdinalIgnoreCase) && w.Length > 4).Select(w => w[..^4]).FirstOrDefault();

    public static List<Match> Find(IEnumerable<MemRecord> traps, string command, string? module)
    {
        // Names in the command: each word whole and each of its dot parts (Orders.oml gives ordersoml, orders and oml).
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in SearchText.Words(command))
        {
            names.Add(SearchText.Norm(w));
            foreach (var part in w.Split('.')) names.Add(SearchText.Norm(part));
        }
        var words = Terms(command);
        var found = new List<Match>();
        foreach (var t in traps)
        {
            var ignore = Terms(t.Module).Concat(Terms(module ?? "")).Append("oml").ToHashSet(StringComparer.Ordinal);
            var elements = t.Elements.Select(e => e[(e.LastIndexOfAny(['/', '.']) + 1)..]).Where(e => e.Length > 0 && names.Contains(SearchText.Norm(e))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var shared = Terms(t.Text).Where(term => term.Length >= 3 && !term.All(char.IsDigit) && !ignore.Contains(term) && words.Contains(term)).Distinct(StringComparer.Ordinal).ToArray();
            if (elements.Length == 0 && shared.Length < 2) continue;
            found.Add(new Match(t, elements.Concat(shared).ToArray(), elements.Length * 10 + shared.Length));
        }
        return found.OrderByDescending(m => m.Score).ThenByDescending(m => m.Trap.Seq).ToList();
    }

    // Normalized terms of the parts of each word (OrderDetail is order and detail), without the joined form the search
    // index adds, so one identifier does not count three times.
    static HashSet<string> Terms(string text) =>
        SearchText.Words(text).SelectMany(SearchText.Parts).Select(p => p.ToLowerInvariant()).Where(p => !SearchText.IsStopWord(p)).Select(SearchText.Stem).ToHashSet(StringComparer.Ordinal);
}
