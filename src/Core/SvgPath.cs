using System.Globalization;
using System.Text;

namespace OmlCli;

// SVG path data as absolute segments: parse any valid `d`, then write it back as short as possible.
// Our own code, written from the SVG 1.1 path grammar.
static class SvgPath
{
    // Kind is M, L, C, Q, A or Z. P holds absolute numbers: L/M (x y), C (x1 y1 x2 y2 x y), Q (x1 y1 x y),
    // A (rx ry rotation large sweep x y), Z (start x, start y).
    internal sealed record Seg(char Kind, double[] P)
    {
        public double X => Kind == 'A' ? P[5] : P[^2];
        public double Y => Kind == 'A' ? P[6] : P[^1];
    }

    static int Count(char c) => char.ToUpperInvariant(c) switch { 'M' or 'L' or 'T' => 2, 'H' or 'V' => 1, 'C' => 6, 'S' or 'Q' => 4, 'A' => 7, _ => 0 };

    // Null when the data is not valid path data; the caller then keeps the attribute unchanged.
    internal static List<Seg>? Parse(string d)
    {
        var segs = new List<Seg>(); var i = 0; char cmd = '\0';
        double cx = 0, cy = 0, sx = 0, sy = 0, lcx = 0, lcy = 0; char last = '\0';
        void Skip() { while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++; }
        bool Flag(out double v)
        {
            Skip(); v = 0;
            if (i < d.Length && d[i] is '0' or '1') { v = d[i++] - '0'; return true; }
            return false;
        }
        bool Num(out double v)
        {
            Skip(); v = 0; var start = i;
            if (i < d.Length && d[i] is '+' or '-') i++;
            var digits = 0;
            while (i < d.Length && char.IsAsciiDigit(d[i])) { i++; digits++; }
            if (i < d.Length && d[i] == '.') { i++; while (i < d.Length && char.IsAsciiDigit(d[i])) { i++; digits++; } }
            if (digits == 0) { i = start; return false; }
            if (i < d.Length && d[i] is 'e' or 'E')
            {
                var e = i++; if (i < d.Length && d[i] is '+' or '-') i++;
                var ed = 0; while (i < d.Length && char.IsAsciiDigit(d[i])) { i++; ed++; }
                if (ed == 0) i = e;
            }
            return double.TryParse(d.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);
        }
        Skip();
        while (i < d.Length)
        {
            if (char.IsAsciiLetter(d[i]))
            {
                cmd = d[i++];
                if (Count(cmd) == 0 && char.ToUpperInvariant(cmd) != 'Z') return null;
                if (segs.Count == 0 && char.ToUpperInvariant(cmd) != 'M') return null;
            }
            else if (cmd == '\0' || char.ToUpperInvariant(cmd) == 'Z') return null;
            var rel = char.IsLower(cmd); var up = char.ToUpperInvariant(cmd);
            if (up == 'Z')
            {
                segs.Add(new('Z', [sx, sy])); cx = sx; cy = sy; last = 'Z'; Skip(); continue;
            }
            var n = new double[Count(cmd)];
            for (var k = 0; k < n.Length; k++)
                if (!(up == 'A' && k is 3 or 4 ? Flag(out n[k]) : Num(out n[k]))) return null;
            double ox = rel ? cx : 0, oy = rel ? cy : 0;
            switch (up)
            {
                case 'M':
                    segs.Add(new('M', [n[0] + ox, n[1] + oy])); cx = sx = n[0] + ox; cy = sy = n[1] + oy;
                    cmd = rel ? 'l' : 'L'; // pairs after a moveto are implicit linetos
                    last = 'M'; break;
                case 'L': Line(n[0] + ox, n[1] + oy); break;
                case 'H': Line(n[0] + ox, cy); break;
                case 'V': Line(cx, n[0] + (rel ? cy : 0)); break;
                case 'C': Cubic(n[0] + ox, n[1] + oy, n[2] + ox, n[3] + oy, n[4] + ox, n[5] + oy); break;
                case 'S':
                    var (rx, ry) = last == 'C' ? (2 * cx - lcx, 2 * cy - lcy) : (cx, cy);
                    Cubic(rx, ry, n[0] + ox, n[1] + oy, n[2] + ox, n[3] + oy); break;
                case 'Q': Quad(n[0] + ox, n[1] + oy, n[2] + ox, n[3] + oy); break;
                case 'T':
                    var (qx, qy) = last == 'Q' ? (2 * cx - lcx, 2 * cy - lcy) : (cx, cy);
                    Quad(qx, qy, n[0] + ox, n[1] + oy); break;
                case 'A':
                    segs.Add(new('A', [Math.Abs(n[0]), Math.Abs(n[1]), n[2], n[3], n[4], n[5] + ox, n[6] + oy]));
                    cx = n[5] + ox; cy = n[6] + oy; last = 'A'; break;
            }
            Skip();
        }
        return segs;

        void Line(double x, double y) { segs.Add(new('L', [x, y])); cx = x; cy = y; last = 'L'; }
        void Cubic(double x1, double y1, double x2, double y2, double x, double y)
        { segs.Add(new('C', [x1, y1, x2, y2, x, y])); lcx = x2; lcy = y2; cx = x; cy = y; last = 'C'; }
        void Quad(double x1, double y1, double x, double y)
        { segs.Add(new('Q', [x1, y1, x, y])); lcx = x1; lcy = y1; cx = x; cy = y; last = 'Q'; }
    }

