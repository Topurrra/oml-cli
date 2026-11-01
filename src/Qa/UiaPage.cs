using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OmlCli.Qa;

// One read of the page's accessibility tree through UI Automation, and the page helper (QaScript) answered from it.
// Chromium passes the DOM to UI Automation: an element's id is its AutomationId (so a widget's Name works as a target,
// as in the DevTools engine), its class attribute is its ClassName (so feedback and validation messages are found by
// their OutSystems classes), and the whole subtree comes back in one cross-process call (a Subtree cache request).
sealed class UiaPage
{
    public sealed class Node
    {
        public required IUIAutomationElement Element;
        public Node? Parent;
        public List<Node> Children = [];
        public string Name = "", Id = "", Class = "", Aria = "", ValueText = "";
        public int Type, State;
        public double X, Y, W, H;
        public bool Enabled, Offscreen, Password, Required, ReadOnly, Invoke, Toggle, SelectItem, Expand, ScrollItem, HasValue;
    }

    public static readonly int[] Properties =
    [
        Uia.BoundingRectangle, Uia.ControlType, Uia.Name, Uia.IsEnabled, Uia.AutomationId, Uia.ClassName, Uia.IsPassword,
        Uia.IsOffscreen, Uia.IsRequiredForForm, Uia.IsExpandCollapsePatternAvailable, Uia.IsInvokePatternAvailable,
        Uia.IsScrollItemPatternAvailable, Uia.IsSelectionItemPatternAvailable, Uia.IsTogglePatternAvailable,
        Uia.IsValuePatternAvailable, Uia.ValueValue, Uia.ValueIsReadOnly, Uia.LegacyState, Uia.AriaRole,
    ];

    const int MaxNodes = 30_000;
    public Node Root { get; }
    public List<Node> All { get; } = [];
    public string Url => Root.ValueText;
    public bool Busy => (Root.State & Uia.StateBusy) != 0;

    // An OutSystems screen change: the old screen loses "active-screen" while the next one loads, then both sit in
    // #transitionContainer during the animation (classes like fade-enter, fade-leave). Settled is one active screen
    // with no enter or leave class. A page without the container (or whose screen divs UIA does not show) is settled.
    public bool ScreenSettled
    {
        get
        {
            var container = All.FirstOrDefault(n => n.Id == "transitionContainer");
            if (container is null) return true;
            var screens = Below(container).Where(n => Regex.IsMatch(n.Class, @"(^|\s)screen-container(\s|$)")).ToList();
            if (screens.Count == 0) return true;
            return screens.Count == 1 && Regex.IsMatch(screens[0].Class, @"(^|\s)active-screen(\s|$)") && !Regex.IsMatch(screens[0].Class, @"-(enter|leave|exit)(-active)?(\s|$)");
        }
    }

    public UiaPage(IUIAutomationElement cachedRoot)
    {
        Root = Build(cachedRoot, null);
    }

    Node Build(IUIAutomationElement e, Node? parent)
    {
        var n = new Node { Element = e, Parent = parent };
        All.Add(n);
        n.Type = Uia.Cached(e, Uia.ControlType) as int? ?? 0;
        n.Name = Uia.Cached(e, Uia.Name) as string ?? "";
        n.Id = Uia.Cached(e, Uia.AutomationId) as string ?? "";
        n.Class = Uia.Cached(e, Uia.ClassName) as string ?? "";
        n.Aria = Uia.Cached(e, Uia.AriaRole) as string ?? "";
        n.ValueText = Uia.Cached(e, Uia.ValueValue) as string ?? "";
        n.State = Uia.Cached(e, Uia.LegacyState) as int? ?? 0;
        if (Uia.Cached(e, Uia.BoundingRectangle) is double[] { Length: 4 } r) (n.X, n.Y, n.W, n.H) = (r[0], r[1], r[2], r[3]);
        n.Enabled = Uia.Cached(e, Uia.IsEnabled) as bool? ?? true;
        n.Offscreen = Uia.Cached(e, Uia.IsOffscreen) as bool? ?? false;
        n.Password = Uia.Cached(e, Uia.IsPassword) as bool? ?? false;
        n.Required = Uia.Cached(e, Uia.IsRequiredForForm) as bool? ?? false;
        n.ReadOnly = Uia.Cached(e, Uia.ValueIsReadOnly) as bool? ?? false;
        n.Invoke = Uia.Cached(e, Uia.IsInvokePatternAvailable) as bool? ?? false;
        n.Toggle = Uia.Cached(e, Uia.IsTogglePatternAvailable) as bool? ?? false;
        n.SelectItem = Uia.Cached(e, Uia.IsSelectionItemPatternAvailable) as bool? ?? false;
        n.Expand = Uia.Cached(e, Uia.IsExpandCollapsePatternAvailable) as bool? ?? false;
        n.ScrollItem = Uia.Cached(e, Uia.IsScrollItemPatternAvailable) as bool? ?? false;
        n.HasValue = Uia.Cached(e, Uia.IsValuePatternAvailable) as bool? ?? false;
        foreach (var child in Uia.Items(e.GetCachedChildren()))
        {
            if (All.Count >= MaxNodes) break;
            n.Children.Add(Build(child, n));
        }
        return n;
    }

    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static JsonElement J(object value) => JsonSerializer.SerializeToElement(value, Json);

