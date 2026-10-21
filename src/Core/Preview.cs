using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OutSystems.Model;
using OutSystems.Model.UI;

namespace OmlCli;

// Reads the widget tree of one block or screen with the typed model and writes it as one static HTML file.
// It never changes the module. OutSystems UI is not available, so this is a layout and content check, not a pixel copy.
static class Preview
{
    internal sealed record Check(string id, string name, bool ok, string? detail);
    internal sealed record Result(string Html, string Element, string Kind, int Widgets, List<string> BlocksRendered,
        Dictionary<string, int> Unsupported, int JavaScriptNodes, List<string> Scripts, int AsyncNodes, List<string> Notes, List<Check> Checklist);
    // One interaction that runs in the page after the lifecycle JavaScript: wait (Ms), click (Selector), type (Selector, Value), eval (Value).
    internal sealed record Step(string Kind, string? Selector = null, string? Value = null, int Ms = 0)
    {
        public string Label => Kind switch
        {
            "wait" => $"wait {Ms}",
            "eval" => "eval " + (Value!.Length > 40 ? Value[..37] + "..." : Value),
            _ => $"{Kind} {Selector}",
        };
    }
    sealed record Val(string Display, string? Literal, bool Muted);

    sealed class Frame
    {
        public readonly Dictionary<string, string?> Scope = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Kinds = new(StringComparer.Ordinal); // input name -> text, number or bool
        public readonly Dictionary<string, string> Ids = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (List<object> widgets, Frame caller)> Slots = new(StringComparer.OrdinalIgnoreCase);
        public List<object> Stack = new();
    }

    const int MaxDepth = 5;
    static readonly string[] Lifecycle = ["OnInitialize", "OnReady", "OnRender"];

    // ---- command ----

