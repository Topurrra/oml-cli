using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace OmlCli;

// `oml setup`: Explorer right-click entries for the current user, so a developer can convert fonts and images, export
// OpenAPI and make architecture pages without a terminal. It writes only under HKCU\Software\Classes (no admin), and
// marks every key it creates with the OmlSetup value: --remove deletes only marked keys, and an unmarked key with oml's
// verb name is never changed.
//
// `oml setup run <action> <path>` is what the entries call. It runs the normal oml command in this process (the same
// runner as the dashboard), then shows the result in Explorer. Failures go to setup.log and to a message box.
[SupportedOSPlatform("windows")]
static class Setup
{
    const string Usage = "oml setup [--remove | --status]   |   oml setup run <font|image|ico|trace|openapi|dashboard|fonts|images|architecture> <path> [--no-ui]";
    const string Marker = "OmlSetup", MarkerValue = "oml-cli";
    const string ClassesRoot = @"Software\Classes";
    // Only the tests use another root: a throwaway key under HKCU\Software, never the real Classes key.
    const string TestRootPrefix = @"Software\oml-setup-test-";
    static readonly string[] Fonts = [".ttf", ".otf", ".woff"];
    static readonly string[] Images = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".svg"];
    static readonly string[] Rasters = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"];

    sealed record Entry(string Parent, string Verb, string Label, string Action, string Arg);

    static IEnumerable<Entry> Entries()
    {
        foreach (var ext in Fonts) yield return new($@"SystemFileAssociations\{ext}\shell", "oml.font.woff2", "oml: Convert to WOFF2", "font", "%1");
        foreach (var ext in Images) yield return new($@"SystemFileAssociations\{ext}\shell", "oml.image.optimize", "oml: Optimize image", "image", "%1");
        foreach (var ext in Images) yield return new($@"SystemFileAssociations\{ext}\shell", "oml.image.ico", "oml: Convert to ICO", "ico", "%1");
        foreach (var ext in Rasters) yield return new($@"SystemFileAssociations\{ext}\shell", "oml.image.trace", "oml: Trace to SVG", "trace", "%1");
        yield return new(@"SystemFileAssociations\.oml\shell", "oml.openapi", "oml: OpenAPI", "openapi", "%1");
        yield return new(@"SystemFileAssociations\.oml\shell", "oml.dashboard", "oml: Open in dashboard", "dashboard", "%1");
        // A right-click on a folder passes it as %1; a right-click on the empty space inside an open folder passes it as %V.
        foreach (var (parent, arg) in new[] { (@"Directory\shell", "%1"), (@"Directory\Background\shell", "%V") })
        {
            yield return new(parent, "oml.folder.fonts", "oml: Convert all fonts to WOFF2", "fonts", arg);
            yield return new(parent, "oml.folder.images", "oml: Optimize all images", "images", arg);
            yield return new(parent, "oml.folder.architecture", "oml: Architecture report", "architecture", arg);
        }
    }

    public static int Run(string[] args)
    {
        if (args.FirstOrDefault() == "run") return RunAction(args[1..]);
        var root = ClassesRoot;
        string? mode = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--remove" or "--status" && mode is null) mode = args[i];
            else if (args[i] == "--registry-root" && i + 1 < args.Length) root = args[++i];
            else return Fail($"Usage: {Usage}", 2);
        }
        if (root != ClassesRoot && !(root.StartsWith(TestRootPrefix, StringComparison.Ordinal) && root.Length > TestRootPrefix.Length
            && root[TestRootPrefix.Length..].All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
            return Fail($"--registry-root is for tests only and must look like {TestRootPrefix}<letters, digits, ->.", 2);
        var exe = Environment.ProcessPath;
        if (mode != "--remove" && (exe is null || !Path.GetFileName(exe).Equals("oml.exe", StringComparison.OrdinalIgnoreCase)))
            return Fail("Run oml.exe itself (not through dotnet), so the entries can call it.", 2);
        // Explorer reads %1, %V, %L, ... in a command; a '%' in the exe path could become one of them.
        if (mode is null && exe!.Contains('%'))
            return Fail($"The path of oml.exe contains '%', which Explorer reads as a placeholder: {exe}. Move oml.exe to a folder without '%' and run oml setup there.", 2);
        try
        {
            return mode switch { "--remove" => Remove(root), "--status" => Status(root, exe!), _ => Install(root, exe!) };
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return Fail($"Cannot change HKCU\\{root}: {e.Message}", 1);
        }
    }

    static int Fail(string message, int code)
    {
        Console.WriteLine(JsonOutput.Serialize(new { error = message }));
        return code;
    }

    static string Command(string exe, Entry e) => $"\"{exe}\" setup run {e.Action} \"{e.Arg}\"";
    static string KeyPath(string root, Entry e) => $@"{root}\{e.Parent}\{e.Verb}";
    static bool Ours(RegistryKey key) => key.GetValue(Marker) as string == MarkerValue;

    static int Install(string root, string exe)
    {
        var hkcu = Registry.CurrentUser;
        var entries = Entries().ToList();
        // Check everything first, so a refusal leaves the registry exactly as it was.
        var foreign = entries.Where(e => { using var k = hkcu.OpenSubKey(KeyPath(root, e)); return k is not null && !Ours(k); })
            .Select(e => @"HKCU\" + KeyPath(root, e)).ToList();
        if (foreign.Count > 0)
            return Fail("Nothing changed. These keys have oml's names but oml did not make them, so it will not overwrite them: "
                + string.Join(", ", foreign) + ". Remove or rename them yourself, then run oml setup again.", 1);
        foreach (var e in entries)
        {
            CreateParents(hkcu, root, e.Parent);
            using var verb = hkcu.CreateSubKey(KeyPath(root, e), writable: true);
            verb.SetValue("", e.Label);
            verb.SetValue(Marker, MarkerValue);
            verb.SetValue("MultiSelectModel", "Single"); // one file or folder at a time; many files at once go through the folder entries
            using var command = verb.CreateSubKey("command", writable: true);
            command.SetValue("", Command(exe, e));
        }
        Console.WriteLine(JsonOutput.Serialize(new
        {
            installed = entries.Select(e => @"HKCU\" + KeyPath(root, e)).ToList(),
            exe,
            note = "Right-click a font, an image, an .oml file or a folder in Explorer. On Windows 11, choose 'Show more options' first. Remove them with: oml setup --remove",
        }));
        return 0;
    }

    // Each missing key on the way to the verbs is created with the marker, so --remove can delete it again once it is empty.
    static void CreateParents(RegistryKey hkcu, string root, string parent)
    {
        var path = root;
        foreach (var part in parent.Split('\\'))
        {
            path += "\\" + part;
            using var existing = hkcu.OpenSubKey(path);
            if (existing is not null) continue;
            using var created = hkcu.CreateSubKey(path, writable: true);
            created.SetValue(Marker, MarkerValue);
        }
    }

    static int Remove(string root)
    {
        var hkcu = Registry.CurrentUser;
        var removed = new List<string>();
        var skipped = new List<string>();
        var parents = Entries().Select(e => e.Parent).Distinct().ToList();
        foreach (var parent in parents)
        {
            using var key = hkcu.OpenSubKey($@"{root}\{parent}", writable: true);
            if (key is null) continue;
            // Every oml.* verb under the parents oml uses, also ones an older oml added; only marked ones are deleted.
            foreach (var name in key.GetSubKeyNames().Where(n => n.StartsWith("oml.", StringComparison.OrdinalIgnoreCase)))
            {
                bool ours;
                using (var verb = key.OpenSubKey(name)) ours = verb is not null && Ours(verb);
                if (ours) { key.DeleteSubKeyTree(name, throwOnMissingSubKey: false); removed.Add($@"HKCU\{root}\{parent}\{name}"); }
                else skipped.Add($@"HKCU\{root}\{parent}\{name}");
            }
        }
        foreach (var parent in parents) PruneParents(hkcu, root, parent);
        Console.WriteLine(JsonOutput.Serialize(new
        {
            removed,
            skipped,
            note = skipped.Count > 0 ? "Skipped keys have oml's names but oml did not make them; they were left as they are." : null,
        }));
        return 0;
    }

    // Deletes, from the deepest up, the keys oml created that are now empty (only the marker left). Stops at the first other key.
    static void PruneParents(RegistryKey hkcu, string root, string parent)
    {
        var parts = parent.Split('\\');
        for (var n = parts.Length; n > 0; n--)
        {
            var path = root + "\\" + string.Join("\\", parts[..n]);
            using (var key = hkcu.OpenSubKey(path))
            {
                if (key is null) continue;
                if (!Ours(key) || key.SubKeyCount > 0 || key.ValueCount > 1) return;
            }
            try { hkcu.DeleteSubKey(path, throwOnMissingSubKey: false); }
            catch (InvalidOperationException) { return; } // another program added a subkey just now: keep the key
        }
    }

    static int Status(string root, string exe)
    {
        var hkcu = Registry.CurrentUser;
        var entries = Entries().Select(e =>
        {
            using var key = hkcu.OpenSubKey(KeyPath(root, e));
            using var command = key?.OpenSubKey("command");
            var text = command?.GetValue("") as string;
            var state = key is null ? "missing" : !Ours(key) ? "not made by oml" : string.IsNullOrEmpty(text) ? "incomplete"
                : text == Command(exe, e) ? "installed" : "other oml.exe";
            return new { key = @"HKCU\" + KeyPath(root, e), label = e.Label, state, command = text };
        }).ToList();
        var installed = entries.Count(e => e.state == "installed");
        Console.WriteLine(JsonOutput.Serialize(new
        {
            installed,
            total = entries.Count,
            entries,
            note = installed == entries.Count ? null
                : entries.Any(e => e.state == "not made by oml") ? "Some keys have oml's names but oml did not make them; oml setup will not overwrite them."
                : "Run oml setup to add or update the missing entries.",
        }));
        return 0;
    }

    // ---- the actions the entries call ----

    sealed record Outcome(bool Ok, string? Output, string? OpenKind, string? OpenPath, string? Message);

    static int RunAction(string[] args)
    {
        var ui = !args.Contains("--no-ui");
        var rest = args.Where(a => a != "--no-ui").ToArray();
        // From a terminal (a console shared with it) the JSON is printed as well; from Explorer the own console is closed.
        var print = !ui || !HideOwnConsole();
        var action = rest.FirstOrDefault() ?? "";
        var input = rest.Length == 2 ? rest[1] : "";
        Outcome outcome;
        try
        {
            if (rest.Length != 2) outcome = new(false, null, null, null, $"Usage: {Usage}");
            else if (string.IsNullOrWhiteSpace(input)) outcome = new(false, null, null, null, "No file or folder was given.");
            else
            {
                // A drive root comes as "D:\" and the command line turns \" into a quote: D:" means D:\ (a quote is never in a path).
                input = Path.GetFullPath(input.EndsWith('"') ? input[..^1] + "\\" : input);
                outcome = Act(action, input);
            }
        }
        catch (Exception e) // the top of a click in Explorer: every failure must reach the person, never a closed window
        {
            outcome = new(false, null, null, null, (e.InnerException ?? e).Message);
        }
        if (ui && outcome.OpenKind is not null && Open(outcome.OpenKind, outcome.OpenPath!) is { } problem)
            outcome = outcome with { Ok = false, Message = outcome.Message is null ? problem : outcome.Message + "\n" + problem };
        Log($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {action} \"{input}\" {(outcome.Ok ? "ok" : "FAILED")}"
            + (outcome.Output is null ? "" : $" -> {outcome.Output}") + (outcome.Message is null ? "" : " : " + outcome.Message.Replace("\n", " | ")));
        if (print)
        {
            Console.WriteLine(JsonOutput.Serialize(new
            {
                action, input, ok = outcome.Ok, output = outcome.Output,
                open = outcome.OpenKind is null ? null : new { kind = outcome.OpenKind, path = outcome.OpenPath },
                message = outcome.Message, log = LogFile,
            }));
        }
        if (ui && outcome.Message is { } message)
            MessageBoxW(IntPtr.Zero, outcome.Ok ? message : $"{message}\n\nLog: {LogFile}", "oml", outcome.Ok ? 0x40u | 0x10000u : 0x10u | 0x10000u);
        return outcome.Ok ? 0 : 1;
    }

    static Outcome Act(string action, string input)
    {
        var folder = Path.GetDirectoryName(input) ?? input;
        var name = Path.GetFileNameWithoutExtension(input);
        switch (action)
        {
            case "font":
            {
                NeedFile(input, Fonts);
                var output = Free(Path.Combine(folder, name + ".woff2"));
                return Done(Dashboard.Execute(["font", "woff2", input, output]), output, "select");
            }
            case "image":
            {
                NeedFile(input, Images);
                var to = ImageFormat(input);
                var output = Free(Path.Combine(folder, $"{name}.optimized.{to}"));
                return Done(Dashboard.Execute(["image", input, "--to", to, "--out", output]), output, "select");
            }
            case "ico":
            {
                NeedFile(input, Images);
                var output = Free(Path.Combine(folder, name + ".ico"));
                return Done(Dashboard.Execute(["image", input, "--to", "ico", "--out", output]), output, "select");
            }
            case "trace":
            {
                NeedFile(input, Rasters);
                var output = Free(Path.Combine(folder, name + ".svg"));
                var r = Dashboard.Execute(["image", input, "--to", "svg", "--out", output]);
                // A photo still gets its SVG, with the advice to keep it as a raster image.
                var note = ((r.Output as JsonObject)?["details"] as JsonObject)?["note"]?.GetValue<string>();
                var done = Done(r, output, "select");
                return done.Ok && note is not null ? done with { Message = note } : done;
            }
            case "openapi":
            {
                NeedFile(input, [".oml"]);
                var r = Dashboard.Execute(["openapi", input, "--out", folder]);
                var first = ((r.Output as JsonObject)?["files"] as JsonArray)?.OfType<JsonObject>().Select(f => f["file"]?.GetValue<string>()).FirstOrDefault(f => f is not null);
                return Done(r, first, "select");
            }
            case "dashboard":
                NeedFile(input, [".oml"]);
                return new(true, null, "dashboard", input, null);
            case "fonts":
                NeedFolder(input);
                return Batch(input, Fonts, "woff2", "fonts", (i, o) => ["font", "woff2", i, o], i => Path.GetFileNameWithoutExtension(i) + ".woff2");
            case "images":
                NeedFolder(input);
                return Batch(input, Images, "optimized", "images", (i, o) => ["image", i, "--to", ImageFormat(i), "--out", o],
                    i => Path.GetFileNameWithoutExtension(i) + "." + ImageFormat(i));
            case "architecture":
            {
                NeedFolder(input);
                if (Path.GetPathRoot(input) is { } driveRoot && Path.TrimEndingDirectorySeparator(driveRoot) == Path.TrimEndingDirectorySeparator(input))
                    return new(false, null, null, null, $"{input} is a whole drive. Right-click the folder that holds your .oap or .oml files instead.");
                if (ScanProblem(input) is { } problem) return problem;
                var output = Free(Path.Combine(input, "architecture.html"));
                return Done(Dashboard.Execute(["architecture", "--in", input, "--out", output]), output, "file");
            }
            default:
                return new(false, null, null, null, $"Unknown action '{action}'. Usage: {Usage}");
        }
    }

    // The architecture command reads every subfolder. Check first, with limits, so a click on a huge folder does not
    // work for minutes without a window.
    const int ScanMaxDepth = 8, ScanMaxEntries = 50_000;

    static Outcome? ScanProblem(string folder)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var baseDepth = Path.TrimEndingDirectorySeparator(folder).Count(c => c == Path.DirectorySeparatorChar);
        var (entries, modules) = (0, 0);
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder, "*", options))
        {
            if (++entries > ScanMaxEntries || entry.Count(c => c == Path.DirectorySeparatorChar) - baseDepth > ScanMaxDepth)
                return new(false, null, null, null, $"{folder} is too big for this entry (more than {ScanMaxEntries} files and folders, or more than {ScanMaxDepth} folder levels). " +
                    "Right-click a smaller folder, or run: oml architecture --in <folder>");
            if (Path.GetExtension(entry).ToLowerInvariant() is ".oml" or ".oap") modules++;
        }
        return modules > 0 ? null : new(true, null, null, null, $"No .oml or .oap files in {folder} or its subfolders.");
    }

    // WebP for raster images (smaller than PNG and JPEG, and every current browser shows it); an SVG stays an optimized SVG.
    static string ImageFormat(string input) => Path.GetExtension(input).Equals(".svg", StringComparison.OrdinalIgnoreCase) ? "svg" : "webp";

    static void NeedFile(string input, string[] types)
    {
        if (!File.Exists(input)) throw new FileNotFoundException($"File not found: {input}");
        if (!types.Contains(Path.GetExtension(input).ToLowerInvariant()))
            throw new ArgumentException($"This entry works on {string.Join(", ", types)} files, not on {Path.GetFileName(input)}.");
    }

    static void NeedFolder(string input)
    {
        if (!Directory.Exists(input)) throw new DirectoryNotFoundException($"Folder not found: {input}");
    }

    static string? Error(Dashboard.Result r) =>
        r.Code == 0 ? null : (r.Output as JsonObject)?["error"]?.ToString() ?? (r.Stderr.Length > 0 ? r.Stderr : $"{r.Cli} ended with exit code {r.Code}.");

    static Outcome Done(Dashboard.Result r, string? output, string openKind) =>
        Error(r) is { } error ? new(false, null, null, null, error)
        : output is null || !File.Exists(output) ? new(false, null, null, null, $"{r.Cli} made no file.")
        : new(true, output, openKind, output, null);

    // Every matching file in the folder (not its subfolders) into a new subfolder; a file that fails does not stop the others.
    static Outcome Batch(string folder, string[] types, string subfolder, string what, Func<string, string, string[]> command, Func<string, string> outputName)
    {
        var inputs = Directory.GetFiles(folder).Where(f => types.Contains(Path.GetExtension(f).ToLowerInvariant())).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (inputs.Length == 0) return new(true, null, null, null, $"No {what} ({string.Join(", ", types)}) in {folder}.");
        var target = Free(Path.Combine(folder, subfolder));
        var errors = new List<string>();
        foreach (var input in inputs)
        {
            var output = Free(Path.Combine(target, outputName(input))); // a.png and a.jpg both become a.webp: the second gets "a (2).webp"
            if (Error(Dashboard.Execute(command(input, output))) is { } error) errors.Add($"{Path.GetFileName(input)}: {error}");
        }
        var made = Directory.Exists(target);
        return errors.Count == 0
            ? new(true, target, "folder", target, null)
            : new(false, made ? target : null, made ? "folder" : null, made ? target : null,
                $"{errors.Count} of {inputs.Length} {what} failed:\n" + string.Join("\n", errors.Take(5)) + (errors.Count > 5 ? $"\n(and {errors.Count - 5} more)" : ""));
    }

    // A name that does not exist yet: name.ext, then "name (2).ext", ... Outputs never replace a file.
    internal static string Free(string path)
    {
        var (folder, name, ext) = (Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path), Path.GetExtension(path));
        for (var n = 2; File.Exists(path) || Directory.Exists(path); n++) path = Path.Combine(folder, $"{name} ({n}){ext}");
        return path;
    }

    // Shows the result: the new file selected in Explorer, the new folder, or the page. Returns a problem, or null.
    static string? Open(string kind, string path)
    {
        try
        {
            if (kind == "select") Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose();
            else if (kind == "dashboard")
            {
                // The dashboard needs its own window (closing it stops the dashboard); start it minimized.
                using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"dashboard --module \"{path}\"")
                    { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized });
                if (p is not null && p.WaitForExit(5000) && p.ExitCode != 0)
                    return $"oml dashboard stopped at once (exit code {p.ExitCode}). Run 'oml dashboard' in a terminal to see why.";
            }
            else Process.Start("explorer.exe", $"\"{path}\"")?.Dispose(); // a folder opens; a file opens in its default app
            return null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return $"Could not open {path}: {e.Message}";
        }
    }

    // OML_SETUP_LOG moves the log (the tests use it); otherwise %LOCALAPPDATA%\oml-cli\setup.log.
    static string LogFile => Environment.GetEnvironmentVariable("OML_SETUP_LOG") is { Length: > 0 } file ? Path.GetFullPath(file)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "oml-cli", "setup.log");

    static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, line + Environment.NewLine);
            // Keep the last 1000 lines once the file passes 256 KB.
            if (new FileInfo(LogFile).Length > 256 * 1024) File.WriteAllLines(LogFile, File.ReadAllLines(LogFile).TakeLast(1000));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the message box still shows the result */ }
    }

    // Started from Explorer, oml.exe gets a new console window of its own. Detach from it so it closes at once; a console
    // shared with a terminal (more than one process on it) is left alone.
    static bool HideOwnConsole()
    {
        var ids = new uint[2];
        if (GetConsoleProcessList(ids, 2) != 1) return false;
        FreeConsole();
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern uint GetConsoleProcessList(uint[] processList, uint count);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
