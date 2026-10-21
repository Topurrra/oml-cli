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
        // An empty value counts as unset: another check can leave the variable empty in a shared session.
        var ss = Environment.GetEnvironmentVariable("OML_SERVICE_STUDIO") is { Length: > 0 } set ? set : @"C:\Program Files\OutSystems\Service Studio 11\Service Studio";
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
            Check(!text.Contains("<!--") && !text.Contains("metadata") && !text.Contains("inkscape") && text.Contains("viewBox") && text.Contains("10.1") && !text.Contains("10.123"), "SVG optimization");
            Check(text.Contains("<title>") == (args[2] == "title"), "SVG title preservation");
        }
        else if (args[0] == "compare") Compare(args[1], args[2], float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture), args.Length > 4 ? args[4] : null);
        else if (args[0] == "ico") IcoCheck(args[1], args[2].Split(',').Select(int.Parse).ToArray());
        else if (args[0] == "pixel")
        {
            using var bitmap = Load(args[1], 1);
            var c = bitmap.GetPixel(int.Parse(args[2]), int.Parse(args[3]));
            Console.WriteLine($"{c.Red},{c.Green},{c.Blue},{c.Alpha}");
        }
        else if (args[0] == "oldico") OldIcons(args[1]);
        else if (args[0] == "alphapng") AlphaPng(args[1], int.Parse(args[2]), int.Parse(args[3]), byte.Parse(args[4]));
        else if (args[0] == "hugeclaim") HugeClaim(args[1]);
        else throw new Exception("Unknown asset test mode");
    }

    // A PNG decodes as is; an SVG is drawn at `scale` times its own size, or at the given pixel size.
    static SKBitmap Load(string path, float scale, int width = 0, int height = 0)
    {
        if (!path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            var decoded = SKBitmap.Decode(path) ?? throw new Exception("Cannot decode " + path);
            using (decoded) return decoded.Copy(SKColorType.Rgba8888)!;
        }
        using var svg = new SKSvg(); var picture = svg.Load(path) ?? throw new Exception("Svg.Skia cannot load " + path);
        var b = picture.CullRect;
        if (width == 0) { width = (int)Math.Ceiling(b.Width * scale); height = (int)Math.Ceiling(b.Height * scale); }
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent); canvas.Scale(width / b.Width, height / b.Height); canvas.Translate(-b.Left, -b.Top); canvas.DrawPicture(picture);
        return bitmap;
    }

    // Pixel difference of a candidate against a reference, both on white: "mismatch" is the percent of pixels where a
    // channel differs by more than 64 of 255, "mean" the mean channel difference. Optional sheet: reference | candidate | diff.
    static void Compare(string reference, string candidate, float scale, string? sheet)
    {
        using var a = Load(reference, scale);
        using var b = Load(candidate, scale, a.Width, a.Height);
        long bad = 0; double sum = 0;
        using var diff = new SKBitmap(a.Width, a.Height);
        static (int r, int g, int b) White(SKColor c) => (255 - (255 - c.Red) * c.Alpha / 255, 255 - (255 - c.Green) * c.Alpha / 255, 255 - (255 - c.Blue) * c.Alpha / 255);
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
            {
                var (p, q) = (White(a.GetPixel(x, y)), White(b.GetPixel(x, y)));
                var d = Math.Max(Math.Abs(p.r - q.r), Math.Max(Math.Abs(p.g - q.g), Math.Abs(p.b - q.b)));
                sum += (Math.Abs(p.r - q.r) + Math.Abs(p.g - q.g) + Math.Abs(p.b - q.b)) / 3.0;
                if (d > 64) bad++;
                diff.SetPixel(x, y, d > 64 ? SKColors.Red : new SKColor((byte)(255 - d), (byte)(255 - d), (byte)(255 - d)));
            }
        var n = (double)a.Width * a.Height;
        // The worst 16 x 16 window (step 8): a local defect (a blob in a letter) shows here even when the whole is fine.
        double window = 0;
        for (var y0 = 0; y0 + 16 <= a.Height; y0 += 8)
            for (var x0 = 0; x0 + 16 <= a.Width; x0 += 8)
            {
                var count = 0;
                for (var y = y0; y < y0 + 16; y++) for (var x = x0; x < x0 + 16; x++) if (diff.GetPixel(x, y) == SKColors.Red) count++;
                window = Math.Max(window, count / 2.56);
            }
        Console.WriteLine(FormattableString.Invariant($"mismatch={100 * bad / n:0.###} mean={sum / n:0.###} window={window:0.#}"));
        if (sheet is null) return;
        using var output = new SKBitmap(a.Width * 3 + 20, a.Height);
        using (var canvas = new SKCanvas(output))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(a, 0, 0); canvas.DrawBitmap(b, a.Width + 10, 0); canvas.DrawBitmap(diff, 2 * a.Width + 20, 0);
        }
        using var png = output.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(sheet, png.ToArray());
    }

    // A size x size transparent PNG with a black square of side `square` in the middle, at exactly `alpha`.
    static void AlphaPng(string path, int size, int square, byte alpha)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(SKColors.Transparent);
        var start = (size - square) / 2;
        for (var y = start; y < start + square; y++) for (var x = start; x < start + square; x++) bitmap.SetPixel(x, y, new SKColor(0, 0, 0, alpha));
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, png.ToArray());
    }

    // An ICO whose only frame is a tiny PNG that claims 60000 x 60000 pixels in its header.
    static void HugeClaim(string path)
    {
        static uint Crc(byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data) { crc ^= b; for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1; }
            return ~crc;
        }
        static byte[] Chunk(string type, byte[] body)
        {
            var typed = Encoding.ASCII.GetBytes(type).Concat(body).ToArray();
            var chunk = new byte[8 + body.Length + 4];
            BinaryPrimitives.WriteInt32BigEndian(chunk, body.Length); typed.CopyTo(chunk, 4);
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + body.Length), Crc(typed));
            return chunk;
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, 60000); BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), 60000);
        ihdr[8] = 8; ihdr[9] = 6;
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", ihdr), .. Chunk("IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]), .. Chunk("IEND", [])];
        var file = new byte[22 + png.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2), 1); BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(10), 1); BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(12), 32);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(14), png.Length); BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(18), 22);
        png.CopyTo(file, 22);
        File.WriteAllBytes(path, file);
    }

    // Old-style 2x2 icons that only use the AND mask for transparency: 24-bit (old24.ico) and 8-bit with a palette
    // (old8.ico). In both, the top-left pixel is masked out, top-right is red, the bottom row is blue.
    static void OldIcons(string folder)
    {
        static byte[] Icon(int bpp, byte[] palette, byte[] rows)
        {
            const int maskStride = 4;
            var dib = new byte[40 + palette.Length + rows.Length + maskStride * 2];
            BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(0), 40); BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 2);
            BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 4); BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), (ushort)bpp);
            palette.CopyTo(dib, 40); rows.CopyTo(dib, 40 + palette.Length);
            dib[40 + palette.Length + rows.Length + maskStride] = 0x80; // mask rows are bottom-up: the second row is the top one
            var file = new byte[6 + 16 + dib.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2), 1); BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), 1);
            file[6] = 2; file[7] = 2; BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(10), 1); BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(12), (ushort)bpp);
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(14), dib.Length); BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(18), 22);
            dib.CopyTo(file, 22);
            return file;
        }
        // Rows are bottom-up and padded to 4 bytes. Pixels are BGR.
        File.WriteAllBytes(Path.Combine(folder, "old24.ico"), Icon(24, [], [255, 0, 0, 255, 0, 0, 0, 0, /* top */ 0, 255, 0, 0, 0, 255, 0, 0]));
        // Palette: 0 = blue, 1 = red, 2 = green.
        File.WriteAllBytes(Path.Combine(folder, "old8.ico"), Icon(8, [255, 0, 0, 0, 0, 0, 255, 0, 0, 255, 0, 0, .. new byte[253 * 4]], [0, 0, 0, 0, /* top */ 2, 1, 0, 0]));
        // The same, but its header claims 300 palette colours (an 8-bit palette has at most 256).
        var claims = Icon(8, [255, 0, 0, 0, 0, 0, 255, 0, 0, 255, 0, 0, .. new byte[253 * 4]], [0, 0, 0, 0, 2, 1, 0, 0]);
        BinaryPrimitives.WriteInt32LittleEndian(claims.AsSpan(22 + 32), 300);
        File.WriteAllBytes(Path.Combine(folder, "used300.ico"), claims);
    }

    // Independent ICO reader: header, one directory entry per expected size, PNG frames above 48 and 32-bit BMP frames
    // with an AND mask up to 48, every frame decodable at its declared size.
    static void IcoCheck(string path, int[] sizes)
    {
        var b = File.ReadAllBytes(path);
        static int U16(byte[] b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p));
        static int I32(byte[] b, int p) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
        Check(U16(b, 0) == 0 && U16(b, 2) == 1 && U16(b, 4) == sizes.Length, "ICO header");
        var end = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            var e = 6 + 16 * i; var size = sizes[i];
            Check((b[e] == 0 ? 256 : b[e]) == size && (b[e + 1] == 0 ? 256 : b[e + 1]) == size && U16(b, e + 4) == 1 && U16(b, e + 6) == 32, $"ICO entry {size}");
            int length = I32(b, e + 8), offset = I32(b, e + 12);
            Check(offset == end && offset + length <= b.Length, $"ICO frame {size} offset");
            end = offset + length;
            var frame = b.AsSpan(offset, length).ToArray();
            if (size > 48)
            {
                Check(frame[0] == 0x89 && frame[1] == (byte)'P', $"ICO frame {size} must be PNG");
                using var png = SKBitmap.Decode(frame); Check(png is not null && png.Width == size && png.Height == size, $"ICO PNG frame {size}");
            }
            else
            {
                var maskStride = (size + 31) / 32 * 4;
                Check(I32(frame, 0) == 40 && I32(frame, 4) == size && I32(frame, 8) == size * 2 && U16(frame, 14) == 32
                    && frame.Length == 40 + size * size * 4 + maskStride * size, $"ICO BMP frame {size}");
            }
        }
        Check(end == b.Length, "ICO trailing bytes");
    }
}
