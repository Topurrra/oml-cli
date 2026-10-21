using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace OmlCli;

// Windows icon files, read and written by oml itself (SkiaSharp 2.88 has no ICO encoder, and its decoder does not pick
// the largest frame). Frames of 48 pixels and less are 32-bit BMP with an AND mask, which every icon reader accepts;
// larger frames are PNG, which keeps the file small and is what Windows Vista and later, and all browsers, read.
static class ImageIco
{
    internal static readonly int[] DefaultSizes = [16, 32, 48];
    const int BmpMax = 48;

    internal static int[] ParseSizes(string? text)
    {
        if (text is null) return DefaultSizes;
        var parts = text.Split(',');
        var sizes = new List<int>();
        foreach (var part in parts)
        {
            if (!int.TryParse(part.Trim(), out var n) || part.Trim().Length == 0)
                throw new ArgumentException($"--sizes must be a comma-separated list of whole numbers, such as 16,32,48; '{part.Trim()}' is not one.");
            if (n is < 1 or > 256) throw new ArgumentException($"--sizes: {n} is outside 1-256. An ICO frame is at most 256 x 256 pixels.");
            if (sizes.Contains(n)) throw new ArgumentException($"--sizes lists {n} twice.");
            sizes.Add(n);
        }
        return [.. sizes.Order()];
    }

