using System.Text;

namespace OmlCli;

// The saved search index of the library (library\index.bin): the sections of every fetched file and their term counts,
// so `oml doc --search --library` neither reads 2,600 files nor tokenizes them again. Its key holds the format, the oml
// version and each source's commit and fetch time: any change makes it stale, and the next search builds it again.
// A cache only: a missing, stale or damaged file is rebuilt, and a failed save is ignored.
static class LibraryIndex
{
    const int Format = 1;
    static string File => Path.Combine(Library.Root, "index.bin");

    static string Key(List<string> folders) =>
        $"{Format}|{typeof(LibraryIndex).Assembly.GetName().Version}|" +
        string.Join("|", folders.Select(f => Path.GetFileName(f) + "@" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(Path.Combine(f, "source.json"))))[..16]));

    // The index of the fetched sources: the saved one when its key matches, else a new one (saved for the next search).
    public static SearchIndex Get(List<string> folders, List<string> notes, Func<List<string>, List<string>, List<SearchDoc>> read)
    {
        var key = Key(folders);
        try { if (Load(key, notes) is { } saved) return saved; }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException or FormatException or ArgumentException or OutOfMemoryException) { }
        var index = new SearchIndex(read(folders, notes));
        try { Save(key, index, notes); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return index;
    }



    // A fetch replaced a source: the next search must not use the old index, even when source.json looks the same.
    public static void Delete() { if (System.IO.File.Exists(File)) System.IO.File.Delete(File); }

    static SearchIndex? Load(string key, List<string> notes)
    {
        if (!System.IO.File.Exists(File)) return null;
        using var r = new BinaryReader(new BufferedStream(System.IO.File.OpenRead(File), 1 << 20), Encoding.UTF8);
        if (r.ReadString() != key) return null;
        var noteCount = r.ReadInt32();
        for (var i = 0; i < noteCount; i++) notes.Add(r.ReadString());
        var count = r.ReadInt32();
        var docs = new List<SearchDoc>(count);
        var tf = new Dictionary<string, int>[count][];
        var length = new int[count][];
        for (var d = 0; d < count; d++)
        {
            var doc = new SearchDoc { Id = r.ReadString(), Kind = r.ReadString(), Title = r.ReadString(), Open = r.ReadString(), Keys = r.ReadString(), Body = r.ReadString() };
            var parent = r.ReadString();
            doc.Parent = parent.Length == 0 ? null : parent;
            doc.TitleIsName = r.ReadBoolean();
            doc.Names = new string[r.ReadInt32()];
            for (var n = 0; n < doc.Names.Length; n++) doc.Names[n] = r.ReadString();
            docs.Add(doc);
            tf[d] = new Dictionary<string, int>[3];
            length[d] = new int[3];
            for (var f = 0; f < 3; f++)
            {
                length[d][f] = r.ReadInt32();
                var terms = r.ReadInt32();
                var counts = new Dictionary<string, int>(terms, StringComparer.Ordinal);
                for (var t = 0; t < terms; t++) counts[r.ReadString()] = r.ReadInt32();
                tf[d][f] = counts;
            }
        }
        var words = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = r.ReadInt32(); i > 0; i--) words[r.ReadString()] = r.ReadString();
        var wordCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = r.ReadInt32(); i > 0; i--) wordCounts[r.ReadString()] = r.ReadInt32();
        return new SearchIndex(docs, tf, length, words, wordCounts);
    }

    // Written to a temp file first, then moved over the old one, so a reader never sees half a file.
    static void Save(string key, SearchIndex index, List<string> notes)
    {
        Directory.CreateDirectory(Library.Root);
        var temp = File + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var w = new BinaryWriter(new BufferedStream(System.IO.File.Create(temp), 1 << 20), Encoding.UTF8))
            {
                w.Write(key);
                w.Write(notes.Count);
                foreach (var n in notes) w.Write(n);
                w.Write(index.Docs.Count);
                for (var d = 0; d < index.Docs.Count; d++)
                {
                    var doc = index.Docs[d];
                    foreach (var s in new[] { doc.Id, doc.Kind, doc.Title, doc.Open, doc.Keys, doc.Body, doc.Parent ?? "" }) w.Write(s);
                    w.Write(doc.TitleIsName);
                    w.Write(doc.Names.Length);
                    foreach (var n in doc.Names) w.Write(n);
                    for (var f = 0; f < 3; f++)
                    {
                        w.Write(index.Lengths[d][f]);
                        w.Write(index.Tf[d][f].Count);
                        foreach (var (t, c) in index.Tf[d][f]) { w.Write(t); w.Write(c); }
                    }
                }
                w.Write(index.Words.Count);
                foreach (var (k, v) in index.Words) { w.Write(k); w.Write(v); }
                w.Write(index.WordCounts.Count);
                foreach (var (k, v) in index.WordCounts) { w.Write(k); w.Write(v); }
            }
            System.IO.File.Move(temp, File, overwrite: true);
        }
        finally { if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp); }
    }
}
