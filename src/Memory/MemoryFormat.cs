using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OmlCli;

// One memory record, and the bytes of one line of memory.log. The layout is a C# port of voli's fixed-width plain-text
// record (crate stela, record.rs), with oml's own fields (module, elements, why) and without encryption:
//
//   seq(8) ts(20) kind(8) conf(3) sup(8) src(16) method(8) tags(48) from(20) until(20) module(40) elements(120) hash(16) text TAB why
//
// Single spaces between the fields. The line is padded with spaces and ends with "\n", to exactly RecordBytes bytes,
// so record n starts at byte n * RecordBytes and a torn write is a partial last line. Every width counts BYTES (UTF-8).
// The hash chains each record to the one before it: SHA-256 (first 8 bytes, hex) over the previous record's hash, the
// header bytes up to the hash, and the text region as stored. Any edit made outside oml breaks the chain.
sealed class MemRecord
{
    public int Seq;
    public string Ts = "", Kind = "", Src = "", Method = "", Module = "", Hash = "", Text = "", Why = "";
    public int Conf = 80;
    public int Sup = -1;                  // the record this one replaces or retracts; -1 = none
    public string[] Tags = [], Elements = [];
    public DateTime? From, Until;         // valid-time window [From, Until); null = open
    public string Id => Seq.ToString(CultureInfo.InvariantCulture);
}

static class MemoryFormat
{
    public const int TextMax = 300, WhyMax = 200;
    const int WSeq = 8, WTs = 20, WKind = 8, WConf = 3, WSup = 8, WSrc = 16, WMethod = 8, WTags = 48, WDate = 20, WModule = 40, WElements = 120, WHash = 16;
    public const int TagsMax = WTags, ModuleMax = WModule, ElementsMax = WElements, SrcMax = WSrc;

    const int OSeq = 0, OTs = OSeq + WSeq + 1, OKind = OTs + WTs + 1, OConf = OKind + WKind + 1, OSup = OConf + WConf + 1, OSrc = OSup + WSup + 1,
        OMethod = OSrc + WSrc + 1, OTags = OMethod + WMethod + 1, OFrom = OTags + WTags + 1, OUntil = OFrom + WDate + 1,
        OModule = OUntil + WDate + 1, OElements = OModule + WModule + 1, BodyLen = OElements + WElements, OHash = BodyLen + 1, OText = OHash + WHash + 1;
    public const int RecordBytes = OText + TextMax + 1 + WhyMax + 1;
    public const string NoHash = "0000000000000000";

    public static readonly string[] Kinds = ["fact", "decision", "pref", "trap", "log", "glossary", "answer"];
    public const string Retract = "retract";

    static readonly UTF8Encoding Strict = new(false, true);

