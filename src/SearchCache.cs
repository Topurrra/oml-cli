namespace OmlCli;

// The saved index of oml's own docs (search-index.bin next to the usage log): `oml doc --search` and `oml spec --search`
// are run many times per task, and building the corpus and its index from the embedded files cost most of each call.
// The format is LibraryIndex's. The key is the format and the assembly's module version id, which changes with every
// build and so with the embedded docs; company skills are not in it, because they never enter this index (WithCompany
// ranks them from the installed files on every search). OML_SEARCH_CACHE: "0", "off" or "false" turns it off, another
// value is the file to use (tests), empty or "1" the default. A cache only: whatever goes wrong with the file means
// "build as before", nothing is thrown, and the save is a temp file moved over the old one, so parallel runs are safe.
static class SearchCache
{
    const int Format = 1;

    static string? File
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("OML_SEARCH_CACHE")?.Trim();
            if (value is "0" or "off" or "false") return null;
            try
            {
                return string.IsNullOrEmpty(value) || value == "1"
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "oml-cli", "search-index.bin")
                    : Path.GetFullPath(value);
            }
            catch (ArgumentException) { return null; }
        }
    }

    public static SearchIndex Get(Func<SearchIndex> build)
    {
        var file = File;
        if (file is null) return build();
        var key = $"{Format}|{typeof(SearchCache).Module.ModuleVersionId}";
        var notes = new List<string>();
        try { if (LibraryIndex.Load(file, key, notes) is { } saved) return saved; }
        catch (Exception) { } // missing, locked, truncated or damaged: build it again
        var index = build();
        try { LibraryIndex.Save(file, key, index, notes); }
        catch (Exception) { }
        return index;
    }
}
