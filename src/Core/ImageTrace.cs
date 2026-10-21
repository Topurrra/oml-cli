using System.Xml.Linq;
using SkiaSharp;

namespace OmlCli;

// Raster to vector tracing for logos and icons. Our own code (no Potrace or other GPL code):
//  1. palette: colours of flat areas, added one by one until every pixel is a palette colour or a mix of two
//     (anti-aliased edges are mixes, so they never become colours of their own), refined by k-means;
//  2. layers: one per colour, largest area first; each layer also reaches up to 2 pixels under the layers drawn after
//     it (not at their outer edges), so neighbouring colours overlap instead of meeting edge to edge and no gap shows;
//  3. contours: marching squares on each layer's soft coverage (alpha times colour weight), which places edges between
//     pixels from the anti-aliasing, so straight and round edges come out smooth instead of as stairs;
//  4. speckles under --min-area go, then corners are found by the turning angle and the parts between corners are
//     fitted with lines and cubic Béziers within --tolerance pixels (least squares with Newton reparametrization).
static class ImageTrace
{
    internal sealed record Options(int Colors, double? Tolerance, double? MinArea, int AlphaThreshold, bool TransparentBackground = false);
    internal sealed record Result(XDocument Svg, int Colors, int Paths, string? Note, string? RemovedBackground);

    const double ColorDistance = 36; // a pixel this close (RGB distance) to a palette colour or a mix of two is explained
    const double CornerDegrees = 55;

    internal static Result Trace(SKBitmap bitmap, Options o)
    {
        int w = bitmap.Width, h = bitmap.Height, n = w * h;
        var px = ImageIco.Pixels(bitmap, SKColorType.Rgba8888);
        int T = o.AlphaThreshold;
        var opaque = new bool[n]; var opaqueCount = 0;
        for (var i = 0; i < n; i++) if (opaque[i] = px[i * 4 + 3] >= T) opaqueCount++;
        if (opaqueCount == 0) throw new InvalidDataException($"The image has no pixels with alpha {T} or more, so there is nothing to trace.");

        // Flat pixels: opaque and like all four neighbours. Anti-aliased edges are not flat.
        var flat = new bool[n];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x; if (!opaque[i]) continue;
                bool Like(int j) => opaque[j] && Math.Max(Math.Abs(px[i * 4] - px[j * 4]), Math.Max(Math.Abs(px[i * 4 + 1] - px[j * 4 + 1]), Math.Abs(px[i * 4 + 2] - px[j * 4 + 2]))) <= 8;
                flat[i] = (x == 0 || Like(i - 1)) && (x == w - 1 || Like(i + 1)) && (y == 0 || Like(i - w)) && (y == h - 1 || Like(i + w));
            }

        // Distinct colours with counts; too many distinct colours (photos) fall back to 5-bit buckets.
        var (colors, flatCount, allCount, entryOf) = Histogram(px, opaque, flat, n, 0);
        if (colors.Count > 65536) (colors, flatCount, allCount, entryOf) = Histogram(px, opaque, flat, n, 3);
        var flatTotal = flatCount.Sum();

        var max = o.Colors > 0 ? o.Colors : 16;
        var palette = new List<double[]>();
        double unexplained = 0;
        while (true)
        {
            unexplained = 0;
            var buckets = new Dictionary<int, (double r, double g, double b, double mass)>();
            for (var e = 0; e < colors.Count; e++)
            {
                var c = colors[e];
                var point = palette.Count == 0 ? double.MaxValue : palette.Min(p => Dist(c, p));
                var mix = Math.Min(point, MixDistance(c, palette).dist);
                double mass = (point > ColorDistance ? flatCount[e] : 0) + (mix > ColorDistance ? allCount[e] - flatCount[e] : 0);
                if (mass == 0) continue;
                unexplained += mass;
                var key = ((int)c[0] >> 4 << 8) | ((int)c[1] >> 4 << 4) | ((int)c[2] >> 4);
                var bucket = buckets.GetValueOrDefault(key);
                // Flat pixels count more: a colour of its own fills areas, a mix only lines edges.
                var weight = flatCount[e] * 4 + (allCount[e] - flatCount[e]);
                buckets[key] = (bucket.r + c[0] * weight, bucket.g + c[1] * weight, bucket.b + c[2] * weight, bucket.mass + weight);
            }
            var enough = palette.Count > 0 && (o.Colors > 0 ? palette.Count >= max : palette.Count >= max || unexplained <= Math.Max(8, opaqueCount * 0.002));
            if (enough || buckets.Count == 0) break;
            var best = buckets.Values.MaxBy(b => b.mass);
            palette.Add([best.r / best.mass, best.g / best.mass, best.b / best.mass]);
            KMeans(palette, colors, flatCount, allCount, flatTotal > 0);
        }

