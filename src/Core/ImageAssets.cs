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
    sealed record Settings(string? To, int Width, int Height, int Quality, int Precision, bool StripTitle);

    public static int Run(string[] args)
    {
        JsonOutput.Pretty = args.Contains("--pretty");
        try
        {
            args = args.Where(a => a != "--pretty").ToArray();
            var batch = args.FirstOrDefault() == "batch";
            var opt = AssetFiles.Parse(batch ? args[1..] : args,
                ["--to", "--max-width", "--max-height", "--quality", "--out", "--precision"], "--force", "--strip-title");
            if (opt.Paths.Count != 1) throw new ArgumentException("oml image <input> [--to png|jpeg|webp|svg] [--max-width N] [--max-height N] [--quality 1-100] [--out file] [--force] [--precision 0-10] [--strip-title]; image batch <folder> [same options] [--out folder]");
            var to = opt.Get("--to")?.ToLowerInvariant();
            if (to is not (null or "png" or "jpeg" or "webp" or "svg")) throw new ArgumentException("--to must be png, jpeg, webp or svg.");
            var settings = new Settings(to, opt.Number("--max-width", int.MaxValue, 1, 100000),
                opt.Number("--max-height", int.MaxValue, 1, 100000), opt.Number("--quality", 85, 1, 100),
                opt.Number("--precision", 3, 0, 10), opt.Has("--strip-title"));
            var inputs = batch ? Directory.GetFiles(opt.Paths[0]).Where(p => Extensions.Contains(Path.GetExtension(p).ToLowerInvariant())).Order(StringComparer.OrdinalIgnoreCase).ToArray() : [Path.GetFullPath(opt.Paths[0])];
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
            Console.WriteLine(JsonOutput.Serialize(batch ? new { results, failed } : results[0]));
            return failed == 0 ? 0 : 1;
        }
        catch (Exception e) { Console.WriteLine(JsonOutput.Serialize(new { error = Error(e) })); return 2; }
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

    static object Convert(string input, string output, Settings settings, bool force)
    {
        var inputFormat = Format(input); var outputFormat = settings.To ?? inputFormat;
        if (outputFormat == "svg" && inputFormat != "svg")
            throw new ArgumentException("Tracing raster images to vectors is not supported; keep PNG/WebP.");
        if (outputFormat is not ("png" or "jpeg" or "webp" or "svg"))
            throw new ArgumentException($"SkiaSharp cannot encode {outputFormat}; select --to png, jpeg or webp.");
        if (inputFormat == "svg" && outputFormat is not ("svg" or "png")) throw new ArgumentException("SVG output must be svg or png.");
        var bytes = File.ReadAllBytes(input);
        byte[] encoded; int width, height, outWidth, outHeight;
        if (inputFormat == "svg")
        {
            var document = ReadSvg(bytes);
            using var svg = new SKSvg();
            using var reader = document.CreateReader();
            var picture = svg.Load(reader) ?? throw new InvalidDataException("Svg.Skia could not render the SVG.");
            var bounds = picture.CullRect;
            width = checked((int)Math.Ceiling(bounds.Width)); height = checked((int)Math.Ceiling(bounds.Height));
            if (outputFormat == "svg")
            {
                Optimize(document, settings.Precision, settings.StripTitle);
                encoded = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
                outWidth = width; outHeight = height;
            }
            else
            {
                (outWidth, outHeight) = Fit(bounds.Width, bounds.Height, settings);
                using var bitmap = new SKBitmap(outWidth, outHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(SKColors.Transparent);
                canvas.Scale(outWidth / bounds.Width, outHeight / bounds.Height); canvas.Translate(-bounds.Left, -bounds.Top);
                canvas.DrawPicture(picture); canvas.Flush();
                encoded = Encode(bitmap, outputFormat, settings.Quality);
            }
        }
        else
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("SkiaSharp could not decode the image (this build may not support ICO).");
            width = codec.Info.Width; height = codec.Info.Height;
            // Guard the full decode as well as the resized output.
            if ((long)width * height > 100_000_000) throw new InvalidDataException("Input exceeds the 100 million pixel decode limit.");
            using var bitmap = SKBitmap.Decode(codec) ?? throw new InvalidDataException("SkiaSharp could not decode the first frame.");
            (outWidth, outHeight) = Fit(width, height, settings);
            if (outWidth != width || outHeight != height)
            {
                using var resized = bitmap.Resize(new SKImageInfo(outWidth, outHeight), SKFilterQuality.High)
                    ?? throw new InvalidDataException("SkiaSharp resize failed.");
                encoded = Encode(resized, outputFormat, settings.Quality);
            }
            else encoded = Encode(bitmap, outputFormat, settings.Quality);
        }
        AssetFiles.Write(output, encoded, force);
        return new { input, output, inputFormat, outputFormat, before = new { width, height }, after = new { width = outWidth, height = outHeight },
            inputBytes = bytes.Length, outputBytes = encoded.Length, ratio = AssetFiles.Ratio(encoded.Length, bytes.Length) };
    }

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
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (doc.Root?.Name.LocalName != "svg") throw new InvalidDataException("Expected an SVG root element.");
        return doc;
    }

    static readonly Regex Number = new(@"[-+]?(?:\d+\.\d*|\.\d+|\d+)(?:[eE][-+]?\d+)?", RegexOptions.CultureInvariant);
    static readonly HashSet<string> NumericAttributes = new("x y x1 y1 x2 y2 cx cy r rx ry width height dx dy d points transform gradientTransform patternTransform viewBox stroke-width stroke-miterlimit stroke-dasharray stroke-dashoffset fill-opacity stroke-opacity opacity offset font-size letter-spacing word-spacing pathLength markerWidth markerHeight refX refY startOffset stdDeviation baseFrequency kernelMatrix keyTimes keySplines".Split(' '));
    static bool Editor(string ns) => ns.Contains("inkscape", StringComparison.OrdinalIgnoreCase) || ns.Contains("sodipodi", StringComparison.OrdinalIgnoreCase)
        || ns.StartsWith("http://ns.adobe.com/", StringComparison.OrdinalIgnoreCase);

    internal static void Optimize(XDocument doc, int precision, bool stripTitle)
    {
        doc.DescendantNodes().OfType<XComment>().Remove();
        foreach (var e in doc.Descendants().Where(e => e != doc.Root && (e.Name.LocalName == "metadata" || Editor(e.Name.NamespaceName) || (stripTitle && e.Name.LocalName == "title"))).ToArray()) e.Remove();
        foreach (var e in doc.Descendants())
        {
            foreach (var a in e.Attributes().ToArray())
            {
                if (Editor(a.Name.NamespaceName) || (a.IsNamespaceDeclaration && Editor(a.Value))) { a.Remove(); continue; }
                // Never rewrite IDs, URLs, CSS, colours or text; digits there are not geometry.
                if (a.Name.NamespaceName.Length == 0 && NumericAttributes.Contains(a.Name.LocalName))
                {
                    // Arc flags are grammar tokens, not general numbers. Leave path data with arcs intact.
                    if (a.Name.LocalName == "d" && (a.Value.Contains('a') || a.Value.Contains('A'))) continue;
                    a.Value = Number.Replace(a.Value, m =>
                    {
                        if (!double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n)) return m.Value;
                        var shortened = Math.Round(n, precision).ToString(precision == 0 ? "0" : "0." + new string('#', precision), CultureInfo.InvariantCulture);
                        // Rounding a negative near zero removes its minus, which may have separated two path numbers.
                        return m.Index > 0 && (char.IsDigit(a.Value[m.Index - 1]) || a.Value[m.Index - 1] == '.') && !shortened.StartsWith('-')
                            ? " " + shortened : shortened;
                    });
                    a.Value = Regex.Replace(a.Value, @"\s+", " ").Trim();
                }
            }
        }
        // Whitespace in text/tspan/style is meaningful. Remove only inter-element indentation outside these trees.
        foreach (var t in doc.DescendantNodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)
                     && !t.Ancestors().Any(a => a.Name.LocalName is "text" or "tspan" or "style" or "title" or "desc" || (string?)a.Attribute(XNamespace.Xml + "space") == "preserve")).ToArray()) t.Remove();
        doc.Declaration = null;
    }
}
