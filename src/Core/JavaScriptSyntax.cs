using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OutSystems.Model;

namespace OmlCli;

// JavaScript syntax check for `oml validate` and `oml apply`. Headless validation never checks JavaScript: Service Studio's
// editor sets that error, so a module built by oml could hold JavaScript that breaks only in Studio or at publish.
// The node.exe that ships with Service Studio compiles every snippet in one process (vm.Script, compile only: nothing
// runs). A JavaScript node is compiled as the body of an async function, as the platform wraps it, so return, await and
// $parameters, $actions, $resolve parse. Scripts, the module's and a Traditional Web screen's or block's JavaScript are
// compiled as plain scripts. Without node.exe the check is skipped with a note.
static class JavaScriptSyntax
{
    public const string Id = "JavaScriptSyntax";
    const int TimeoutSeconds = 60;

    internal sealed record Snippet(IObjectSignature Owner, string Key, string Code, bool Body);

    // OML_NODE (tests) wins; otherwise node.exe in the Service Studio folder. Null when there is none.
    static string Candidate => Environment.GetEnvironmentVariable("OML_NODE") is { Length: > 0 } env ? env : Path.Combine(Model.ServiceStudioDir, "node.exe");

    internal static string? NodePath() => File.Exists(Candidate) ? Candidate : null;

    internal static string NotFound => $"JavaScript syntax was not checked: {Candidate} was not found. node.exe ships with Service Studio 11; reinstall it, or check the JavaScript in Service Studio.";

    // Every element with a JavaScript text that is not empty. Referenced elements (proxies) have no body and are skipped.
    internal static List<Snippet> Collect(IESpace m)
    {
        var list = new List<Snippet>();
        var seen = new HashSet<IObjectSignature>();
        var pending = new Stack<IObjectSignature>();
        pending.Push(m);
        while (pending.TryPop(out var o))
        {
            // Widgets hold no JavaScript text.
            if (!seen.Add(o) || o is OutSystems.Model.UI.IWidgetSignature || o.GetType().Name.StartsWith("Reference", StringComparison.Ordinal)) continue;
            if (Reflect.Property(o, "JavaScript") is { CanRead: true } property)
            {
                string code;
                try { code = Describe.Text(property.GetValue(o)); }
                catch (System.Reflection.TargetInvocationException) { code = ""; } // a Service module has none: the getter throws
                if (code.Trim().Length > 0) list.Add(new Snippet(o, o.GlobalKey.ToString()!, code, ElementPaths.Kind(o).Equals("JavaScriptNode", StringComparison.OrdinalIgnoreCase)));
            }
            // A screen's or block's Children reads its whole widget tree (most of the walk on a big module); of a screen or
            // block only the actions can hold JavaScript nodes. Most of the time left is the model loading each action's flow.
            List<IObjectSignature> children;
            try { children = o is OutSystems.Model.UI.IScreenSignature || Inspect.IsBlock(o) ? Items(o, "ScreenActions") : o.Children.ToList(); }
            catch (Exception e) when (e is NotSupportedException or NullReferenceException or InvalidCastException or System.Reflection.TargetInvocationException) { continue; }
            foreach (var child in children) pending.Push(child);
        }
        return list;
    }

    static List<IObjectSignature> Items(IObjectSignature o, string property) =>
        (Reflect.Get(o, property) as System.Collections.IEnumerable)?.OfType<IObjectSignature>().ToList() ?? [];