    static readonly HashSet<int> Clickable = [Uia.Button, Uia.Hyperlink, Uia.CheckBox, Uia.RadioButton, Uia.MenuItem, Uia.TabItem, Uia.SplitButton];
    static readonly HashSet<int> Wrappers = [Uia.Group, Uia.Custom, Uia.Pane, Uia.Document, Uia.Table, Uia.DataGrid, Uia.DataItem, Uia.ListItem, Uia.HeaderItem];
    static bool IsClickable(Node n) => Clickable.Contains(n.Type) || n.Invoke && n.Id.Length > 0 && n.Type is Uia.Group or Uia.Custom or Uia.Image;
    static bool IsField(Node n) => n.Type is Uia.Edit or Uia.ComboBox;
    static bool Vis(Node n) => n.W > 0 && n.H > 0 && (!n.Offscreen || n.ScrollItem);
    static IEnumerable<Node> Below(Node n) => n.Children.SelectMany(c => Below(c).Prepend(c));
    static bool Contains(Node outer, Node inner) { for (var p = inner.Parent; p is not null; p = p.Parent) if (p == outer) return true; return false; }

    // What a person reads in the element: its own name or value, or for a container the readable parts inside it
    // (text leaves, and named buttons, links and fields that hold no text node of their own).
    public static string Txt(Node n)
    {
        if (n.Type == Uia.Edit) return n.ValueText;
        if (n.Name.Length > 0 && !Wrappers.Contains(n.Type)) return Clean(n.Name);
        return Clean(string.Join(" ", Readable(n)));
    }

    static IEnumerable<string> Readable(Node n)
    {
        foreach (var c in n.Children)
        {
            if (c.Type == Uia.Text && c.Name.Length > 0 && !c.Children.Any(x => x.Type == Uia.Text)) yield return c.Name;
            else if ((IsClickable(c) || IsField(c)) && c.Name.Length > 0 && !Below(c).Any(x => x.Type == Uia.Text)) yield return c.Name;
            else foreach (var t in Readable(c)) yield return t;
        }
    }
    static string Clean(string s) => Regex.Replace(s, @"\s+", " ").Trim();
    static string Short(string s, int max) => s.Length > max ? s[..max] : s;

    // "b2-b3-ThemeButton" is the widget ThemeButton; "$b1" and "b1-$b2" are block wrappers, not widgets.
    static string? WidgetName(string id) => id[(id.LastIndexOf('-') + 1)..] is { Length: > 0 } name && !name.StartsWith('$') && id != "RootWebArea" ? name : null;
    List<Node> ByName(string name) => All.Where(n => n.Id == name || n.Id.EndsWith("-" + name, StringComparison.Ordinal)).ToList();
    static string Label(Node n) => Clean(n.Name);