    public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    public static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // Text of a record: one line, no control characters, single spaces. The text is data, so it must not break the line.
    public static string Sanitize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) sb.Append(char.IsControl(c) || c is '\u2028' or '\u2029' ? ' ' : c);
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static int Bytes(string s) => Strict.GetByteCount(s);

    // A header field: UTF-8 bytes cut at a character boundary and padded with spaces to the width.
    static void Put(byte[] buf, int offset, int width, string value)
    {
        var bytes = Strict.GetBytes(value);
        var n = Math.Min(bytes.Length, width);
        while (n > 0 && n < bytes.Length && (bytes[n] & 0xC0) == 0x80) n--; // do not cut inside a character
        Array.Fill(buf, (byte)' ', offset, width);
        Array.Copy(bytes, 0, buf, offset, n);
    }

    public static string ChainHash(string previous, ReadOnlySpan<byte> body, ReadOnlySpan<byte> textRegion)
    {
        var all = new byte[previous.Length + body.Length + textRegion.Length];
        Encoding.ASCII.GetBytes(previous, all);
        body.CopyTo(all.AsSpan(previous.Length));
        textRegion.CopyTo(all.AsSpan(previous.Length + body.Length));
        return Convert.ToHexString(SHA256.HashData(all).AsSpan(0, 8)).ToLowerInvariant();
    }

    // The bytes of one line. The caller has checked the sizes (MemoryStore.Validate); a value over a width is an error here.
    public static byte[] Pack(MemRecord r, string previousHash)
    {
        if (Bytes(r.Text) > TextMax || Bytes(r.Why) > WhyMax) throw new InvalidOperationException("text or why is too long");
        var buf = new byte[RecordBytes];
        Array.Fill(buf, (byte)' ');
        Put(buf, OSeq, WSeq, r.Seq.ToString("D8", CultureInfo.InvariantCulture));
        Put(buf, OTs, WTs, r.Ts);
        Put(buf, OKind, WKind, r.Kind);
        Put(buf, OConf, WConf, Math.Clamp(r.Conf, 0, 100).ToString("D3", CultureInfo.InvariantCulture));
        Put(buf, OSup, WSup, r.Sup < 0 ? "-" : r.Sup.ToString(CultureInfo.InvariantCulture));
        Put(buf, OSrc, WSrc, r.Src);
        Put(buf, OMethod, WMethod, r.Method);
        Put(buf, OTags, WTags, string.Join(',', r.Tags));
        Put(buf, OFrom, WDate, r.From is { } f ? Iso(f) : "-");
        Put(buf, OUntil, WDate, r.Until is { } u ? Iso(u) : "-");
        Put(buf, OModule, WModule, r.Module);
        Put(buf, OElements, WElements, string.Join(',', r.Elements));
        var region = Strict.GetBytes(r.Text + "\t" + r.Why);
        var hash = ChainHash(previousHash, buf.AsSpan(0, BodyLen), region);
        Put(buf, OHash, WHash, hash);
        Array.Copy(region, 0, buf, OText, region.Length);
        buf[RecordBytes - 1] = (byte)'\n';
        return buf;
    }

    // Parses the line at buf[start..start + RecordBytes]. Null (with the reason) for a line that is not a record.
    public static MemRecord? Unpack(byte[] buf, int start, out string error)
    {
        error = "";
        if (start + RecordBytes > buf.Length) { error = "incomplete line"; return null; }
        var line = buf.AsSpan(start, RecordBytes);
        if (line[^1] != (byte)'\n') { error = "the line does not end where a record ends (a record was cut or its length changed)"; return null; }
        foreach (var gap in new[] { OTs, OKind, OConf, OSup, OSrc, OMethod, OTags, OFrom, OUntil, OModule, OElements, OHash, OText })
            if (line[gap - 1] != (byte)' ') { error = "the fields are not where a record has them (a record was edited or its length changed)"; return null; }
        try
        {
            string F(int o, int w) => Strict.GetString(buf, start + o, w).TrimEnd(' ');
            if (!int.TryParse(F(OSeq, WSeq), NumberStyles.None, CultureInfo.InvariantCulture, out var seq)) { error = "the record number is not a number"; return null; }
            if (!int.TryParse(F(OConf, WConf), NumberStyles.None, CultureInfo.InvariantCulture, out var conf)) { error = "conf is not a number"; return null; }
            var supText = F(OSup, WSup);
            var sup = -1;
            if (supText != "-" && !int.TryParse(supText, NumberStyles.None, CultureInfo.InvariantCulture, out sup)) { error = "sup is not a record number"; return null; }
            DateTime? Date(string text) => text == "-" ? null : DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : throw new FormatException();
            var region = Strict.GetString(line.Slice(OText, TextMax + 1 + WhyMax)).TrimEnd(' ');
            var tab = region.IndexOf('\t');
            if (tab < 0) { error = "the text and why are not separated"; return null; }
            return new MemRecord
            {
                Seq = seq, Ts = F(OTs, WTs), Kind = F(OKind, WKind), Conf = conf, Sup = sup, Src = F(OSrc, WSrc), Method = F(OMethod, WMethod),
                Tags = Split(F(OTags, WTags)), From = Date(F(OFrom, WDate)), Until = Date(F(OUntil, WDate)), Module = F(OModule, WModule),
                Elements = Split(F(OElements, WElements)), Hash = F(OHash, WHash), Text = region[..tab], Why = region[(tab + 1)..],
            };
        }
        catch (Exception e) when (e is DecoderFallbackException or FormatException)
        {
            error = "a field holds bytes that are not valid text or a valid date";
            return null;
        }
    }

    static string[] Split(string s) => s.Length == 0 ? [] : s.Split(',', StringSplitOptions.RemoveEmptyEntries);

    // The stored hash field of the line at start, when it is 16 hex digits; else null. A line that does not parse still
    // tells the next record what to chain from.
    public static string? RawHash(byte[] buf, int start)
    {
        if (start + RecordBytes > buf.Length) return null;
        var field = Encoding.ASCII.GetString(buf, start + OHash, WHash);
        return field.All(Uri.IsHexDigit) ? field : null;
    }

    // The hash this line should carry, from the previous record's hash and the bytes as stored.
    public static string Recompute(string previous, byte[] buf, int start)
    {
        var line = buf.AsSpan(start, RecordBytes);
        var region = line.Slice(OText, TextMax + 1 + WhyMax);
        var end = region.Length;
        while (end > 0 && region[end - 1] == (byte)' ') end--;
        return ChainHash(previous, line[..BodyLen], region[..end]);
    }
}