        // Photos and gradients: interior (flat) pixels far from every palette colour, or almost no flat areas at all.
        double flatError = 0; var flatPixels = flatCount.Sum();
        for (var e = 0; e < colors.Count; e++) if (flatCount[e] > 0) flatError += flatCount[e] * palette.Min(p => Dist(colors[e], p));
        flatError = flatPixels == 0 ? 0 : flatError / flatPixels;
        var flatShare = (double)flatPixels / opaqueCount;
        var notes = new List<string>();
        if ((opaqueCount >= 4096 && flatShare < 0.25) || flatError > 6 || (o.Colors == 0 && unexplained > opaqueCount * 0.02))
            notes.Add("This looks like a photo or a gradient (smooth shading that flat colour areas cannot match). Tracing is meant for logos and icons; for photos use --mode embed.");
        // Semi-transparent areas (shadows, glass) are not just the soft rim of a shape: alpha there is even, not changing.
        long visible = 0, translucent = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var a = px[(y * w + x) * 4 + 3];
                if (a == 0) continue;
                visible++;
                if (a is < 16 or > 239) continue;
                bool Even(int j) => Math.Abs(px[j * 4 + 3] - a) <= 24;
                if ((x == 0 || Even(y * w + x - 1)) && (x == w - 1 || Even(y * w + x + 1)) && (y == 0 || Even((y - 1) * w + x)) && (y == h - 1 || Even((y + 1) * w + x))) translucent++;
            }
        if (translucent > Math.Max(64, visible * 0.05))
            notes.Add($"{100.0 * translucent / visible:0.#}% of the visible area is semi-transparent (a shadow or a see-through shape). Tracing makes such areas solid (alpha {T} or more) or leaves them out; --alpha-threshold moves the line.");

        // Each pixel: a palette colour (flat pixels), or a mix of two with weight t for the second.
        var k = palette.Count;
        var expA = new int[colors.Count]; var expB = new int[colors.Count]; var expT = new double[colors.Count]; var pointOf = new int[colors.Count];
        for (var e = 0; e < colors.Count; e++)
        {
            var c = colors[e];
            pointOf[e] = Enumerable.Range(0, k).MinBy(p => Dist(c, palette[p]));
            var (dist, a, b, t) = MixDistance(c, palette);
            if (a >= 0 && dist + 4 < Dist(c, palette[pointOf[e]])) (expA[e], expB[e], expT[e]) = (a, b, t);
            else (expA[e], expB[e], expT[e]) = (pointOf[e], pointOf[e], 0);
        }
        var area = new long[k];
        for (var i = 0; i < n; i++) if (opaque[i]) area[pointOf[entryOf[i]]]++;
        var order = Enumerable.Range(0, k).OrderByDescending(p => area[p]).ToArray();
        // --transparent-bg: the colour that fills at least half of the border and touches all four edges is drawn first
        // and then left out, so the logo stands on a transparent background.
        string? removedBackground = null;
        if (o.TransparentBackground)
        {
            var border = new long[k]; var edges = new int[k]; long borderPixels = 0;
            for (var side = 0; side < 4; side++)
            {
                var seen = new bool[k];
                var count = side < 2 ? w : h;
                for (var j = 0; j < count; j++)
                {
                    var i = side switch { 0 => j, 1 => (h - 1) * w + j, 2 => j * w, _ => j * w + w - 1 };
                    borderPixels++;
                    if (!opaque[i]) continue;
                    var label = pointOf[entryOf[i]]; border[label]++; seen[label] = true;
                }
                for (var p = 0; p < k; p++) if (seen[p]) edges[p]++;
            }
            var bg = Enumerable.Range(0, k).MaxBy(p => border[p]);
            if (border[bg] * 2 >= borderPixels && edges[bg] == 4 && k > 1)
            {
                order = [bg, .. order.Where(p => p != bg)];
                var c = palette[bg];
                removedBackground = $"#{(int)Math.Round(c[0]):x2}{(int)Math.Round(c[1]):x2}{(int)Math.Round(c[2]):x2}";
            }
            else notes.Add("--transparent-bg: no colour fills at least half of the border on all four edges, so nothing was left out.");
        }
        var rank = new int[k]; for (var r = 0; r < k; r++) rank[order[r]] = r;

        var minArea = o.MinArea ?? Math.Max(4, n / 50000.0);
        var root = new XElement(Ns + "svg", new XAttribute("viewBox", $"0 0 {w} {h}"), new XAttribute("width", w), new XAttribute("height", h));
        // Without anti-aliasing (every opaque pixel is a palette colour) outlines are pixel stairs: smooth them more and
        // allow a wider default tolerance, so straight edges come out straight.
        var softAlpha = false;
        for (var i = 0; i < n && !softAlpha; i++) softAlpha = px[i * 4 + 3] is > 0 and < 255;
        var hard = colors.Count <= k && !softAlpha;
        var tolerance = o.Tolerance ?? (hard ? 0.6 : 0.3);
        // Cover 0.5 at exactly the threshold alpha, which counts as opaque (contours take 0.5 as inside).
        var cover = new float[n];
        for (var i = 0; i < n; i++) cover[i] = Math.Min(1f, px[i * 4 + 3] / (2f * T));
        // Layer r covers its own pixels, and reaches under higher layers only where layers r and up cover a whole 3x3
        // neighbourhood, and only up to 2 pixels from its own pixels. Where a higher layer meets the background or a
        // lower layer, its anti-aliased edge then lies over what it really borders, never over a layer in between (a
        // halo), and a lower layer never grows shapes of its own inside a higher one (blobs in thin letters).
        var own = new float[n]; var stack = new float[n]; var under = new float[n]; var near = new float[n]; var field = new float[n];
        var paths = 0; var next = new int[2 * (w + 2) * (h + 2)];
        for (var r = 0; r < k; r++)
        {
            for (var i = 0; i < n; i++)
            {
                if (cover[i] == 0) { own[i] = stack[i] = 0; continue; }
                var e = entryOf[i];
                int la = rank[expA[e]], lb = rank[expB[e]];
                double mine = (la == r ? 1 - expT[e] : 0) + (lb == r ? expT[e] : 0), up = (la >= r ? 1 - expT[e] : 0) + (lb >= r ? expT[e] : 0);
                own[i] = (float)(cover[i] * mine); stack[i] = (float)(cover[i] * up);
            }
            Erode(stack, under, field, w, h);
            Dilate(own, near, field, w, h);
            for (var i = 0; i < n; i++) field[i] = Math.Max(own[i], Math.Min(under[i], near[i]));
            var segs = new List<SvgPath.Seg>();
            foreach (var loop in Contours(field, w, h, next))
            {
                if (Math.Abs(Area(loop)) < minArea) continue;
                Fit(loop, tolerance, hard ? 4 : 2, segs);
            }
            if (segs.Count == 0 || (r == 0 && removedBackground is not null)) continue;
            var color = palette[order[r]];
            root.Add(new XElement(Ns + "path", new XAttribute("fill", $"#{(int)Math.Round(color[0]):x2}{(int)Math.Round(color[1]):x2}{(int)Math.Round(color[2]):x2}"),
                new XAttribute("d", SvgPath.Write(segs, 3))));
            paths++;
        }
        if (paths == 0)
            throw new InvalidDataException(removedBackground is not null ? "Only the background was found; nothing is left without it."
                : $"Every shape is smaller than --min-area ({minArea:0.##} square pixels), so nothing was traced. Lower --min-area.");
        return new(new XDocument(root), k, paths, notes.Count == 0 ? null : string.Join(" ", notes), removedBackground);
    }

    static readonly XNamespace Ns = "http://www.w3.org/2000/svg";

    // 5x5 maximum (rows, then columns): 2 pixels around. `scratch` is a buffer of the same size.
    static void Dilate(float[] source, float[] result, float[] scratch, int w, int h)
    {
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var m = 0f;
                for (var d = Math.Max(0, x - 2); d <= Math.Min(w - 1, x + 2); d++) m = Math.Max(m, source[y * w + d]);
                scratch[y * w + x] = m;
            }
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var m = 0f;
                for (var d = Math.Max(0, y - 2); d <= Math.Min(h - 1, y + 2); d++) m = Math.Max(m, scratch[d * w + x]);
                result[y * w + x] = m;
            }
    }

    // 3x3 minimum (rows, then columns), edges clamped. `scratch` is a buffer of the same size.
    static void Erode(float[] source, float[] result, float[] scratch, int w, int h)
    {
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                scratch[i] = Math.Min(source[i], Math.Min(source[y * w + Math.Max(0, x - 1)], source[y * w + Math.Min(w - 1, x + 1)]));
            }
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                result[y * w + x] = Math.Min(scratch[y * w + x], Math.Min(scratch[Math.Max(0, y - 1) * w + x], scratch[Math.Min(h - 1, y + 1) * w + x]));
    }

    static (List<double[]> colors, List<int> flat, List<int> all, int[] entryOf) Histogram(byte[] px, bool[] opaque, bool[] flat, int n, int shift)
    {
        var index = new Dictionary<int, int>(); var colors = new List<double[]>(); var flatCount = new List<int>(); var allCount = new List<int>();
        var sums = new List<double[]>(); var entryOf = new int[n];
        for (var i = 0; i < n; i++)
        {
            if (px[i * 4 + 3] == 0) continue;
            var key = ((px[i * 4] >> shift) << 16) | ((px[i * 4 + 1] >> shift) << 8) | (px[i * 4 + 2] >> shift);
            if (!index.TryGetValue(key, out var e)) { e = index[key] = colors.Count; colors.Add(new double[3]); sums.Add(new double[3]); flatCount.Add(0); allCount.Add(0); }
            entryOf[i] = e;
            if (!opaque[i]) continue;
            sums[e][0] += px[i * 4]; sums[e][1] += px[i * 4 + 1]; sums[e][2] += px[i * 4 + 2];
            allCount[e]++; if (flat[i]) flatCount[e]++;
        }
        // Entries seen only in pixels under the alpha threshold keep their bucket colour; they do not count.
        foreach (var (key, e) in index)
            colors[e] = allCount[e] > 0 ? [sums[e][0] / allCount[e], sums[e][1] / allCount[e], sums[e][2] / allCount[e]]
                : [(key >> 16 & 255) << shift, (key >> 8 & 255) << shift, (key & 255) << shift];
        return (colors, flatCount, allCount, entryOf);
    }

    static double Dist(double[] a, double[] b)
    {
        double dr = a[0] - b[0], dg = a[1] - b[1], db = a[2] - b[2];
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    // Distance from c to the nearest segment between two palette colours, and where on it c lies.
    static (double dist, int a, int b, double t) MixDistance(double[] c, List<double[]> palette)
    {
        (double dist, int a, int b, double t) best = (double.MaxValue, -1, -1, 0);
        for (var a = 0; a < palette.Count; a++)
            for (var b = a + 1; b < palette.Count; b++)
            {
                double[] pa = palette[a], pb = palette[b];
                double vx = pb[0] - pa[0], vy = pb[1] - pa[1], vz = pb[2] - pa[2], len = vx * vx + vy * vy + vz * vz;
                if (len == 0) continue;
                var t = Math.Clamp(((c[0] - pa[0]) * vx + (c[1] - pa[1]) * vy + (c[2] - pa[2]) * vz) / len, 0, 1);
                double dx = pa[0] + vx * t - c[0], dy = pa[1] + vy * t - c[1], dz = pa[2] + vz * t - c[2];
                var d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d < best.dist) best = (d, a, b, t);
            }
        return best;
    }

    // Lloyd iterations over the flat colours (all colours when nothing is flat).
    static void KMeans(List<double[]> palette, List<double[]> colors, List<int> flat, List<int> all, bool useFlat)
    {
        for (var iteration = 0; iteration < 6; iteration++)
        {
            var sums = palette.Select(_ => new double[4]).ToArray();
            for (var e = 0; e < colors.Count; e++)
            {
                double weight = useFlat ? flat[e] : all[e];
                if (weight == 0) continue;
                var c = colors[e]; var best = 0; var bestD = double.MaxValue;
                for (var p = 0; p < palette.Count; p++) { var d = Dist(c, palette[p]); if (d < bestD) (best, bestD) = (p, d); }
                sums[best][0] += c[0] * weight; sums[best][1] += c[1] * weight; sums[best][2] += c[2] * weight; sums[best][3] += weight;
            }
            for (var p = 0; p < palette.Count; p++)
                if (sums[p][3] > 0) palette[p] = [sums[p][0] / sums[p][3], sums[p][1] / sums[p][3], sums[p][2] / sums[p][3]];
        }
    }

    static double Area(List<(double X, double Y)> loop)
    {
        double a = 0;
        for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++) a += (loop[j].X + loop[i].X) * (loop[j].Y - loop[i].Y);
        return a / 2;
    }

    // Marching squares at 0.5 over pixel centres, with a border of zeros so every contour closes. Each crossing on a
    // grid edge starts exactly one segment and ends one, all with the inside on the same side, so outlines and holes
    // come out with opposite directions and the nonzero fill rule draws holes as holes.
    // `next` is a reusable buffer of 2 x (w + 2) x (h + 2) entries.
    static IEnumerable<List<(double X, double Y)>> Contours(float[] f, int w, int h, int[] next)
    {
        int gw = w + 2, gh = h + 2;
        double F(int gx, int gy) => gx < 1 || gy < 1 || gx > w || gy > h ? 0 : f[(gy - 1) * w + gx - 1];
        Array.Fill(next, -1);
        int H(int gx, int gy) => 2 * (gy * gw + gx); int V(int gx, int gy) => 2 * (gy * gw + gx) + 1;
        Span<double> v = stackalloc double[4]; Span<int> ids = stackalloc int[4]; Span<int> cross = stackalloc int[4]; Span<bool> enter = stackalloc bool[4];
        for (var gy = 0; gy < gh - 1; gy++)
            for (var gx = 0; gx < gw - 1; gx++)
            {
                v[0] = F(gx, gy); v[1] = F(gx + 1, gy); v[2] = F(gx + 1, gy + 1); v[3] = F(gx, gy + 1);
                var bits = (v[0] >= 0.5 ? 1 : 0) | (v[1] >= 0.5 ? 2 : 0) | (v[2] >= 0.5 ? 4 : 0) | (v[3] >= 0.5 ? 8 : 0);
                if (bits is 0 or 15) continue;
                ids[0] = H(gx, gy); ids[1] = V(gx + 1, gy); ids[2] = H(gx, gy + 1); ids[3] = V(gx, gy);
                var count = 0;
                for (var e = 0; e < 4; e++)
                {
                    bool a = v[e] >= 0.5, b = v[(e + 1) % 4] >= 0.5;
                    if (a != b) { cross[count] = ids[e]; enter[count] = !a; count++; }
                }
                var connected = count == 4 && (v[0] + v[1] + v[2] + v[3]) / 4 >= 0.5;
                for (var c = 0; c < count; c++)
                    if (enter[c]) next[cross[c]] = cross[connected ? (c + count - 1) % count : (c + 1) % count];
            }
        (double, double) Point(int id)
        {
            var cell = id >> 1; int gx = cell % gw, gy = cell / gw;
            if ((id & 1) == 0) { double a = F(gx, gy), b = F(gx + 1, gy); return (gx + (0.5 - a) / (b - a) - 0.5, gy - 0.5); }
            else { double a = F(gx, gy), b = F(gx, gy + 1); return (gx - 0.5, gy + (0.5 - a) / (b - a) - 0.5); }
        }
        for (var start = 0; start < next.Length; start++)
        {
            if (next[start] < 0) continue;
            var loop = new List<(double X, double Y)>();
            for (var id = start; next[id] >= 0;)
            {
                loop.Add(Point(id));
                var after = next[id]; next[id] = -1; id = after;
            }
            yield return loop;
        }
    }

    // Splits a closed outline at its corners and fits each part; appends M ... Z.
    static void Fit(List<(double X, double Y)> raw, double tolerance, int smoothing, List<SvgPath.Seg> segs)
    {
        var p = new List<(double X, double Y)>(raw.Count);
        foreach (var q in raw) if (p.Count == 0 || Math.Abs(q.X - p[^1].X) + Math.Abs(q.Y - p[^1].Y) > 1e-6) p.Add(q);
        while (p.Count > 1 && Math.Abs(p[0].X - p[^1].X) + Math.Abs(p[0].Y - p[^1].Y) <= 1e-6) p.RemoveAt(p.Count - 1);
        var n = p.Count;
        if (n < 3) return;
        var arc = new double[n + 1];
        for (var i = 1; i <= n; i++) arc[i] = arc[i - 1] + Len(p[i % n], p[i - 1]);
        var total = arc[n];
        var reach = Math.Clamp(total / 8, 0.75, 2.0);
        int Walk(int i, double distance, int step)
        {
            double gone = 0; var j = i;
            for (var guard = 0; guard < n && gone < distance; guard++) { var k = (j + step + n) % n; gone += Len(p[k], p[j]); j = k; }
            return j;
        }
        var turn = new double[n];
        for (var i = 0; i < n; i++)
        {
            var (b, a) = (p[Walk(i, reach, -1)], p[Walk(i, reach, 1)]);
            double ux = p[i].X - b.X, uy = p[i].Y - b.Y, vx = a.X - p[i].X, vy = a.Y - p[i].Y;
            var lu = Math.Sqrt(ux * ux + uy * uy); var lv = Math.Sqrt(vx * vx + vy * vy);
            turn[i] = lu == 0 || lv == 0 ? 0 : Math.Acos(Math.Clamp((ux * vx + uy * vy) / (lu * lv), -1, 1)) * 180 / Math.PI;
        }
        var corners = new List<int>();
        for (var i = 0; i < n; i++)
        {
            if (turn[i] < CornerDegrees) continue;
            // Keep only the strongest point of each bend (ties go to the first), looking 1 pixel each way: the two corners
            // of a narrow line end are about 2 pixels apart and must both stay.
            var strongest = true;
            var suppress = Math.Min(reach, 1.0);
            for (int j = Walk(i, suppress, -1); j != i && strongest; j = (j + 1) % n) strongest = turn[j] < turn[i];
            for (int j = (i + 1) % n, end = Walk(i, suppress, 1); strongest && end != i; j = (j + 1) % n) { strongest = turn[j] <= turn[i]; if (j == end) break; }
            if (strongest) corners.Add(i);
        }
        // Marching squares cuts a sharp corner by up to half a pixel. Put the corner back where the two edges meet.
        // Arc length forward from corner a to corner b (the whole outline when they are the same). The windows around a
        // corner stop halfway to the next corner, so two corners close together (a small step) do not bend each other.
        double Gap(int a, int b) { var g = (arc[b] - arc[a] + total) % total; return g <= 0 ? total : g; }
        double Before(int c) => Gap(corners[(c - 1 + corners.Count) % corners.Count], corners[c]) / 2;
        double After(int c) => Gap(corners[c], corners[(c + 1) % corners.Count]) / 2;
        for (var c = 0; c < corners.Count; c++)
        {
            var i = corners[c];
            double back = Math.Min(Math.Min(4, reach * 2), Before(c)), ahead = Math.Min(Math.Min(4, reach * 2), After(c));
            var (b1, b2, f1, f2) = (p[Walk(i, Math.Min(1, back / 2), -1)], p[Walk(i, back, -1)], p[Walk(i, Math.Min(1, ahead / 2), 1)], p[Walk(i, ahead, 1)]);
            double ax = b1.X - b2.X, ay = b1.Y - b2.Y, bx = f1.X - f2.X, by = f1.Y - f2.Y;
            var cross = ax * by - ay * bx; var la = Math.Sqrt(ax * ax + ay * ay); var lb = Math.Sqrt(bx * bx + by * by);
            if (la == 0 || lb == 0 || Math.Abs(cross) < 0.3 * la * lb) continue;
            var t = ((f2.X - b2.X) * by - (f2.Y - b2.Y) * bx) / cross;
            (double X, double Y) meet = (b2.X + ax * t, b2.Y + ay * t);
            if (Len(meet, p[i]) <= 1.5) p[i] = meet;
        }
        // Light smoothing (weights 1 2 3 2 1, wider for hard edges) removes the small wave that interpolated anti-aliasing leaves in the
        // outline. It never reaches across a corner, and corners stay where they are.
        var unsmoothed = p.ToArray();
        if (corners.Count == 0)
            for (var i = 0; i < n; i++)
            {
                double x = 0, y = 0;
                for (var o = -smoothing; o <= smoothing; o++) { var wgt = smoothing + 1 - Math.Abs(o); x += unsmoothed[(i + o + n) % n].X * wgt; y += unsmoothed[(i + o + n) % n].Y * wgt; }
                p[i] = (x / ((smoothing + 1) * (smoothing + 1)), y / ((smoothing + 1) * (smoothing + 1)));
            }
        else
            for (var c = 0; c < corners.Count; c++)
            {
                int from = corners[c], length = (corners[(c + 1) % corners.Count] - from + n - 1) % n + 1;
                for (var d = 1; d < length; d++)
                {
                    var half = Math.Min(smoothing, Math.Min(d, length - d));
                    double x = 0, y = 0, sum = 0;
                    for (var o = -half; o <= half; o++) { var wgt = half + 1 - Math.Abs(o); var q = unsmoothed[(from + d + o) % n]; x += q.X * wgt; y += q.Y * wgt; sum += wgt; }
                    p[(from + d) % n] = (x / sum, y / sum);
                }
            }
        // Tangents come from a least-squares line through about 3 pixels of outline, which averages out the
        // sub-pixel noise of single points.
        var window = Math.Max(reach, Math.Min(3, total / 8));
        IEnumerable<(double X, double Y)> Window(int from, int to) { for (var j = from; ; j = (j + 1) % n) { yield return p[j]; if (j == to) yield break; } }
        // Unit direction from corner p[i] along the outline `step` (+1 or -1). It skips the first pixel: at the end of a
        // thin line the corner is the tip, and the outline turns around the line end there before it runs along a side.
        (double X, double Y) Tangent(int i, int step, double limit)
        {
            int start = Walk(i, Math.Min(1, limit / 2), step), end = Walk(i, Math.Min(window + 1, limit), step);
            return Direction(step > 0 ? Window(start, end) : Window(end, start), (p[end].X - p[i].X, p[end].Y - p[i].Y));
        }
        (double X, double Y) Centre(int i)
        {
            int back = Walk(i, window, -1), ahead = Walk(i, window, 1);
            return Direction(Window(back, ahead), (p[ahead].X - p[back].X, p[ahead].Y - p[back].Y));
        }
        var starts = corners.Count > 0 ? corners : [0];
        segs.Add(new('M', [p[starts[0]].X, p[starts[0]].Y]));
        for (var c = 0; c < starts.Count; c++)
        {
            int from = starts[c], to = starts[(c + 1) % starts.Count];
            var run = new List<(double X, double Y)>();
            for (var i = from; ; i = (i + 1) % n) { run.Add(p[i]); if (run.Count > 1 && i == to) break; }
            (double, double) t1, t2;
            if (corners.Count > 0) { t1 = Tangent(from, 1, After(c)); t2 = Tangent(to, -1, Before((c + 1) % starts.Count)); }
            else { var t = Centre(from); t1 = t; t2 = (-t.X, -t.Y); }
            var before = segs.Count;
            FitCubic(run, 0, run.Count - 1, t1, t2, tolerance, segs, 0);
            for (var j = before + 1; j < segs.Count; j++) Smooth(segs, j - 1, j);
            if (corners.Count == 0 && segs.Count - before > 1) Smooth(segs, segs.Count - 1, before);
        }
        segs.Add(new('Z', [p[starts[0]].X, p[starts[0]].Y]));
    }

    // At a join inside a smooth run, a curve's handle follows the line next to it, so the outline has no kink there.
    static void Smooth(List<SvgPath.Seg> segs, int i, int j)
    {
        SvgPath.Seg a = segs[i], b = segs[j];
        var start = i == 0 ? null : segs[i - 1];
        (double X, double Y) from = start is null ? (double.NaN, double.NaN) : (start.X, start.Y);
        if (a.Kind == 'L' && b.Kind == 'C' && start is not null)
        {
            double dx = a.X - from.X, dy = a.Y - from.Y, l = Math.Sqrt(dx * dx + dy * dy), h = Math.Sqrt(Sq(b.P[0] - a.X) + Sq(b.P[1] - a.Y));
            if (l > 0) { b.P[0] = a.X + dx / l * h; b.P[1] = a.Y + dy / l * h; }
        }
        else if (a.Kind == 'C' && b.Kind == 'L')
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, l = Math.Sqrt(dx * dx + dy * dy), h = Math.Sqrt(Sq(a.P[2] - a.X) + Sq(a.P[3] - a.Y));
            if (l > 0) { a.P[2] = a.X - dx / l * h; a.P[3] = a.Y - dy / l * h; }
        }
        static double Sq(double v) => v * v;
    }

    // Principal direction of the points (a least-squares line that also works for vertical runs), signed like `towards`.
    static (double X, double Y) Direction(IEnumerable<(double X, double Y)> points, (double X, double Y) towards)
    {
        double sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0; var count = 0;
        foreach (var (x, y) in points) { sx += x; sy += y; sxx += x * x; sxy += x * y; syy += y * y; count++; }
        var tl = Math.Sqrt(towards.X * towards.X + towards.Y * towards.Y);
        if (count < 3) return tl == 0 ? (0, 0) : (towards.X / tl, towards.Y / tl);
        double mx = sx / count, my = sy / count, cxx = sxx / count - mx * mx, cxy = sxy / count - mx * my, cyy = syy / count - my * my;
        var angle = 0.5 * Math.Atan2(2 * cxy, cxx - cyy);
        (double X, double Y) d = (Math.Cos(angle), Math.Sin(angle));
        return d.X * towards.X + d.Y * towards.Y < 0 ? (-d.X, -d.Y) : d;
    }

    static double Len((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // Least-squares cubic through points first..last with the given end tangents; splits at the worst point until every
    // point is within the tolerance. Straight runs become lines. (After Schneider's curve-fitting method, our own code.)
    static void FitCubic(List<(double X, double Y)> q, int first, int last, (double X, double Y) t1, (double X, double Y) t2, double tolerance,
        List<SvgPath.Seg> segs, int depth)
    {
        var a = q[first]; var b = q[last];
        // A line only where the run is straight and both end tangents run along it; otherwise a gentle curve would meet
        // its neighbours at a visible kink.
        if (last - first < 2 || (Straight(q, first, last, Math.Min(tolerance, 0.08)) && Along(t1, a, b) && Along((-t2.X, -t2.Y), a, b))) { segs.Add(new('L', [b.X, b.Y])); return; }
        var u = Parameterize(q, first, last);
        var bez = Generate(q, first, last, u, t1, t2);
        var (error, split) = MaxError(q, first, last, bez, u);
        var limit = tolerance * tolerance;
        if (error > limit && error < limit * 16)
            for (var i = 0; i < 6 && error > limit; i++)
            {
                u = Reparameterize(q, first, last, u, bez);
                bez = Generate(q, first, last, u, t1, t2);
                (error, split) = MaxError(q, first, last, bez, u);
            }
        if (error <= limit || depth > 40)
        {
            segs.Add(new('C', [bez[1].X, bez[1].Y, bez[2].X, bez[2].Y, bez[3].X, bez[3].Y]));
            return;
        }
        split = Math.Clamp(split, first + 1, last - 1);
        // The tangent at the split comes from about 3 pixels of outline on each side, so joins stay smooth.
        int lo = split, hi = split;
        for (double gone = 0; lo > first && gone < 3; lo--) gone += Len(q[lo], q[lo - 1]);
        for (double gone = 0; hi < last && gone < 3; hi++) gone += Len(q[hi], q[hi + 1]);
        var centre = Direction(q.Skip(lo).Take(hi - lo + 1), (q[hi].X - q[lo].X, q[hi].Y - q[lo].Y));
        FitCubic(q, first, split, t1, (-centre.Item1, -centre.Item2), tolerance, segs, depth + 1);
        FitCubic(q, split, last, centre, t2, tolerance, segs, depth + 1);
    }

    static bool Along((double X, double Y) t, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, l = Math.Sqrt(dx * dx + dy * dy);
        return l > 0 && (t.X * dx + t.Y * dy) / l > Math.Cos(1.5 * Math.PI / 180);
    }

    static bool Straight(List<(double X, double Y)> q, int first, int last, double tolerance)
    {
        var a = q[first]; var b = q[last];
        double dx = b.X - a.X, dy = b.Y - a.Y, l = Math.Sqrt(dx * dx + dy * dy);
        if (l == 0) return false;
        for (var i = first + 1; i < last; i++)
        {
            var d = Math.Abs((q[i].X - a.X) * dy - (q[i].Y - a.Y) * dx) / l;
            var along = ((q[i].X - a.X) * dx + (q[i].Y - a.Y) * dy) / (l * l);
            if (d > tolerance || along < -0.01 || along > 1.01) return false;
        }
        return true;
    }

    static double[] Parameterize(List<(double X, double Y)> q, int first, int last)
    {
        var u = new double[last - first + 1];
        for (var i = first + 1; i <= last; i++) u[i - first] = u[i - first - 1] + Len(q[i], q[i - 1]);
        for (var i = 1; i < u.Length; i++) u[i] /= u[^1];
        return u;
    }

    static (double X, double Y) Bez((double X, double Y)[] c, double t)
    {
        double s = 1 - t, b0 = s * s * s, b1 = 3 * s * s * t, b2 = 3 * s * t * t, b3 = t * t * t;
        return (b0 * c[0].X + b1 * c[1].X + b2 * c[2].X + b3 * c[3].X, b0 * c[0].Y + b1 * c[1].Y + b2 * c[2].Y + b3 * c[3].Y);
    }

    static (double X, double Y)[] Generate(List<(double X, double Y)> q, int first, int last, double[] u, (double X, double Y) t1, (double X, double Y) t2)
    {
        double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
        var a = q[first]; var b = q[last];
        for (var i = 0; i < u.Length; i++)
        {
            double t = u[i], s = 1 - t, b0 = s * s * s, b1 = 3 * s * s * t, b2 = 3 * s * t * t, b3 = t * t * t;
            (double X, double Y) A1 = (t1.X * b1, t1.Y * b1), A2 = (t2.X * b2, t2.Y * b2);
            c00 += A1.X * A1.X + A1.Y * A1.Y; c01 += A1.X * A2.X + A1.Y * A2.Y; c11 += A2.X * A2.X + A2.Y * A2.Y;
            double tx = q[first + i].X - (a.X * (b0 + b1) + b.X * (b2 + b3)), ty = q[first + i].Y - (a.Y * (b0 + b1) + b.Y * (b2 + b3));
            x0 += A1.X * tx + A1.Y * ty; x1 += A2.X * tx + A2.Y * ty;
        }
        var det = c00 * c11 - c01 * c01;
        double alpha1 = det == 0 ? 0 : (x0 * c11 - x1 * c01) / det, alpha2 = det == 0 ? 0 : (c00 * x1 - c01 * x0) / det;
        var chord = Len(a, b);
        // Degenerate or wild solutions fall back to the usual third-of-the-chord handles.
        if (alpha1 < chord * 1e-3 || alpha2 < chord * 1e-3 || alpha1 > chord * 2 || alpha2 > chord * 2) alpha1 = alpha2 = chord / 3;
        return [a, (a.X + t1.X * alpha1, a.Y + t1.Y * alpha1), (b.X + t2.X * alpha2, b.Y + t2.Y * alpha2), b];
    }

    static (double error, int split) MaxError(List<(double X, double Y)> q, int first, int last, (double X, double Y)[] bez, double[] u)
    {
        double max = 0; var split = (first + last) / 2;
        for (var i = first + 1; i < last; i++)
        {
            var p = Bez(bez, u[i - first]);
            var d = (p.X - q[i].X) * (p.X - q[i].X) + (p.Y - q[i].Y) * (p.Y - q[i].Y);
            if (d > max) { max = d; split = i; }
        }
        return (max, split);
    }

    static double[] Reparameterize(List<(double X, double Y)> q, int first, int last, double[] u, (double X, double Y)[] c)
    {
        var result = new double[u.Length];
        for (var i = 0; i < u.Length; i++)
        {
            double t = u[i], s = 1 - t;
            var p = Bez(c, t);
            (double X, double Y) d1 = (3 * (s * s * (c[1].X - c[0].X) + 2 * s * t * (c[2].X - c[1].X) + t * t * (c[3].X - c[2].X)),
                                       3 * (s * s * (c[1].Y - c[0].Y) + 2 * s * t * (c[2].Y - c[1].Y) + t * t * (c[3].Y - c[2].Y)));
            (double X, double Y) d2 = (6 * (s * (c[2].X - 2 * c[1].X + c[0].X) + t * (c[3].X - 2 * c[2].X + c[1].X)),
                                       6 * (s * (c[2].Y - 2 * c[1].Y + c[0].Y) + t * (c[3].Y - 2 * c[2].Y + c[1].Y)));
            double dx = p.X - q[first + i].X, dy = p.Y - q[first + i].Y;
            var num = dx * d1.X + dy * d1.Y; var den = d1.X * d1.X + d1.Y * d1.Y + dx * d2.X + dy * d2.Y;
            result[i] = den == 0 ? t : Math.Clamp(t - num / den, 0, 1);
        }
        return result;
    }
}