    public static int Run(List<string> args)
    {
        string? theme = null, width = null, height = null, outPath = null, shot = null;
        bool serve = false, noOpen = false;
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        var steps = new List<Step>();
        var positional = new List<string>();
        for (var i = 1; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--serve") serve = true;
            else if (a == "--no-open") noOpen = true;
            else if (a is "--theme" or "--width" or "--height" or "--out" or "--screenshot" or "--arg" or "--wait" or "--click" or "--type" or "--eval")
            {
                if (i + 1 >= args.Count) throw new CliError($"{a} needs a value.", 2);
                var v = args[++i];
                if (a == "--theme") theme = v;
                else if (a == "--width") width = v;
                else if (a == "--height") height = v;
                else if (a == "--out") outPath = v;
                else if (a == "--screenshot") shot = v;
                else if (a == "--wait")
                    steps.Add(int.TryParse(v, out var ms) && ms is >= 0 and <= 60_000 ? new Step("wait", Ms: ms) : throw new CliError("--wait needs a whole number of milliseconds from 0 to 60000.", 2));
                else if (a == "--click")
                    steps.Add(v.Trim().Length > 0 ? new Step("click", v) : throw new CliError("--click needs a CSS selector, for example --click \"#SaveButton\".", 2));
                else if (a == "--eval")
                    steps.Add(v.Trim().Length > 0 ? new Step("eval", Value: v) : throw new CliError("--eval needs JavaScript code.", 2));
                else if (a == "--type")
                    steps.Add(TypeEquals(v) is var eq and > 0 ? new Step("type", v[..eq], v[(eq + 1)..])
                        : throw new CliError("--type needs <selector>=<text>, for example --type \"#NameInput=Ann\".", 2));
                else
                {
                    var eq = v.IndexOf('=');
                    if (eq <= 0) throw new CliError("--arg needs Name=<expression>, for example --arg Title=\"\\\"Hello\\\"\".", 2);
                    overrides[v[..eq]] = v[(eq + 1)..];
                }
            }
            else if (a.StartsWith("--")) throw new CliError("preview accepts --arg Name=<expression>, --theme light|dark, --width <px>, --height <px>, --out <file.html>, --screenshot <file.png>, --wait <ms>, --click <selector>, --type <selector>=<text>, --eval <js>, --serve and --no-open.", 2);
            else positional.Add(a);
        }
        if (positional.Count != 2) throw new CliError("Usage: " + Usage, 2);
        theme ??= "light";
        if (theme is not ("light" or "dark")) throw new CliError("--theme must be light or dark.", 2);
        if (!TryPixels(width ?? "1200", out var w) || !TryPixels(height ?? "900", out var h)) throw new CliError("--width and --height must be whole numbers from 200 to 4000.", 2);
        if (noOpen && !serve) throw new CliError("--no-open works only with --serve.", 2);
        var element = positional[1];
        if (serve)
        {
            if (outPath is not null || shot is not null) throw new CliError("--serve writes no files; leave out --out and --screenshot.", 2);
            return PreviewServer.Run(Path.GetFullPath(positional[0]), element, overrides, theme, w, steps, !noOpen);
        }
        var html = Path.GetFullPath(outPath ?? element + ".preview.html");
        var png = shot is null ? null : Path.GetFullPath(shot);
        foreach (var p in new[] { html, png }.OfType<string>())
            if (File.Exists(p) || Directory.Exists(p))
                throw new CliError($"Output already exists: {p}. Use a new output path, or delete that file first.", 2);
        if (string.Equals(html, png, StringComparison.OrdinalIgnoreCase)) throw new CliError("--out and --screenshot must be different files.", 2);

        using var open = Model.Borrow(Path.GetFullPath(positional[0])); var module = open.Module;
        var result = Render(module, element, overrides, theme, w, steps);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(html)!);
            using var stream = new FileStream(html, FileMode.CreateNew, FileAccess.Write);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(result.Html);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new CliError(e.Message, 2); }

        // The browser runs for a screenshot, or to play the interaction steps; the page reports what its scripts did.
        PreviewBrowser.Outcome? browser = png is null && steps.Count == 0 ? null : PreviewBrowser.Capture(html, png, w, h, Budget(result, steps));
        var page = browser?.Page;
        JsonElement? Part(string name) => page is { } p && p.TryGetProperty(name, out var v) ? v : null;
        var items = (string name) => Part(name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().ToList() : [];
        int? Total(string name, int shown) => Part(name) is { ValueKind: JsonValueKind.Number } t && t.GetInt32() > shown ? t.GetInt32() : null;
        // The JSON keeps the first few calls and messages, shortened; the page's #oml-preview-report keeps up to 100 of each.
        var stubbed = items("stubbed").Take(ReportItems).Select(x => Cut(x.GetString() ?? "")).ToList();
        var console = items("console").Take(ReportItems).Select(x => new { level = x.GetProperty("level").GetString(), text = Cut(x.GetProperty("text").GetString() ?? "") }).ToList();
        var errors = items("errors").Take(ReportItems).Select(x => new { node = x.GetProperty("node").GetString(), message = Cut(x.GetProperty("message").GetString() ?? "") }).ToList();
        return Cli.Emit(new
        {
            html, screenshot = browser?.Screenshot, screenshotNote = browser?.Note,
            element = result.Element, kind = result.Kind, theme, width = w,
            widgets = result.Widgets, blocksRendered = result.BlocksRendered,
            unsupported = result.Unsupported.Select(p => new { kind = p.Key, count = p.Value }).ToArray(),
            javaScriptNodes = result.JavaScriptNodes, scripts = result.Scripts,
            scriptsRan = browser is null ? (bool?)null : page is not null,
            scriptErrors = errors, errorsTotal = Total("errorsTotal", errors.Count), skipped = Part("skipped"),
            stubbed, stubbedTotal = Total("stubbedTotal", stubbed.Count), console, consoleTotal = Total("consoleTotal", console.Count),
            blocked = Part("blocked"), steps = Part("steps"),
            horizontalOverflow = browser?.HorizontalOverflow,
            notes = result.Notes.Concat(browser?.Notes ?? []).ToList(), checklist = result.Checklist,
        });
    }

    const int ReportItems = 20, ReportText = 160;
    static string Cut(string s) => s.Length > ReportText ? s[..(ReportText - 3)] + "..." : s;

    internal const string Usage = "oml preview <module.oml> <Block|Screen|Email> [--arg Name=<expression>]... [--theme light|dark] [--width <px>] [--height <px>] [--out <file.html>] [--screenshot <file.png>] [--wait <ms>] [--click <selector>] [--type <selector>=<text>] [--eval <js>] [--serve [--no-open]]";

    static bool TryPixels(string text, out int value) => int.TryParse(text, out value) && value is >= 200 and <= 4000;

    // Virtual time the headless browser gives the page: 1 s after load, every --wait, 50 ms per step, and the time each
    // asynchronous node or eval step may take (2 s each). Capped, so a module with many async nodes cannot stall the command.
    internal static int Budget(Result r, List<Step> steps) => (int)Math.Min(MaxBudgetMs,
        1000L + steps.Sum(s => (long)s.Ms) + 50L * steps.Count + AsyncTimeoutMs * (long)(r.AsyncNodes + steps.Count(s => s.Kind == "eval")));
    internal const int AsyncTimeoutMs = 2000, MaxBudgetMs = 60_000;

    // The '=' that ends the selector of --type: the first one outside [...] and quotes, so input[name=q]=Ann works.
    internal static int TypeEquals(string v)
    {
        var depth = 0;
        char quote = '\0';
        for (var i = 0; i < v.Length; i++)
        {
            var c = v[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
            else if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == '=' && depth == 0) return v[..i].Trim().Length > 0 ? i : -1;
        }
        return -1;
    }

    // ---- render ----

    public static Result Render(IESpace m, string name, IReadOnlyDictionary<string, string> overrides, string theme, int width, List<Step>? steps = null)
    {
        var emails = m.MobileFlows.SelectMany(ElementPaths.Children).OfType<OutSystems.Model.UI.IEmail>().ToList();
        var found = Inspect.Screens(m).Select(s => s.screen).Concat(emails).Where(s => Model.NameOf(s) == name).ToList();
        if (found.Count == 0)
            throw new CliError($"No screen, block or email named '{name}'. Blocks: {string.Join(", ", Inspect.Screens(m).Where(s => Inspect.IsBlock(s.screen)).Select(s => Model.NameOf(s.screen)))}. Screens: {string.Join(", ", Inspect.Screens(m).Where(s => !Inspect.IsBlock(s.screen)).Select(s => Model.NameOf(s.screen)))}."
                + (emails.Count > 0 ? $" Emails: {string.Join(", ", emails.Select(x => x.Name))}." : ""), 2);
        return new Renderer(m).Run(found[0], overrides, theme, width, steps ?? []);
    }

    sealed class Renderer(IESpace m)
    {
        readonly HashSet<string> usedIds = new(StringComparer.Ordinal);
        readonly HashSet<object> rendered = new(ReferenceEqualityComparer.Instance);
        readonly List<(string owner, string css)> styles = new();
        readonly HashSet<object> styled = new(ReferenceEqualityComparer.Instance);
        readonly List<(string owner, string node, string code)> jsNodes = new();
        readonly List<(string label, string code)> fnCodes = new();
        readonly Dictionary<object, int> fnIndex = new(ReferenceEqualityComparer.Instance);
        // A job is one JavaScript node for one rendered instance. Its label gets [n] when the owner is rendered more than once.
        readonly List<(int phase, int order, int seq, string owner, int instance, string rest, string json)> jobs = new();
        readonly Dictionary<string, int> instances = new(StringComparer.Ordinal);
        readonly List<(string label, string code)> scripts = new();
        readonly HashSet<object> scriptSeen = new(ReferenceEqualityComparer.Instance);
        readonly List<object> localScripts = Content.Items(m, "Scripts").ToList();
        int preOrder, postOrder, asyncNodes;
        readonly List<string> blocksRendered = new();
        readonly Dictionary<string, int> unsupported = new(StringComparer.Ordinal);
        readonly List<string> notes = new();
        readonly HashSet<string> externalBlocks = new(StringComparer.Ordinal);
        readonly List<(string owner, string kind, string text)> labels = new();
        readonly List<(string owner, string text)> literals = new();
        readonly List<(string owner, List<string> rootClasses, bool hasRoot)> roots = new();
        readonly List<IObjectSignature> localBlocks = Inspect.Screens(m).Where(s => Inspect.IsBlock(s.screen)).Select(s => s.screen).ToList();
        int ifCount, depthLimited;

        public Result Run(IObjectSignature element, IReadOnlyDictionary<string, string> overrides, string theme, int width, List<Step> steps)
        {
            var kind = element is OutSystems.Model.UI.IEmail ? "Email" : Inspect.IsBlock(element) ? "Block" : "Screen";
            var name = Model.NameOf(element)!;
            var frame = new Frame { Stack = [element] };
            var inputs = Describe.List(element, "InputParameters").ToList();
            foreach (var key in overrides.Keys.Where(k => !inputs.Any(p => Model.NameOf(p) == k)))
                throw new CliError($"{name} has no input '{key}'. Inputs: {string.Join(", ", inputs.Select(p => Model.NameOf(p)))}.", 2);
            foreach (var p in inputs)
            {
                var pn = Model.NameOf(p)!;
                if (overrides.TryGetValue(pn, out var given))
                {
                    var v = Eval(given, null);
                    if (v.Literal is null) { v = new Val(given, Quote(given), false); notes.Add($"--arg {pn} is not a literal; used as plain text."); }
                    frame.Scope[pn] = v.Literal;
                }
                else frame.Scope[pn] = DefaultLiteral(p);
                if (TypeKind(Model.Describe(m, Reflect.Get(p, "DataType") as OutSystems.Model.Types.ITypeSignature)) is { } kindOf) frame.Kinds[pn] = kindOf;
            }

            // Module scripts first, as the app loads them for every screen: the module's required scripts, then its JavaScript.
            foreach (var s in Content.Items(m, "RequiredScripts")) Require(s);
            if (Reflect.Get(m, "JavaScript") is string moduleJs && moduleJs.Trim().Length > 0) scripts.Add(("(module JavaScript)", moduleJs));
            var pre = preOrder++;
            var body = Body(element, frame);
            Scripts(element, frame, pre);
            var sb = new StringBuilder();
            sb.Append("<!doctype html>\n<html lang=\"en\" data-theme=\"").Append(theme).Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
              .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(Csp).Append("\">\n")
              .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>")
              .Append(Enc(name)).Append(" preview</title>\n<style id=\"oml-base\">").Append(BaseCss).Append("</style>\n");
            var themeCss = ThemeCss();
            if (themeCss is not null) styles.Insert(0, ("(theme)", themeCss));
            foreach (var (owner, css) in styles)
                sb.Append("<style data-source=\"").Append(Enc(owner)).Append("\">\n").Append(css.Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase)).Append("\n</style>\n");
            sb.Append(Runtime).Append("</head>\n<body>\n");
            // Scripts run at the top of <body>, before the widgets exist, as in the app. The marker names the script in a syntax error.
            foreach (var (label, code) in scripts)
                sb.Append("<script>__omlAt(").Append(Json("script " + label)).Append(");</script>\n<script data-oml-script=\"").Append(Enc(label)).Append("\">\n")
                  .Append(ScriptSafe(code)).Append("\n</script>\n");
            sb.Append("<script>__omlAt('page');</script>\n<div class=\"oml-frame\" id=\"oml-frame\" style=\"width:").Append(width).Append("px\">\n")
              .Append(body).Append("\n</div>\n<pre id=\"oml-preview-errors\" hidden></pre>\n<pre id=\"oml-preview-report\" hidden></pre>\n");
            for (var i = 0; i < fnCodes.Count; i++)
                sb.Append("<script>__omlAt(").Append(Json(fnCodes[i].label)).Append(");</script>\n<script>__omlFns[").Append(i)
                  .Append("]=function($parameters, $actions, $roles, $public, $resolve, $reject){\n").Append(ScriptSafe(fnCodes[i].code)).Append("\n};</script>\n");
            // OnInitialize runs parent first; OnReady and OnRender run children first, as in the app.
            var ordered = jobs.OrderBy(j => j.phase).ThenBy(j => j.order).ThenBy(j => j.seq)
                .Select(j => $"{{n:{Json(j.owner + (instances[j.owner] > 1 ? $"[{j.instance}]" : "") + j.rest)},{j.json}}}");
            var stepJson = steps.Select(s => s.Kind switch
            {
                "wait" => (object)new { k = s.Kind, d = s.Label, v = s.Ms },
                "eval" => new { k = s.Kind, d = s.Label, v = s.Value },
                _ => new { k = s.Kind, d = s.Label, s = s.Selector, v = s.Value },
            });
            sb.Append("<script>__omlAt('page');__omlRun([").Append(string.Join(",\n", ordered)).Append("],").Append(Json(stepJson))
              .Append(",").Append(AsyncTimeoutMs).Append(");</script>\n</body>\n</html>\n");

            Notes(theme, steps.Count);
            var loaded = scripts.Select(s => s.label).ToList();
            var unused = localScripts.Where(s => !scriptSeen.Contains(s)).Select(Model.NameOf).OfType<string>().Order(StringComparer.Ordinal).ToList();
            if (unused.Count > 0) notes.Add($"Scripts of the module that this {kind.ToLowerInvariant()} does not require were not loaded: {string.Join(", ", unused)}. Add them to requiredScripts if its code needs them.");
            return new Result(sb.ToString(), name, kind, rendered.Count, blocksRendered, unsupported,
                jsNodes.Count, loaded, asyncNodes, notes, PreviewChecks.Run(labels, literals, roots, jsNodes, styles));
        }

        // "</script" would end the element; "<!--" with a later "<script" can make the HTML parser swallow the end tag.
        // "<!--" with a later "<script" can make the HTML parser swallow the end tag. \x3C is "<" in a string, a template and
        // a regex (also with the u flag), so the code means the same; only "<!--" as an old-style comment outside them changes.
        static string ScriptSafe(string code) =>
            code.Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase).Replace("<!--", "\\x3C!--");

        // A Script element and, first, the scripts it requires. Scripts of other modules have no code here; a note says so.
        void Require(object script)
        {
            if (!scriptSeen.Add(script)) return;
            foreach (var dependency in Content.Items(script, "RequiredScripts")) Require(dependency);
            var name = Model.NameOf(script) ?? "?";
            if (localScripts.Any(s => ReferenceEquals(s, script)))
                scripts.Add((name, Reflect.Get(script, "JavaScript") as string ?? ""));
            else
            {
                var producer = m.References.FirstOrDefault(r => ModuleReferences.Elements(r).Any(e => ReferenceEquals(e, script)))?.Name ?? "another module";
                notes.Add($"Script {name} of {producer} was not loaded: its code is not in this module. Code that needs it fails in the preview.");
            }
        }

        void Notes(string theme, int steps)
        {
            notes.Insert(0, "OutSystems UI styles, fonts and JavaScript (OSUI, OutSystems.*) are not available. Classes such as card or btn are not styled here, and code that calls OutSystems UI fails here; check them in the published app.");
            notes.Add($"data-theme=\"{theme}\" is set on <html>. Only CSS that reads it (or CSS variables) changes with the theme.");
            if (ifCount > 0) notes.Add($"{ifCount} If widget(s) rendered as the true branch.");
            if (externalBlocks.Count > 0) notes.Add("Blocks from other modules are shown as a div with the block name as class, with their placeholder content: " + string.Join(", ", externalBlocks) + ".");
            if (depthLimited > 0) notes.Add($"{depthLimited} nested block(s) not expanded (more than {MaxDepth} levels deep, or a cycle).");
            notes.Add("List and table rows repeat the row template 3 times. Expressions that depend on data show as {expression}.");
            if (jsNodes.Count > 0) notes.Add("JavaScript nodes of OnInitialize, OnReady and OnRender run in flow order, once per rendered instance, after the page loaded (OnInitialize too). $actions, $roles, $public and controller are stubs that log their calls in stubbed. Client actions called from those actions are not followed.");
            if (steps > 0) notes.Add("The interaction steps are in the HTML file too, so they run again when you open it.");
        }

        string? ThemeCss()
        {
            foreach (var key in new[] { "DefaultMobileTheme", "DefaultWebTheme", "DefaultTheme" })
                if (Reflect.Get(m, key) is { } t && Reflect.Get(t, "StyleSheet") is string css && css.Length > 0
                    && (Reflect.Get(m, "MobileThemes") as System.Collections.IEnumerable ?? Array.Empty<object>()).Cast<object>().Concat(
                        (Reflect.Get(m, "WebThemes") as System.Collections.IEnumerable ?? Array.Empty<object>()).Cast<object>()).Any(x => ReferenceEquals(x, t)))
                    return css;
            return null;
        }

        // ---- blocks and screens ----

        string Body(IObjectSignature owner, Frame f)
        {
            var name = Model.NameOf(owner) ?? "";
            if (styled.Add(owner))
            {
                if (Reflect.Get(owner, "StyleSheet") is string css && css.Trim().Length > 0) styles.Add((name, css));
                foreach (var s in Content.Items(owner, "RequiredScripts")) Require(s);
            }
            var top = Kids(owner).ToList();
            if (roots.All(r => r.owner != name))
            {
                var root = top.FirstOrDefault(k => ElementPaths.Kind(k) == "Container");
                roots.Add((name, root is null ? [] : ClassNames(Text(Reflect.Get(root, "Style"))), root is not null));
            }
            return string.Concat(top.Select(k => W(k, f, name)));
        }

        static IEnumerable<object> Kids(object w) =>
            w is IObjectSignature o ? ElementPaths.Children(o).Where(c => c is IWidgetSignature).Cast<object>() : [];

        string Kids(object w, Frame f, string owner) => string.Concat(Kids(w).Select(k => W(k, f, owner)));

        string Repeat(object w, Frame f, string owner, int times = 3)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < times; i++) sb.Append(Kids(w, f, owner));
            return sb.ToString();
        }

        // ---- widgets ----

        string W(object w, Frame f, string owner)
        {
            rendered.Add(w);
            var kind = ElementPaths.Kind(w);
            var name = Model.NameOf(w);
            var id = Id(f, name);
            var cls = Text(Reflect.Get(w, "Style")) is { Length: > 0 } s ? s : Text(Reflect.Get(w, "StyleClasses"));
            var classes = string.Join(" ", ClassNames(cls));
            var hidden = Text(Reflect.Get(w, "Visible")).Equals("False", StringComparison.OrdinalIgnoreCase) ? " hidden" : "";
            var (ext, extClass) = ExtendedAttributes(w, f);
            string A(string extra = "", string more = "") => (id is null ? "" : $" id=\"{Enc(id)}\"")
                + ((classes + " " + extClass + " " + more).Trim() is { Length: > 0 } all ? $" class=\"{Enc(all)}\"" : "") + hidden + ext + extra;
            string Tag(string tag, string inner, string extra = "") => $"<{tag}{A(extra)}>{inner}</{tag}>";

            if (kind.EndsWith("BlockInstance", StringComparison.Ordinal)) return BlockInstance(w, f, owner, id, hidden);
            switch (kind)
            {
                case "Container": return Tag("div", Kids(w, f, owner));
                case "Text":
                {
                    var text = Reflect.Get(w, "Text") as string ?? "";
                    literals.Add((owner, text));
                    return classes.Length > 0 ? Tag("span", Enc(text)) : Enc(text);
                }
                case "Expression":
                {
                    var v = Eval(Text(Reflect.Get(w, "Value")), f.Scope);
                    if (v.Literal is not null) literals.Add((owner, v.Display));
                    return $"<span{A("", v.Muted ? "oml-muted" : "")}>{Enc(v.Display)}</span>";
                }
                case "Link":
                    labels.Add((owner, "Link", PlainText(w, f)));
                    return Tag("a", Kids(w, f, owner), " href=\"#\"");
                case "Button":
                    labels.Add((owner, "Button", PlainText(w, f)));
                    return Tag("button", Kids(w, f, owner), " type=\"button\"");
                case "Image": return Image(w, f, A);
                case "Icon":
                    return $"<i{(id is null ? "" : $" id=\"{Enc(id)}\"")} class=\"{Enc((classes + " " + (Reflect.Get(w, "Icon") as string)).Trim())}\" aria-hidden=\"true\"{hidden}></i>";
                case "Input":
                {
                    var type = (Reflect.Get(w, "InputType")?.ToString() ?? "").ToLowerInvariant() switch
                    { var t when t.Contains("password") => "password", var t when t.Contains("email") => "email", var t when t.Contains("phone") => "tel",
                      var t when t.Contains("number") || t.Contains("integer") || t.Contains("decimal") => "number", var t when t.Contains("date") => "date", _ => "text" };
                    var v = Eval(Text(Reflect.Get(w, "Variable")), f.Scope);
                    var prompt = Eval(Text(Reflect.Get(w, "Prompt")), f.Scope);
                    return $"<input type=\"{type}\"{A()}{(v.Muted ? "" : $" value=\"{Enc(v.Display)}\"")}{(prompt.Literal is null ? "" : $" placeholder=\"{Enc(prompt.Display)}\"")}>";
                }
                case "TextArea": return Tag("textarea", Enc(Eval(Text(Reflect.Get(w, "Variable")), f.Scope) is { Muted: false } tv ? tv.Display : ""));
                case "Dropdown": return Tag("select", "<option>Option 1</option><option>Option 2</option><option>Option 3</option>");
                case "Checkbox": return $"<input type=\"checkbox\"{A(Eval(Text(Reflect.Get(w, "Variable")), f.Scope).Display == "True" ? " checked" : "")}>";
                case "Switch": return $"<input type=\"checkbox\" role=\"switch\"{A(Eval(Text(Reflect.Get(w, "Variable")), f.Scope).Display == "True" ? " checked" : "")}>";
                case "RadioButton": return $"<input type=\"radio\"{A()}>";
                case "RadioGroup": return Tag("div", Kids(w, f, owner), " role=\"radiogroup\"");
                case "ButtonGroup": return Tag("div", Kids(w, f, owner), " role=\"group\"");
                case "ButtonGroupItem": return Tag("button", Kids(w, f, owner), " type=\"button\"");
                case "Label": return Tag("label", Kids(w, f, owner));
                case "Form": return Tag("form", Kids(w, f, owner));
                case "List": return Tag("div", Repeat(w, f, owner));
                case "ListItem": return Tag("div", string.Concat(Kids(w).Select(k => W(k, f, owner))), " data-oml=\"list-item\"");
                case "If":
                {
                    ifCount++;
                    var branch = Reflect.Get(w, "TrueBranch") ?? Kids(w).FirstOrDefault();
                    return branch is null ? "" : $"<!-- if {Enc(Text(Reflect.Get(w, "Condition")).Replace("--", "- -"))}: true branch -->" + Kids(branch, f, owner);
                }
                case "TableRecords":
                {
                    var cells = Kids(w).Where(k => ElementPaths.Kind(k) == "CustomPlaceholderWidget").ToList();
                    var head = cells.FirstOrDefault(c => string.Equals(Model.NameOf(c), "headerRow", StringComparison.OrdinalIgnoreCase));
                    var row = cells.FirstOrDefault(c => string.Equals(Model.NameOf(c), "row", StringComparison.OrdinalIgnoreCase));
                    var showHeader = Reflect.Get(w, "ShowHeader") as bool? ?? true;
                    var sb = new StringBuilder();
                    if (showHeader && head is not null) sb.Append("<thead><tr>").Append(Kids(head, f, owner)).Append("</tr></thead>");
                    sb.Append("<tbody>");
                    if (row is not null) for (var i = 0; i < 3; i++) sb.Append("<tr>").Append(Kids(row, f, owner)).Append("</tr>");
                    return Tag("table", sb.Append("</tbody>").ToString());
                }
                case "HeaderCell": return Tag("th", Kids(w, f, owner));
                case "RowCell": return Tag("td", Kids(w, f, owner));
                case "CustomPlaceholderWidget":
                    return Model.NameOf(w) is "LeftActions" or "RightActions" ? "" : Kids(w, f, owner);
                case "Placeholder": return Placeholder(w, f, owner, name ?? "Content", A);
                case "AdvancedHtml":
                {
                    var tag = Reflect.Get(w, "Tag") as string ?? "";
                    if (!Regex.IsMatch(tag, "^[a-zA-Z][a-zA-Z0-9-]*$") || tag.Equals("script", StringComparison.OrdinalIgnoreCase)) tag = "div";
                    return VoidTags.Contains(tag.ToLowerInvariant()) ? $"<{tag}{A()}>" : Tag(tag, Kids(w, f, owner));
                }
                default:
                    unsupported[kind] = unsupported.GetValueOrDefault(kind) + 1;
                    return $"<div{A($" data-oml-kind=\"{Enc(kind)}\"", "oml-unknown")}><span class=\"oml-tag\">{Enc(kind)}{(name is null ? "" : " " + Enc(name))}</span>{Kids(w, f, owner)}</div>";
            }
        }

        static readonly HashSet<string> VoidTags = ["br", "hr", "img", "input", "meta", "link", "wbr"];

        // Extended properties are extra HTML attributes (aria-label, data-*, class). Only literal values and safe names are kept.
        static (string attributes, string cssClass) ExtendedAttributes(object w, Frame f)
        {
            var sb = new StringBuilder();
            var cssClass = "";
            foreach (var p in Describe.List(w, "ExtendedProperties"))
            {
                var name = Reflect.Get(p, "Property") as string ?? "";
                if (!Regex.IsMatch(name, @"^[a-zA-Z_:][-a-zA-Z0-9_:.]*$") || name.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("id", StringComparison.OrdinalIgnoreCase) || Eval(Text(Reflect.Get(p, "Value")), f.Scope) is not { Literal: not null } v) continue;
                if (name.Equals("class", StringComparison.OrdinalIgnoreCase)) cssClass += " " + v.Display;
                else sb.Append($" {name}=\"{Enc(v.Display)}\"");
            }
            return (sb.ToString(), cssClass.Trim());
        }

        string Placeholder(object w, Frame f, string owner, string name, Func<string, string, string> a)
        {
            if (f.Slots.TryGetValue(name, out var slot))
                return $"<div{a($" data-placeholder=\"{Enc(name)}\"", "")}>" + string.Concat(slot.widgets.Select(k => W(k, slot.caller, owner))) + "</div>";
            return $"<div{a($" data-placeholder=\"{Enc(name)}\"", "oml-placeholder")}><span class=\"oml-tag\">{Enc(name)}</span> Sample content for this placeholder.</div>";
        }

        string Image(object w, Frame f, Func<string, string, string> a)
        {
            var url = Eval(Text(Reflect.Get(w, "Url")), f.Scope);
            if (url.Literal is not null && url.Display.Length > 0) return $"<img{a("", "")} src=\"{Enc(url.Display)}\" alt=\"\">";
            var label = Reflect.Get(w, "Image") is { } img ? Model.NameOf(img) : url.Display.Length > 0 ? url.Display : "image";
            return $"<div{a(" role=\"img\" data-oml-image", "")}>{Enc(label ?? "image")}</div>";
        }

        string BlockInstance(object w, Frame f, string owner, string? id, string hidden)
        {
            var source = Reflect.Get(w, "SourceBlock");
            var blockName = source is null ? "?" : Model.NameOf(source) ?? "?";
            var slotsHere = Describe.List(w, "PlaceholdersContent").ToList();
            string Slots() => string.Concat(slotsHere.Select(pc =>
                $"<div class=\"oml-slot\" data-placeholder=\"{Enc(Model.NameOf(Reflect.Get(pc, "Placeholder") ?? pc) ?? "")}\">{Kids(pc, f, owner)}</div>"));
            var idAttr = id is null ? "" : $" id=\"{Enc(id)}\"";
            object? local = source is null ? null : localBlocks.FirstOrDefault(b => ReferenceEquals(b, source));
            if (source is null || local is null)
            {
                externalBlocks.Add(blockName);
                return $"<div{idAttr} class=\"{Enc(blockName)}\" data-oml-block=\"{Enc(blockName)}\" data-oml-external{hidden}>{Slots()}</div>";
            }
            if (f.Stack.Count > MaxDepth || f.Stack.Any(b => ReferenceEquals(b, local)))
            {
                depthLimited++;
                return $"<div{idAttr} class=\"oml-unknown\" data-oml-block=\"{Enc(blockName)}\"><span class=\"oml-tag\">{Enc(blockName)} not expanded</span></div>";
            }
            blocksRendered.Add(blockName);
            var inner = new Frame { Stack = [.. f.Stack, local] };
            foreach (var p in Describe.List(local, "InputParameters"))
            {
                var pn = Model.NameOf(p)!;
                var arg = Describe.List(w, "Arguments").FirstOrDefault(x => Reflect.Get(x, "Parameter") is { } q && Model.NameOf(q) == pn);
                var expr = arg is null ? "" : Text(Reflect.Get(arg, "Value"));
                inner.Scope[pn] = expr.Trim().Length > 0 ? Eval(expr, f.Scope).Literal : DefaultLiteral(p);
                if (TypeKind(Model.Describe(m, Reflect.Get(p, "DataType") as OutSystems.Model.Types.ITypeSignature)) is { } kindOf) inner.Kinds[pn] = kindOf;
            }
            foreach (var pc in slotsHere)
                if (Model.NameOf(Reflect.Get(pc, "Placeholder") ?? pc) is { } slotName)
                    inner.Slots[slotName] = (Kids(pc).ToList(), f);
            var pre = preOrder++;
            var body = Body((IObjectSignature)local, inner);
            Scripts((IObjectSignature)local, inner, pre);
            return $"<div{idAttr} data-oml-block=\"{Enc(blockName)}\" style=\"display:contents\"{hidden}>{body}</div>";
        }

        string PlainText(object w, Frame f)
        {
            var kind = ElementPaths.Kind(w);
            if (kind == "Text") return Reflect.Get(w, "Text") as string ?? "";
            if (kind == "Expression") return Eval(Text(Reflect.Get(w, "Value")), f.Scope) is { Literal: not null } v ? v.Display : "";
            return string.Concat(Kids(w).Select(k => PlainText(k, f)));
        }

        string? Id(Frame f, string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var id = name;
            for (var n = 2; !usedIds.Add(id); n++) id = name + "-" + n;
            f.Ids.TryAdd(name, id);
            return id;
        }

        // ---- JavaScript ----

        // One job per JavaScript node of each lifecycle action, for this rendered instance. `pre` is the instance's place
        // before its children render (OnInitialize order); the place after them is taken here (OnReady and OnRender order).
        void Scripts(IObjectSignature owner, Frame f, int pre)
        {
            var ownerName = Model.NameOf(owner) ?? "";
            var post = postOrder++;
            var instance = instances[ownerName] = instances.GetValueOrDefault(ownerName) + 1;
            // An input that an Assign of this screen or block changes may differ from its start value when an If runs.
            var assigned = Describe.List(owner, "ScreenActions").SelectMany(a => Describe.List(a, "Nodes"))
                .SelectMany(n => Describe.List(n, "Assignments")).Select(a => Regex.Match(Text(Reflect.Get(a, "Variable")), @"^\s*(\w+)").Groups[1].Value).ToHashSet();
            var conditionScope = f.Scope.Where(p => !assigned.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            for (var phase = 0; phase < Lifecycle.Length; phase++)
            {
                var key = Lifecycle[phase];
                if (Reflect.Get(owner, key) is not { } evt || Reflect.Get(evt, "Destination") is not { } action) continue;
                foreach (var node in JsNodes(action, conditionScope, f.Kinds, $"{ownerName}.{key}"))
                {
                    var label = $"{ownerName}.{key}.{Model.NameOf(node)}";
                    var code = Reflect.Get(node, "JavaScript") as string ?? "";
                    if (!fnIndex.TryGetValue(node, out var index))
                    {
                        index = fnCodes.Count;
                        fnIndex[node] = index;
                        fnCodes.Add((label, code));
                        jsNodes.Add((ownerName, Model.NameOf(node) ?? "JavaScript", code));
                    }
                    var parameters = new Dictionary<string, object?>();
                    foreach (var p in Describe.List(node, "InputParameters")) parameters[Model.NameOf(p)!] = null;
                    foreach (var arg in Describe.List(node, "Arguments"))
                        if (Reflect.Get(arg, "Parameter") is { } parameter && Model.NameOf(parameter) is { } pn)
                            parameters[pn] = JsValue(Text(Reflect.Get(arg, "Value")), f);
                    // The app makes a node asynchronous when its code uses $resolve or $reject; the flow then waits for it.
                    var isAsync = Regex.IsMatch(code, @"\$(resolve|reject)\b");
                    if (isAsync) asyncNodes++;
                    jobs.Add((phase, phase == 0 ? pre : post, jobs.Count, ownerName, instance, $".{key}.{Model.NameOf(node)}",
                        $"g:'{post}.{phase}',c:{Json(fnCodes[index].label)},f:{index},p:{Json(parameters)},async:{(isAsync ? "true" : "false")}"));
                }
            }
        }

        // Every JavaScript node reachable from Start, in connector order. An If whose condition the preview can evaluate
        // (literals and inputs, compared with = or <>) follows only that branch; any other If or Switch follows every branch,
        // and a note names it.
        List<object> JsNodes(object action, Dictionary<string, string?> scope, Dictionary<string, string> kinds, string where)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var found = new List<object>();
            var start = Describe.List(action, "Nodes").FirstOrDefault(n => ElementPaths.Kind(n) is "Start" or "StartNode");
            void Walk(object node)
            {
                if (!seen.Add(node)) return;
                var kind = ElementPaths.Kind(node);
                if (kind.Equals("JavascriptNode", StringComparison.OrdinalIgnoreCase)) found.Add(node);
                if (node is not IObjectSignature o) return;
                var connectors = ElementPaths.Children(o).Where(c => Reflect.Get(c, "TargetNode") is not null).ToList();
                if (node is OutSystems.Model.Logic.Nodes.IIfNode)
                {
                    var condition = Text(Reflect.Get(node, "Condition"));
                    if (Condition(condition, scope, kinds) is bool b)
                        connectors = connectors.Where(c => ElementPaths.Kind(c).Equals(b ? "True" : "False", StringComparison.OrdinalIgnoreCase)).ToList();
                    else Note($"If {Model.NameOf(node)} in {where} cannot be evaluated here ({condition}), so the JavaScript nodes of both branches run.");
                }
                var targets = connectors.Select(c => Reflect.Get(c, "TargetNode")!).ToList();
                if (node is OutSystems.Model.Logic.Nodes.ISwitchNode)
                {
                    Note($"Switch {Model.NameOf(node)} in {where}: the JavaScript nodes of every branch run.");
                    // A Switch keeps its case targets on each condition's Link (as describe reads them).
                    targets.AddRange(Describe.List(node, "Conditions").Select(c => Reflect.Get(Reflect.Get(c, "Link") ?? c, "TargetNode")).OfType<object>());
                }
                foreach (var t in targets) Walk(t);
            }
            if (start is not null) Walk(start);
            return found;
        }

        void Note(string text) { if (!notes.Contains(text)) notes.Add(text); }

        object? JsValue(string expr, Frame f)
        {
            expr = expr.Trim();
            var id = Regex.Match(expr, @"^(\w+)\.Id$");
            if (id.Success) return f.Ids.TryGetValue(id.Groups[1].Value, out var rendered) ? rendered : null;
            return FromLiteral(Eval(expr, f.Scope).Literal);
        }

        // An OutSystems literal (text, number, True or False) as a JavaScript value. No literal gives null.
        static object? FromLiteral(string? literal)
        {
            if (literal is null) return null;
            if (Str.Match(literal) is { Success: true } s) return s.Groups[1].Value.Replace("\"\"", "\"");
            if (Num.IsMatch(literal)) return double.Parse(literal, System.Globalization.CultureInfo.InvariantCulture);
            return literal.Equals("True", StringComparison.OrdinalIgnoreCase);
        }

        // JSON for a <script>: every "<" is \u003c, so neither "</script" nor "<!--" appears in the page.
        static string Json(object? value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace("<", "\\u003c");

        // ---- expressions ----

        static string? DefaultLiteral(object parameter)
        {
            var text = Text(Reflect.Get(parameter, "DefaultValue"));
            if (text.Length == 0 && parameter is IObjectSignature o)
                text = ElementPaths.Children(o).OfType<IExpression>().Select(e => e.Text).FirstOrDefault() ?? "";
            return text.Length == 0 ? null : Eval(text, null).Literal;
        }
    }

    // ---- helpers shared with the checks ----

    internal static readonly Regex Str = new("^\"((?:[^\"]|\"\")*)\"$", RegexOptions.Singleline);
    static readonly Regex Num = new(@"^-?\d+(\.\d+)?$");
    static readonly Regex Ident = new(@"^[A-Za-z_]\w*$");
    internal static string Enc(string s) => WebUtility.HtmlEncode(s);
    static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    internal static string Text(object? o) => o is IExpression e ? e.Text : o as string ?? "";

    // Class names in a Style expression: every quoted piece, so "card " + If(x, "a", "b") still yields card, a and b.
    internal static List<string> ClassNames(string expression) =>
        Regex.Matches(expression, "\"((?:[^\"]|\"\")*)\"").SelectMany(x => x.Groups[1].Value.Replace("\"\"", "\"").Split(' ', StringSplitOptions.RemoveEmptyEntries)).Distinct().ToList();

    // Literals, input parameters in scope, and + joins of those. Anything else shows as {expression}.
    static Val Eval(string? expression, Dictionary<string, string?>? scope)
    {
        var e = (expression ?? "").Trim();
        if (e.Length == 0) return new Val("", null, false);
        if (Str.Match(e) is { Success: true } s) return new Val(s.Groups[1].Value.Replace("\"\"", "\""), e, false);
        if (Num.IsMatch(e) || e.Equals("True", StringComparison.OrdinalIgnoreCase) || e.Equals("False", StringComparison.OrdinalIgnoreCase)) return new Val(e, e, false);
        if (Ident.IsMatch(e) && scope is not null && scope.TryGetValue(e, out var value))
            return value is null ? new Val("{" + e + "}", null, true) : Eval(value, null);
        var parts = SplitPlus(e);
        if (parts.Count > 1)
        {
            // A text join only when a part is text: Count + 1 is a sum, and showing "31" would be wrong.
            var values = parts.Select(p => Eval(p, scope)).ToList();
            if (values.All(v => v.Literal is not null) && values.Any(v => Str.IsMatch(v.Literal!)))
            { var joined = string.Concat(values.Select(v => v.Display)); return new Val(joined, Quote(joined), false); }
        }
        return new Val("{" + e + "}", null, true);
    }

    // A flow If condition: True, False, a Boolean input, not X, or two values of the same kind compared with = or <>. Null
    // when the preview cannot tell: any other operator, a function, a local variable, data, an input whose value does not
    // match its declared type (kinds), texts that differ only in case. The caller leaves inputs that an Assign changes out of scope.
    internal static bool? Condition(string? expression, Dictionary<string, string?>? scope, Dictionary<string, string>? kinds = null)
    {
        var e = (expression ?? "").Trim();
        while (e.Length > 1 && e[0] == '(' && e[^1] == ')' && Outer(e[1..^1]).balanced) e = e[1..^1].Trim();
        if (Regex.Match(e, @"^not\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline) is { Success: true } not)
            return Condition(not.Groups[1].Value, scope, kinds) is bool inner ? !inner : null;
        var o = Outer(e);
        if (!o.balanced || o.unsupported) return null;
        if (o.at < 0) return Side(e, scope, kinds) is { } l && LiteralKind(l) == "bool" ? bool.Parse(l) : null;
        if (Side(e[..o.at], scope, kinds) is not { } left || Side(e[(o.at + o.length)..], scope, kinds) is not { } right) return null;
        if (LiteralKind(left) is not { } kind || kind != LiteralKind(right)) return null;
        bool equal;
        if (kind == "number")
            equal = decimal.Parse(left, System.Globalization.CultureInfo.InvariantCulture) == decimal.Parse(right, System.Globalization.CultureInfo.InvariantCulture);
        else if (kind == "bool") equal = bool.Parse(left) == bool.Parse(right);
        else
        {
            var (a, b) = (Eval(left, null).Display, Eval(right, null).Display);
            // Whether OutSystems compares these texts with or without case is not certain here, so a case-only difference is unknown.
            if (a != b && string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return null;
            equal = a == b;
        }
        return o.length == 1 ? equal : !equal;
    }

    // One side of a condition as a literal. An input whose value does not have the kind of its declared type is unknown
    // (for example an --arg that was used as plain text for an Integer input).
    static string? Side(string side, Dictionary<string, string?>? scope, Dictionary<string, string>? kinds)
    {
        var s = side.Trim();
        if (Eval(s, scope).Literal is not { } literal) return null;
        if (Ident.IsMatch(s) && kinds is not null && scope is not null && scope.ContainsKey(s))
            return kinds.TryGetValue(s, out var declared) && declared == LiteralKind(literal) ? literal : null;
        return literal;
    }

    static string? LiteralKind(string literal) =>
        Str.IsMatch(literal) ? "text" : Num.IsMatch(literal) ? "number"
        : literal.Equals("True", StringComparison.OrdinalIgnoreCase) || literal.Equals("False", StringComparison.OrdinalIgnoreCase) ? "bool" : null;

    // The literal kind of a declared OutSystems type, or null when the preview does not model it (dates, records, ...).
    internal static string? TypeKind(string? type) => type switch
    {
        "Text" or "Email" or "Phone Number" => "text",
        "Integer" or "Long Integer" or "Decimal" or "Currency" => "number",
        "Boolean" => "bool",
        _ => null,
    };

    // The first top-level "=" or "<>" (outside quotes and brackets). Another comparison or a top-level and/or is unsupported.
    static (bool balanced, int at, int length, bool unsupported) Outer(string e)
    {
        int depth = 0, at = -1, length = 0;
        bool quoted = false, unsupported = false;
        for (var i = 0; i < e.Length; i++)
        {
            var c = e[i];
            if (c == '"') quoted = !quoted;
            else if (quoted) continue;
            else if (c == '(') depth++;
            else if (c == ')') { if (--depth < 0) return (false, -1, 0, true); }
            else if (depth > 0) continue;
            else if (c == '<' && i + 1 < e.Length && e[i + 1] == '>') { if (at < 0) (at, length) = (i, 2); i++; }
            else if (c == '=') { if (at < 0) (at, length) = (i, 1); }
            else if (c is '<' or '>') unsupported = true;
            else if (char.IsWhiteSpace(c) && Regex.IsMatch(e[i..], @"^\s+(and|or)\s", RegexOptions.IgnoreCase)) unsupported = true;
        }
        return (depth == 0 && !quoted, at, length, unsupported);
    }

    static List<string> SplitPlus(string e)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        var quoted = false;
        for (var i = 0; i < e.Length; i++)
        {
            if (e[i] == '"') quoted = !quoted;
            else if (quoted) continue;
            else if (e[i] == '(') depth++;
            else if (e[i] == ')') depth--;
            else if (e[i] == '+' && depth == 0) { parts.Add(e[start..i]); start = i + 1; }
        }
        parts.Add(e[start..]);
        return parts;
    }

    // ---- page parts ----

    const string BaseCss = """
        *,*::before,*::after{box-sizing:border-box}
        html{font-family:system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;line-height:1.5;color:#1b1f24;background:#fff}
        html[data-theme=dark]{color-scheme:dark;color:#e6e8eb;background:#14171c}
        body{margin:0}
        .oml-frame{margin:0}
        a{color:#2563eb}html[data-theme=dark] a{color:#8ab4ff}
        button,input,select,textarea{font:inherit;color:inherit}
        button{padding:.4em .9em;border:1px solid #8a919a;border-radius:6px;background:#f3f4f6;cursor:pointer}
        html[data-theme=dark] button{background:#262b33;border-color:#4a515b}
        input:not([type=checkbox]):not([type=radio]),select,textarea{padding:.35em .5em;border:1px solid #8a919a;border-radius:4px;background:transparent}
        table{border-collapse:collapse}th,td{padding:.4em .7em;border-bottom:1px solid #c8ccd2;text-align:left}
        html[data-theme=dark] th,html[data-theme=dark] td{border-color:#3a4049}
        i[class*="fa-"]::before{content:"\25A0";font-size:.8em;opacity:.6}
        .oml-muted{opacity:.6;font-style:italic}
        .oml-placeholder{border:1px dashed #8a919a;padding:8px;opacity:.85}
        .oml-unknown{border:1px solid #d97706;padding:6px}
        .oml-tag{display:inline-block;font-size:11px;padding:0 4px;margin-right:6px;background:#8a919a33;border-radius:3px}
        [data-oml-image]{display:flex;align-items:center;justify-content:center;min-height:80px;min-width:120px;background:#8a919a33;font-size:12px}
        #oml-preview-errors{margin:16px 0 0;padding:8px 12px;border:1px solid #dc2626;background:#fee2e2;color:#7f1d1d;font:12px/1.4 ui-monospace,Consolas,monospace;white-space:pre-wrap}
        """;

    // No network: scripts, styles, images and fonts come only from the page itself or data: URLs. 'unsafe-eval' is for --eval and
    // for code that uses eval or new Function. A blocked request is listed in the report's `blocked`. CSP does not stop a
    // navigation: the runtime cancels it, and the headless browser has no proxy and no name resolution (PreviewBrowser).
    internal const string Csp = "default-src 'none'; script-src 'unsafe-inline' 'unsafe-eval'; style-src 'unsafe-inline'; img-src data: blob:; font-src data:; media-src data: blob:; connect-src 'none'; frame-src 'none'; worker-src 'none'; base-uri 'none'; form-action 'none'";

    // Global names of the OutSystems client runtime and OutSystems UI. "X is not defined" for one of them gets a hint.
    internal static readonly string[] PlatformGlobals = ["OSUI", "OutSystems", "OutSystemsUI", "OSFramework", "OutSystemsDebugger", "SapphireWidgets"];

    // The stub of the OutSystems client runtime. It records errors (with the node or script name), stub calls, console messages,
    // blocked requests, cancelled navigations, step results and the item that runs now (`at`) as JSON in
    // <pre id="oml-preview-report">, which `--dump-dom` reads back.
    static readonly string Runtime = """
        <script>
        var __omlFns=[];window.__omlPreviewErrors=[];
        (function(){
        var R={errors:[],errorsTotal:0,skipped:[],stubbed:[],stubbedTotal:0,console:[],consoleTotal:0,blocked:[],navigation:[],steps:[],at:'page'},failed={},at='page',CAP=100,root=document.documentElement;
        var PLATFORM=@@GLOBALS@@,consoleError=console.error.bind(console);
        function cut(s,n){s=String(s);return s.length>n?s.slice(0,n-3)+'...':s;}
        function text(v){if(typeof v==='string')return v;try{var s=JSON.stringify(v);return s===undefined?String(v):s;}catch(e){return String(v);}}
        function arg(a){if(typeof a==='function')return 'function';if(typeof Element!=='undefined'&&a instanceof Element)return '<'+a.tagName.toLowerCase()+(a.id?'#'+a.id:'')+'>';var s;try{s=JSON.stringify(a);}catch(e){}return cut(s===undefined?String(a):s,60);}
        function msg(e){if(e instanceof Error)return String(e);if(e&&typeof e==='object'&&e.message)return String(e.message);return text(e);}
        function save(){var p=document.getElementById('oml-preview-report');if(p)p.textContent=JSON.stringify(R);root.setAttribute('data-oml-errors',String(R.errors.length));
        var panel=document.getElementById('oml-preview-errors');if(panel&&R.errors.length){panel.hidden=false;panel.textContent=window.__omlPreviewErrors.join('\n');}}
        function add(list,v){if(list.length>=CAP)return;list.push(v);save();}
        function setAt(n){at=n;R.at=n;save();}
        function fail(where,e){var m=cut(msg(e).replace(/^Uncaught /,''),500);var g=/^ReferenceError: ([\w$]+) is not defined/.exec(m);
        if(g&&PLATFORM.indexOf(g[1])>=0)m+=' (not available in the preview; works in the published app)';
        R.errorsTotal++;var line=where+': '+m;if(window.__omlPreviewErrors.indexOf(line)>=0){save();return;}
        window.__omlPreviewErrors.push(line);add(R.errors,{node:where,message:m});}
        function note(s){R.stubbedTotal++;s=cut(s,300);if(R.stubbed.indexOf(s)<0)add(R.stubbed,s);else save();}
        function call(path,args){return path+'('+Array.prototype.map.call(args,arg).join(', ')+')';}
        function deep(path){return new Proxy(function(){},{get:function(t,k){if(k===Symbol.toPrimitive)return function(){return '';};if(typeof k==='symbol'||k==='then'||k==='toJSON')return undefined;return deep(path+'.'+k);},
        apply:function(t,self,args){note(at+': '+call(path,args));return undefined;},set:function(){return true;}});}
        // $actions.X() returns a promise, so both .then() and a plain call work. Service Studio already refuses unknown action names.
        var actions=new Proxy({},{get:function(t,k){if(typeof k==='symbol'||k==='then'||k==='toJSON')return undefined;return function(){note(at+': '+call('$actions.'+k,arguments));return Promise.resolve({});};}});
        window.__omlAt=setAt;
        if(!('controller' in window))window.controller=deep('controller');
        window.addEventListener('error',function(e){fail(at==='page'?'window':at,e.error||e.message);});
        window.addEventListener('unhandledrejection',function(e){fail(at==='page'?'promise':at,e.reason);});
        // A blocked file: or data: request has no URL to show, so it names the scheme and what asked for it.
        var KINDS={'connect-src':'XHR or fetch','worker-src':'Worker','frame-src':'frame','child-src':'frame','img-src':'image','font-src':'font','style-src':'stylesheet','style-src-elem':'stylesheet','script-src':'script','script-src-elem':'script','media-src':'media','default-src':'request','form-action':'form'};
        document.addEventListener('securitypolicyviolation',function(e){var u=e.blockedURI||'',k=KINDS[e.effectiveDirective||e.violatedDirective]||e.effectiveDirective;
        if(u==='inline'||u==='eval')return;if(/^(file|data|blob)$/.test(u))u=u+(u==='file'?'://':':')+' ('+k+')';u=cut(u,300);if(u&&R.blocked.indexOf(u)<0)add(R.blocked,u);});
        // CSP does not stop navigation: cancel it here, report it, and keep the page (the browser has no network either way).
        // A navigation that cannot be cancelled is also named on stderr, which survives when the page does not.
        function navigated(url,how,leaving){var u=cut(url,300);if(leaving)consoleError('__omlnav '+u);if(R.navigation.indexOf(u)<0)add(R.navigation,u);var b=u+' ('+how+')';if(R.blocked.indexOf(b)<0)add(R.blocked,b);}
        // `oml preview --serve` reloads the page itself (window.__omlReload); a reload that page code starts is cancelled like any navigation.
        if(window.navigation)navigation.addEventListener('navigate',function(e){if(e.destination.sameDocument||window.__omlReload)return;navigated(e.destination.url,'navigation',!e.cancelable);if(e.cancelable)e.preventDefault();});
        window.open=function(url){navigated(String(url||'about:blank'),'window.open');return null;};
        // Forms: a submit event (requestSubmit, Enter in an input, a submit button) is cancelled; form.submit() fires no event, so it is replaced.
        function formUrl(f){var a=f.getAttribute('action');return a?new URL(a,document.baseURI).href:document.baseURI;}
        document.addEventListener('submit',function(e){e.preventDefault();navigated(formUrl(e.target),'form'+(e.target.target?' target='+e.target.target:''));},true);
        HTMLFormElement.prototype.submit=function(){navigated(formUrl(this),'form.submit()'+(this.target?' target='+this.target:''));};
        // A link that opens another window or frame never reaches this page's navigate event.
        document.addEventListener('click',function(e){var a=e.target&&e.target.closest&&e.target.closest('a[href],area[href]');var t=a&&(a.getAttribute('target')||'').toLowerCase();
        if(!a||!t||t==='_self')return;e.preventDefault();navigated(a.href,'link target='+t);},true);
        // Speculation rules prefetch and prerender other pages: such a script is dropped before it reaches the document.
        function speculative(n){return n&&n.nodeName==='SCRIPT'&&String(n.type).trim().toLowerCase()==='speculationrules';}
        function strip(n){if(speculative(n)){if(R.blocked.indexOf('speculation rules (script)')<0)add(R.blocked,'speculation rules (script)');return null;}
        if(n&&n.nodeType===11)Array.prototype.forEach.call(n.querySelectorAll('script'),function(s){if(speculative(s)){s.remove();strip(s);}});return n;}
        ['appendChild','insertBefore','replaceChild'].forEach(function(k){var o=Node.prototype[k];Node.prototype[k]=function(n){return strip(n)===null?n:o.apply(this,arguments);};});
        ['append','prepend','after','before','replaceWith'].forEach(function(k){var o=Element.prototype[k];Element.prototype[k]=function(){return o.apply(this,Array.prototype.filter.call(arguments,function(n){return strip(n)!==null;}));};});
        var insertAdjacentElement=Element.prototype.insertAdjacentElement;Element.prototype.insertAdjacentElement=function(w,n){return strip(n)===null?null:insertAdjacentElement.apply(this,arguments);};
        ['log','info','warn','error','debug'].forEach(function(l){var o=console[l];console[l]=function(){R.consoleTotal++;var v={level:l,text:cut(Array.prototype.map.call(arguments,text).join(' '),300)};if(R.console.length<CAP)add(R.console,v);else save();return o.apply(console,arguments);};});
        function job(j,timeout){return new Promise(function(done){
        if(failed[j.g]){add(R.skipped,j.n);done();return;}
        var fn=__omlFns[j.f],over=false,timer=null;setAt(j.n);
        function stop(e){failed[j.g]=true;fail(j.n,e);end();}
        function end(){if(over)return;over=true;if(timer)clearTimeout(timer);setAt('page');done();}
        if(!fn){failed[j.g]=true;if(!R.errors.some(function(x){return x.node===j.c;}))fail(j.c,'the JavaScript node did not compile');end();return;}
        try{var r=fn(j.p,actions,deep('$roles'),deep('$public'),function(){end();},function(e){stop('$reject: '+msg(e));});
        if(r&&typeof r.then==='function')r.then(null,function(e){fail(j.n,e);});}
        catch(e){stop(e);return;}
        if(!j.async)end();else if(!over)timer=setTimeout(function(){stop('$resolve() or $reject() was not called within '+timeout+' ms');},timeout);});}
        function step(s,i,timeout){return new Promise(function(done){
        var out={step:s.d,ok:true},over=false;R.steps.push(out);setAt('step '+(i+1)+' '+s.d);
        function bad(m){out.ok=false;out.error=cut(m,500);}
        function next(){if(over)return;over=true;setAt('page');setTimeout(done,0);}
        try{
        if(s.k==='wait'){save();setTimeout(next,s.v);return;}
        if(s.k==='eval'){var v=(0,eval)(s.v);if(v&&typeof v.then==='function'){var t=setTimeout(function(){bad('the promise did not settle within '+timeout+' ms');next();},timeout);
        v.then(function(x){clearTimeout(t);if(x!==undefined)out.result=cut(text(x),1000);next();},function(e){clearTimeout(t);bad(msg(e));next();});return;}
        if(v!==undefined)out.result=cut(text(v),1000);next();return;}
        var el=document.querySelector(s.s);
        if(!el)bad('no element matches '+s.s);
        else if(s.k==='click')el.click();
        else if(el instanceof HTMLInputElement||el instanceof HTMLTextAreaElement||el instanceof HTMLSelectElement){el.focus();
        var d=Object.getOwnPropertyDescriptor(Object.getPrototypeOf(el),'value');if(d&&d.set)d.set.call(el,s.v);else el.value=s.v;
        el.dispatchEvent(new Event('input',{bubbles:true}));el.dispatchEvent(new Event('change',{bubbles:true}));}
        else if(el.isContentEditable){el.focus();el.textContent=s.v;el.dispatchEvent(new Event('input',{bubbles:true}));}
        else bad(s.s+' is not a text field (input, textarea, select or contenteditable)');
        }catch(e){bad(msg(e));}
        next();});}
        function overflow(){var f=document.getElementById('oml-frame');if(f)root.setAttribute('data-oml-overflow',String(f.scrollWidth>f.clientWidth+1));}
        window.__omlRun=function(jobs,steps,timeout){
        document.addEventListener('DOMContentLoaded',function(){save();var p=Promise.resolve();
        jobs.forEach(function(j){p=p.then(function(){return job(j,timeout);});});
        steps.forEach(function(s,i){p=p.then(function(){return step(s,i,timeout);});});
        p.then(function(){overflow();root.setAttribute('data-oml-done','true');save();});});
        window.addEventListener('load',function(){overflow();root.setAttribute('data-oml-viewport',innerWidth+','+innerHeight);});};
        })();
        </script>

        """.Replace("@@GLOBALS@@", JsonSerializer.Serialize(PlatformGlobals));
}
