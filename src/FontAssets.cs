using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace OmlCli;

static class FontAssets
{
    // Indices are assigned by W3C WOFF2 section 4.1, including the significant spaces in tags.
    internal static readonly string[] KnownTags = ["cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ", "VORG", "EBDT", "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH", "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar", "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar", "gvar", "hsty", "just", "lcar", "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill"];
    internal sealed record Table(string Tag, byte[] Data);
    internal sealed record Font(uint Flavor, List<Table> Tables);
    internal static uint U32(byte[] b, int p) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p, 4));
    internal static ushort U16(byte[] b, int p) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p, 2));
    internal static void Put32(byte[] b, int p, uint n) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p, 4), n);
    internal static void Put16(byte[] b, int p, int n) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p, 2), checked((ushort)n));
    internal static int Align(int n) => checked((n + 3) & ~3);

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is not ("woff2" or "batch"))
                throw new ArgumentException("oml font woff2 <input> [output.woff2] [--force] | font batch <folder> [--out <folder>] [--force]");
            var batch = args[0] == "batch";
            var opt = AssetFiles.Parse(args[1..], batch ? ["--out"] : [], "--force");
            if (opt.Paths.Count < 1 || opt.Paths.Count > (batch ? 1 : 2)) throw new ArgumentException("Give an input and optional output.");
            if (!batch)
            {
                var input = Path.GetFullPath(opt.Paths[0]);
                var output = Path.GetFullPath(opt.Paths.Count == 2 ? opt.Paths[1] : Path.ChangeExtension(input, ".woff2"));
                Console.WriteLine(JsonOutput.Serialize(Convert(input, output, opt.Has("--force"), [input])));
                return 0;
            }
            var files = Directory.GetFiles(opt.Paths[0]).Where(Supported).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var results = new List<object>(); var failed = 0;
            var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var input in files)
            {
                try
                {
                    var output = Path.GetFullPath(Path.Combine(opt.Get("--out") ?? opt.Paths[0], Path.GetFileNameWithoutExtension(input) + ".woff2"));
                    if (!outputs.Add(output)) throw new IOException($"Multiple inputs map to {output}; use separate batches.");
                    results.Add(Convert(input, output, opt.Has("--force"), files));
                }
                catch (Exception e) { failed++; results.Add(new { input, error = e.Message }); }
            }
            Console.WriteLine(JsonOutput.Serialize(new { results, failed })); return failed == 0 ? 0 : 1;
        }
        catch (Exception e) { Console.WriteLine(JsonOutput.Serialize(new { error = e.Message })); return 2; }
    }

    static bool Supported(string path) => new[] { ".ttf", ".otf", ".woff", ".woff2", ".ttc" }.Contains(Path.GetExtension(path).ToLowerInvariant());
    static object Convert(string input, string output, bool force, string[] inputs)
    {
        if (!Supported(input)) throw new ArgumentException("Supported fonts: .ttf, .otf, .woff, .woff2. Font collections (.ttc) are not supported.");
        var bytes = File.ReadAllBytes(input);
        if (bytes.Length >= 48 && U32(bytes, 0) == 0x774f4632)
            return new { input, status = "already woff2", inputBytes = bytes.Length };
        if (!Path.GetExtension(output).Equals(".woff2", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must have extension .woff2.");
        AssetFiles.CheckOutput(output, inputs, force);
        var font = Prepare(Read(bytes));
        var encoded = Encode(font);
        AssetFiles.Write(output, encoded, force);
        return new { input, output, inputBytes = bytes.Length, outputBytes = encoded.Length,
            ratio = AssetFiles.Ratio(encoded.Length, bytes.Length), normalization = "WOFF2 requires removing DSIG, setting head.flags bit 11, and recalculating checkSumAdjustment; all other table bytes are preserved",
            tables = font.Tables.Select(t => new { tag = t.Tag, bytes = t.Data.Length }) };
    }

    internal static Font Read(byte[] bytes)
    {
        if (bytes.Length < 12) throw new InvalidDataException("Truncated font header.");
        var signature = U32(bytes, 0);
        if (signature == 0x74746366) throw new InvalidDataException("Font collections (.ttc) are not supported; supply a single TTF or OTF.");
        var woff = signature == 0x774f4646;
        var flavor = woff ? U32(bytes, 4) : signature;
        if (flavor is not (0x00010000 or 0x4f54544f or 0x74727565)) throw new InvalidDataException("Unsupported sfnt flavor; expected TrueType or OpenType CFF.");
        var n = U16(bytes, woff ? 12 : 4);
        if (n == 0 || n > 4095) throw new InvalidDataException("Invalid table count.");
        var start = woff ? 44 : 12; var stride = woff ? 20 : 16;
        if (bytes.Length < start + stride * n || (woff && (U32(bytes, 8) != bytes.Length || U16(bytes, 14) != 0)))
            throw new InvalidDataException("Invalid font directory/header.");
        var tables = new List<(int Offset, Table Table)>();
        var tags = new HashSet<string>(); var ranges = new List<(int Start, int End)>();
        for (var i = 0; i < n; i++)
        {
            var p = start + i * stride; var tag = Encoding.ASCII.GetString(bytes, p, 4);
            if (!tags.Add(tag) || bytes.AsSpan(p, 4).ToArray().Any(b => b < 32 || b > 126)) throw new InvalidDataException("Duplicate or invalid font table tag.");
            var offset = checked((int)U32(bytes, p + (woff ? 4 : 8)));
            var length = checked((int)U32(bytes, p + 12));
            var stored = woff ? checked((int)U32(bytes, p + 8)) : length;
            if (offset < start + n * stride || stored > length || offset > bytes.Length - stored || offset % 4 != 0)
                throw new InvalidDataException($"Invalid bounds/alignment for {tag}.");
            if (ranges.Any(r => stored > 0 && offset < r.End && offset + stored > r.Start)) throw new InvalidDataException("Overlapping font tables.");
            ranges.Add((offset, checked(offset + stored)));
            byte[] data;
            if (stored < length)
            {
                using var z = new ZLibStream(new MemoryStream(bytes, offset, stored), CompressionMode.Decompress);
                data = new byte[length]; z.ReadExactly(data);
                if (z.ReadByte() != -1) throw new InvalidDataException($"Incorrect decompressed length for {tag}.");
            }
            else data = bytes.AsSpan(offset, length).ToArray();
            tables.Add((offset, new Table(tag, data)));
        }
        if (!tags.Contains("head") || tables.Single(t => t.Table.Tag == "head").Table.Data.Length < 54)
            throw new InvalidDataException("Missing or truncated head table.");
        if (tags.Contains("glyf") != tags.Contains("loca")) throw new InvalidDataException("glyf and loca must both be present.");
        return new Font(flavor, tables.OrderBy(t => t.Offset).Select(t => t.Table).ToList());
    }

    static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (var j = 0; j < 4; j++) word = (word << 8) | (i + j < data.Length ? data[i + j] : 0u);
            sum = unchecked(sum + word);
        }
        return sum;
    }

    static Font Prepare(Font input)
    {
        var tables = input.Tables.Where(t => t.Tag != "DSIG").Select(t => t.Tag == "head" ? t with { Data = t.Data.ToArray() } : t).ToList();
        var head = tables.Single(t => t.Tag == "head").Data;
        Put16(head, 16, U16(head, 16) | 0x800); Put32(head, 8, 0);
        var n = tables.Count; var directory = new byte[12 + n * 16];
        Put32(directory, 0, input.Flavor); Put16(directory, 4, n);
        var power = 1; var log = 0; while (power * 2 <= n) { power *= 2; log++; }
        Put16(directory, 6, power * 16); Put16(directory, 8, log); Put16(directory, 10, n * 16 - power * 16);
        var offsets = new Dictionary<string, int>(); var offset = directory.Length; uint dataSum = 0;
        foreach (var t in tables) { offsets.Add(t.Tag, offset); offset = checked(offset + Align(t.Data.Length)); dataSum = unchecked(dataSum + Checksum(t.Data)); }
        var sorted = tables.OrderBy(t => t.Tag, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < n; i++)
        {
            var t = sorted[i]; var p = 12 + i * 16;
            Encoding.ASCII.GetBytes(t.Tag).CopyTo(directory, p); Put32(directory, p + 4, Checksum(t.Data));
            Put32(directory, p + 8, (uint)offsets[t.Tag]); Put32(directory, p + 12, (uint)t.Data.Length);
        }
        Put32(head, 8, unchecked(0xb1b0afba - Checksum(directory) - dataSum));
        return new Font(input.Flavor, tables);
    }

    internal static byte[] Encode(Font font)
    {
        using var directory = new MemoryStream(); using var raw = new MemoryStream();
        var sfntSize = checked(12 + 16 * font.Tables.Count);
        foreach (var t in font.Tables)
        {
            var index = Array.IndexOf(KnownTags, t.Tag);
            directory.WriteByte((byte)((index < 0 ? 63 : index) | (t.Tag is "glyf" or "loca" ? 0xc0 : 0)));
            if (index < 0) directory.Write(Encoding.ASCII.GetBytes(t.Tag));
            Base128(directory, (uint)t.Data.Length);
            // WOFF2's Brotli stream has no inter-table padding. Only reconstructed sfnt tables are aligned.
            raw.Write(t.Data); sfntSize = checked(sfntSize + Align(t.Data.Length));
        }
        var source = raw.ToArray(); var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(source.Length)];
        if (!BrotliEncoder.TryCompress(source, compressed, out var written, quality: 11, window: 22)) throw new IOException("Brotli compression failed.");
        // Chromium's WOFF2 decoder requires the final compressed block to end on a four-byte boundary.
        var output = new byte[Align(checked(48 + (int)directory.Length + written))];
        Put32(output, 0, 0x774f4632); Put32(output, 4, font.Flavor); Put32(output, 8, (uint)output.Length);
        Put16(output, 12, font.Tables.Count); Put32(output, 16, (uint)sfntSize); Put32(output, 20, (uint)written);
        var head = font.Tables.Single(t => t.Tag == "head").Data;
        Put16(output, 24, U16(head, 4)); Put16(output, 26, U16(head, 6));
        directory.ToArray().CopyTo(output, 48); compressed.AsSpan(0, written).CopyTo(output.AsSpan(48 + (int)directory.Length));
        // A decoder also recalculates these checksums if it chooses a different physical sfnt layout.
        return output;
    }

    static void Base128(Stream stream, uint value)
    {
        Span<byte> b = stackalloc byte[5]; var p = 4; b[p] = (byte)(value & 127);
        while ((value >>= 7) != 0) b[--p] = (byte)((value & 127) | 128);
        stream.Write(b[p..]);
    }
}
