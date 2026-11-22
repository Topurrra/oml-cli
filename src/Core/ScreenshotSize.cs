using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace OmlCli;

// The size of a screenshot an agent reads. Image tokens grow with the pixel count (about width x height / 750), not with
// the file format, so a smaller picture is what saves tokens. `oml browser ... --max-width N --max-height N` (or the
// OML_SCREENSHOT_MAX_WIDTH and OML_SCREENSHOT_MAX_HEIGHT variables) shrink a capture to fit, keeping its aspect ratio,
// and never enlarge it. The browser's PNG is shrunk here (8-bit RGB or RGBA, as browsers write it), so the browser commands
// need no image library and work the same for every engine, window size, zoom and device pixel ratio.
public static class ScreenshotSize
{
    // The size to write a width x height capture at, within the limits (null or 0: no limit).
    public static (int Width, int Height) Fit(int width, int height, int? maxWidth, int? maxHeight)
    {
        var scale = 1.0;
        if (maxWidth is > 0 && width > maxWidth) scale = Math.Min(scale, (double)maxWidth.Value / width);
        if (maxHeight is > 0 && height > maxHeight) scale = Math.Min(scale, (double)maxHeight.Value / height);
        if (scale >= 1) return (width, height);
        var w = Math.Max(1, (int)Math.Round(width * scale));
        var h = Math.Max(1, (int)Math.Round(height * scale));
        return (maxWidth is > 0 ? Math.Min(w, maxWidth.Value) : w, maxHeight is > 0 ? Math.Min(h, maxHeight.Value) : h);
    }

    // About what a picture costs an agent to read: width x height / 750 (Claude's rule of thumb; others are close).
    public static int Tokens(int width, int height) => (int)Math.Round((double)width * height / 750);

    // What the JSON output reports next to the file: the size written, its cost, and the capture size when it was shrunk.
    public static JsonObject Report(int width, int height, int fromWidth, int fromHeight)
    {
        var report = new JsonObject { ["width"] = width, ["height"] = height, ["tokens"] = Tokens(width, height) };
        if (fromWidth != width || fromHeight != height) report["scaledFrom"] = $"{fromWidth}x{fromHeight}";
        return report;
    }

