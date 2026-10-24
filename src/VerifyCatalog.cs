namespace OmlCli;

// Verification message ids, embedded once as data: the ids the platform can emit (Entity_*, InvalidFlow_*, Expression*,
// Parser*, Operator*, SQL_* ...). They are not translations and no fix text is shipped; they exist so `oml warn` can
// name a message even when oml has no written page for it, and so the check and validate reports can be
// cross-referenced with the IDE.
static class VerifyCatalog
{
    const string ResourceName = "OmlCli.verifymessageids.txt";
    static readonly HashSet<string>? ids = Read();

    static HashSet<string>? Read()
    {
        try
        {
            using var stream = typeof(VerifyCatalog).Assembly.GetManifestResourceStream(ResourceName)
                               ?? throw new InvalidOperationException($"This build has no embedded resource {ResourceName}.");
            using var reader = new StreamReader(stream);
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in reader.ReadToEnd().Split('\n'))
            {
                var id = line.Trim();
                if (id.Length > 0) set.Add(id);
            }
            return set;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            return null;
        }
    }

    public static int Count => ids?.Count ?? 0;
    public static bool IsKnown(string? id) => id is not null && ids?.Contains(id) == true;

    // The known ids that start with the given prefix, sorted, limited for display.
    public static string[] Matching(string? prefix)
    {
        if (ids is null) return [];
        prefix = prefix?.Trim() ?? "";
        var matches = prefix.Length == 0 ? ids.ToArray()
            : ids.Where(id => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        Array.Sort(matches, StringComparer.Ordinal);
        return matches;
    }
}