    List<Node> ByText(string t)
    {
        var all = All.Where(n => Vis(n) && IsClickable(n)).ToList();
        var m = all.Where(n => Txt(n).Equals(t, StringComparison.OrdinalIgnoreCase)).ToList();
        if (m.Count == 0) m = all.Where(n => Txt(n).Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
        if (m.Count == 0) m = All.Where(n => Vis(n) && IsField(n) && Label(n).Equals(t, StringComparison.OrdinalIgnoreCase)).ToList();
        return m.Where(n => !m.Any(o => o != n && Contains(n, o))).ToList();
    }

    static Node? Field(Node n) => IsField(n) ? n : Below(n).FirstOrDefault(IsField);

    static string Tag(Node n) => n.Type switch
    {
        Uia.Button => "button", Uia.Hyperlink => "a", Uia.Edit => "input", Uia.ComboBox => "select", Uia.CheckBox => "checkbox",
        Uia.RadioButton => "radio", Uia.DataGrid or Uia.Table => "table", Uia.Text => "text", Uia.Image => "img", _ => n.Aria.Length > 0 ? n.Aria : "element",
    };
    static object Desc(Node n) => new { tag = Tag(n), id = n.Id.Length > 0 ? n.Id : null, text = Short(Txt(n), 60) is { Length: > 0 } t ? t : null };

    string? Target(Node n)
    {
        if (WidgetName(n.Id) is { } name)
        {
            var same = ByName(name).Where(Vis).ToList();
            return same.Count <= 1 ? name : name + "#" + same.IndexOf(n);
        }
        var t = Txt(n);
        return t.Length > 0 ? "text:" + Short(t, 40) : null;
    }

    // The same answers as the page helper in QaScript, so the runner works the same with either engine.
    public JsonElement Locate(string t, int? nth, bool wantField, out Node? found)
    {
        found = null;
        string how;
        List<Node> m;
        var hash = Regex.Match(t, @"^(.*)#(\d+)$");
        if (hash.Success && !t.StartsWith("css:")) { t = hash.Groups[1].Value; nth ??= int.Parse(hash.Groups[2].Value); }
        if (t.StartsWith("css:")) return J(new { found = 0, how = "css", error = "css: targets need the DevTools engine; use a widget Name or text: here" });
        if (t.StartsWith("text:")) { how = "text"; m = ByText(t[5..]); }
        else { how = "widget"; m = ByName(t); if (m.Count == 0) { how = "text"; m = ByText(t); } }
        if (wantField) m = m.Select(Field).OfType<Node>().ToList();
        var shown = m.Where(Vis).ToList();
        var pool = shown.Count > 0 ? shown : m;
        if (pool.Count == 0) return J(new { found = 0, how });
        if (pool.Count > 1 && nth is null) return J(new { found = pool.Count, how, ambiguous = pool.Take(5).Select(Desc).ToList() });
        if ((nth ?? 0) >= pool.Count) return J(new { found = pool.Count, how, error = $"#{nth} is out of range: {pool.Count} match" });
        var e = pool[nth ?? 0];
        found = e;
        return J(new
        {
            found = pool.Count, how, x = e.X + e.W / 2, y = e.Y + e.H / 2, visible = Vis(e), disabled = !e.Enabled, el = Desc(e),
            kind = e.Type == Uia.ComboBox ? "select" : e.Password ? "password" : null,
        });
    }

    public JsonElement Feedback() => J(FeedbackList());
    List<object> FeedbackList() => All.Where(n => Vis(n) && Regex.IsMatch(n.Class, @"(^|\s)feedback-message(\s|$)")).Select(n => (object)new
    {
        type = Regex.Match(n.Class, @"feedback-message-(\w+)") is { Success: true } t ? t.Groups[1].Value : "info",
        text = Txt(Below(n).FirstOrDefault(c => c.Class.Contains("feedback-message-text")) ?? n),
    }).ToList();

    public JsonElement Invalid() => J(InvalidList());
    List<object> InvalidList() => All.Where(n => Vis(n) && n.Class.Contains("validation-message")).Select(n =>
    {
        var siblings = n.Parent?.Children ?? [];
        var before = siblings.Take(siblings.IndexOf(n)).Reverse().Select(Field).FirstOrDefault(f => f is not null);
        return (object)new { target = before is null ? null : Target(before), text = Txt(n) };
    }).ToList();

    public string Text() => string.Join("\n", All.Where(n => n.Type == Uia.Text && n.Name.Length > 0 && !n.Children.Any(c => c.Type == Uia.Text)).Select(n => n.Name));

    public JsonElement Table(string t, int? nth)
    {
        var r = Locate(t, nth, false, out var found);
        if (found is null) return r;
        var table = found.Type is Uia.DataGrid or Uia.Table ? found : Below(found).FirstOrDefault(n => n.Type is Uia.DataGrid or Uia.Table);
        if (table is null) return J(new { error = "not a table" });
        var (columns, rows) = Rows(table);
        return J(new { columns, rows });
    }

    // Rows are the children (through wrappers) that hold cells; a row of header items gives the columns.
    static (List<string> Columns, List<List<string>> Rows) Rows(Node table)
    {
        var rows = Below(table).Where(n => n.Aria == "row" || n.Children.Count > 0 && n.Children.All(c => c.Type is Uia.DataItem or Uia.HeaderItem or Uia.ListItem) && n.Type is not (Uia.DataGrid or Uia.Table)).ToList();
        rows = rows.Where(r => !rows.Any(o => o != r && Contains(r, o))).ToList();
        var columns = new List<string>();
        var data = new List<List<string>>();
        foreach (var row in rows)
        {
            var cells = row.Children.ToList();
            if (cells.All(c => c.Type == Uia.HeaderItem || c.Aria is "columnheader")) columns = cells.Select(Txt).ToList();
            else data.Add(cells.Select(Txt).ToList());
        }
        return (columns, data);
    }

    // For recording: the smallest action or field under a screen point (UIA bounds are screen coordinates), and the
    // same answer as the page helper's recordAt (target, text, tag, field, and the inputs as they are now).
    public JsonElement? RecordAt(double x, double y)
    {
        var n = All.Where(n => Vis(n) && (IsClickable(n) || IsField(n)) && x >= n.X && x < n.X + n.W && y >= n.Y && y < n.Y + n.H)
            .OrderBy(n => n.W * n.H).FirstOrDefault();
        if (n is null || Target(n) is not { } target) return null;
        var text = Short(Txt(n), 50);
        return J(new { target, text = text.Length > 0 ? text : null, tag = n.Type == Uia.Hyperlink ? "a" : IsField(n) ? "field" : "button", field = IsField(n), inputs = Summary(Root.Name).GetProperty("inputs") });
    }

    // The same shape as the page helper's boxes(): positions relative to the document (the viewport), outermost
    // actions and fields only. UI Automation has no scroll width, so horizontal overflow comes from the boxes alone.
    public JsonElement Boxes()
    {
        var items = All.Where(n => Vis(n) && (IsClickable(n) || IsField(n)))
            .Where(n => !All.Any(o => o != n && Contains(o, n) && o.Type is Uia.Button or Uia.Hyperlink))
            .Select(n => new { target = Target(n), tag = n.Type == Uia.Hyperlink ? "a" : IsField(n) ? "field" : "button", x = n.X - Root.X, y = n.Y - Root.Y, w = n.W, h = n.H })
            .Where(i => i.target is not null).ToList();
        return J(new { viewport = new { w = Root.W, h = Root.H }, items });
    }

    public JsonElement Summary(string title)
    {
        var actions = All.Where(n => Vis(n) && IsClickable(n)).Where(n => !All.Any(o => o != n && Contains(o, n) && o.Type is Uia.Button or Uia.Hyperlink))
            .Select(n => new { target = Target(n), text = Short(Txt(n), 50) is { Length: > 0 } t ? t : null, href = n.Type == Uia.Hyperlink && n.ValueText.StartsWith("http") ? n.ValueText : null, disabled = n.Enabled ? (bool?)null : true })
            .Where(a => a.target is not null).ToList();
        var inputs = All.Where(n => Vis(n) && IsField(n)).Select(n => new
        {
            target = Target(n), type = n.Type == Uia.ComboBox ? "select" : n.Password ? "password" : "text", label = Label(n) is { Length: > 0 } l ? l : null,
            value = n.Password ? (n.ValueText.Length > 0 ? "***" : null) : n.ValueText is { Length: > 0 } v ? v : null,
            required = n.Required ? (bool?)true : null, disabled = n.Enabled ? (bool?)null : true,
        }).ToList();
        var tables = All.Where(n => Vis(n) && n.Type is Uia.DataGrid or Uia.Table).Select(n =>
        {
            var (columns, rows) = Rows(n);
            return new { target = Target(n), columns, rows = rows.Count, sample = rows.Take(3).Select(r => r.Select(c => Short(c, 40)).ToList()).ToList() };
        }).ToList();
        var used = new HashSet<string?>(actions.Select(a => a.target).Concat(inputs.Select(i => i.target)).Concat(tables.Select(t => t.target)));
        var widgets = All.Where(Vis).Select(n => WidgetName(n.Id)).Where(n => n is not null && !used.Contains(n) && n is not ("reactContainer" or "transitionContainer")).Distinct().Take(60).ToList();
        var headings = All.Where(n => Vis(n) && (n.Aria == "heading" || Regex.IsMatch(n.Class, @"\bheading\d"))).Select(Txt).Where(t => t.Length > 0).Distinct().Take(15).ToList();
        return J(new { title, headings, feedback = FeedbackList(), invalid = InvalidList(), actions, inputs, tables, widgets });
    }
}
