using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// `oml query <module.oml> "<ModelQL>"`: read-only questions on a module's model graph, such as Root { Entities { Name } }.
// ModelQL is GraphQL over the Model API (OutSystems.ModelQL). Offline it runs through Service Studio's oml-tool.exe
// (runtimes\win-x64, one JSON-RPC line in and out per process, Service Studio may be closed); --live asks Service Studio's
// MCP tool runQuery on the open module instead. The query engine compiles @filter conditions as C#, so a condition is
// held to a small safe subset before anything runs (Refusal). oml-tool's code-execution verb is never enabled.
static class ModelQuery
{
    public const int DefaultLimit = 50;
    public const int DefaultMaxChars = 20000;
    const int TimeoutSeconds = 120;
    const string Example = "Root { Entities { Name Attributes { Name } } }";

    // OML_TOOL wins; else Service Studio's own copy.
    public static string ToolPath() => Environment.GetEnvironmentVariable("OML_TOOL") is { Length: > 0 } tool
        ? tool : Path.Combine(Model.ServiceStudioDir, "runtimes", "win-x64", "oml-tool.exe");

    public static int? Run(List<string> args)
    {
        var positional = new List<string>();
        int limit = DefaultLimit, maxChars = DefaultMaxChars;
        string? fields = null;
        var live = false;
        for (var i = 1; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--live") live = true;
            else if (a is "--limit" or "--max-chars" or "--fields")
            {
                if (i + 1 >= args.Count) return null;
                var value = args[++i];
                if (a == "--fields") fields = value;
                else if (!int.TryParse(value, out var n) || n < 1)
                    throw new CliError($"{a} takes a whole number of 1 or more, not '{value}'.", 2);
                else if (a == "--limit") limit = n; else maxChars = n;
            }
            else if (a.StartsWith("--")) return null;
            else positional.Add(a);
        }
        if (positional.Count != (fields is null ? 2 : 1)) return null;
        var target = positional[0];
        var query = fields is null ? positional[1] : FieldsQuery(fields);
        if (Refusal(query) is { } refused) throw new CliError(refused, 2);

        JsonNode response;
        string module;
        if (live)
        {
            module = File.Exists(target) && target.EndsWith(".oml", StringComparison.OrdinalIgnoreCase)
                ? ModuleHeader.Read(target)?.Name ?? Path.GetFileNameWithoutExtension(target) : target;
            response = Timings.Measure("query", () => Studio.Query(module, query));
        }
        else
        {
            module = Path.GetFullPath(target);
            if (!File.Exists(module)) throw new CliError($"Module file not found: {module}.", 2);
            if (ModuleHeader.Read(module) is null) throw new CliError($"{module} is not a readable .oml module.", 2);
            response = Timings.Measure("query", () => Offline(module, query));
        }

        JsonNode data;
        try { data = Answer(response); }
        catch (QueryError e)
        {
            Cli.Emit(new { error = "ModelQL: " + e.Message, next = Hint(e.Message, target, live) });
            return 2;
        }
        if (fields is not null) return EmitFields(module, fields, data, target, live);

        var shaped = Shape(data, limit, maxChars);
        if (!shaped.Fits)
        {
            Cli.Emit(new
            {
                error = $"The result is {shaped.Chars} characters even with {shaped.Limit} item(s) per list, over --max-chars {maxChars}.",
                next = "Name only the fields you need (a bare object returns all its fields), leave out ModelAPICode, narrow a list with (name: \"X\") or limit: N, or raise --max-chars.",
            });
            return 2;
        }
        // Written as it is: the usual compact output drops nulls and empty lists, which would change the answer
        // (an item whose fields are all null would vanish from its list).
        var report = new JsonObject { ["module"] = module, ["data"] = shaped.Data, ["chars"] = shaped.Chars };
        if (shaped.Cuts.Count > 0)
        {
            report["limit"] = shaped.Limit;
            report["cut"] = new JsonArray(shaped.Cuts.Take(10).Select(c => (JsonNode?)new JsonObject { ["path"] = c.Path, ["total"] = c.Total }).ToArray());
            if (shaped.Cuts.Count > 10) report["moreCuts"] = shaped.Cuts.Count - 10;
            report["next"] = $"Lists were cut to {shaped.Limit} items. Narrow a list with (name: \"X\"), limit: N or @filter, or pass --limit.";
        }
        if (Timings.Enabled) report["timings"] = Timings.Report();
        TerminalOutput.PrintNodeRaw(report);
        return 0;
    }

