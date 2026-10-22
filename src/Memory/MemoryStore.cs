using System.Globalization;
using System.Text;

namespace OmlCli;

// A command error with its exit code and an optional next command for the reader.
sealed class MemoryException(string message, int exitCode = 2, string? hint = null) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
    public string? Hint { get; } = hint;
}

// What a pass over memory.log found. Live = records not replaced or retracted (their windows are checked at use).
sealed class MemState
{
    public List<MemRecord> Candidates = [];                       // not superseded, not a retraction, hash intact
    public Dictionary<int, (int By, string ByKind)> Replaced = []; // record number -> the record that replaced or retracted it
    public int Total, Peak, Bad, FirstBad = -1;                          // records on disk, records that failed their check, the first of them
    public string IndexStatus = "off";                             // hit | rebuilt | off

    public List<MemRecord> LiveNow() { var now = DateTime.UtcNow; return Candidates.Where(r => (r.From is null || r.From <= now) && (r.Until is null || now < r.Until)).ToList(); }
}

// <project folder>\.oml\memory\memory.log: one append-only file per project, plus .lock (the writers' lock) and
// memory.idx (a cache that is rebuilt whenever it does not match the log). Only `oml memory init` creates the folder.
sealed class MemStore
{
    public readonly string Folder, Dir, LogPath, LockPath, IndexPath;

    MemStore(string folder)
    {
        Folder = folder;
        Dir = Path.Combine(folder, ".oml", "memory");
        (LogPath, LockPath, IndexPath) = (Path.Combine(Dir, "memory.log"), Path.Combine(Dir, ".lock"), Path.Combine(Dir, "memory.idx"));
    }

    public static string LogPathOf(string folder) => Path.Combine(folder, ".oml", "memory", "memory.log");

    // The store of a folder, or null when `oml memory init` never ran there.
    public static MemStore? Open(string folder) => File.Exists(LogPathOf(folder)) ? new MemStore(folder) : null;

    public static MemStore Create(string folder)
    {
        var store = new MemStore(folder);
        Directory.CreateDirectory(store.Dir);
        using (new FileStream(store.LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite)) { }
        return store;
    }

    public long Length => new FileInfo(LogPath).Length;
    public int Count => (int)(Length / MemoryFormat.RecordBytes);

    byte[] ReadBytes()
    {
        using var f = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[f.Length];
        f.ReadExactly(buf);
        return buf;
    }

    // The record with this number, read by position (no chain check), or null.
    public MemRecord? ReadAt(int seq)
    {
        if (seq < 0 || (seq + 1L) * MemoryFormat.RecordBytes > Length) return null;
        using var f = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        f.Seek((long)seq * MemoryFormat.RecordBytes, SeekOrigin.Begin);
        var buf = new byte[MemoryFormat.RecordBytes];
        f.ReadExactly(buf);
        var r = MemoryFormat.Unpack(buf, 0, out _);
        return r is not null && r.Seq == seq ? r : null;
    }

    // True when the record's hash matches the chain: the stored hash of the record before it, and its own bytes.
    public bool IsIntact(int seq)
    {
        if (seq < 0 || (seq + 1L) * MemoryFormat.RecordBytes > Length) return false;
        using var f = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var first = Math.Max(0, seq - 1);
        f.Seek((long)first * MemoryFormat.RecordBytes, SeekOrigin.Begin);
        var buf = new byte[(seq - first + 1) * MemoryFormat.RecordBytes];
        f.ReadExactly(buf);
        var start = (seq - first) * MemoryFormat.RecordBytes;
        var previous = seq == 0 ? MemoryFormat.NoHash : MemoryFormat.RawHash(buf, 0);
        var r = MemoryFormat.Unpack(buf, start, out _);
        return previous is not null && r is not null && r.Seq == seq && MemoryFormat.Recompute(previous, buf, start) == r.Hash;
    }

    // ---- the whole log -------------------------------------------------------------------------------------------

    public sealed record Problem(int Id, string What);

    // Every record, checked: the line parses, the number matches its place, `sup` points back, and the hash matches the
    // chain. A record that fails any check is listed in `problems` and is not in `records`.
    public (List<MemRecord> Records, List<Problem> Problems, int Total, long TailBytes) Scan() => ScanBytes(ReadBytes());

    public static (List<MemRecord> Records, List<Problem> Problems, int Total, long TailBytes) ScanBytes(byte[] buf)
    {
        var total = buf.Length / MemoryFormat.RecordBytes;
        var records = new List<MemRecord>(total);
        var problems = new List<Problem>();
        var previous = MemoryFormat.NoHash;
        for (var i = 0; i < total; i++)
        {
            var start = i * MemoryFormat.RecordBytes;
            var r = MemoryFormat.Unpack(buf, start, out var error);
            if (r is null)
            {
                problems.Add(new Problem(i, "unreadable: " + error));
                previous = MemoryFormat.RawHash(buf, start) ?? previous; // the next record chains from what this line says, so one bad line is one problem
                continue;
            }
            var want = MemoryFormat.Recompute(previous, buf, start);
            previous = r.Hash;
            if (r.Seq != i) problems.Add(new Problem(i, $"the record number is {r.Seq}, but this is place {i} (a record was removed, moved or renumbered)"));
            else if (want != r.Hash) problems.Add(new Problem(i, "the hash does not match (this record, or the one before it, was changed outside oml)"));
            else if (r.Sup >= i) problems.Add(new Problem(i, "it replaces a record that does not come before it"));
            else records.Add(r);
        }
        return (records, problems, total, buf.Length % MemoryFormat.RecordBytes);
    }

