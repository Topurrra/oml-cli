using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace OmlCli;

// The module header, read without loading the model. A current-format module starts with the 3-byte "OML" magic,
// then an Int32 length, then a UTF-8 pipe-separated header string. Reading it is O(header size) and never touches
// the compressed fragments, so `oml meta`, folder scans and package checks can inspect hundreds of modules without
// paying for a full model load. Old XML modules are read through the root attributes; old ZIP modules are not decoded here.
static class ModuleHeader
{
    public const string NewFormatMagic = "OML";

    public enum Kind { NewFormat, OldXml, OldZip, NotAModule }

    public sealed record Info(
        Kind Format,
        string Name,
        string Description,
        string Version,
        string LastUpgradeVersion,
        string Key,
        string ContentHash,
        string GeneralHash,
        string Signature,
        string ElementsOrderVersion,
        string CompilationUnitsListVersion,
        bool? Valid,
        bool? NeedsRecover,
        string ESpaceType,
        string ProductId,
        string ProductName,
        string LastChangedBy,
        string LastModifiedUtc,
        bool? IsTemplatesBundle,
        string ModelFeatures,
        bool HasLuvUpgrade,
        string Note)
    {
        public string Summary => $"{(Format == Kind.NewFormat ? "OML11" : Format.ToString())} {Name}@{Version} key={Key}";
    }

    // A header that could not be read at all, carrying the reason.
    public sealed record ReadFailure(string Path, string Reason);

    public static bool IsModule(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 79 && bytes[1] == 77 && bytes[2] == 76;

    // Reads the header only. Returns null when the file is not a module we can parse (or is an old-ZIP module).
    public static Info? Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var first = new byte[5];
            var got = stream.Read(first, 0, first.Length);
            stream.Position = 0;
            if (got != first.Length) return null;
            if (first[0] == 79 && first[1] == 77 && first[2] == 76) return ReadNewFormat(stream);
            if (BytesStartWithXml(first)) return ReadOldXml(stream);
            return ReadOldZipNotDecoded();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
        {
            return null;
        }
    }

    static bool BytesStartWithXml(byte[] first) =>
        first.Length >= 5 && first[0] == (byte)'<' && first[1] == (byte)'?' && first[2] == (byte)'x'
        && first[3] == (byte)'m' && first[4] == (byte)'l';

    static Info ReadNewFormat(Stream stream)
    {
        stream.Position = 3; // the "OML" magic
        var lengthBuf = new byte[4];
        ReadExactly(stream, lengthBuf);
        var length = BitConverter.ToInt32(lengthBuf, 0);
        if (length < 0 || length > 512 * 1024) throw new InvalidDataException($"bad header length {length}");
        var buf = new byte[length];
        ReadExactly(stream, buf);
        var fields = Encoding.UTF8.GetString(buf).Split('|').Select(Unescape).ToArray();
        return FromFields(fields, "OML11");
    }

    static Info FromFields(string[] f, string note)
    {
        string At(int i) => i < f.Length ? f[i] : "";
        var version = At(1);
        var lastUpgrade = At(0);
        var elementsOrder = At(9);
        var compilationUnits = At(12);
        var signature = "";
        // Version 5.0.0.60+ stores SUVersion at 13, NeedsRecover at 14, Signature at 15; older modules are shorter.
        if (f.Length > 15 && VersionAtLeast(version, 5, 0, 0, 60)) signature = At(15);
        var valid = ParseBool(At(5));
        var needsRecover = f.Length > 14 && VersionAtLeast(version, 5, 0, 0, 60) ? ParseBool(At(14)) : null;
        var isTemplatesBundle = f.Length > 32 ? ParseBool(At(32)) : null;
        var eSpaceType = At(31);
        return new Info(
            Kind.NewFormat, At(6), At(7), version, lastUpgrade,
            At(3), At(4), At(25), signature, elementsOrder, compilationUnits,
            valid, needsRecover, eSpaceType, At(21), At(22), At(17), At(8), isTemplatesBundle, At(33),
            lastUpgrade.Length > 0 && version.Length > 0 && IsLuvAhead(lastUpgrade, version),
            note);
    }

    static bool IsLuvAhead(string lastUpgrade, string version)
    {
        var a = ParseVersion(lastUpgrade);
        var b = ParseVersion(version);
        return a is not null && b is not null && a < b;
    }

    static bool VersionAtLeast(string v, int major, int minor, int build, int rev)
    {
        var p = ParseVersion(v);
        return p is not null && p >= new Version(major, minor, build, rev);
    }

    static Version? ParseVersion(string v) => Version.TryParse(v, out var version) ? version : null;

    static Info ReadOldXml(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var doc = XDocument.Load(reader);
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "eSpace") throw new InvalidDataException("not an eSpace XML");
        string A(string name) => root.Attribute(name)?.Value ?? "";
        var version = A("Version");
        var lastUpgrade = A("LastUpgradeVersion");
        return new Info(Kind.OldXml, A("Name"), A("Description"), version, lastUpgrade,
            A("Key"), "", "", "", "", "",
            ParseBool(A("Valid")), null, "", "", "", A("LastChangedBy"), A("LastModifiedUTC"), null, "",
            lastUpgrade.Length > 0 && version.Length > 0 && IsLuvAhead(lastUpgrade, version),
            "older XML module");
    }

    static Info ReadOldZipNotDecoded() =>
        new(Kind.OldZip, "", "", "", "", "", "", "", "", "", "", null, null, "", "", "", "", "", null, "", false,
            "older ZIP module");

    static bool? ParseBool(string s) =>
        bool.TryParse(s, out var b) ? b
        : s.Equals("1", StringComparison.Ordinal) ? true
        : s.Equals("0", StringComparison.Ordinal) ? false
        : s.Equals("Yes", StringComparison.OrdinalIgnoreCase) ? true
        : s.Equals("No", StringComparison.OrdinalIgnoreCase) ? false
        : (bool?)null;

    static void ReadExactly(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer, total, buffer.Length - total);
            if (n <= 0) throw new EndOfStreamException();
            total += n;
        }
    }

    // Header fields escape '|' as "&sep;" and '&' as "&amp;" (the obfuscated OmlHeader constants). Unescape in the
    // reverse order of the writer. A field may carry "&elem;" for ',' but that is only decoded for the list fields.
    static string Unescape(string field)
    {
        if (field.Length == 0) return field;
        field = field.Replace("&sep;", "|");
        return field.Replace("&amp;", "&");
    }

    // A canonical form of an ObjectKey string for comparing keys that arrived in different formats (header F/S base64
    // vs manifest DB "D" Guid). Guid-shaped keys become lowercase hyphenated Guids; local (#...) and legacy keys stay.
    public static string NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";
        key = key.Trim();
        if (key.StartsWith('#')) return key;
        if (Guid.TryParse(key, out var guid)) return guid.ToString("D").ToLowerInvariant();
        var fromBase64 = GuidFromBase64(key);
        if (fromBase64 != Guid.Empty) return fromBase64.ToString("D").ToLowerInvariant();
        return key;
    }

    public static Guid GuidFromBase64(string str)
    {
        var s = str.Replace('_', '/');
        switch (24 - s.Length)
        {
            case 2: s += "=="; break;
            case 1: s += "="; break;
        }
        if (s.Length % 4 != 0) return Guid.Empty;
        try
        {
            var bytes = Convert.FromBase64String(s);
            return bytes.Length == 16 ? new Guid(bytes) : Guid.Empty;
        }
        catch (FormatException) { return Guid.Empty; }
    }
}