    // `render(size)` returns a size x size bitmap; the ICO keeps the frames in ascending size.
    internal static byte[] Write(int[] sizes, Func<int, SKBitmap> render)
    {
        var frames = new List<byte[]>();
        foreach (var size in sizes)
        {
            using var bitmap = render(size);
            frames.Add(size <= BmpMax ? Dib(bitmap) : Png(bitmap));
        }
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)sizes.Length);
        stream.Write(header);
        var offset = 6 + 16 * sizes.Length;
        Span<byte> entry = stackalloc byte[16];
        for (var i = 0; i < sizes.Length; i++)
        {
            entry.Clear();
            entry[0] = (byte)(sizes[i] == 256 ? 0 : sizes[i]); entry[1] = entry[0]; // 0 means 256
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);   // planes
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32);  // bits per pixel
            BinaryPrimitives.WriteInt32LittleEndian(entry[8..], frames[i].Length);
            BinaryPrimitives.WriteInt32LittleEndian(entry[12..], offset);
            stream.Write(entry);
            offset += frames[i].Length;
        }
        foreach (var f in frames) stream.Write(f);
        return stream.ToArray();
    }

    static byte[] Png(SKBitmap bitmap)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidDataException("SkiaSharp cannot encode PNG on this build.");
        return data.ToArray();
    }

    internal static byte[] Pixels(SKBitmap bitmap, SKColorType type)
    {
        var info = new SKImageInfo(bitmap.Width, bitmap.Height, type, SKAlphaType.Unpremul);
        var pixels = new byte[info.BytesSize];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { using var pixmap = bitmap.PeekPixels(); if (pixmap is null || !pixmap.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes)) throw new InvalidDataException("SkiaSharp could not read the pixels."); }
        finally { handle.Free(); }
        return pixels;
    }

    // BITMAPINFOHEADER with double height, bottom-up BGRA rows, then the 1-bit AND mask (1 = transparent).
    static byte[] Dib(SKBitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height, maskStride = (w + 31) / 32 * 4;
        var bgra = Pixels(bitmap, SKColorType.Bgra8888);
        var result = new byte[40 + w * h * 4 + maskStride * h];
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0), 40);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), w);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), h * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(14), 32);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(20), w * h * 4 + maskStride * h);
        var mask = 40 + w * h * 4;
        for (var y = 0; y < h; y++)
        {
            var row = h - 1 - y;
            Buffer.BlockCopy(bgra, y * w * 4, result, 40 + row * w * 4, w * 4);
            for (var x = 0; x < w; x++)
                if (bgra[(y * w + x) * 4 + 3] == 0) result[mask + row * maskStride + x / 8] |= (byte)(0x80 >> (x % 8));
        }
        return result;
    }

    // The largest frame (then the deepest colour) of an ICO, decoded from PNG or BMP.
    internal static SKBitmap Read(byte[] bytes)
    {
        if (bytes.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(bytes) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)) != 1)
            throw new InvalidDataException("Not an ICO file (the header does not start with 0, 1).");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        if (count == 0) throw new InvalidDataException("The ICO file has no images.");
        if (bytes.Length < 6 + 16 * count) throw new InvalidDataException("The ICO directory is cut off.");
        var frames = Enumerable.Range(0, count).Select(i =>
        {
            var e = bytes.AsSpan(6 + 16 * i);
            int w = e[0] == 0 ? 256 : e[0], h = e[1] == 0 ? 256 : e[1];
            int bpp = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]);
            var size = BinaryPrimitives.ReadInt32LittleEndian(e[8..]); var offset = BinaryPrimitives.ReadInt32LittleEndian(e[12..]);
            return (w, h, bpp, size, offset);
        }).ToList();
        var best = frames.OrderByDescending(f => (long)f.w * f.h).ThenByDescending(f => f.bpp).First();
        if (best.offset < 6 || best.size <= 0 || (long)best.offset + best.size > bytes.Length)
            throw new InvalidDataException($"The {best.w}x{best.h} ICO frame points outside the file.");
        var frame = bytes.AsSpan(best.offset, best.size);
        if (frame.Length >= 8 && frame[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            // The PNG header states the real size, whatever the directory says: check it before decoding.
            using var data = SKData.CreateCopy(frame.ToArray());
            using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("The PNG frame of the ICO file cannot be decoded.");
            if ((long)codec.Info.Width * codec.Info.Height > 100_000_000)
                throw new InvalidDataException($"The PNG frame of the ICO file is {codec.Info.Width} x {codec.Info.Height} pixels, over the 100 million pixel decode limit.");
            return SKBitmap.Decode(codec) ?? throw new InvalidDataException("The PNG frame of the ICO file cannot be decoded.");
        }
        return DecodeDib(frame);
    }

    static SKBitmap DecodeDib(ReadOnlySpan<byte> d)
    {
        if (d.Length < 40) throw new InvalidDataException("The BMP frame of the ICO file is cut off.");
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(d);
        int w = BinaryPrimitives.ReadInt32LittleEndian(d[4..]), h = BinaryPrimitives.ReadInt32LittleEndian(d[8..]) / 2;
        int bpp = BinaryPrimitives.ReadUInt16LittleEndian(d[14..]), compression = BinaryPrimitives.ReadInt32LittleEndian(d[16..]);
        var used = BinaryPrimitives.ReadInt32LittleEndian(d[32..]);
        if (headerSize < 40 || w is < 1 or > 1024 || h is < 1 or > 1024) throw new InvalidDataException("The BMP frame of the ICO file has an invalid size.");
        if (bpp is not (1 or 4 or 8 or 24 or 32) || compression is not (0 or 3)) throw new InvalidDataException($"The BMP frame of the ICO file uses {bpp}-bit colour or compression {compression}, which oml does not read.");
        if (bpp <= 8 && (used < 0 || used > 1 << bpp)) throw new InvalidDataException($"The BMP frame of the ICO file claims {used} palette colours; {bpp}-bit colour has at most {1 << bpp}.");
        var paletteCount = bpp <= 8 ? (used > 0 ? used : 1 << bpp) : 0;
        // BI_BITFIELDS keeps three masks after a 40-byte header; icons use the standard BGRA order.
        var paletteStart = headerSize + (compression == 3 && headerSize == 40 ? 12 : 0);
        var pixelStart = paletteStart + paletteCount * 4;
        int stride = (w * bpp + 31) / 32 * 4, maskStride = (w + 31) / 32 * 4;
        var maskStart = pixelStart + stride * h;
        if (pixelStart + (long)stride * h > d.Length) throw new InvalidDataException("The BMP frame of the ICO file is cut off.");
        var hasMask = maskStart + (long)maskStride * h <= d.Length;
        if (!hasMask && bpp != 32) throw new InvalidDataException("The BMP frame of the ICO file has no transparency mask.");
        var rgba = new byte[w * h * 4];
        var anyAlpha = false;
        for (var y = 0; y < h; y++)
        {
            var row = d.Slice(pixelStart + (h - 1 - y) * stride, stride);
            for (var x = 0; x < w; x++)
            {
                byte b, g, r, a = 255;
                if (bpp == 32) { b = row[x * 4]; g = row[x * 4 + 1]; r = row[x * 4 + 2]; a = row[x * 4 + 3]; anyAlpha |= a != 0; }
                else if (bpp == 24) { b = row[x * 3]; g = row[x * 3 + 1]; r = row[x * 3 + 2]; }
                else
                {
                    var bit = x * bpp; var index = (row[bit / 8] >> (8 - bpp - bit % 8)) & ((1 << bpp) - 1);
                    if (index >= paletteCount || paletteStart + index * 4 + 3 > d.Length) throw new InvalidDataException("The BMP frame of the ICO file uses a missing palette entry.");
                    var p = d.Slice(paletteStart + index * 4, 3); b = p[0]; g = p[1]; r = p[2];
                }
                var o = (y * w + x) * 4; rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = a;
            }
        }
        // Old 32-bit icons leave alpha at 0 and use the mask; every other depth always uses the mask.
        if (hasMask && (bpp != 32 || !anyAlpha))
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var transparent = (d[maskStart + (h - 1 - y) * maskStride + x / 8] & (0x80 >> (x % 8))) != 0;
                    rgba[(y * w + x) * 4 + 3] = transparent ? (byte)0 : (byte)255;
                }
        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
        return bitmap;
    }

    // Square size x size frame: the image is scaled down in halves (a box filter) until its long side is under 4 x size,
    // centred on a transparent square, scaled down in halves again and finished with SkiaSharp's high-quality filter,
    // so a 1024-pixel logo stays clean at 16 pixels. Shrinking before padding keeps a 20000 x 100 strip small.
    internal static SKBitmap Square(SKBitmap source, int size)
    {
        SKBitmap? shrunk = null; var fitted = source;
        while (Math.Max(fitted.Width, fitted.Height) >= size * 4)
        {
            var next = fitted.Resize(new SKImageInfo(Math.Max(1, fitted.Width / 2), Math.Max(1, fitted.Height / 2), SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Medium)
                ?? throw new InvalidDataException("SkiaSharp resize failed.");
            shrunk?.Dispose(); shrunk = fitted = next;
        }
        var side = Math.Max(fitted.Width, fitted.Height);
        var current = new SKBitmap(side, side, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(current))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(fitted, (side - fitted.Width) / 2, (side - fitted.Height) / 2);
        }
        shrunk?.Dispose();
        while (current.Width >= size * 2)
        {
            var half = current.Width / 2;
            var next = current.Resize(new SKImageInfo(half, half, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Medium)
                ?? throw new InvalidDataException("SkiaSharp resize failed.");
            current.Dispose(); current = next;
        }
        if (current.Width == size) return current;
        var result = current.Resize(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High)
            ?? throw new InvalidDataException("SkiaSharp resize failed.");
        current.Dispose();
        return result;
    }
}