    static MemState Resolve(List<MemRecord> records, int total, int bad, int firstBad)
    {
        var state = new MemState { Total = total, Bad = bad, FirstBad = firstBad };
        foreach (var r in records)
            if (r.Sup >= 0) state.Replaced[r.Sup] = (r.Seq, r.Kind);
        state.Candidates = records.Where(r => r.Kind != MemoryFormat.Retract && !state.Replaced.ContainsKey(r.Seq)).ToList();
        return state;
    }

    // ---- state, with the index cache -------------------------------------------------------------------------------

    // The live set. useIndex: read memory.idx when it matches the log, and write it after a rebuild. A missing, old or
    // unreadable index is rebuilt from the log without a word: the log is the only truth.
    public MemState Load(bool useIndex)
    {
        if (useIndex && MemoryIndex.TryRead(this) is { } cached) { cached.IndexStatus = "hit"; return cached; }
        var info = new FileInfo(LogPath);
        var ticks = info.LastWriteTimeUtc.Ticks;
        var remembered = useIndex ? MemoryIndex.Remembered(this) : null;
        var buf = ReadBytes();
        var (records, problems, total, _) = ScanBytes(buf);
        var state = Resolve(records, total, problems.Count, problems.Count > 0 ? problems[0].Id : -1);
        state.Peak = Math.Max(total, remembered?.Peak ?? 0); // the most records this log ever had, as far as an index remembers
        state.IndexStatus = "off";
        if (useIndex)
        {
            state.IndexStatus = "rebuilt";
            MemoryIndex.TryWrite(this, state, buf.Length, ticks);
        }
        return state;
    }

    // ---- writing -------------------------------------------------------------------------------------------------

