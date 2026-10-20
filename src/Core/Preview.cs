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
        Dictionary<string, int> Unsupported, int JavaScriptNodes, List<string> Notes, List<Check> Checklist);
    sealed record Val(string Display, string? Literal, bool Muted);

    sealed class Frame
    {
        public readonly Dictionary<string, string?> Scope = new(StringComparer.Ordinal);
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
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        var positional = new List<string>();
        for (var i = 1; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "--theme" or "--width" or "--height" or "--out" or "--screenshot" or "--arg")
            {
                if (i + 1 >= args.Count) throw new CliError($"{a} needs a value.", 2);
                var v = args[++i];
                if (a == "--theme") theme = v;
                else if (a == "--width") width = v;
                else if (a == "--height") height = v;
                else if (a == "--out") outPath = v;
                else if (a == "--screenshot") shot = v;
                else
                {
                    var eq = v.IndexOf('=');
                    if (eq <= 0) throw new CliError("--arg needs Name=<expression>, for example --arg Title=\"\\\"Hello\\\"\".", 2);
                    overrides[v[..eq]] = v[(eq + 1)..];
                }
            }
            else if (a.StartsWith("--")) throw new CliError("preview accepts --arg Name=<expression>, --theme light|dark, --width <px>, --height <px>, --out <file.html> and --screenshot <file.png>.", 2);
            else positional.Add(a);
        }
        if (positional.Count != 2) throw new CliError("Usage: oml preview <module.oml> <Block|Screen> [--arg Name=<expression>]... [--theme light|dark] [--width <px>] [--height <px>] [--out <file.html>] [--screenshot <file.png>]", 2);
        theme ??= "light";
        if (theme is not ("light" or "dark")) throw new CliError("--theme must be light or dark.", 2);
        if (!TryPixels(width ?? "1200", out var w) || !TryPixels(height ?? "900", out var h)) throw new CliError("--width and --height must be whole numbers from 200 to 4000.", 2);
        var element = positional[1];
        var html = Path.GetFullPath(outPath ?? element + ".preview.html");
        var png = shot is null ? null : Path.GetFullPath(shot);
        foreach (var p in new[] { html, png }.OfType<string>())
            if (File.Exists(p) || Directory.Exists(p))
                throw new CliError($"Output already exists: {p}. Use a new output path, or delete that file first.", 2);
        if (string.Equals(html, png, StringComparison.OrdinalIgnoreCase)) throw new CliError("--out and --screenshot must be different files.", 2);

        using var open = Model.Borrow(Path.GetFullPath(positional[0])); var module = open.Module;
        var result = Render(module, element, overrides, theme, w);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(html)!);
            using var stream = new FileStream(html, FileMode.CreateNew, FileAccess.Write);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(result.Html);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new CliError(e.Message, 2); }

        PreviewBrowser.Outcome? browser = png is null ? null : PreviewBrowser.Capture(html, png, w, h);
        return Cli.Emit(new
        {
            html, screenshot = browser?.Screenshot, screenshotNote = browser?.Note,
            element = result.Element, kind = result.Kind, theme, width = w,
            widgets = result.Widgets, blocksRendered = result.BlocksRendered,
            unsupported = result.Unsupported.Select(p => new { kind = p.Key, count = p.Value }).ToArray(),
            javaScriptNodes = result.JavaScriptNodes,
            javaScriptErrors = browser?.JavaScriptErrors, horizontalOverflow = browser?.HorizontalOverflow,
            notes = result.Notes, checklist = result.Checklist,
        });
    }

    static bool TryPixels(string text, out int value) => int.TryParse(text, out value) && value is >= 200 and <= 4000;

    // ---- render ----

    public static Result Render(IESpace m, string name, IReadOnlyDictionary<string, string> overrides, string theme, int width)
    {
        var found = Inspect.Screens(m).Where(s => Model.NameOf(s.screen) == name).Select(s => s.screen).ToList();
        if (found.Count == 0)
            throw new CliError($"No screen or block named '{name}'. Blocks: {string.Join(", ", Inspect.Screens(m).Where(s => Inspect.IsBlock(s.screen)).Select(s => Model.NameOf(s.screen)))}. Screens: {string.Join(", ", Inspect.Screens(m).Where(s => !Inspect.IsBlock(s.screen)).Select(s => Model.NameOf(s.screen)))}.", 2);
        return new Renderer(m).Run(found[0], overrides, theme, width);
    }

    sealed class Renderer(IESpace m)
    {
        readonly HashSet<string> usedIds = new(StringComparer.Ordinal);
        readonly HashSet<object> rendered = new(ReferenceEqualityComparer.Instance);
        readonly List<(string owner, string css)> styles = new();
        readonly HashSet<object> styled = new(ReferenceEqualityComparer.Instance);
        readonly List<(string owner, string node, string code)> jsNodes = new();
        readonly List<string> fnCodes = new();
        readonly Dictionary<object, int> fnIndex = new(ReferenceEqualityComparer.Instance);
        readonly List<string> jobs = new();
        readonly List<string> blocksRendered = new();
        readonly Dictionary<string, int> unsupported = new(StringComparer.Ordinal);
        readonly List<string> notes = new();
        readonly HashSet<string> externalBlocks = new(StringComparer.Ordinal);
        readonly List<(string owner, string kind, string text)> labels = new();
        readonly List<(string owner, string text)> literals = new();
        readonly List<(string owner, List<string> rootClasses, bool hasRoot)> roots = new();
        readonly List<IObjectSignature> localBlocks = Inspect.Screens(m).Where(s => Inspect.IsBlock(s.screen)).Select(s => s.screen).ToList();
        int ifCount, depthLimited;

        public Result Run(IObjectSignature element, IReadOnlyDictionary<string, string> overrides, string theme, int width)
        {
            var kind = Inspect.IsBlock(element) ? "Block" : "Screen";
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
            }

            var body = Body(element, frame);
            Scripts(element, frame);
            var sb = new StringBuilder();
            sb.Append("<!doctype html>\n<html lang=\"en\" data-theme=\"").Append(theme).Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
              .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>")
              .Append(Enc(name)).Append(" preview</title>\n<style id=\"oml-base\">").Append(BaseCss).Append("</style>\n");
            var themeCss = ThemeCss();
            if (themeCss is not null) styles.Insert(0, ("(theme)", themeCss));
            foreach (var (owner, css) in styles)
                sb.Append("<style data-source=\"").Append(Enc(owner)).Append("\">\n").Append(css.Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase)).Append("\n</style>\n");
            sb.Append("</head>\n<body>\n<div class=\"oml-frame\" id=\"oml-frame\" style=\"width:").Append(width).Append("px\">\n")
              .Append(body).Append("\n</div>\n<pre id=\"oml-preview-errors\" hidden></pre>\n");
            sb.Append(Runtime);
            for (var i = 0; i < fnCodes.Count; i++)
                sb.Append("<script>__omlFns[").Append(i).Append("]=function($parameters, $actions, $roles, $public){\n")
                  .Append(fnCodes[i].Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase)).Append("\n};</script>\n");
            sb.Append("<script>__omlRun([").Append(string.Join(",\n", jobs)).Append("]);</script>\n</body>\n</html>\n");

            Notes(theme);
            return new Result(sb.ToString(), name, kind, rendered.Count, blocksRendered, unsupported,
                jsNodes.Count, notes, PreviewChecks.Run(labels, literals, roots, jsNodes, styles));
        }

        void Notes(string theme)
        {
            notes.Insert(0, "OutSystems UI styles and fonts are not available. Classes such as card or btn are not styled here; check them in the published app.");
            notes.Add($"data-theme=\"{theme}\" is set on <html>. Only CSS that reads it (or CSS variables) changes with the theme.");
            if (ifCount > 0) notes.Add($"{ifCount} If widget(s) rendered as the true branch.");
            if (externalBlocks.Count > 0) notes.Add("Blocks from other modules are shown as a div with the block name as class, with their placeholder content: " + string.Join(", ", externalBlocks) + ".");
            if (depthLimited > 0) notes.Add($"{depthLimited} nested block(s) not expanded (more than {MaxDepth} levels deep, or a cycle).");
            notes.Add("List and table rows repeat the row template 3 times. Expressions that depend on data show as {expression}.");
            if (jsNodes.Count > 0) notes.Add("JavaScript nodes of OnInitialize, OnReady and OnRender run once per rendered block with $actions, $roles and $public as stubs. Client actions called from those actions are not followed.");
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
            if (styled.Add(owner) && Reflect.Get(owner, "StyleSheet") is string css && css.Trim().Length > 0) styles.Add((name, css));
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
            }
            foreach (var pc in slotsHere)
                if (Model.NameOf(Reflect.Get(pc, "Placeholder") ?? pc) is { } slotName)
                    inner.Slots[slotName] = (Kids(pc).ToList(), f);
            var body = Body((IObjectSignature)local, inner);
            Scripts((IObjectSignature)local, inner);
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

        void Scripts(IObjectSignature owner, Frame f)
        {
            var ownerName = Model.NameOf(owner) ?? "";
            foreach (var key in Lifecycle)
            {
                if (Reflect.Get(owner, key) is not { } evt || Reflect.Get(evt, "Destination") is not { } action) continue;
                foreach (var node in JsNodes(action))
                {
                    if (!fnIndex.TryGetValue(node, out var index))
                    {
                        index = fnCodes.Count;
                        fnIndex[node] = index;
                        var code = Reflect.Get(node, "JavaScript") as string ?? "";
                        fnCodes.Add(code);
                        jsNodes.Add((ownerName, Model.NameOf(node) ?? "JavaScript", code));
                    }
                    var parameters = new Dictionary<string, object?>();
                    foreach (var p in Describe.List(node, "InputParameters")) parameters[Model.NameOf(p)!] = null;
                    foreach (var arg in Describe.List(node, "Arguments"))
                        if (Reflect.Get(arg, "Parameter") is { } parameter && Model.NameOf(parameter) is { } pn)
                            parameters[pn] = JsValue(Text(Reflect.Get(arg, "Value")), f);
                    jobs.Add($"{{n:{Json($"{ownerName}.{key}.{Model.NameOf(node)}")},f:{index},p:{Json(parameters)}}}");
                }
            }
        }

        // Every JavaScript node reachable from Start, in connector order.
        static IEnumerable<object> JsNodes(object action)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var start = Describe.List(action, "Nodes").FirstOrDefault(n => ElementPaths.Kind(n) is "Start" or "StartNode");
            IEnumerable<object> Walk(object node)
            {
                if (!seen.Add(node)) yield break;
                if (ElementPaths.Kind(node).Equals("JavascriptNode", StringComparison.OrdinalIgnoreCase)) yield return node;
                if (node is not IObjectSignature o) yield break;
                var next = ElementPaths.Children(o).Select(c => Reflect.Get(c, "TargetNode")).Where(t => t is not null).Cast<object>().ToList();
                foreach (var target in next) foreach (var hit in Walk(target)) yield return hit;
            }
            return start is null ? [] : Walk(start);
        }

        object? JsValue(string expr, Frame f)
        {
            expr = expr.Trim();
            var id = Regex.Match(expr, @"^(\w+)\.Id$");
            if (id.Success) return f.Ids.TryGetValue(id.Groups[1].Value, out var rendered) ? rendered : null;
            var v = Eval(expr, f.Scope);
            if (v.Literal is null) return null;
            if (Str.IsMatch(v.Literal)) return v.Display;
            if (Num.IsMatch(v.Literal)) return double.Parse(v.Literal, System.Globalization.CultureInfo.InvariantCulture);
            return v.Literal.Equals("True", StringComparison.OrdinalIgnoreCase);
        }

        static string Json(object? value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace("</", "<\\/");

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
            var values = parts.Select(p => Eval(p, scope)).ToList();
            if (values.All(v => v.Literal is not null)) { var joined = string.Concat(values.Select(v => v.Display)); return new Val(joined, Quote(joined), false); }
        }
        return new Val("{" + e + "}", null, true);
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

    const string Runtime = """
        <script>
        var __omlFns=[];window.__omlPreviewErrors=[];
        (function(){
        function log(where,e){window.__omlPreviewErrors.push(where+': '+(e&&e.stack?e.stack:String(e)));var p=document.getElementById('oml-preview-errors');if(p){p.hidden=false;p.textContent=window.__omlPreviewErrors.join('\n\n');}document.documentElement.setAttribute('data-oml-errors',String(window.__omlPreviewErrors.length));}
        function stub(name){return new Proxy({},{get:function(t,k){if(k in t)return t[k];if(typeof k==='symbol'||k==='then')return undefined;return function(){console.warn('[oml-preview] unknown call '+name+'.'+String(k));};}});}
        window.addEventListener('error',function(e){log('window',e.error||e.message);});
        window.addEventListener('unhandledrejection',function(e){log('promise',e.reason);});
        window.__omlRun=function(jobs){document.addEventListener('DOMContentLoaded',function(){
        jobs.forEach(function(j){var fn=__omlFns[j.f];if(!fn){log(j.n,'function missing (syntax error in the JavaScript node)');return;}
        try{var r=fn(j.p,stub('$actions'),stub('$roles'),stub('$public'));if(r&&r.catch)r.catch(function(e){log(j.n,e);});}catch(e){log(j.n,e);}});
        document.documentElement.setAttribute('data-oml-errors',String(window.__omlPreviewErrors.length));
        });
        window.addEventListener('load',function(){var f=document.getElementById('oml-frame');document.documentElement.setAttribute('data-oml-overflow',String(f.scrollWidth>f.clientWidth+1));document.documentElement.setAttribute('data-oml-viewport',innerWidth+','+innerHeight);});};
        })();
        </script>

        """;
}
