using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SkiaSharp;
using Svg.Skia;

namespace OmlCli;

// Lossless-looking SVG optimization in the spirit of SVGO, written for oml (no SVGO code). Every result is drawn with
// Svg.Skia at 1x and 4x and compared with the original; if the pixels differ, the original is kept.
static class SvgOptimizer
{
    internal sealed record Result(byte[] Bytes, bool Kept, string? Reason, int? Decimals, double Mismatch);

    static readonly XNamespace Ns = "http://www.w3.org/2000/svg";
    static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    static readonly HashSet<string> Presentation = new(("alignment-baseline baseline-shift clip clip-path clip-rule color color-interpolation color-interpolation-filters " +
        "color-profile color-rendering cursor direction display dominant-baseline fill fill-opacity fill-rule filter flood-color flood-opacity font-family " +
        "font-size font-size-adjust font-stretch font-style font-variant font-weight glyph-orientation-horizontal glyph-orientation-vertical image-rendering " +
        "kerning letter-spacing lighting-color marker-end marker-mid marker-start mask opacity overflow pointer-events shape-rendering stop-color stop-opacity " +
        "stroke stroke-dasharray stroke-dashoffset stroke-linecap stroke-linejoin stroke-miterlimit stroke-opacity stroke-width text-anchor text-decoration " +
        "text-rendering unicode-bidi visibility word-spacing writing-mode").Split(' '));
    static readonly HashSet<string> Inherited = new(("clip-rule color color-interpolation color-interpolation-filters color-rendering cursor direction fill " +
        "fill-opacity fill-rule font-family font-size font-size-adjust font-stretch font-style font-variant font-weight glyph-orientation-horizontal " +
        "glyph-orientation-vertical image-rendering kerning letter-spacing marker-end marker-mid marker-start pointer-events shape-rendering stroke " +
        "stroke-dasharray stroke-dashoffset stroke-linecap stroke-linejoin stroke-miterlimit stroke-opacity stroke-width text-anchor text-rendering " +
        "visibility word-spacing writing-mode").Split(' '));
    static readonly HashSet<string> ColorProps = ["fill", "stroke", "stop-color", "flood-color", "lighting-color", "color"];
    // Inherited properties at their initial value: removable when no ancestor sets the property.
    static readonly Dictionary<string, string[]> InheritedDefaults = new()
    {
        ["fill"] = ["#000", "black"], ["fill-opacity"] = ["1"], ["fill-rule"] = ["nonzero"], ["clip-rule"] = ["nonzero"], ["stroke"] = ["none"],
        ["stroke-width"] = ["1"], ["stroke-opacity"] = ["1"], ["stroke-linecap"] = ["butt"], ["stroke-linejoin"] = ["miter"],
        ["stroke-miterlimit"] = ["4"], ["stroke-dasharray"] = ["none"], ["stroke-dashoffset"] = ["0"], ["visibility"] = ["visible"],
    };
    // Lengths take the decimals chosen from the viewBox size; ratios (0..1 and similar) always keep 3 decimals.
    static readonly HashSet<string> LengthAttributes = new("x y x1 y1 x2 y2 cx cy r rx ry fx fy width height dx dy points stroke-width stroke-dasharray stroke-dashoffset font-size letter-spacing word-spacing markerWidth markerHeight refX refY startOffset".Split(' '));
    static readonly HashSet<string> RatioAttributes = new("fill-opacity stroke-opacity opacity stop-opacity flood-opacity offset stroke-miterlimit".Split(' '));
    static readonly HashSet<string> Animation = ["animate", "set", "animateTransform", "animateMotion", "animateColor"];
    static readonly HashSet<string> Paint = ["linearGradient", "radialGradient", "pattern", "clipPath", "mask", "marker", "filter"];
    static readonly Regex Number = new(@"[-+]?(?:\d+\.\d*|\.\d+|\d+)(?:[eE][-+]?\d+)?", RegexOptions.CultureInvariant);
    static readonly Regex UrlRef = new(@"url\(\s*['""]?#([^)'""\s]+)", RegexOptions.CultureInvariant);

    static bool Editor(string ns) => ns.Contains("inkscape", StringComparison.OrdinalIgnoreCase) || ns.Contains("sodipodi", StringComparison.OrdinalIgnoreCase)
        || ns.StartsWith("http://ns.adobe.com/", StringComparison.OrdinalIgnoreCase) || ns.Contains("sketch", StringComparison.OrdinalIgnoreCase)
        || ns.StartsWith("http://www.figma.com", StringComparison.OrdinalIgnoreCase);