    // Width and height from a PNG's header; (0, 0) when it is not a PNG.
    public static (int Width, int Height) PngSize(ReadOnlySpan<byte> png) =>
        png.Length >= 24 && png[0] == 0x89 && png[1] == (byte)'P' && png[12] == (byte)'I' && png[15] == (byte)'R'
            ? (BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..]))
            : (0, 0);

    // The PNG shrunk to fit the limits, or the same bytes when it fits already or is not a plain 8-bit RGB(A) PNG.
    public static byte[] ShrinkPng(byte[] png, int? maxWidth, int? maxHeight)
    {
        var (width, height) = PngSize(png);
        var (toWidth, toHeight) = Fit(width, height, maxWidth, maxHeight);
        if (toWidth >= width && toHeight >= height || Decode(png) is not { } rgba) return png;
        return Encode(Shrink(rgba, width, height, toWidth, toHeight), toWidth, toHeight);
    }

    // A 4-bytes-per-pixel image shrunk to toWidth x toHeight: each target pixel is the average of the source pixels it covers
    // (a box filter, which keeps thin lines and text readable better than skipping pixels).
    public static byte[] Shrink(byte[] pixels, int width, int height, int toWidth, int toHeight)
    {
        if (toWidth >= width && toHeight >= height) return pixels;
        var result = new byte[toWidth * toHeight * 4];
        for (var ty = 0; ty < toHeight; ty++)
        {
            int y0 = (int)((long)ty * height / toHeight), y1 = Math.Max(y0 + 1, (int)((long)(ty + 1) * height / toHeight));
            for (var tx = 0; tx < toWidth; tx++)
            {
                int x0 = (int)((long)tx * width / toWidth), x1 = Math.Max(x0 + 1, (int)((long)(tx + 1) * width / toWidth));
                long c0 = 0, c1 = 0, c2 = 0, c3 = 0;
                for (var y = y0; y < y1; y++)
                    for (var x = x0; x < x1; x++)
                    {
                        var i = (y * width + x) * 4;
                        c0 += pixels[i]; c1 += pixels[i + 1]; c2 += pixels[i + 2]; c3 += pixels[i + 3];
                    }
                var n = (y1 - y0) * (x1 - x0);
                var o = (ty * toWidth + tx) * 4;
                result[o] = (byte)((c0 + n / 2) / n); result[o + 1] = (byte)((c1 + n / 2) / n); result[o + 2] = (byte)((c2 + n / 2) / n); result[o + 3] = (byte)((c3 + n / 2) / n);
            }
        }
        return result;
    }

    // RGBA pixels of an 8-bit, non-interlaced RGB or RGBA PNG; null for any other kind.
    public static byte[]? Decode(byte[] png)
    {
        var (width, height) = PngSize(png);
        if (width <= 0 || height <= 0 || png[24] != 8 || png[25] is not (2 or 6) || png[28] != 0) return null;
        var bpp = png[25] == 6 ? 4 : 3;
        var data = new MemoryStream();
        for (var at = 8; at + 12 <= png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            if (length < 0 || at + 12 + length > png.Length) return null;
            if (png.AsSpan(at + 4, 4).SequenceEqual("IDAT"u8)) data.Write(png, at + 8, length);
            at += 12 + length;
        }
        data.Position = 0;
        var stride = width * bpp;
        var raw = new byte[(stride + 1) * height];
        try
        {
            using var z = new ZLibStream(data, CompressionMode.Decompress);
            z.ReadExactly(raw);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { return null; }
        var rgba = new byte[width * height * 4];
        var previous = new byte[stride];
        var row = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            if (filter > 4) return null;
            Array.Copy(raw, y * (stride + 1) + 1, row, 0, stride);
            for (var i = 0; i < stride; i++)
            {
                int a = i >= bpp ? row[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
                row[i] += filter switch
                {
                    0 => 0, 1 => (byte)a, 2 => (byte)b, 3 => (byte)((a + b) / 2),
                    4 => (byte)(Math.Abs(b - c) <= Math.Abs(a - c) && Math.Abs(b - c) <= Math.Abs(a + b - 2 * c) ? a : Math.Abs(a - c) <= Math.Abs(a + b - 2 * c) ? b : c),
                    _ => 0,
                };
            }
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                rgba[o] = row[x * bpp]; rgba[o + 1] = row[x * bpp + 1]; rgba[o + 2] = row[x * bpp + 2]; rgba[o + 3] = bpp == 4 ? row[x * bpp + 3] : (byte)255;
            }
            (previous, row) = (row, previous);
        }
        return rgba;
    }

    // RGBA pixels as an 8-bit RGB PNG (a screenshot is opaque), filter 0 on every row.
    public static byte[] Encode(byte[] rgba, int width, int height)
    {
        var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + width * 3];
            for (var y = 0; y < height; y++)
            {
                for (int x = 0, s = y * width * 4, d = 1; x < width; x++, s += 4, d += 3)
                    (row[d], row[d + 1], row[d + 2]) = (rgba[s], rgba[s + 1], rgba[s + 2]);
                z.Write(row);
            }
        }
        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        (header[8], header[9]) = (8, 2);
        Chunk(png, "IHDR"u8, header);
        Chunk(png, "IDAT"u8, raw.ToArray());
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    static void Chunk(Stream s, ReadOnlySpan<byte> type, byte[] data)
    {
        var head = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
        type.CopyTo(head.AsSpan(4));
        s.Write(head);
        s.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in head.AsSpan(4)) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        var tail = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tail, crc ^ 0xFFFFFFFFu);
        s.Write(tail);
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();
}