    sealed class QueryError(string message) : Exception(message);

    // One oml-tool process per query: it reads one JSON-RPC line, answers one line and exits.
    static JsonNode Offline(string module, string query)
    {
        var tool = ToolPath();
        if (!File.Exists(tool))
            throw new CliError($"oml-tool.exe was not found at {tool}. It comes with recent Service Studio 11 builds (runtimes\\win-x64). " +
                               "Set OML_SERVICE_STUDIO to the Service Studio folder or OML_TOOL to oml-tool.exe, or use --live with the module open in Service Studio.", 2);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "runQuery",
            ["params"] = new JsonObject { ["omlPath"] = module, ["query"] = query },
        }.ToJsonString();
        var info = new ProcessStartInfo(tool)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("--stdio");
        // Never pass on a code-execution switch the caller's environment may hold.
        info.Environment.Remove("OMLTOOL_ENABLE_CODE_EXEC");
        info.Environment.Remove("OMLTOOL_RESTRICTED_CODE_EXEC");
        using var process = Process.Start(info) ?? throw new CliError($"Could not start {tool}.", 2);
        var stderr = process.StandardError.ReadToEndAsync(); // its debug log; read so it cannot fill the pipe
        var stdout = process.StandardOutput.ReadLineAsync();
        process.StandardInput.WriteLine(request);
        process.StandardInput.Close();
        if (!stdout.Wait(TimeSpan.FromSeconds(TimeoutSeconds)))
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            throw new CliError($"oml-tool did not answer in {TimeoutSeconds} s. Try a smaller query, or check that {module} opens in Service Studio.", 2);
        }
        if (!process.WaitForExit(10000)) try { process.Kill(true); } catch (InvalidOperationException) { }
        var line = stdout.Result;
        if (string.IsNullOrWhiteSpace(line))
            throw new CliError($"oml-tool gave no answer (exit {(process.HasExited ? process.ExitCode : -1)}): {LastLine(stderr)}", 2);
        try { return JsonNode.Parse(line) ?? new JsonObject(); }
        catch (JsonException) { throw new CliError($"oml-tool answered text instead of JSON: {Short(line)}", 2); }
    }

    static string LastLine(Task<string> stderr)
    {
        var text = stderr.Wait(2000) ? stderr.Result : "";
        return Short(text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "no message");
    }

    static string Short(string text) => text.Length <= 300 ? text : text[..300] + "...";

    // The data of a JSON-RPC answer (offline) or of Service Studio's runQuery result (live).
    static JsonNode Answer(JsonNode response)
    {
        if (response["error"] is { } error)
        {
            var message = error["message"]?.ToString() ?? error.ToJsonString();
            if (error["code"]?.ToString() == "-32601") throw new CliError($"This oml-tool has no runQuery ({message}). Use --live with Service Studio open.", 2);
            throw new QueryError(message);
        }
        var result = response["result"] ?? response;
        JsonNode? document = result["queryResponse"] is JsonValue text && text.TryGetValue<string>(out var json) ? JsonNode.Parse(json) : result;
        if (document?["errors"] is JsonArray { Count: > 0 } errors && document["data"] is null)
            throw new QueryError(string.Join(" ", errors.Select(e => e?["message"]?.ToString())));
        return document?["data"] ?? throw new CliError($"runQuery returned no data: {Short(result.ToJsonString())}", 2);
    }

    // The next step for a refused query: a field name the type lacks, a condition that did not compile, a parse error.
    public static string Hint(string message, string target, bool live)
    {
        var where = live ? $"{target} --live" : target;
        var field = Regex.Match(message, "fetch field '([^']+)' from type '([^']+)'");
        if (field.Success)
            return $"{field.Groups[2].Value} has no field {field.Groups[1].Value}. List its fields: oml query {where} --fields {field.Groups[2].Value}";
        if (message.Contains("condition", StringComparison.OrdinalIgnoreCase))
            return "Write the @filter condition in C# over _ (the object): @filter(condition: \"_.Name.StartsWith(\\\"Get\\\")\").";
        if (message.Contains("parsing", StringComparison.OrdinalIgnoreCase) || message.Contains("Expected", StringComparison.Ordinal))
            return $"ModelQL is GraphQL without the outer braces, starting at Root: {Example}";
        return $"Check the field names with oml query {where} --fields ESpace (Root is the ESpace).";
    }

    // GraphQL introspection of one type, printed as "Name: Type (arguments)" lines.
    static string FieldsQuery(string type)
    {
        if (!Regex.IsMatch(type, "^[A-Za-z_][A-Za-z0-9_]*$")) throw new CliError($"--fields takes a type name such as ESpace, Entity or IEntity, not '{type}'.", 2);
        return $"__type(name: \"{type}\") {{ name kind fields {{ name args {{ name }} type {{ name kind ofType {{ name kind ofType {{ name kind ofType {{ name }} }} }} }} }} possibleTypes {{ name }} }}";
    }

    static int EmitFields(string module, string type, JsonNode data, string target, bool live)
    {
        if (data["__type"] is not JsonObject t)
        {
            Cli.Emit(new
            {
                error = $"ModelQL has no type '{type}'.",
                next = $"Use the __typename of an object, such as ESpace, Entity or MobileScreen, or a Model API interface, such as IEntity. Start with: oml query {(live ? target + " --live" : target)} --fields ESpace",
            });
            return 2;
        }
        static string TypeName(JsonNode? n) => n is null ? "" : (string?)n["kind"] switch
        {
            "LIST" => "[" + TypeName(n["ofType"]) + "]",
            "NON_NULL" => TypeName(n["ofType"]),
            _ => (string?)n["name"] ?? "",
        };
        var fields = (t["fields"] as JsonArray ?? new JsonArray()).Select(f => f!).Where(f => (string?)f["name"] is not ("_dummy" or "type_"))
            .Select(f =>
            {
                var args = (f["args"] as JsonArray ?? new JsonArray()).Select(a => (string?)a?["name"]).ToList();
                return $"{f["name"]}: {TypeName(f["type"])}" + (args.Count > 0 ? $" ({string.Join(", ", args)})" : "");
            }).ToList();
        var implementations = (t["possibleTypes"] as JsonArray)?.Select(p => (string?)p?["name"]).ToList();
        Cli.Emit(new
        {
            module, type = (string?)t["name"], kind = (string?)t["kind"],
            fields = fields.Count > 0 ? fields : null,
            implementations = implementations is { Count: > 0 } ? implementations.Take(50).ToList() : null,
        });
        return 0;
    }

    public sealed record Cut(string Path, int Total);
    public sealed record Shaped(JsonNode Data, List<Cut> Cuts, int Limit, int Chars, bool Fits);

    // Every list cut to `limit` items; while the result is over maxChars, the limit halves (down to 1).
    public static Shaped Shape(JsonNode data, int limit, int maxChars)
    {
        var lim = limit;
        while (true)
        {
            var copy = data.DeepClone();
            var cuts = new List<Cut>();
            var longest = CapLists(copy, lim, "", cuts);
            var chars = copy.ToJsonString().Length;
            if (chars <= maxChars) return new(copy, cuts, lim, chars, true);
            var next = cuts.Count > 0 ? lim / 2 : longest / 2; // no list was cut: start below the longest one
            if (next < 1) return new(copy, cuts, lim, chars, false);
            lim = next;
        }
    }

    // Cuts the lists in place; returns the longest list length seen (before cutting).
    static int CapLists(JsonNode? node, int limit, string path, List<Cut> cuts)
    {
        var longest = 0;
        if (node is JsonObject o)
            foreach (var (k, v) in o.ToList()) longest = Math.Max(longest, CapLists(v, limit, path.Length == 0 ? k : path + "." + k, cuts));
        else if (node is JsonArray a)
        {
            longest = a.Count;
            if (a.Count > limit)
            {
                cuts.Add(new(path, a.Count));
                while (a.Count > limit) a.RemoveAt(a.Count - 1);
            }
            for (var i = 0; i < a.Count; i++) longest = Math.Max(longest, CapLists(a[i], limit, $"{path}[{i}]", cuts));
        }
        return longest;
    }

    // ---------------------------------------------------------------- safety

    // The query engine compiles a @filter condition into a C# predicate and runs it in the tool's process, so any C# would run.
    // Only a small subset passes: the object `_` and its properties, literals, comparisons and logic, and a few string methods.
    // An @ofType type name is compiled too, so it must be a plain name. Returns why a query is refused, or null.
    public static string? Refusal(string query)
    {
        var tokens = new List<(char kind, string text)>(); // n name, s string, p punctuation
        for (var i = 0; i < query.Length;)
        {
            var c = query[i];
            if (char.IsWhiteSpace(c) || c == ',' || c == '﻿') { i++; continue; }
            if (c == '#') { while (i < query.Length && query[i] is not ('\n' or '\r')) i++; continue; }
            if (c == '"')
            {
                if (query.AsSpan(i).StartsWith("\"\"\"")) return "Block strings (\"\"\") are not supported in oml query: use a normal \"...\" string.";
                var (value, end, error) = GraphQlString(query, i);
                if (error is not null) return error;
                tokens.Add(('s', value!));
                i = end;
                continue;
            }
            if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = i;
                while (i < query.Length && (char.IsAsciiLetterOrDigit(query[i]) || query[i] == '_')) i++;
                tokens.Add(('n', query[start..i]));
                continue;
            }
            if (c == '$') return "Variables ($name) are not supported in oml query: write the values in the query.";
            if (char.IsAsciiDigit(c) || c == '-') { while (i < query.Length && (char.IsAsciiLetterOrDigit(query[i]) || query[i] is '.' or '-' or '+')) i++; tokens.Add(('n', "0")); continue; }
            if ("{}()[]:@!=|&.".Contains(c)) { tokens.Add(('p', c.ToString())); i++; continue; }
            return $"oml query does not read the character '{c}' in a query.";
        }
        // Inside an argument list, `name:` sets the argument the next values belong to.
        string? argument = null;
        var depth = 0;
        for (var k = 0; k < tokens.Count; k++)
        {
            var (kind, text) = tokens[k];
            if (kind == 'p' && text == "(") { depth++; argument = null; }
            else if (kind == 'p' && text == ")") { depth = Math.Max(0, depth - 1); argument = null; }
            else if (depth > 0 && kind == 'n' && k + 1 < tokens.Count && tokens[k + 1] == ('p', ":")) argument = text;
            else if (depth > 0 && kind == 's')
            {
                if (argument == "condition" && ConditionRefusal(text) is { } why)
                    return $"The @filter condition \"{text}\" is refused: {why} Use _ and its properties, literals, == != < > <= >= && || !, and StartsWith, EndsWith, Contains, Equals, ToLower, ToUpper, Trim: _.Name.StartsWith(\"Get\").";
                if (argument is "typename" or "ofType" && !Regex.IsMatch(text, "^[A-Za-z_][A-Za-z0-9_.]*$"))
                    return $"The type name \"{text}\" is refused: use a plain type name such as IMobileScreen.";
            }
        }
        return null;
    }

    // A GraphQL "..." string starting at i: its value as the engine reads it, the index after it, or an error.
    static (string? value, int end, string? error) GraphQlString(string s, int i)
    {
        var sb = new StringBuilder();
        for (var j = i + 1; j < s.Length; j++)
        {
            var c = s[j];
            if (c == '"') return (sb.ToString(), j + 1, null);
            if (c is '\n' or '\r') break;
            if (c != '\\') { sb.Append(c); continue; }
            if (++j >= s.Length) break;
            switch (s[j])
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (j + 4 < s.Length && int.TryParse(s.AsSpan(j + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code))
                    { sb.Append((char)code); j += 4; break; }
                    return (null, 0, "oml query reads \\u escapes only as \\uXXXX (four hex digits).");
                default: return (null, 0, $"Unknown escape \\{s[j]} in a query string.");
            }
        }
        return (null, 0, "A string in the query has no closing \".");
    }

    static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
        { "StartsWith", "EndsWith", "Contains", "Equals", "ToLower", "ToUpper", "ToLowerInvariant", "ToUpperInvariant", "Trim", "ToString" };
    static readonly string[] Operators = ["==", "!=", "<=", ">=", "&&", "||", "??", "?.", "<", ">", "!", "+", "-", "*", "/", "%", "?", ":", ".", "(", ")", ","];

    // Why a C# condition is outside the safe subset, or null when it is inside.
    public static string? ConditionRefusal(string condition)
    {
        var tokens = new List<(char kind, string text)>(); // i identifier, l literal, o operator
        for (var i = 0; i < condition.Length;)
        {
            var c = condition[i];
            if (c is ' ' or '\t' or '\r' or '\n') { i++; continue; }
            if (c == '"')
            {
                var j = i + 1;
                while (j < condition.Length && condition[j] != '"') j += condition[j] == '\\' ? 2 : 1;
                if (j >= condition.Length) return "a string has no closing quote.";
                tokens.Add(('l', condition[i..(j + 1)]));
                i = j + 1;
                continue;
            }
            if (c == '\'')
            {
                var m = Regex.Match(condition[i..], @"^'(\\.|[^'\\])'");
                if (!m.Success) return "a character literal is not closed.";
                tokens.Add(('l', m.Value));
                i += m.Length;
                continue;
            }
            if (char.IsAsciiDigit(c))
            {
                var m = Regex.Match(condition[i..], @"^\d+(\.\d+)?[mMdDfFlL]?");
                tokens.Add(('l', m.Value));
                i += m.Length;
                continue;
            }
            if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = i;
                while (i < condition.Length && (char.IsAsciiLetterOrDigit(condition[i]) || condition[i] == '_')) i++;
                tokens.Add(('i', condition[start..i]));
                continue;
            }
            if (Operators.FirstOrDefault(o => condition.AsSpan(i).StartsWith(o)) is { } op)
            {
                tokens.Add(('o', op));
                i += op.Length;
                continue;
            }
            return $"the character '{c}' is not allowed.";
        }
        var depth = 0;
        for (var k = 0; k < tokens.Count; k++)
        {
            var (kind, text) = tokens[k];
            var afterDot = k > 0 && tokens[k - 1] is ('o', "." or "?.");
            var call = k + 1 < tokens.Count && tokens[k + 1] == ('o', "(");
            if (kind == 'o' && text == "(") depth++;
            if (kind == 'o' && text == ")" && --depth < 0) return "the parentheses do not match.";
            if (kind == 'o' && text is "." or "?." && (k + 1 >= tokens.Count || tokens[k + 1].kind != 'i')) return "a dot must be followed by a name.";
            if (kind == 'o' && text == "(" && k > 0 && tokens[k - 1].kind == 'i' && !afterDotAt(tokens, k - 1)) return "only methods of _ can be called.";
            if (kind != 'i') continue;
            if (!afterDot && text is not ("_" or "true" or "false" or "null")) return $"'{text}' is not allowed: start every name at _ (the object).";
            if (afterDot && call && !Methods.Contains(text)) return $"the method {text} is not allowed.";
            if (afterDot && text is "GetType" or "Assembly" or "Invoke" or "DynamicInvoke") return $"'{text}' is not allowed.";
        }
        if (depth != 0) return "the parentheses do not match.";
        return null;

        static bool afterDotAt(List<(char kind, string text)> t, int k) => k > 0 && t[k - 1] is ('o', "." or "?.");
    }
}