    // Default decimals: 3 significant digits of the viewBox size. The quality gate then tries 1 and 2 more if needed.
    internal static int AutoDecimals(XDocument doc)
    {
        var root = doc.Root!;
        double size = 0;
        if ((string?)root.Attribute("viewBox") is { } vb && vb.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries) is { Length: 4 } parts
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var vw) && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var vh))
            size = Math.Max(Math.Abs(vw), Math.Abs(vh));
        else if (double.TryParse((string?)root.Attribute("width"), NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
            && double.TryParse((string?)root.Attribute("height"), NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
            size = Math.Max(w, h);
        return size > 0 && double.IsFinite(size) ? Math.Clamp((int)Math.Ceiling(3 - Math.Log10(size)), 0, 6) : 3;
    }

    internal static Result Run(byte[] original, int? precision, bool stripTitle, bool aggressive, bool removeIds)
    {
        var source = ImageAssets.ReadSvg(original);
        var before = Draw(source);
        if (before is null) return new(original, true, "Svg.Skia draws nothing for this SVG, so a result could not be checked. Kept the original.", null, 0);
        var auto = AutoDecimals(source);
        var tries = precision is { } p ? [p] : new[] { auto, auto + 1, auto + 2 }.Select(d => Math.Min(d, 10)).Distinct().ToArray();
        string? reason = null; double mismatch = 0;
        foreach (var decimals in tries)
        {
            var doc = new XDocument(source);
            Optimize(doc, decimals, stripTitle, aggressive, removeIds);
            var bytes = Encoding.UTF8.GetBytes(Serialize(doc));
            var (same, percent) = Compare(before, Draw(doc));
            mismatch = percent;
            if (same)
                return bytes.Length < original.Length ? new(bytes, false, null, decimals, mismatch)
                    : new(original, true, bytes.Length == original.Length ? "Nothing to remove. Kept the original." : "The optimized SVG is not smaller. Kept the original.", decimals, mismatch);
            reason = $"Optimizing with {decimals} decimals changes {mismatch:0.###}% of the pixels";
        }
        return new(original, true, reason + (precision is null ? "" : "; try a higher --precision") + ". Kept the original.", null, mismatch);
    }

    // LINQ to XML writes "<path ... />"; the space before "/>" is never needed.
    internal static string Serialize(XDocument doc)
    {
        var text = doc.ToString(SaveOptions.DisableFormatting);
        return doc.DescendantNodes().OfType<XText>().Any(t => t.Value.Contains("\" />")) ? text : text.Replace("\" />", "\"/>");
    }

    // Two drawings of the SVG: `Fine` at up to 4x (2048 pixels at most on the long side), `Coarse` the same averaged
    // down to 1x. Averaging cancels the sub-pixel steps of Skia's anti-aliasing, which a tiny coordinate change can flip.
    internal sealed record Drawing(byte[] Fine, int FineCount, byte[] Coarse, int CoarseCount);

    internal static Drawing? Draw(XDocument doc)
    {
        SKPicture? picture;
        using var svg = new SKSvg();
        try { using var reader = doc.CreateReader(); picture = svg.Load(reader); }
        catch (Exception) { return null; }
        var bounds = picture?.CullRect ?? SKRect.Empty;
        if (picture is null || !(bounds.Width > 0 && bounds.Height > 0 && float.IsFinite(bounds.Width) && float.IsFinite(bounds.Height))) return null;
        // The 1x drawing has a long side of 256 to 1024 pixels: browsers often show an SVG larger than its own size (an
        // SVG with only a viewBox is 300 pixels wide by default), and the result must look the same there too.
        var longSide = Math.Max(bounds.Width, bounds.Height);
        var baseScale = Math.Clamp(longSide, 256f, 1024f) / longSide;
        int w = Math.Max(1, (int)Math.Ceiling(bounds.Width * baseScale)), h = Math.Max(1, (int)Math.Ceiling(bounds.Height * baseScale));
        var s = Math.Clamp(2048 / Math.Max(w, h), 1, 4);
        using var bitmap = new SKBitmap(w * s, h * s, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(baseScale * s); canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
        }
        var fine = bitmap.Bytes;
        if (!fine.Where((_, i) => i % 4 == 3).Any(a => a != 0)) return null;
        var coarse = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                for (var c = 0; c < 4; c++)
                {
                    var sum = 0;
                    for (var dy = 0; dy < s; dy++) for (var dx = 0; dx < s; dx++) sum += fine[(((y * s + dy) * w * s) + x * s + dx) * 4 + c];
                    coarse[(y * w + x) * 4 + c] = (byte)((sum + s * s / 2) / (s * s));
                }
        return new(fine, w * s * h * s, coarse, w * h);
    }

    // Same when at most 0.05% of the 1x pixels differ by more than 24 of 255 in a channel, and at most 0.1% of the
    // fine pixels by more than 64 (at least one pixel each). The percent is the 1x share.
    internal static (bool Same, double Percent) Compare(Drawing a, Drawing? b)
    {
        if (b is null || a.Fine.Length != b.Fine.Length) return (false, 100);
        static long Count(byte[] x, byte[] y, int limit)
        {
            long bad = 0;
            for (var i = 0; i < x.Length; i += 4)
                if (Math.Abs(x[i] - y[i]) > limit || Math.Abs(x[i + 1] - y[i + 1]) > limit || Math.Abs(x[i + 2] - y[i + 2]) > limit || Math.Abs(x[i + 3] - y[i + 3]) > limit) bad++;
            return bad;
        }
        long coarse = Count(a.Coarse, b.Coarse, 24), fine = Count(a.Fine, b.Fine, 64);
        var same = coarse <= Math.Max(1, a.CoarseCount * 0.0005) && fine <= Math.Max(1, a.FineCount * 0.001);
        return (same, 100.0 * coarse / a.CoarseCount);
    }

    static bool IsSvg(XElement e) => e.Name.Namespace == Ns && !e.Ancestors().Any(a => a.Name == Ns + "foreignObject");
    static string? Attr(XElement e, string name) => (string?)e.Attribute(name);

    // The default keeps what other files and page CSS can use: ids and the definitions that carry them (sprites,
    // `file.svg#icon`), style attributes (they beat page CSS), explicit inherited values such as fill="#000" (page CSS
    // on the svg would reach them otherwise) and element names other than g. `aggressive` drops those too and turns
    // shapes into paths; it is for SVGs used as <img> or a CSS background. `removeIds` drops ids nothing in the file uses.
    internal static void Optimize(XDocument doc, int decimals, bool stripTitle, bool aggressive = false, bool removeIds = false)
    {
        var root = doc.Root!;
        doc.Declaration = null;
        // Comments go, except "<!--!" legal notices.
        doc.DescendantNodes().OfType<XComment>().Where(c => !c.Value.StartsWith('!')).Remove();
        foreach (var e in doc.Descendants().Where(e => e != root && (Editor(e.Name.NamespaceName)
                     || (e.Name == Ns + "metadata") || (stripTitle && (e.Name == Ns + "title" || e.Name == Ns + "desc")))).ToArray()) e.Remove();
        foreach (var e in doc.Descendants())
            foreach (var a in e.Attributes().ToArray())
                if (Editor(a.Name.NamespaceName) || (a.IsNamespaceDeclaration && Editor(a.Value)) || (a.Name.LocalName == "enable-background" && a.Name.Namespace == XNamespace.None)
                    || (a.Value.Trim().Length == 0 && a.Name.Namespace == XNamespace.None && (a.Name.LocalName is "style" or "class" || Presentation.Contains(a.Name.LocalName))))
                    a.Remove();
        foreach (var style in doc.Descendants(Ns + "style")) foreach (var t in style.Nodes().OfType<XText>()) t.Value = Minify.Css(t.Value);

        // Scripts and animation can reach any element or attribute: then only text-level changes are made. A stylesheet
        // can select elements by name, structure or class, so it also keeps the structure and the style attributes,
        // but unused definitions and ids can still go (its #id references count as references).
        var scripted = doc.Descendants().Any(e => e.Name == Ns + "script" || Animation.Contains(e.Name.LocalName))
            || doc.Descendants().Attributes().Any(a => a.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) && a.Name.Namespace == XNamespace.None);
        var structural = !scripted && !doc.Descendants(Ns + "style").Any();
        var markers = doc.Descendants(Ns + "marker").Any() || doc.Descendants().Attributes().Any(a => a.Name.LocalName.StartsWith("marker"));

        // Content that a <use> shows inherits the <use>'s own properties, so an explicit initial value there matters.
        var shown = doc.Descendants(Ns + "use").Select(u => u.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value)
            .Where(h => h?.StartsWith('#') == true).Select(h => h![1..]).ToHashSet();
        bool Shown(XElement e) => e.AncestorsAndSelf().Any(a => Attr(a, "id") is { } id && shown.Contains(id));
        foreach (var e in doc.Descendants().Where(IsSvg).ToArray())
        {
            if (scripted) break;
            foreach (var a in e.Attributes().Where(a => a.Name.Namespace == XNamespace.None && ColorProps.Contains(a.Name.LocalName)).ToArray()) a.Value = Color(a.Value);
            RemoveDefaults(e, inherited: aggressive && structural && !Shown(e), styles: aggressive && structural);
            if (aggressive && structural) StyleToAttributes(e);
        }
        if (structural)
        {
            if (aggressive) foreach (var e in doc.Descendants().Where(IsSvg).ToArray()) ShapeToPath(e, decimals, markers);
            for (var changed = true; changed;)
            {
                changed = CollapseGroups(doc, removeIds);
                changed |= RemoveUnused(doc, removeIds);
            }
            // Group attributes moved down can now be initial values with nothing above them.
            foreach (var e in doc.Descendants().Where(IsSvg).ToArray()) RemoveDefaults(e, inherited: aggressive && !Shown(e), styles: aggressive);
            // Markers take their size and angle from the path's coordinate system and its vertices.
            if (!markers)
            {
                foreach (var e in doc.Descendants(Ns + "path").Where(IsSvg).ToArray()) ApplyTransform(e, decimals, Shown(e));
                MergePaths(doc);
            }
        }
        else if (!scripted) while (RemoveUnused(doc, removeIds)) { }
        // A path morph animation or a script may depend on the exact numbers and commands: then they stay as written.
        foreach (var e in doc.Descendants().Where(IsSvg).Where(_ => !scripted))
        {
            // Filters, masks, patterns, gradients, clips and markers often use fractions of a bounding box.
            var local = e.AncestorsAndSelf().Any(a => Paint.Contains(a.Name.LocalName)) ? Math.Max(decimals, 3) : decimals;
            foreach (var a in e.Attributes().Where(a => a.Name.Namespace == XNamespace.None).ToArray())
            {
                var name = a.Name.LocalName;
                if (name == "d") { if (SvgPath.Parse(a.Value) is { } segs && SvgPath.Write(segs, local) is var d && d.Length <= a.Value.Length) a.Value = d; }
                // Scale and rotation entries multiply coordinates, so transforms keep at least 5 decimals.
                else if (name is "transform" or "gradientTransform" or "patternTransform") a.Value = Round(a.Value, Math.Max(local + 2, 5));
                else if (RatioAttributes.Contains(name)) a.Value = Round(a.Value, Math.Max(local, 3));
                else if (LengthAttributes.Contains(name) && !(e == root && name is "width" or "height" or "x" or "y")) a.Value = Round(a.Value, local);
            }
        }
        // Namespace declarations nothing uses any more (xlink after hrefs went, editor prefixes).
        var used = doc.Descendants().Select(e => e.Name.Namespace).Concat(doc.Descendants().Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => a.Name.Namespace)).ToHashSet();
        foreach (var a in doc.Descendants().Attributes().Where(a => a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.Xmlns && !used.Contains(a.Value)).ToArray()) a.Remove();
        // Whitespace in text/tspan/style is meaningful. Remove only inter-element indentation outside these trees.
        foreach (var t in doc.DescendantNodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)
                     && !t.Ancestors().Any(a => a.Name.LocalName is "text" or "tspan" or "style" or "script" or "title" or "desc" or "foreignObject" || (string?)a.Attribute(XNamespace.Xml + "space") == "preserve")).ToArray()) t.Remove();
    }

    static string Round(string value, int decimals)
    {
        var result = Number.Replace(value, m =>
        {
            if (!double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n)) return m.Value;
            var shortened = SvgPath.Num(n, decimals);
            // Rounding a negative near zero removes its minus, which may have separated two numbers.
            return m.Index > 0 && (char.IsDigit(value[m.Index - 1]) || value[m.Index - 1] == '.') && !shortened.StartsWith('-') ? " " + shortened : shortened;
        });
        return Regex.Replace(result, @"\s+", " ").Trim();
    }

    // Declarations split on ';' outside quotes and parentheses; null when the text is not plain declarations.
    static List<(string Name, string Value)>? Declarations(string style)
    {
        var parts = new List<string>(); var depth = 0; char quote = '\0'; var start = 0;
        for (var i = 0; i < style.Length; i++)
        {
            var c = style[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c; else if (c == '(') depth++; else if (c == ')') depth--;
            else if (c == ';' && depth == 0) { parts.Add(style[start..i]); start = i + 1; }
            else if (c == '/' && i + 1 < style.Length && style[i + 1] == '*') return null;
        }
        parts.Add(style[start..]);
        var result = new List<(string, string)>();
        foreach (var part in parts.Where(p => p.Trim().Length > 0))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0) return null;
            result.Add((part[..colon].Trim().ToLowerInvariant(), part[(colon + 1)..].Trim()));
        }
        return result;
    }

    // style="fill:red" becomes fill="red" when that is shorter. A style beats presentation attributes, so it replaces them.
    static void StyleToAttributes(XElement e)
    {
        if (e.Attribute("style") is not { } style || Declarations(style.Value) is not { } decls) return;
        if (decls.Count == 0) { style.Remove(); return; }
        var movable = decls.Where(d => Presentation.Contains(d.Name) && !d.Value.Contains("!important") && d.Value.Length > 0).ToList();
        var rest = decls.Except(movable).ToList();
        var restText = string.Join(";", rest.Select(d => d.Name + ":" + d.Value));
        var asStyle = 9 + string.Join(";", decls.Select(d => d.Name + ":" + d.Value)).Length;
        var asAttributes = movable.Sum(d => d.Name.Length + d.Value.Length + 4) + (rest.Count > 0 ? 9 + restText.Length : 0);
        if (movable.Count == 0 || asAttributes >= asStyle) { style.Value = string.Join(";", decls.Select(d => d.Name + ":" + Normalize(d))); return; }
        foreach (var d in movable) e.SetAttributeValue(d.Name, Normalize(d));
        if (rest.Count > 0) style.Value = restText; else style.Remove();
        static string Normalize((string Name, string Value) d) => ColorProps.Contains(d.Name) ? Color(d.Value) : d.Value;
    }

    static readonly (Dictionary<string, string> NameToHex, Dictionary<string, string> HexToName) Names = BuildNames();
    static (Dictionary<string, string>, Dictionary<string, string>) BuildNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var hexToName = new Dictionary<string, string>();
        foreach (var known in Enum.GetValues<System.Drawing.KnownColor>())
        {
            var c = System.Drawing.Color.FromKnownColor(known);
            if (c.IsSystemColor || c.A != 255) continue;
            var hex = ShortHex($"#{c.R:x2}{c.G:x2}{c.B:x2}"); var name = c.Name.ToLowerInvariant();
            names[name] = hex;
            if (name.Length < hex.Length && (!hexToName.TryGetValue(hex, out var other) || other.Length > name.Length)) hexToName[hex] = name;
        }
        return (names, hexToName);
    }
    static string ShortHex(string hex) => hex.Length == 7 && hex[1] == hex[2] && hex[3] == hex[4] && hex[5] == hex[6] ? $"#{hex[1]}{hex[3]}{hex[5]}" : hex;
    static readonly Regex Rgb = new(@"^rgb\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*\)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // #ffffff -> #fff, rgb(255,0,0) -> red, white -> #fff. Anything else (currentColor, url(), none) is kept.
    internal static string Color(string value)
    {
        var v = value.Trim(); string? hex = null;
        if (Regex.IsMatch(v, "^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")) hex = v.ToLowerInvariant();
        else if (Rgb.Match(v) is { Success: true } m && m.Groups.Values.Skip(1).All(g => int.Parse(g.Value, CultureInfo.InvariantCulture) <= 255))
            hex = "#" + string.Concat(m.Groups.Values.Skip(1).Select(g => int.Parse(g.Value, CultureInfo.InvariantCulture).ToString("x2")));
        else if (Names.NameToHex.TryGetValue(v, out var named)) hex = named;
        if (hex is null) return value;
        hex = ShortHex(hex);
        return Names.HexToName.TryGetValue(hex, out var name) ? name : hex;
    }

    // The element's own value of a property: its style declaration beats its attribute.
    static string? Prop(XElement e, string name)
    {
        if (Attr(e, "style") is { } style)
        {
            // A style we cannot read counts as setting the property, which keeps every check on the safe side.
            if (Declarations(style) is not { } decls) return style.Contains(name, StringComparison.OrdinalIgnoreCase) ? "?" : Attr(e, name);
            if (decls.LastOrDefault(d => d.Name == name) is { Name: not null } d) return d.Value;
        }
        return Attr(e, name);
    }

    static bool SetAbove(XElement e, string name) => e.Ancestors().Any(a => Prop(a, name) is not null);

    static bool Zero(string? v) => v is not null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n == 0;

    // Initial values: non-inherited ones always go (a stylesheet rule would beat the attribute anyway); inherited ones
    // only when `inherited` is set and nothing above sets the property. Declarations in a style attribute only when
    // `styles` is set: a style attribute beats a stylesheet rule, so with a stylesheet even "opacity:1" matters.
    static void RemoveDefaults(XElement e, bool inherited, bool styles)
    {
        var local = e.Name.LocalName;
        bool Initial(string name, string value) =>
            (InheritedDefaults.TryGetValue(name, out var defaults) && defaults.Contains(ColorProps.Contains(name) ? Color(value) : value) && inherited && !SetAbove(e, name))
            || (name is "opacity" or "stop-opacity" or "flood-opacity" && value == "1") || (name == "display" && value == "inline");
        if (styles && e.Attribute("style") is { } style && Declarations(style.Value) is { } decls && decls.All(d => !d.Value.Contains("!important")))
        {
            var kept = decls.Where(d => !Initial(d.Name, d.Value)).ToList();
            if (kept.Count == 0) style.Remove();
            else if (kept.Count < decls.Count) style.Value = string.Join(";", kept.Select(d => d.Name + ":" + d.Value));
        }
        foreach (var a in e.Attributes().Where(a => a.Name.Namespace == XNamespace.None).ToArray())
        {
            var name = a.Name.LocalName; var value = a.Value.Trim();
            // A style declaration of the same property beats the attribute, so the attribute alone decides nothing then.
            if (Initial(name, value) && Prop(e, name) == a.Value) a.Remove();
            else if (Zero(value) && ((local is "rect" or "use" && name is "x" or "y") || (local is "circle" or "ellipse" && name is "cx" or "cy")
                || (local == "line" && name is "x1" or "y1" or "x2" or "y2"))) a.Remove();
            else if (e.Parent is null && (name is "x" or "y" && Zero(value) || name == "preserveAspectRatio" && value == "xMidYMid meet" || name == "version")) a.Remove();
        }
    }

    static bool Plain(string? v, out double n)
    {
        n = 0;
        return v is null || (double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out n) && double.IsFinite(n));
    }

    static void ShapeToPath(XElement e, int decimals, bool markers)
    {
        // Markers apply to paths but not to rectangles.
        if (markers && e.Name.LocalName == "rect") return;
        List<SvgPath.Seg>? segs = null; string[] remove;
        switch (e.Name.LocalName)
        {
            case "rect":
                remove = ["x", "y", "width", "height"];
                if (e.Attribute("rx") is not null || e.Attribute("ry") is not null) return;
                if (!Plain(Attr(e, "x"), out var x) || !Plain(Attr(e, "y"), out var y) || !Plain(Attr(e, "width"), out var w) || !Plain(Attr(e, "height"), out var h)
                    || Attr(e, "width") is null || Attr(e, "height") is null || w <= 0 || h <= 0) return;
                segs = [new('M', [x, y]), new('L', [x + w, y]), new('L', [x + w, y + h]), new('L', [x, y + h]), new('Z', [x, y])];
                break;
            case "line":
                remove = ["x1", "y1", "x2", "y2"];
                if (!Plain(Attr(e, "x1"), out var x1) || !Plain(Attr(e, "y1"), out var y1) || !Plain(Attr(e, "x2"), out var x2) || !Plain(Attr(e, "y2"), out var y2)) return;
                segs = [new('M', [x1, y1]), new('L', [x2, y2])];
                break;
            case "polyline" or "polygon":
                remove = ["points"];
                var nums = Number.Matches(Attr(e, "points") ?? "").Select(m => double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
                if (nums.Length < 4 || nums.Length % 2 != 0 || Regex.IsMatch(Attr(e, "points")!, @"[^\d\s,.eE+-]")) return;
                segs = [new('M', [nums[0], nums[1]])];
                for (var i = 2; i < nums.Length; i += 2) segs.Add(new('L', [nums[i], nums[i + 1]]));
                if (e.Name.LocalName == "polygon") segs.Add(new('Z', [nums[0], nums[1]]));
                break;
            default: return;
        }
        var d = SvgPath.Write(segs, decimals);
        var tags = e.Nodes().Any() ? 2 : 1;
        var before = remove.Sum(n => e.Attribute(n) is { } a ? n.Length + Round(a.Value, decimals).Length + 4 : 0) + e.Name.LocalName.Length * tags;
        if (d.Length + 5 + 4 * tags >= before) return;
        foreach (var n in remove) e.Attribute(n)?.Remove();
        e.Name = Ns + "path";
        e.SetAttributeValue("d", d);
    }

    static bool HasTitle(XElement e) => e.Elements().Any(c => c.Name == Ns + "title" || c.Name == Ns + "desc");

    static bool CollapseGroups(XDocument doc, bool removeIds)
    {
        var changed = false; var refs = Referenced(doc);
        foreach (var g in doc.Descendants(Ns + "g").Where(IsSvg).Reverse().ToArray())
        {
            // A switch picks one child; a clipPath may not hold groups at all, so renderers skip them: both stay as they are.
            if (g.Parent is null || g.Parent.Name == Ns + "switch" || g.Ancestors(Ns + "clipPath").Any() || HasTitle(g)) continue;
            var attrs = g.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList();
            var children = g.Elements().ToList();
            if (children.Count == 0 && g.Nodes().All(n => n is XText t && string.IsNullOrWhiteSpace(t.Value)) && g.Attribute("filter") is null
                && (Attr(g, "id") is null || (removeIds && !refs.Contains(Attr(g, "id")!))))
            { g.Remove(); changed = true; continue; }
            if (g.Nodes().Any(n => n is XText t && !string.IsNullOrWhiteSpace(t.Value))) continue;
            if (attrs.Count == 0) { g.ReplaceWith(g.Nodes()); changed = true; continue; }
            if (children.Count != 1 || !IsSvg(children[0]) || children[0].Name == Ns + "title" || children[0].Name == Ns + "desc") continue;
            if (!attrs.All(a => a.Name.Namespace == XNamespace.None && (a.Name.LocalName == "transform" || Inherited.Contains(a.Name.LocalName)))) continue;
            var child = children[0];
            // A nested <svg> takes no transform in SVG 1.1; a transform-origin or transform-box on the child would apply to
            // the group's transform as well once it moves down.
            if (g.Attribute("transform") is not null && (child.Name == Ns + "svg" || HasOrigin(child))) continue;
            // The child's own style attribute would beat a pushed-down attribute only for the same property; check both forms.
            if (Attr(child, "style") is { } childStyle && attrs.Any(a => childStyle.Contains(a.Name.LocalName, StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var a in attrs)
            {
                if (a.Name.LocalName == "transform")
                    child.SetAttributeValue("transform", (a.Value.Trim() + " " + (Attr(child, "transform") ?? "")).Trim());
                else if (child.Attribute(a.Name) is null) child.SetAttributeValue(a.Name, a.Value);
            }
            g.ReplaceWith(child);
            changed = true;
        }
        return changed;
    }

    internal static HashSet<string> Referenced(XDocument doc)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in doc.Descendants().Attributes())
        {
            foreach (Match m in UrlRef.Matches(a.Value)) ids.Add(m.Groups[1].Value);
            var name = a.Name.LocalName;
            if (name == "href" && a.Value.StartsWith('#')) ids.Add(a.Value[1..]);
            if (name is "aria-labelledby" or "aria-describedby" or "aria-controls" or "aria-owns" or "aria-activedescendant" or "aria-flowto" or "begin" or "end" or "for")
                foreach (var token in a.Value.Split([' ', ';', '\t', '\n', '\r', '+', '-'], StringSplitOptions.RemoveEmptyEntries)) ids.Add(token.Split('.')[0]);
        }
        foreach (var t in doc.DescendantNodes().OfType<XText>().Where(t => t.Parent?.Name.LocalName is "style" or "script"))
            foreach (Match m in Regex.Matches(t.Value, @"#([A-Za-z_][\w-]*)")) ids.Add(m.Groups[1].Value);
        return ids;
    }

    // Definitions nothing can reach. By default anything with an id stays, because another file or page may use it
    // (`file.svg#icon`, <use href="sprite.svg#icon">, a :target stack). With `removeIds`, unreferenced definitions and
    // ids go too, except symbol and view ids.
    static bool RemoveUnused(XDocument doc, bool removeIds)
    {
        var changed = false;
        var refs = Referenced(doc);
        bool Used(XElement e) => e.DescendantsAndSelf().Any(d => Attr(d, "id") is { } id && (!removeIds || refs.Contains(id)));
        foreach (var e in doc.Descendants().Where(IsSvg).ToArray())
        {
            if (e.Parent is null) continue;
            var inDefs = e.Parent.Name == Ns + "defs";
            // Fonts and colour profiles are found by name, not by id.
            if ((inDefs && e.Name.LocalName is not ("style" or "script" or "symbol" or "font" or "font-face" or "color-profile") || Paint.Contains(e.Name.LocalName)) && !Used(e))
            { e.Remove(); changed = true; }
        }
        foreach (var defs in doc.Descendants(Ns + "defs").Where(d => !d.HasElements && d.Attribute("id") is null).ToArray()) { defs.Remove(); changed = true; }
        if (!removeIds) return changed;
        refs = Referenced(doc);
        foreach (var e in doc.Descendants().Where(IsSvg))
            if (e.Attribute("id") is { } id && !refs.Contains(id.Value) && e.Parent is not null && e.Name.LocalName is not ("symbol" or "view"))
            { id.Remove(); changed = true; }
        return changed;
    }

    static double[]? Matrix(string transform)
    {
        double[] m = [1, 0, 0, 1, 0, 0];
        var rest = transform.Trim();
        foreach (Match f in Regex.Matches(transform, @"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)\s*,?\s*"))
        {
            var v = Number.Matches(f.Groups[2].Value).Select(x => double.Parse(x.Value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
            double[] t;
            switch (f.Groups[1].Value)
            {
                case "matrix" when v.Length == 6: t = v; break;
                case "translate" when v.Length is 1 or 2: t = [1, 0, 0, 1, v[0], v.Length == 2 ? v[1] : 0]; break;
                case "scale" when v.Length is 1 or 2: t = [v[0], 0, 0, v.Length == 2 ? v[1] : v[0], 0, 0]; break;
                case "rotate" when v.Length is 1 or 3:
                    var r = v[0] * Math.PI / 180; double c = Math.Cos(r), s = Math.Sin(r);
                    t = [c, s, -s, c, 0, 0];
                    if (v.Length == 3) t = Multiply(Multiply([1, 0, 0, 1, v[1], v[2]], t), [1, 0, 0, 1, -v[1], -v[2]]);
                    break;
                case "skewX" when v.Length == 1: t = [1, 0, Math.Tan(v[0] * Math.PI / 180), 1, 0, 0]; break;
                case "skewY" when v.Length == 1: t = [1, Math.Tan(v[0] * Math.PI / 180), 0, 1, 0, 0]; break;
                default: return null;
            }
            m = Multiply(m, t);
            rest = rest.Replace(f.Value.Trim(), "");
        }
        return rest.Trim(' ', ',').Length == 0 && m.All(double.IsFinite) ? m : null;
    }
    static double[] Multiply(double[] a, double[] b) =>
        [a[0] * b[0] + a[2] * b[1], a[1] * b[0] + a[3] * b[1], a[0] * b[2] + a[2] * b[3], a[1] * b[2] + a[3] * b[3],
         a[0] * b[4] + a[2] * b[5] + a[4], a[1] * b[4] + a[3] * b[5] + a[5]];

    static bool HasOrigin(XElement e) => e.Attribute("transform-origin") is not null || e.Attribute("transform-box") is not null
        || Attr(e, "style")?.Contains("transform-", StringComparison.OrdinalIgnoreCase) == true;

    static bool Stroked(XElement e) => e.AncestorsAndSelf().Select(a => Prop(a, "stroke")).FirstOrDefault(s => s is not null) is { } s && s.Trim() != "none";

    // Moves a transform into the path data when nothing depends on the coordinate system: no paint servers, clips,
    // masks, filters or markers; a stroke only allows a translation, and arcs only a translation or uniform scale.
    // A <use> may give shown content its own stroke, so a shown path also takes a translation only. transform-origin and
    // transform-box change what the transform means, so such a path keeps it.
    static void ApplyTransform(XElement e, int decimals, bool shown)
    {
        if (Attr(e, "transform") is not { } transform || Attr(e, "d") is not { } d || e.Attribute("style") is not null || HasOrigin(e)) return;
        if (e.Attributes().Any(a => a.Value.Contains("url(") || a.Name.LocalName is "clip-path" or "mask" or "filter" || a.Name.LocalName.StartsWith("marker"))) return;
        if (e.Ancestors().Any(a => Prop(a, "fill")?.Contains("url(") == true || Prop(a, "stroke")?.Contains("url(") == true)) return;
        if (Matrix(transform) is not { } m || SvgPath.Parse(d) is not { } segs) return;
        var translateOnly = m[0] == 1 && m[1] == 0 && m[2] == 0 && m[3] == 1;
        var uniform = m[1] == 0 && m[2] == 0 && m[0] == m[3] && m[0] > 0;
        if ((Stroked(e) || shown) && !translateOnly) return;
        if (segs.Any(s => s.Kind == 'A') && !uniform) return;
        (double, double) P(double x, double y) => (m[0] * x + m[2] * y + m[4], m[1] * x + m[3] * y + m[5]);
        var moved = segs.Select(s =>
        {
            if (s.Kind == 'A')
            {
                var (x, y) = P(s.P[5], s.P[6]);
                return new SvgPath.Seg('A', [s.P[0] * m[0], s.P[1] * m[0], s.P[2], s.P[3], s.P[4], x, y]);
            }
            var p = new double[s.P.Length];
            for (var i = 0; i < p.Length; i += 2) (p[i], p[i + 1]) = P(s.P[i], s.P[i + 1]);
            return new SvgPath.Seg(s.Kind, p);
        }).ToList();
        var newD = SvgPath.Write(moved, decimals);
        if (newD.Length < SvgPath.Write(segs, decimals).Length + transform.Length + 12)
        {
            e.SetAttributeValue("d", newD);
            e.Attribute("transform")!.Remove();
        }
    }

    static (double X0, double Y0, double X1, double Y1) Box(List<SvgPath.Seg> segs)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, cx = 0, cy = 0;
        foreach (var s in segs)
        {
            if (s.Kind == 'A')
            {
                // Radii too small to reach the end point are scaled up by the renderer, so the chord bounds them too.
                var r = Math.Max(Math.Max(s.P[0], s.P[1]), Math.Sqrt((s.P[5] - cx) * (s.P[5] - cx) + (s.P[6] - cy) * (s.P[6] - cy)));
                x0 = Math.Min(x0, Math.Min(cx, s.P[5]) - 2 * r); x1 = Math.Max(x1, Math.Max(cx, s.P[5]) + 2 * r);
                y0 = Math.Min(y0, Math.Min(cy, s.P[6]) - 2 * r); y1 = Math.Max(y1, Math.Max(cy, s.P[6]) + 2 * r);
                (cx, cy) = (s.X, s.Y);
                continue;
            }
            (cx, cy) = (s.X, s.Y);
            for (var i = 0; i < s.P.Length; i += 2) { x0 = Math.Min(x0, s.P[i]); x1 = Math.Max(x1, s.P[i]); y0 = Math.Min(y0, s.P[i + 1]); y1 = Math.Max(y1, s.P[i + 1]); }
        }
        return (x0, y0, x1, y1);
    }

    // Neighbouring paths with the same attributes become one path when their boxes do not touch, so neither the fill
    // rule nor transparency can see the difference. Stroked paths stay apart.
    static void MergePaths(XDocument doc)
    {
        foreach (var parent in doc.Descendants().Where(IsSvg).ToArray())
        {
            XElement? current = null; List<SvgPath.Seg>? merged = null; var boxes = new List<(double, double, double, double)>();
            foreach (var node in parent.Nodes().ToArray())
            {
                if (node is XText t && string.IsNullOrWhiteSpace(t.Value)) continue;
                if (node is not XElement e || e.Name != Ns + "path" || e.Attribute("id") is not null || Stroked(e)
                    || e.Attributes().Any(a => a.Value.Contains("url(")) || e.HasElements || SvgPath.Parse(Attr(e, "d") ?? "") is not { Count: > 0 } segs)
                { current = null; continue; }
                var box = Box(segs);
                if (current is not null && Same(current, e) && boxes.All(b => box.X0 > b.Item3 || box.X1 < b.Item1 || box.Y0 > b.Item4 || box.Y1 < b.Item2))
                {
                    merged!.AddRange(segs); boxes.Add(box);
                    current.SetAttributeValue("d", SvgPath.Write(merged, 12));
                    e.Remove();
                    continue;
                }
                current = e; merged = segs; boxes = [box];
            }
        }
        static bool Same(XElement a, XElement b)
        {
            var aa = a.Attributes().Where(x => x.Name.LocalName != "d").Select(x => (x.Name, x.Value)).OrderBy(x => x.Name.ToString()).ToList();
            var bb = b.Attributes().Where(x => x.Name.LocalName != "d").Select(x => (x.Name, x.Value)).OrderBy(x => x.Name.ToString()).ToList();
            return aa.SequenceEqual(bb);
        }
    }
}
