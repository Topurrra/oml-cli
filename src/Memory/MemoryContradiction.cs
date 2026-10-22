namespace OmlCli;

// The contradiction warning of oml memory: a C# port of voli's offline classifier (crate stela, contradiction.rs).
// Advisory only: the new record is written, and the caller names the clash so the agent can retract the old record.
// Lexical, not semantic (a hit needs a negation or an antonym): no model, no network, the same answer every time. Two records whose valid-time windows do
// not overlap are history, not a clash.
static class MemoryContradiction
{
    public enum Relation { Entailment, Neutral, Contradiction }

    // Share of common words (Jaccard) below which two records are about different things.
    const double MinSubjectOverlap = 0.2;
    // At or above this overlap, with no conflict cue, the new record only restates the old one.
    const double EntailOverlap = 0.6;

    static readonly string[] Negations = ["not", "never", "no", "none", "n't", "cannot", "false"];

    static readonly (string A, string B)[] Antonyms =
    [
        ("likes", "hates"), ("likes", "dislikes"), ("loves", "hates"), ("love", "hate"), ("on", "off"), ("open", "closed"),
        ("true", "false"), ("yes", "no"), ("married", "single"), ("alive", "dead"),
        // oml's words
        ("public", "private"), ("allow", "forbid"), ("enable", "disable"), ("enabled", "disabled"), ("required", "optional"),
    ];

    static HashSet<string> Tokens(string s) =>
        Words(s).Where(t => t.Length >= 2).Select(t => t.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    // Runs of letters and digits.
    static IEnumerable<string> Words(string s) => System.Text.RegularExpressions.Regex.Matches(s, @"[\p{L}\p{N}]+").Select(m => m.Value);

    static double Overlap(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var inter = a.Count(b.Contains);
        return inter / (double)(a.Count + b.Count - inter);
    }

    // "no" and "not" count as words only ("now" and "nope" do not); "n't" and "cannot" count anywhere.
    static bool HasNegation(string lower)
    {
        var words = Words(lower).ToArray();
        return Negations.Any(n => n is "n't" or "cannot" ? lower.Contains(n, StringComparison.Ordinal) : words.Contains(n));
    }

    // a is the new record, b an existing one.
    public static Relation Classify(string a, string b)
    {
        var (ta, tb) = (Tokens(a), Tokens(b));
        var overlap = Overlap(ta, tb);
        // oml addition: a short new record about an element ("Orders_CS is public") shares little with a long old one, so
        // an antonym pair also counts when the two share at least two words (the module and the element, usually).
        var sameSubject = overlap >= MinSubjectOverlap;
        if (!sameSubject && ta.Count(tb.Contains) < 2) return Relation.Neutral;
        foreach (var (x, y) in Antonyms)
            if (ta.Contains(x) && tb.Contains(y) || ta.Contains(y) && tb.Contains(x)) return Relation.Contradiction;
        if (!sameSubject) return Relation.Neutral;
        if (HasNegation(a.ToLowerInvariant()) != HasNegation(b.ToLowerInvariant())) return Relation.Contradiction;
        // voli also called a changed value a clash ("Berlin" and "Lisbon"). Here only a negation or an antonym counts: two
        // records that merely share words ("after dev append", "after edit") are not a clash.
        return overlap >= EntailOverlap ? Relation.Entailment : Relation.Neutral;
    }

    // Two windows [from, until) overlap unless one ends before the other starts (touching at a point does not overlap).
    public static bool WindowsOverlap(DateTime? aFrom, DateTime? aUntil, DateTime? bFrom, DateTime? bUntil) =>
        !(aUntil is { } au && bFrom is { } bf && au <= bf) && !(bUntil is { } bu && aFrom is { } af && bu <= af);
}