    // Runs `body` while holding the writers' lock (an exclusive open of .lock, released by the system if the process dies).
    public T WithLock<T>(Func<T> body)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        FileStream? held = null;
        while (held is null)
        {
            try { held = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) when (e is not DirectoryNotFoundException && DateTime.UtcNow < until) { Thread.Sleep(15); } // a lock held by another process
            catch (IOException e) when (e is not DirectoryNotFoundException) { throw new MemoryException("Another oml memory command holds the lock and did not finish in 30 seconds.", 1, "Run the command again."); }
        }
        using (held) return body();
    }

    // Removes a partial last line (a write that was cut short) so the next record starts at a record boundary. Holds the lock.
    // The removed bytes are never lost silently: they are copied to memory.log.torn-<utc time> first, and the caller reports it.
    // It refuses when the last whole record is not a record at all: then the file was edited and the boundaries are not
    // known, and cutting would destroy data.
    public (long Bytes, string SavedTo)? RepairTail()
    {
        var length = Length;
        var extra = length % MemoryFormat.RecordBytes;
        if (extra == 0) return null;
        var whole = length - extra;
        if (whole > 0 && ReadAt((int)(whole / MemoryFormat.RecordBytes) - 1) is null)
            throw new MemoryException($"memory.log ends with {extra} stray bytes, and the last whole record is not readable, so the file was edited outside oml.", 1,
                "Run 'oml memory doctor'. Restore memory.log from a copy, or move it away and run 'oml memory init' again.");
        var tail = new byte[extra];
        using var f = new FileStream(LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        f.Seek(whole, SeekOrigin.Begin);
        f.ReadExactly(tail);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var saved = LogPath + ".torn-" + stamp;
        for (var n = 2; File.Exists(saved); n++) saved = LogPath + ".torn-" + stamp + "-" + n;
        File.WriteAllBytes(saved, tail); // if this fails nothing is cut
        f.SetLength(whole);
        f.Flush(true);
        return (extra, saved);
    }

    public string[] TornFiles() => Directory.Exists(Dir) ? Directory.GetFiles(Dir, "memory.log.torn-*").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()! : [];

    // The raw hash field of the last record (the chain continues from it), or the start value for an empty log.
    public string LastHash()
    {
        var n = Count;
        if (n == 0) return MemoryFormat.NoHash;
        return ReadAt(n - 1)?.Hash ?? throw new MemoryException($"The last record (#{n - 1}) is not readable, so the chain cannot continue.", 1, "Run 'oml memory doctor'.");
    }

    // Appends one record to the end and flushes it to disk. Holds the lock; the caller has set Seq and Ts.
    public void Append(MemRecord r)
    {
        var bytes = MemoryFormat.Pack(r, LastHash());
        using var f = new FileStream(LogPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        f.Seek(0, SeekOrigin.End);
        f.Write(bytes);
        f.Flush(true);
    }

    public static string Date(string ts) => ts.Length >= 10 ? ts[..10] : ts;
}

// memory.idx: the live set of the log in a compact binary form, so a recall does not read, check and resolve the whole
// log each time. It is valid only for the exact log it was made from (length, write time and last hash).
static class MemoryIndex
{
    const string Magic = "OMLMEMIDX2";

    static (long Length, long Ticks, string LastHash) KeyOf(MemStore store)
    {
        var info = new FileInfo(store.LogPath);
        var n = (int)(info.Length / MemoryFormat.RecordBytes);
        return (info.Length, info.LastWriteTimeUtc.Ticks, n == 0 ? "" : store.ReadAt(n - 1)?.Hash ?? "?");
    }

    public static MemState? TryRead(MemStore store)
    {
        try
        {
            if (!File.Exists(store.IndexPath)) return null;
            using var f = new FileStream(store.IndexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new BinaryReader(f, Encoding.UTF8);
            if (r.ReadString() != Magic) return null;
            var (length, ticks, last) = (r.ReadInt64(), r.ReadInt64(), r.ReadString());
            if (KeyOf(store) != (length, ticks, last)) return null;
            var state = new MemState { Total = r.ReadInt32(), Peak = r.ReadInt32(), Bad = r.ReadInt32(), FirstBad = r.ReadInt32() };
            var replaced = r.ReadInt32();
            for (var i = 0; i < replaced; i++) state.Replaced[r.ReadInt32()] = (r.ReadInt32(), r.ReadString());
            var count = r.ReadInt32();
            state.Candidates = new List<MemRecord>(count);
            for (var i = 0; i < count; i++) state.Candidates.Add(ReadRecord(r));
            return r.ReadString() == Magic ? state : null; // the closing mark proves the file was written to its end
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException or FormatException or ArgumentException or UnauthorizedAccessException)
        {
            return null; // unreadable or cut short: rebuilt by the caller
        }
    }

    // What an index remembers about the log it was made from (records, most records ever seen), even when the log has
    // changed since. Null when there is no readable index. `doctor` compares it with the log to see removed records.
    public static (int Total, int Peak)? Remembered(MemStore store)
    {
        try
        {
            if (!File.Exists(store.IndexPath)) return null;
            using var f = new FileStream(store.IndexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new BinaryReader(f, Encoding.UTF8);
            if (r.ReadString() != Magic) return null;
            r.ReadInt64(); r.ReadInt64(); r.ReadString();
            return (r.ReadInt32(), r.ReadInt32());
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException or FormatException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Best effort: the index is only a cache, so a folder that cannot be written (read-only, locked) is not an error.
    public static void TryWrite(MemStore store, MemState state, long length, long ticks)
    {
        var temp = store.IndexPath + "." + Environment.ProcessId + ".tmp";
        try
        {
            var last = state.Total == 0 ? "" : store.ReadAt(state.Total - 1)?.Hash ?? "?";
            using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new BinaryWriter(f, Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(length); w.Write(ticks); w.Write(last);
                w.Write(state.Total); w.Write(Math.Max(state.Peak, state.Total)); w.Write(state.Bad); w.Write(state.FirstBad);
                w.Write(state.Replaced.Count);
                foreach (var (target, (by, kind)) in state.Replaced) { w.Write(target); w.Write(by); w.Write(kind); }
                w.Write(state.Candidates.Count);
                foreach (var rec in state.Candidates) WriteRecord(w, rec);
                w.Write(Magic);
            }
            File.Move(temp, store.IndexPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { /* a stray .tmp file is harmless */ }
        }
    }

    static void WriteRecord(BinaryWriter w, MemRecord r)
    {
        w.Write(r.Seq); w.Write(r.Ts); w.Write(r.Kind); w.Write((byte)r.Conf); w.Write(r.Sup); w.Write(r.Src); w.Write(r.Method);
        w.Write(string.Join(',', r.Tags)); w.Write(r.From?.Ticks ?? 0L); w.Write(r.Until?.Ticks ?? 0L);
        w.Write(r.Module); w.Write(string.Join(',', r.Elements)); w.Write(r.Hash); w.Write(r.Text); w.Write(r.Why);
    }

    static MemRecord ReadRecord(BinaryReader r)
    {
        var rec = new MemRecord { Seq = r.ReadInt32(), Ts = r.ReadString(), Kind = r.ReadString(), Conf = r.ReadByte(), Sup = r.ReadInt32(), Src = r.ReadString(), Method = r.ReadString() };
        rec.Tags = Split(r.ReadString());
        var (from, until) = (r.ReadInt64(), r.ReadInt64());
        rec.From = from == 0 ? null : new DateTime(from, DateTimeKind.Utc);
        rec.Until = until == 0 ? null : new DateTime(until, DateTimeKind.Utc);
        rec.Module = r.ReadString();
        rec.Elements = Split(r.ReadString());
        (rec.Hash, rec.Text, rec.Why) = (r.ReadString(), r.ReadString(), r.ReadString());
        return rec;
    }

    static string[] Split(string s) => s.Length == 0 ? [] : s.Split(',');
}
