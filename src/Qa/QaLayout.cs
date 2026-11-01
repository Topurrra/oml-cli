using System.Text.Json;

namespace OmlCli.Qa;

// Layout findings from the boxes of a screen's actions and fields: positions are for checking, never for clicking.
//   overflow  the page or an element is wider than the window (a horizontal scroll bar, often at phone width)
//   small     a button or field smaller than 24 x 24 px (WCAG 2.2, 2.5.8 Target Size); links in text are exempt
//   overlap   two actions or fields cover each other (more than a third of the smaller one)
static class QaLayout
{
    const double MinTarget = 24, Slack = 1;

    sealed record Box(string Target, string Tag, double X, double Y, double W, double H)
    {
        public double Right => X + W;
        public double Bottom => Y + H;
    }

    static List<Box> Items(JsonElement boxes) => boxes.GetProperty("items").EnumerateArray()
        .Select(i => new Box(i.GetProperty("target").GetString()!, i.GetProperty("tag").GetString()!,
            i.GetProperty("x").GetDouble(), i.GetProperty("y").GetDouble(), i.GetProperty("w").GetDouble(), i.GetProperty("h").GetDouble()))
        .ToList();

    public static List<string> Findings(JsonElement boxes)
    {
        var findings = new List<string>();
        var width = boxes.GetProperty("viewport").GetProperty("w").GetDouble();
        if (boxes.TryGetProperty("scrollWidth", out var sw) && sw.ValueKind == JsonValueKind.Number && sw.GetDouble() > width + Slack)
            findings.Add($"overflow: the page is {sw.GetDouble():0} px wide in a {width:0} px window (horizontal scroll)");
        var items = Items(boxes);
        // Crossing an edge is a defect; lying wholly outside is how off-canvas menus hide, so that is left alone.
        foreach (var b in items.Where(b => b.X < -Slack && b.Right > Slack || b.X < width - Slack && b.Right > width + Slack).Take(10))
            findings.Add($"overflow: {b.Target} sticks out of the window ({b.X:0} to {b.Right:0} px of {width:0})");
        foreach (var b in items.Where(b => b.Tag != "a" && (b.W < MinTarget || b.H < MinTarget)).Take(10))
            findings.Add($"small: {b.Target} is {b.W:0} x {b.H:0} px; a click target needs 24 x 24 px");
        var overlaps = 0;
        for (var i = 0; i < items.Count && overlaps < 10; i++)
            for (var j = i + 1; j < items.Count && overlaps < 10; j++)
            {
                var (a, b) = (items[i], items[j]);
                var w = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
                var h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
                if (w <= Slack || h <= Slack) continue;
                var smaller = Math.Min(a.W * a.H, b.W * b.H);
                if (smaller > 0 && w * h > smaller / 3 && !Inside(a, b) && !Inside(b, a))
                {
                    findings.Add($"overlap: {a.Target} and {b.Target} cover each other");
                    overlaps++;
                }
            }
        return findings;
    }

    static bool Inside(Box inner, Box outer) =>
        inner.X >= outer.X - Slack && inner.Y >= outer.Y - Slack && inner.Right <= outer.Right + Slack && inner.Bottom <= outer.Bottom + Slack;

    // Whether the target is wholly inside the window at the current scroll position (no scrolling needed to see it).
    public static string? InView(JsonElement boxes, string target)
    {
        var view = boxes.GetProperty("viewport");
        var (w, h) = (view.GetProperty("w").GetDouble(), view.GetProperty("h").GetDouble());
        var name = target.StartsWith("text:") ? target : target.Split('#')[0];
        var box = Items(boxes).FirstOrDefault(b => b.Target == target || b.Target.Split('#')[0] == name || b.Target == "text:" + target);
        if (box is null) return $"{target} is not an action or field on the screen";
        if (box.Y >= h - Slack) return $"{target} is below the visible part of the window (at {box.Y:0} px of {h:0}): the user must scroll";
        if (box.X < -Slack || box.Right > w + Slack || box.Y < -Slack || box.Bottom > h + Slack) return $"{target} is only partly in the window";
        return null;
    }
}
