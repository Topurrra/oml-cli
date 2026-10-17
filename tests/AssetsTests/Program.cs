using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using SkiaSharp;
using Svg.Skia;

public static class Program
{
    public static int Main(string[] args)
    {
        var ss = Environment.GetEnvironmentVariable("OML_SERVICE_STUDIO") ?? @"C:\Program Files\OutSystems\Service Studio 11\Service Studio";
        var context = new GraphicsContext(ss);
        var assembly = context.LoadFromAssemblyPath(typeof(Program).Assembly.Location);
        try { assembly.GetType("AssetChecks")!.GetMethod("Run")!.Invoke(null, [args]); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e.InnerException ?? e); return 1; }
    }
}

sealed class GraphicsContext(string ss) : AssemblyLoadContext
{
    protected override Assembly? Load(AssemblyName name)
    {
        var path = Path.Combine(ss, name.Name + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }
    protected override IntPtr LoadUnmanagedDll(string name)
    {
        var path = Path.Combine(ss, name.EndsWith(".dll") ? name : name + ".dll");
        if (!File.Exists(path)) path = Path.Combine(ss, "runtimes", "win-x64", "native", name.EndsWith(".dll") ? name : name + ".dll");
        return File.Exists(path) ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}

public static class AssetChecks
{
    static uint U32(byte[] b, int p) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p));
    static int U16(byte[] b, int p) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p));
    static void P32(byte[] b, int p, uint n) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p), n);
    static void P16(byte[] b, int p, int n) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p), (ushort)n);
    static int Align(int n) => (n + 3) & ~3;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static uint Sum(byte[] b)
    {
        uint result = 0;
        for (var i = 0; i < b.Length; i += 4)
        {
            uint word = 0; for (var j = 0; j < 4; j++) word = (word << 8) | (i + j < b.Length ? b[i + j] : 0u);
            result = unchecked(result + word);
        }
        return result;
    }

    // Independent test decoder for this encoder's null-transform subset of WOFF2.
    static byte[] Decode(byte[] woff, out Dictionary<string, byte[]> tables)
    {
        string[] known = "cmap|head|hhea|hmtx|maxp|name|OS/2|post|cvt |fpgm|glyf|loca|prep|CFF |VORG|EBDT|EBLC|gasp|hdmx|kern|LTSH|PCLT|VDMX|vhea|vmtx|BASE|GDEF|GPOS|GSUB|EBSC|JSTF|MATH|CBDT|CBLC|COLR|CPAL|SVG |sbix|acnt|avar|bdat|bloc|bsln|cvar|fdsc|feat|fmtx|fvar|gvar|hsty|just|lcar|mort|morx|opbd|prop|trak|Zapf|Silf|Glat|Gloc|Feat|Sill".Split('|');
        Check(U32(woff, 0) == 0x774f4632 && U32(woff, 8) == woff.Length && U16(woff, 14) == 0, "WOFF2 header");
        var n = U16(woff, 12); var p = 48;
        var directory = new List<(string Tag, int Length)>();
        for (var i = 0; i < n; i++)
        {
            var flag = woff[p++]; var index = flag & 63;
            var tag = index == 63 ? Encoding.ASCII.GetString(woff, p, 4) : known[index];
            if (index == 63) p += 4;
            Check((flag >> 6) == (tag is "glyf" or "loca" ? 3 : 0), "Unexpected transform");
            uint length = 0;
            for (var j = 0; ; j++)
            {
                var b = woff[p++]; Check(j < 5 && !(j == 0 && b == 128) && (length & 0xfe000000) == 0, "Invalid UIntBase128");
                length = (length << 7) | (uint)(b & 127); if ((b & 128) == 0) break;
            }
            directory.Add((tag, checked((int)length)));
        }
        var end = checked(p + (int)U32(woff, 20));
        Check(Align(end) == woff.Length && woff.AsSpan(end).ToArray().All(b => b == 0), "WOFF2 compressed length/padding");
        using var stream = new BrotliStream(new MemoryStream(woff, p, (int)U32(woff, 20)), CompressionMode.Decompress);
        using var decoded = new MemoryStream(); stream.CopyTo(decoded); var raw = decoded.ToArray();
        Check(raw.Length == directory.Sum(t => t.Length), "Unexpected stream padding");
        tables = new(); var cursor = 0;
        foreach (var t in directory) { tables.Add(t.Tag, raw.AsSpan(cursor, t.Length).ToArray()); cursor += t.Length; }
        // Rebuild aligned sfnt, sorted directory, original stream order. Checksums use a zero adjustment.
        var result = new byte[12 + 16 * n + directory.Sum(t => Align(t.Length))];
        Check(result.Length == U32(woff, 16), "totalSfntSize");
        P32(result, 0, U32(woff, 4)); P16(result, 4, n);
        var power = 1; var log = 0; while (power * 2 <= n) { power *= 2; log++; }
        P16(result, 6, power * 16); P16(result, 8, log); P16(result, 10, n * 16 - power * 16);
        var offset = 12 + 16 * n; var offsets = new Dictionary<string, int>();
        foreach (var t in directory) { tables[t.Tag].CopyTo(result, offset); offsets[t.Tag] = offset; offset += Align(t.Length); }
        P32(result, offsets["head"] + 8, 0);
        var sorted = directory.OrderBy(t => t.Tag, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < n; i++)
        {
            var t = sorted[i]; var entry = 12 + i * 16;
            Encoding.ASCII.GetBytes(t.Tag).CopyTo(result, entry);
            P32(result, entry + 4, Sum(result.AsSpan(offsets[t.Tag], t.Length).ToArray()));
            P32(result, entry + 8, (uint)offsets[t.Tag]); P32(result, entry + 12, (uint)t.Length);
        }
        P32(result, offsets["head"] + 8, unchecked(0xb1b0afba - Sum(result)));
        Check(Sum(result) == 0xb1b0afba, "sfnt checksum adjustment");
        return result;
    }

    static Dictionary<string, byte[]> ReadTables(byte[] sfnt)
    {
        var tables = new Dictionary<string, byte[]>();
        for (var i = 0; i < U16(sfnt, 4); i++)
        {
            var p = 12 + i * 16; var tag = Encoding.ASCII.GetString(sfnt, p, 4);
            tables.Add(tag, sfnt.AsSpan((int)U32(sfnt, p + 8), (int)U32(sfnt, p + 12)).ToArray());
        }
        return tables;
    }

    static void FontCheck(string source, string output)
    {
        var input = File.ReadAllBytes(source);
        var expected = U32(input, 0) == 0x774f4646 ? ReadWoff(input) : ReadTables(input);
        expected.Remove("DSIG");
        var head = expected["head"]; P16(head, 16, U16(head, 16) | 0x800);
        var rebuilt = Decode(File.ReadAllBytes(output), out var actual);
        Check(actual.Count == expected.Count, "Table count changed");
        // The two W3C-mandated head fields are checked separately; all other bytes must match.
        Check((U16(actual["head"], 16) & 0x800) != 0, "WOFF2 modifying-transform flag");
        Check(U32(actual["head"], 8) == U32(ReadTables(rebuilt)["head"], 8), "Encoder head checksum adjustment");
        P32(head, 8, U32(actual["head"], 8));
        foreach (var t in expected) Check(actual[t.Key].SequenceEqual(t.Value), "Table bytes changed: " + t.Key);
        var rebuiltTables = ReadTables(rebuilt);
        foreach (var t in expected)
        {
            var original = t.Value.ToArray(); var restored = rebuiltTables[t.Key];
            // sfnt offsets may be canonicalized: only checkSumAdjustment is allowed to change.
            if (t.Key == "head") { P32(original, 8, 0); P32(restored, 8, 0); }
            Check(original.SequenceEqual(restored), "Rebuilt table changed: " + t.Key);
        }
        File.WriteAllBytes(output + ".sfnt", rebuilt);
    }

    static Dictionary<string, byte[]> ReadWoff(byte[] woff)
    {
        var tables = new Dictionary<string, byte[]>();
        for (var i = 0; i < U16(woff, 12); i++)
        {
            var p = 44 + i * 20; var len = (int)U32(woff, p + 12);
            var stored = (int)U32(woff, p + 8); var offset = (int)U32(woff, p + 4);
            byte[] data;
            if (stored == len) data = woff.AsSpan(offset, len).ToArray();
            else { using var z = new ZLibStream(new MemoryStream(woff, offset, stored), CompressionMode.Decompress); data = new byte[len]; z.ReadExactly(data); }
            tables.Add(Encoding.ASCII.GetString(woff, p, 4), data);
        }
        return tables;
    }

    static void MakeWoff(string source, string output)
    {
        var input = File.ReadAllBytes(source); var tables = ReadTables(input); var n = tables.Count;
        using var result = new MemoryStream(); result.Write(new byte[44 + 20 * n]); var entries = new List<(string Tag, int Offset, byte[] Raw, byte[] Stored)>();
        foreach (var t in tables)
        {
            using var packed = new MemoryStream(); using (var z = new ZLibStream(packed, CompressionLevel.SmallestSize, true)) z.Write(t.Value);
            var compressed = packed.ToArray(); var stored = compressed.Length < t.Value.Length ? compressed : t.Value;
            entries.Add((t.Key, (int)result.Position, t.Value, stored)); result.Write(stored);
            while (result.Position % 4 != 0) result.WriteByte(0);
        }
        var bytes = result.ToArray(); P32(bytes, 0, 0x774f4646); P32(bytes, 4, U32(input, 0)); P32(bytes, 8, (uint)bytes.Length);
        P16(bytes, 12, n); P32(bytes, 16, (uint)(12 + 16 * n + tables.Sum(t => Align(t.Value.Length))));
        for (var i = 0; i < n; i++)
        {
            var t = entries[i]; var p = 44 + 20 * i; Encoding.ASCII.GetBytes(t.Tag).CopyTo(bytes, p);
            P32(bytes, p + 4, (uint)t.Offset); P32(bytes, p + 8, (uint)t.Stored.Length); P32(bytes, p + 12, (uint)t.Raw.Length);
            var checksumData = t.Raw.ToArray(); if (t.Tag == "head") P32(checksumData, 8, 0);
            P32(bytes, p + 16, Sum(checksumData));
        }
        File.WriteAllBytes(output, bytes);
    }

    static void Generate(string folder)
    {
        Directory.CreateDirectory(folder);
        using var bitmap = new SKBitmap(640, 320);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(640, 320), new SKColor[] { SKColors.Red, SKColors.Blue }, null, SKShaderTileMode.Clamp) };
        canvas.DrawRect(0, 0, 640, 320, paint); canvas.Flush();
        using (var png = bitmap.Encode(SKEncodedImageFormat.Png, 100)) File.WriteAllBytes(Path.Combine(folder, "gradient.png"), png.ToArray());
        using (var jpeg = bitmap.Encode(SKEncodedImageFormat.Jpeg, 100)) File.WriteAllBytes(Path.Combine(folder, "photo.jpeg"), jpeg.ToArray());
        canvas.Clear(SKColors.CornflowerBlue); canvas.Flush();
        using (var png = bitmap.Encode(SKEncodedImageFormat.Png, 100)) File.WriteAllBytes(Path.Combine(folder, "flat.png"), png.ToArray());
        // Tiny independent decoder fixtures for formats SkiaSharp reads but cannot encode.
        File.WriteAllBytes(Path.Combine(folder, "first-frame.gif"), Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"));
        var bmp = new byte[62]; bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54); BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 2); BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26), 1); BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28), 24);
        bmp[54] = 255; bmp[58] = 255; File.WriteAllBytes(Path.Combine(folder, "tiny.bmp"), bmp);
        File.WriteAllText(Path.Combine(folder, "drawing.svg"), """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape" width="640" height="320" viewBox="0 0 640 320">
              <!-- disposable editor comment -->
              <metadata>editor data editor data editor data editor data editor data editor data editor data</metadata>
              <title>Keep this accessible title</title>
              <rect inkscape:label="Rect" x="0.00000" y="0.00000" width="640.00000" height="320.00000" fill="#123456"/>
              <path d="M10.123456 20.123456 L30.123456 40.123456" stroke="red"/>
            </svg>
            """);
    }

    public static void Run(string[] args)
    {
        if (args[0] == "generate") { Generate(args[1]); MakeWoff(args[2], Path.Combine(args[1], "source.woff")); }
        else if (args[0] == "font") FontCheck(args[1], args[2]);
        else if (args[0] == "image")
        {
            using var bitmap = SKBitmap.Decode(args[1]); Check(bitmap is not null && bitmap.Width == int.Parse(args[2]) && bitmap.Height == int.Parse(args[3]), "Image decode/dimensions: " + args[1]);
        }
        else if (args[0] == "svg")
        {
            using var svg = new SKSvg(); var picture = svg.Load(args[1]);
            Check(picture is not null && picture.CullRect.Width == 640 && picture.CullRect.Height == 320, "SVG dimensions");
            var text = File.ReadAllText(args[1]);
            Check(!text.Contains("<!--") && !text.Contains("metadata") && !text.Contains("inkscape") && text.Contains("viewBox") && text.Contains("10.12"), "SVG optimization");
            Check(text.Contains("<title>") == (args[2] == "title"), "SVG title preservation");
        }
        else throw new Exception("Unknown asset test mode");
    }
}
