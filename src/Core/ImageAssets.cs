using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SkiaSharp;
using Svg.Skia;

namespace OmlCli;

// Asset-only entry point: deliberately does not initialize Model or register Service Studio plugins.
public static class ImageAssets
{
    static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".svg"];
    sealed record Settings(string? To, int Width, int Height, int Quality, int? Precision, bool StripTitle, int[] Sizes, string Mode, ImageTrace.Options Trace,
        bool Aggressive, bool RemoveIds, string? RasterOnly, bool Batch);
    const string Usage = "oml image <input> [--to png|jpeg|webp|svg|ico] [--max-width N] [--max-height N] [--quality 1-100] [--out file] [--force] [--precision 0-10] [--strip-title] " +
        "[--aggressive] [--remove-ids] [--sizes 16,32,48] [--mode trace|embed] [--colors 1-32] [--tolerance 0.1-5] [--min-area N] [--alpha-threshold 1-255] [--transparent-bg]; " +
        "image batch <folder> [same options] [--out folder]";
    static readonly string[] TraceOptions = ["--colors", "--tolerance", "--min-area", "--alpha-threshold"];

    public static int Run(string[] args)
    {
        JsonOutput.Pretty = args.Contains("--pretty");
        try
        {
            args = args.Where(a => a != "--pretty").ToArray();
            var batch = args.FirstOrDefault() == "batch";
            var opt = AssetFiles.Parse(batch ? args[1..] : args,
                ["--to", "--max-width", "--max-height", "--quality", "--out", "--precision", "--sizes", "--mode", .. TraceOptions], "--force", "--strip-title",
                "--aggressive", "--remove-ids", "--transparent-bg");
            if (opt.Paths.Count != 1) throw new ArgumentException(Usage);
            var to = opt.Get("--to")?.ToLowerInvariant();
            if (to is not (null or "png" or "jpeg" or "webp" or "svg" or "ico")) throw new ArgumentException("--to must be png, jpeg, webp, svg or ico.");
            if (opt.Get("--sizes") is not null && to != "ico") throw new ArgumentException("--sizes needs --to ico.");
            if (to == "ico" && (opt.Get("--max-width") is not null || opt.Get("--max-height") is not null))
                throw new ArgumentException("--max-width and --max-height do not apply to ICO output; --sizes sets the frame sizes.");
            var mode = opt.Get("--mode")?.ToLowerInvariant() ?? "trace";
            if (opt.Get("--mode") is not null && to != "svg") throw new ArgumentException("--mode needs --to svg (trace or embed a raster image).");
            if (mode is not ("trace" or "embed")) throw new ArgumentException("--mode must be trace or embed.");
            var traceOption = TraceOptions.FirstOrDefault(o => opt.Get(o) is not null) ?? (opt.Has("--transparent-bg") ? "--transparent-bg" : null);
            if (traceOption is not null && (to != "svg" || mode != "trace"))
                throw new ArgumentException($"{traceOption} only applies to tracing: --to svg with --mode trace (the default).");
            if ((opt.Has("--aggressive") || opt.Has("--remove-ids")) && to is not (null or "svg"))
                throw new ArgumentException($"{(opt.Has("--aggressive") ? "--aggressive" : "--remove-ids")} only applies to SVG optimization (SVG input, SVG output).");
            var settings = new Settings(to, opt.Number("--max-width", int.MaxValue, 1, 100000),
                opt.Number("--max-height", int.MaxValue, 1, 100000), opt.Number("--quality", 85, 1, 100),
                opt.Get("--precision") is null ? null : opt.Number("--precision", 3, 0, 10), opt.Has("--strip-title"),
                ImageIco.ParseSizes(opt.Get("--sizes")), mode,
                new ImageTrace.Options(opt.Number("--colors", 0, 1, 32), opt.Get("--tolerance") is null ? null : Decimal(opt.Get("--tolerance"), "--tolerance", 0, 0.1, 5),
                    opt.Get("--min-area") is null ? null : Decimal(opt.Get("--min-area"), "--min-area", 0, 0, 1_000_000), opt.Number("--alpha-threshold", 128, 1, 255),
                    opt.Has("--transparent-bg")),
                opt.Has("--aggressive"), opt.Has("--aggressive") || opt.Has("--remove-ids"),
                traceOption ?? (opt.Get("--mode") is not null ? "--mode" : null), batch);
            if (batch && !Directory.Exists(opt.Paths[0])) throw new DirectoryNotFoundException($"Folder not found: {opt.Paths[0]}");
            var inputs = batch ? Directory.GetFiles(opt.Paths[0]).Where(p => Extensions.Contains(Path.GetExtension(p).ToLowerInvariant())).Order(StringComparer.OrdinalIgnoreCase).ToArray() : [Path.GetFullPath(opt.Paths[0])];
            if (batch && inputs.Length == 0) throw new ArgumentException($"No images in {opt.Paths[0]} (looked for {string.Join(", ", Extensions)}; subfolders are not read).");
            var results = new List<object>(); var failed = 0;
            var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var input in inputs)
            {
                try
                {
                    var format = settings.To ?? Format(input);
                    var output = batch ? Path.Combine(opt.Get("--out") ?? opt.Paths[0], Path.GetFileNameWithoutExtension(input) + ".optimized." + format)
                        : AssetFiles.Output(input, opt.Get("--out"), format);
                    output = Path.GetFullPath(output);
                    if (!outputs.Add(output)) throw new IOException($"Multiple inputs map to {output}; use separate batches.");
                    AssetFiles.CheckOutput(output, inputs, opt.Has("--force"));
                    results.Add(Convert(input, output, settings, opt.Has("--force")));
                }
                catch (Exception e)
                {
                    if (!batch) throw;
                    failed++; results.Add(new { input, error = Error(e) });
                }
            }
            TerminalOutput.PrintObject(batch ? new { results, failed } : results[0]);
            return failed == 0 ? 0 : 1;
        }
        catch (Exception e) { TerminalOutput.PrintObject(new { error = Error(e) }); return 2; }
    }

    static double Decimal(string? text, string key, double fallback, double min, double max)
    {
        if (text is null) return fallback;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n < min || n > max)
            throw new ArgumentException($"{key} must be a number between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}.");
        return n;
    }

    static string Error(Exception e)
    {
        for (Exception? current = e; current is not null; current = current.InnerException)
            if (current is DllNotFoundException or BadImageFormatException || current.Message.Contains("libSkiaSharp", StringComparison.OrdinalIgnoreCase))
                return "Cannot load SkiaSharp's native libSkiaSharp.dll from the Service Studio installation (including runtimes/win-x64/native). Check OML_SERVICE_STUDIO and the native library architecture. " + current.Message;
        return e.Message;
    }

    static string Format(string input) => Path.GetExtension(input).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "jpeg", ".png" => "png", ".webp" => "webp", ".svg" => "svg",
        ".gif" => "gif", ".bmp" => "bmp", ".ico" => "ico",
        _ => throw new ArgumentException("Unsupported image extension.")
    };

    static (int Width, int Height) Fit(float width, float height, Settings s)
    {
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0) throw new InvalidDataException("Image has no finite positive dimensions.");
        var scale = Math.Min(1d, Math.Min(s.Width / (double)width, s.Height / (double)height));
        var w = Math.Max(1, checked((int)Math.Floor(width * scale)));
        var h = Math.Max(1, checked((int)Math.Floor(height * scale)));
        if ((long)w * h > 100_000_000) throw new InvalidDataException("Image exceeds the 100 million pixel allocation limit; set smaller dimensions.");
        return (w, h);
    }

    static SKPicture LoadSvg(XDocument document, SKSvg svg)
    {
        using var reader = document.CreateReader();
        var picture = svg.Load(reader) ?? throw new InvalidDataException("Svg.Skia could not render the SVG.");
        var b = picture.CullRect;
        if (!float.IsFinite(b.Width) || !float.IsFinite(b.Height) || b.Width <= 0 || b.Height <= 0) throw new InvalidDataException("The SVG has no size (set width and height, or a viewBox).");
        return picture;
    }

    // Raster input, any supported format. ICO is read by oml itself: the largest frame, PNG or BMP.
    static SKBitmap LoadRaster(byte[] bytes, string format)
    {
        if (format == "ico") return ImageIco.Read(bytes);
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("SkiaSharp could not decode the image.");
        // Guard the full decode as well as the resized output.
        if ((long)codec.Info.Width * codec.Info.Height > 100_000_000) throw new InvalidDataException("Input exceeds the 100 million pixel decode limit.");
        return SKBitmap.Decode(codec) ?? throw new InvalidDataException("SkiaSharp could not decode the first frame.");
    }

    static SKBitmap Shrink(SKBitmap bitmap, int w, int h) =>
        bitmap.Width == w && bitmap.Height == h ? bitmap.Copy()
        : bitmap.Resize(new SKImageInfo(w, h), SKFilterQuality.High) ?? throw new InvalidDataException("SkiaSharp resize failed.");

    static object Convert(string input, string output, Settings settings, bool force)
    {
        var inputFormat = Format(input); var outputFormat = settings.To ?? inputFormat;
        if (settings.To is null && inputFormat == "ico") throw new ArgumentException("ICO input needs --to png, jpeg, webp, svg or ico.");
        if (outputFormat is not ("png" or "jpeg" or "webp" or "svg" or "ico"))
            throw new ArgumentException($"SkiaSharp cannot encode {outputFormat}; select --to png, jpeg, webp, svg or ico.");
        if (inputFormat == "svg" && outputFormat is not ("svg" or "png" or "ico")) throw new ArgumentException("SVG output must be svg, png or ico.");
        var bytes = File.ReadAllBytes(input);
        byte[] encoded; int width, height, outWidth, outHeight;
        object? extra = null;
        // Raster-only options on an SVG: refused for one file, noted in a batch (where rasters may use them).
        string? ignored = null;
        if (inputFormat == "svg" && settings.RasterOnly is { } rasterOnly)
        {
            if (!settings.Batch) throw new ArgumentException($"{rasterOnly} applies to raster input (PNG, JPEG, ...); {Path.GetFileName(input)} is an SVG.");
            ignored = $"{rasterOnly} and the other tracing options apply to raster input only; this SVG was optimized without them.";
        }
        if ((settings.Aggressive || settings.RemoveIds) && inputFormat != "svg" && !settings.Batch)
            throw new ArgumentException($"{(settings.Aggressive ? "--aggressive" : "--remove-ids")} only applies to SVG input; {Path.GetFileName(input)} is a raster image.");
        if (inputFormat == "svg" && outputFormat == "svg")
        {
            var r = SvgOptimizer.Run(bytes, settings.Precision, settings.StripTitle, settings.Aggressive, settings.RemoveIds);
            using var svg = new SKSvg();
            var bounds = LoadSvg(ReadSvg(bytes), svg).CullRect;
            width = outWidth = checked((int)Math.Ceiling(bounds.Width)); height = outHeight = checked((int)Math.Ceiling(bounds.Height));
            encoded = r.Bytes;
            extra = r.Kept ? new { kept = "original", reason = r.Reason, mismatch = Math.Round(r.Mismatch, 4), note = ignored }
                : new { decimals = r.Decimals, mismatch = Math.Round(r.Mismatch, 4), note = ignored };
        }
        else if (inputFormat == "svg")
        {
            var document = ReadSvg(bytes);
            using var svg = new SKSvg();
            var picture = LoadSvg(document, svg);
            var bounds = picture.CullRect;
            width = checked((int)Math.Ceiling(bounds.Width)); height = checked((int)Math.Ceiling(bounds.Height));
            if (outputFormat == "ico")
            {
                // Each size is drawn from the vector, centred on a transparent square.
                encoded = ImageIco.Write(settings.Sizes, size =>
                {
                    var scale = size / Math.Max(bounds.Width, bounds.Height);
                    var bitmap = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
                    using var canvas = new SKCanvas(bitmap);
                    canvas.Clear(SKColors.Transparent);
                    canvas.Translate((size - bounds.Width * scale) / 2, (size - bounds.Height * scale) / 2); canvas.Scale(scale); canvas.Translate(-bounds.Left, -bounds.Top);
                    canvas.DrawPicture(picture); canvas.Flush();
                    return bitmap;
                });
                outWidth = outHeight = settings.Sizes[^1];
                extra = IcoReport(settings.Sizes);
            }
            else
            {
                (outWidth, outHeight) = Fit(bounds.Width, bounds.Height, settings);
                using var bitmap = new SKBitmap(outWidth, outHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(SKColors.Transparent);
                    canvas.Scale(outWidth / bounds.Width, outHeight / bounds.Height); canvas.Translate(-bounds.Left, -bounds.Top);
                    canvas.DrawPicture(picture); canvas.Flush();
                }
                encoded = Encode(bitmap, outputFormat, settings.Quality);
            }
        }
        else
        {
            using var bitmap = LoadRaster(bytes, inputFormat);
            width = bitmap.Width; height = bitmap.Height;
            if (outputFormat == "ico")
            {
                encoded = ImageIco.Write(settings.Sizes, size => ImageIco.Square(bitmap, size));
                outWidth = outHeight = settings.Sizes[^1];
                extra = IcoReport(settings.Sizes);
            }
            else
            {
                (outWidth, outHeight) = Fit(width, height, settings);
                using var resized = Shrink(bitmap, outWidth, outHeight);
                if (outputFormat == "svg" && settings.Mode == "embed")
                {
                    var png = Encode(resized, "png", 100);
                    var doc = new XDocument(new XElement(SvgNs + "svg", new XAttribute("viewBox", $"0 0 {outWidth} {outHeight}"), new XAttribute("width", outWidth), new XAttribute("height", outHeight),
                        new XElement(SvgNs + "image", new XAttribute("width", outWidth), new XAttribute("height", outHeight), new XAttribute("href", "data:image/png;base64," + System.Convert.ToBase64String(png)))));
                    encoded = Encoding.UTF8.GetBytes(SvgOptimizer.Serialize(doc));
                    extra = new { mode = "embed", note = "A PNG inside an SVG file, not a vector: it does not stay sharp when scaled up. Meant for photos." };
                }
                else if (outputFormat == "svg")
                {
                    if ((long)outWidth * outHeight > 16_777_216) throw new InvalidDataException("Tracing is limited to 16 million pixels (4096 x 4096); set --max-width or --max-height.");
                    var traced = ImageTrace.Trace(resized, settings.Trace);
                    var decimals = settings.Precision ?? (Math.Max(outWidth, outHeight) <= 64 ? 2 : 1);
                    SvgOptimizer.Optimize(traced.Svg, decimals, false);
                    encoded = Encoding.UTF8.GetBytes(SvgOptimizer.Serialize(traced.Svg));
                    extra = new { mode = "trace", colors = traced.Colors, paths = traced.Paths, decimals, removedBackground = traced.RemovedBackground, note = traced.Note };
                }
                else encoded = Encode(resized, outputFormat, settings.Quality);
            }
        }
        AssetFiles.Write(output, encoded, force);
        return new { input, output, inputFormat, outputFormat, before = new { width, height }, after = new { width = outWidth, height = outHeight },
            inputBytes = bytes.Length, outputBytes = encoded.Length, ratio = AssetFiles.Ratio(encoded.Length, bytes.Length), details = extra };
    }

    static readonly XNamespace SvgNs = "http://www.w3.org/2000/svg";

    static object IcoReport(int[] sizes) => new { sizes, frames = sizes.Select(s => s <= 48 ? "bmp" : "png").ToArray() };

    static byte[] Encode(SKBitmap bitmap, string format, int quality)
    {
        // Rebuilding pixels removes EXIF/XMP/ICC and animation. SkiaSharp 2.88 has no indexed-color encoder.
        using var clean = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(clean))
        {
            canvas.Clear(format == "jpeg" ? SKColors.White : SKColors.Transparent);
            canvas.DrawBitmap(bitmap, 0, 0);
        }
        using var encoded = clean.Encode(format switch { "png" => SKEncodedImageFormat.Png, "jpeg" => SKEncodedImageFormat.Jpeg, _ => SKEncodedImageFormat.Webp }, format == "png" ? 100 : quality)
            ?? throw new InvalidDataException($"SkiaSharp cannot encode {format} on this build.");
        return encoded.ToArray();
    }

    internal static XDocument ReadSvg(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        // A plain <!DOCTYPE svg PUBLIC ...> (old editors write one) is skipped, never fetched. A DOCTYPE with its own
        // declarations (entities) is refused.
        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        var doctype = Regex.Match(head, @"<!DOCTYPE[^>\[]*(\[)?", RegexOptions.IgnoreCase);
        var plain = doctype.Success && !doctype.Groups[1].Success && head.IndexOf('>', doctype.Index) > 0;
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = plain ? DtdProcessing.Ignore : DtdProcessing.Prohibit, XmlResolver = null });
        var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (doc.Root?.Name.LocalName != "svg") throw new InvalidDataException("Expected an SVG root element.");
        return doc;
    }
}