    internal static string Num(double v, int decimals)
    {
        var r = Math.Round(v, decimals, MidpointRounding.AwayFromZero);
        if (r == 0) return "0";
        var s = r.ToString("0." + new string('#', Math.Max(1, decimals)), CultureInfo.InvariantCulture);
        if (s.StartsWith("0.")) s = s[1..]; else if (s.StartsWith("-0.")) s = "-" + s[2..];
        return s;
    }

    // Appends numbers with the fewest separators: none before '-', none before '.' when the previous number has a '.'.
    sealed class Writer(StringBuilder sb)
    {
        bool numberOpen, hasDot;
        public void Letter(char c) { sb.Append(c); numberOpen = false; }
        public void Number(string n)
        {
            if (numberOpen && !(n[0] == '-' || (n[0] == '.' && hasDot))) sb.Append(' ');
            sb.Append(n); numberOpen = true; hasDot = n.Contains('.');
        }
        public static int Length(string[] nums, bool afterNumber, bool prevDot)
        {
            var len = 0; var open = afterNumber; var dot = prevDot;
            foreach (var n in nums) { if (open && !(n[0] == '-' || (n[0] == '.' && dot))) len++; len += n.Length; open = true; dot = n.Contains('.'); }
            return len;
        }
        public bool Open => numberOpen;
        public bool Dot => hasDot;
    }

    // Writes the segments with rounded absolute positions; relative numbers are differences of rounded positions, so
    // rounding never drifts along the path. Each segment takes its shorter absolute or relative form and H/V/S/T forms.
    internal static string Write(List<Seg> segs, int decimals)
    {
        var sb = new StringBuilder(); var w = new Writer(sb);
        double R(double v) => Math.Round(v, decimals, MidpointRounding.AwayFromZero);
        double cx = 0, cy = 0, sx = 0, sy = 0, lcx = 0, lcy = 0; char lastKind = '\0', lastLetter = '\0';
        foreach (var s in segs)
        {
            var p = s.P.Select((v, k) => s.Kind == 'A' && k is 2 or 3 or 4 ? v : R(v)).ToArray();
            var options = new List<(char letter, double[] nums)>();
            double D(double v, double origin) => R(v - origin);
            switch (s.Kind)
            {
                case 'M':
                    options.Add(('M', [p[0], p[1]]));
                    if (lastKind != '\0') options.Add(('m', [D(p[0], cx), D(p[1], cy)]));
                    break;
                case 'L':
                    if (p[1] == cy) { options.Add(('H', [p[0]])); options.Add(('h', [D(p[0], cx)])); }
                    else if (p[0] == cx) { options.Add(('V', [p[1]])); options.Add(('v', [D(p[1], cy)])); }
                    else { options.Add(('L', [p[0], p[1]])); options.Add(('l', [D(p[0], cx), D(p[1], cy)])); }
                    break;
                case 'C':
                    if (lastKind == 'C' && R(2 * cx - lcx) == p[0] && R(2 * cy - lcy) == p[1])
                    { options.Add(('S', [p[2], p[3], p[4], p[5]])); options.Add(('s', [D(p[2], cx), D(p[3], cy), D(p[4], cx), D(p[5], cy)])); }
                    else
                    { options.Add(('C', p)); options.Add(('c', [D(p[0], cx), D(p[1], cy), D(p[2], cx), D(p[3], cy), D(p[4], cx), D(p[5], cy)])); }
                    break;
                case 'Q':
                    if (lastKind == 'Q' && R(2 * cx - lcx) == p[0] && R(2 * cy - lcy) == p[1])
                    { options.Add(('T', [p[2], p[3]])); options.Add(('t', [D(p[2], cx), D(p[3], cy)])); }
                    else
                    { options.Add(('Q', p)); options.Add(('q', [D(p[0], cx), D(p[1], cy), D(p[2], cx), D(p[3], cy)])); }
                    break;
                case 'A':
                    options.Add(('A', p)); options.Add(('a', [p[0], p[1], p[2], p[3], p[4], D(p[5], cx), D(p[6], cy)]));
                    break;
                case 'Z':
                    options.Add(('z', []));
                    break;
            }
            char bestLetter = ' '; string[] bestNums = []; var bestOmit = false; var bestCost = int.MaxValue;
            foreach (var (letter, nums) in options)
            {
                var text = nums.Select((v, k) => s.Kind == 'A' && k is 3 or 4 ? (v == 0 ? "0" : "1") : Num(v, s.Kind == 'A' && k == 2 ? Math.Max(decimals, 3) : decimals)).ToArray();
                // A repeated command needs no letter; pairs after M/m are implicit L/l. M and z always take their letter.
                var omit = (letter == lastLetter && letter is not ('M' or 'm' or 'z')) || (lastLetter == 'M' && letter == 'L') || (lastLetter == 'm' && letter == 'l');
                var cost = omit ? Writer.Length(text, w.Open, w.Dot) : 1 + Writer.Length(text, false, false);
                if (cost < bestCost) (bestLetter, bestNums, bestOmit, bestCost) = (letter, text, omit, cost);
            }
            if (!bestOmit) w.Letter(bestLetter);
            foreach (var n in bestNums) w.Number(n);
            lastLetter = bestLetter;
            switch (s.Kind)
            {
                case 'M': cx = sx = p[0]; cy = sy = p[1]; break;
                case 'Z': cx = sx; cy = sy; break;
                case 'A': cx = p[5]; cy = p[6]; break;
                default: cx = p[^2]; cy = p[^1]; break;
            }
            if (s.Kind == 'C') { lcx = p[2]; lcy = p[3]; }
            if (s.Kind == 'Q') { lcx = p[0]; lcy = p[1]; }
            lastKind = s.Kind;
        }
        return sb.ToString();
    }
}