    // The JavaScript texts by element key, to find the snippets an apply added or changed.
    internal static Dictionary<string, string> Snapshot(IESpace m) => Collect(m).GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.First().Code);

    // One node process for all snippets. note: why the check did not run (no node.exe, node failed); null when it ran.
    internal static (IValidationMessage[] errors, string? note) Check(IReadOnlyList<Snippet> snippets)
    {
        if (snippets.Count == 0) return ([], null);
        if (NodePath() is not { } node) return ([], NotFound);
        var input = new JsonArray(snippets.Select(s => (JsonNode)new JsonObject { ["code"] = s.Code, ["body"] = s.Body }).ToArray()).ToJsonString();
        var info = new ProcessStartInfo(node)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("-e");
        info.ArgumentList.Add(Program);
        // Never pass on switches from the caller's environment that load or run other code (--require, --import).
        info.Environment.Remove("NODE_OPTIONS");
        string output;
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("no process");
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            process.StandardInput.Write(input);
            process.StandardInput.Close();
            if (!stdout.Wait(TimeSpan.FromSeconds(TimeoutSeconds)))
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                return ([], $"JavaScript syntax was not checked: node.exe did not answer in {TimeoutSeconds} s.");
            }
            process.WaitForExit(5000);
            output = stdout.Result;
            if (process.ExitCode != 0 || output.Length == 0)
                return ([], $"JavaScript syntax was not checked: node.exe failed (exit {process.ExitCode}): {First(stderr.Wait(2000) ? stderr.Result : "")}");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return ([], $"JavaScript syntax was not checked: node.exe did not start ({e.Message}).");
        }
        var errors = new List<IValidationMessage>();
        foreach (var item in JsonNode.Parse(output)!.AsArray())
        {
            var snippet = snippets[item!["i"]!.GetValue<int>()];
            int line = item["line"]!.GetValue<int>(), column = item["column"]!.GetValue<int>();
            var text = item["message"]!.GetValue<string>();
            var source = snippet.Code.Replace("\r\n", "\n").Split('\n').ElementAtOrDefault(line - 1)?.Trim() ?? "";
            if (source.Length > 120) source = source[..117] + "...";
            var what = snippet.Body ? "JavaScript node" : snippet.Owner is IESpace ? "module JavaScript" : ElementPaths.Kind(snippet.Owner) + " JavaScript";
            errors.Add(new SyntaxMessage(snippet.Owner, $"JavaScript syntax error at line {line}, column {column}: {text}",
                $"{what}; line {line}: {source}"));
        }
        return (errors.ToArray(), null);
    }

    static string First(string text) => text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) is { } l ? (l.Length > 200 ? l[..200] : l) : "no message";

    // Reads [{code, body}] on stdin, writes [{i, line, column, message}] for each snippet that does not compile.
    // vm.Script only compiles; nothing is run. The first stack line is "js:<line>"; the caret line marks the column.
    const string Program = """
        const vm = require('vm'); let s = ''; process.stdin.setEncoding('utf8');
        process.stdin.on('data', d => s += d);
        process.stdin.on('end', () => {
          const out = [];
          JSON.parse(s).forEach((x, i) => {
            const src = x.body ? '(async function($parameters, $actions, $roles, $public, $resolve, $reject) {\n' + x.code + '\n})' : x.code;
            try { new vm.Script(src, { filename: 'js', lineOffset: x.body ? -1 : 0 }); }
            catch (e) {
              if (!e || e.name !== 'SyntaxError') return;
              const lines = String(e.stack || '').split('\n');
              const m = /^js:(\d+)/.exec(lines[0] || '');
              const caret = (lines[2] || '').indexOf('^');
              out.push({ i, line: m ? Math.max(1, +m[1]) : 1, column: caret >= 0 ? caret + 1 : 1, message: e.message });
            }
          });
          process.stdout.write(JSON.stringify(out));
        });
        """;

    // A validation message of oml's own, in the shape the model's messages have, so reports group and subtract it the same way.
    sealed class SyntaxMessage(IObjectSignature owner, string message, string detail) : IValidationMessage
    {
        public string Id => JavaScriptSyntax.Id;
        public ValidationMessageType Type => ValidationMessageType.Error;
        public string Message { get; } = message;
        public string Detail { get; } = detail;
        public bool Hidden { get; set; }
        public IObjectSignature Owner { get; } = owner;
    }
}
