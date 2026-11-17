namespace OmlCli;

// `oml menu`: the flows (groups, tasks, file picker, settings editor, every command) on top of Screen, and the real
// console behind IKeys. For people only: agents and scripts call the commands, and a bare oml opens this menu only
// at a real terminal (MenuModel.Bare). A task runs through the same dispatch as `oml <command>`, in this process.
internal sealed class ConsoleKeys : IKeys
{
    public ConsoleKeyInfo Read() => Console.ReadKey(true);
}

internal static class MenuApp
{
    static readonly object TypePath = new();
    const string PickHint = "Enter chooses   arrows or j/k move   type to filter   Esc goes back   q quits";

    public static int Run(string[] rest)
    {
        if (rest.Length > 0) { TerminalOutput.PrintObject(new { error = "oml menu takes no arguments.", hint = "Usage: oml menu" }); return 2; }
        if (Console.IsInputRedirected)
        {
            TerminalOutput.PrintObject(new { error = "oml menu needs a terminal: standard input is redirected. Call the commands directly (oml --help)." });
            return 2;
        }
        // Cursor movement needs a console that understands ANSI. NO_COLOR only turns the colors off (TerminalColors), not this.
        var term = Environment.GetEnvironmentVariable("TERM");
        var cursor = !Console.IsOutputRedirected && term != "dumb" && (TerminalColors.Enabled || !OperatingSystem.IsWindows() && !string.IsNullOrEmpty(term));
        var screen = new Screen(Console.Out, new ConsoleKeys(), cursor, ConsoleSize);
        // A task writes its own output: Ctrl+C must stop it (not be a key of the menu) and the cursor must show.
        screen.Suspend = () => { SetControlC(false); if (cursor) Console.Out.Write(Screen.Show); };
        screen.Resume = () => SetControlC(true);
        ConsoleCancelEventHandler restore = (_, _) => { try { if (cursor) Console.Out.Write(Screen.Show); } catch { } };
        Console.CancelKeyPress += restore;
        SetControlC(true);
        try { Loop(screen, DefaultExec, Environment.CurrentDirectory); return 0; }
        finally
        {
            SetControlC(false);
            Console.CancelKeyPress -= restore;
            screen.Restore(); // never leave the cursor hidden
        }
    }

    static void SetControlC(bool asInput)
    {
        try { Console.TreatControlCAsInput = asInput; } catch (Exception e) when (e is IOException or PlatformNotSupportedException) { }
    }

    static (int Width, int Height) ConsoleSize()
    {
        try { return (Console.WindowWidth > 40 ? Console.WindowWidth : 100, Console.WindowHeight > 8 ? Console.WindowHeight : 30); }
        catch (IOException) { return (100, 30); }
    }

    // The same entry as `oml <command>`: the old command names, the human view, the launcher's dispatch and its last guard.
    static int DefaultExec(string[] args)
    {
        TerminalOutput.ForceHuman = true;
        var a = CliUsage.Normalize(args);
        TerminalOutput.Command = a[0];
        return Program.Dispatch(a, a);
    }

    // ---------------------------------------------------------------- the main list

    // Returns when the person quits (q or Ctrl+C anywhere, or Esc at the main list).
    internal static void Loop(Screen s, Func<string[], int> exec, string cwd)
    {
        var st = new PickState();
        try
        {
            while (true)
            {
                var items = MenuModel.Groups.Select(g => new Item(g.Title, g.About, g)).ToList();
                // Typing at the main list searches every task, so a person need not know the group.
                IReadOnlyList<Item> Deep(string q) => MenuModel.Search(q).Select(x => new Item(x.Task.Title, x.Task.About + " (" + x.Group.Title + ")", x.Task)).ToList();
                var picked = s.Pick("oml menu: what do you want to do?", items, st, PickHint, deep: Deep);
                if (picked is null) return;
                s.Clear();
                switch (picked.Item.Tag)
                {
                    case MenuTask task: RunTask(s, task, exec, cwd); break;
                    case MenuGroup { Special: "settings" }: Settings(s, cwd); break;
                    case MenuGroup { Special: "commands" }: Commands(s, exec); break;
                    case MenuGroup g: Group(s, g, exec, cwd); break;
                }
                s.Clear();
            }
        }
        catch (QuitMenu) { s.Clear(); }
    }

    static void Group(Screen s, MenuGroup g, Func<string[], int> exec, string cwd)
    {
        var st = new PickState();
        var items = MenuModel.Tasks(g).Select(t => new Item(t.Title, t.About, t)).ToList();
        while (s.Pick(g.Title + ": " + g.About, items, st, PickHint) is { } picked)
        {
            s.Clear();
            RunTask(s, (MenuTask)picked.Item.Tag!, exec, cwd);
            s.Clear();
        }
    }

    // ---------------------------------------------------------------- a task

    // Questions, then the exact command line, then the run with the human view, then "Press any key to go back".
    static void RunTask(Screen s, MenuTask t, Func<string[], int> exec, string cwd)
    {
        var answers = new Dictionary<string, string>();
        foreach (var ask in t.Asks)
        {
            if (AskFor(s, t, ask, cwd) is not { } value) return;
            answers[ask.Key] = value;
        }
        var steps = new List<string[]>();
        if (t.Before is not null) steps.Add(t.Before);
        steps.Add(t.Build(answers));
        var lines = new List<string> { t.About, "", steps.Count > 1 ? "This runs, one after the other:" : "This runs:" };
        lines.AddRange(steps.Select(x => "  " + MenuModel.Line(x)));
        lines.Add("");
        lines.Add("The command is what you would type yourself: oml <command> --help explains its options.");
        if (!s.Confirm(t.Title, lines, "Enter runs it   Esc goes back")) return;
        s.Clear();
        RunSteps(s, steps, exec);
    }

    static void RunSteps(Screen s, IEnumerable<string[]> steps, Func<string[], int> exec)
    {
        s.Suspend();
        var first = true;
        foreach (var step in steps)
        {
            if (!first) s.Out.WriteLine();
            first = false;
            s.Out.WriteLine("> " + MenuModel.Line(step));
            var code = exec(step);
            if (code != 0) s.Out.WriteLine($"(exit code {code})");
        }
        s.Resume();
        s.Wait();
    }

    static string? AskFor(Screen s, MenuTask t, Ask a, string cwd)
    {
        switch (a.Kind)
        {
            case AskKind.Text:
                while (true)
                {
                    if (s.ReadLine(a.Prompt + ": ", "", [t.Title + ": " + t.About]) is not { } v) return null;
                    if (v.Trim().Length > 0 || a.Optional) return v.Trim();
                }
            case AskKind.Module: return PickPath(s, a.Prompt, [".oml"], false, cwd);
            case AskKind.Font: return PickPath(s, a.Prompt, [".ttf", ".otf"], false, cwd);
            case AskKind.Image: return PickPath(s, a.Prompt, [".png", ".jpg", ".jpeg", ".webp", ".svg", ".gif", ".bmp"], false, cwd);
            default: return PickPath(s, a.Prompt, null, a.Optional, cwd);
        }
    }

    // ---------------------------------------------------------------- the file picker

    // Files (exts) or folders (null) of this folder and modules/ and staged/, newest first, then "Type a path...".
    // "" when an optional folder is skipped; null when the person goes back.
    internal static string? PickPath(Screen s, string prompt, string[]? exts, bool optional, string cwd)
    {
        var st = new PickState();
        var status = "";
        while (true)
        {
            var items = new List<Item>();
            if (optional) items.Add(new Item("(none)", "Leave this out", ""));
            if (exts is null) items.Add(new Item(".", "This folder", "."));
            foreach (var (path, when) in exts is null ? MenuModel.Folders(cwd) : MenuModel.Files(cwd, exts))
                items.Add(new Item(path, when == DateTime.MinValue ? "" : when.ToString("yyyy-MM-dd HH:mm"), path));
            items.Add(new Item("Type a path...", "Any " + (exts is null ? "folder" : "file") + " on this computer", TypePath));
            var title = prompt + (exts is null ? " (a folder)" : " (" + string.Join(" ", exts) + " here, in modules/ and in staged/)");
            if (s.Pick(title, items, st, PickHint, status, statusBad: true) is not { } picked) return null;
            if (picked.Item.Tag != TypePath) return (string)picked.Item.Tag!;
            var typed = s.ReadLine("Path: ", "", [prompt])?.Trim().Trim('"');
            if (string.IsNullOrEmpty(typed)) { status = ""; continue; }
            if (exts is null ? Directory.Exists(typed) : File.Exists(typed)) return typed;
            status = (exts is null ? "Not a folder: " : "Not a file: ") + typed;
        }
    }

    // ---------------------------------------------------------------- every command

    static void Commands(Screen s, Func<string[], int> exec)
    {
        var st = new PickState();
        var items = MenuModel.Commands().Select(c => new Item(c.Name, c.Lines[0][(4 + c.Name.Length)..].Trim(), c)).ToList();
        while (s.Pick("Every command: Enter shows its usage and lets you type it", items, st, PickHint) is { } picked)
        {
            var (name, lines) = ((string, string[]))picked.Item.Tag!;
            var typed = s.ReadLine("oml ", name + " ", lines);
            s.Clear();
            if (typed is null) continue;
            var args = MenuModel.Words(typed);
            if (MenuModel.Refusal(args) is { } refusal)
            {
                s.Confirm("Not run", [refusal], "Enter or Esc goes back");
                s.Clear();
                continue;
            }
            RunSteps(s, [args], exec);
            s.Clear();
        }
    }

    // ---------------------------------------------------------------- project settings

    // One row per setting of .oml/config.json: its value and what it does. Space or Enter changes it and saves at once.
    internal static void Settings(Screen s, string dir)
    {
        var root = ProjectConfig.RootOf(dir);
        if (root is null)
        {
            var path = ProjectConfig.PathIn(dir);
            if (!s.Confirm("Project settings", ["There is no project profile here or above it:", "  " + path, "", "Create it with the default settings?"], "Enter creates it   Esc goes back")) return;
            ProjectConfig.Save(dir, new ProjectConfig.Values());
            root = dir;
        }
        var st = new PickState();
        var status = "";
        var bad = false;
        var file = ProjectConfig.PathIn(root);
        while (true)
        {
            var v = ProjectConfig.Load(root);
            if (v.Error is not null) { s.Clear(); s.Confirm("Project settings", [v.Error, "Fix or remove the file first."], "Enter or Esc goes back"); return; }
            var items = ProjectConfig.Settings.Select(x => new Item(x.Key, x.About, x, MenuModel.Show(x, v.Get(x.Key)) + (x.Locked ? " (locked)" : ""), Dim: x.Locked)).ToList();
            var hint = "Space or Enter changes it, and saves   type to filter   Esc goes back";
            if (s.Pick("Project settings: " + file, items, st, hint, status, bad, space: true) is not { } picked) return;
            (status, bad) = Edit(s, root, (ProjectConfig.Setting)picked.Item.Tag!, v);
        }
    }

    // Toggle, cycle, tick-list or text prompt, then the same validation and save as `oml config set`.
    internal static (string Text, bool Bad) Edit(Screen s, string root, ProjectConfig.Setting set, ProjectConfig.Values v)
    {
        if (set.Locked) return ($"{set.Key} is locked: it stays {MenuModel.Show(set, set.Default)}. {set.About}", true);
        var text = MenuModel.Next(set, v.Get(set.Key));
        if (text is null && set.Kind == "list")
        {
            var current = v.List(set.Key);
            var ticks = s.Tick(set.Key + ": " + set.About, set.Choices!.Select(c => new Item(c, "", null, "", false, current.Contains(c))).ToList(), "Space ticks   Enter keeps the choice   Esc leaves it");
            if (ticks is null) return ("Not changed.", false);
            text = string.Join(",", set.Choices!.Where((_, i) => ticks[i]));
        }
        else if (text is null)
        {
            if (s.ReadLine(set.Key + ": ", v.Get(set.Key), [set.About]) is not { } typed) return ("Not changed.", false);
            text = typed;
        }
        var (problem, _, value, _) = ProjectConfig.Change(root, set, text);
        return problem is not null ? (problem, true) : ($"Saved: {set.Key} = {MenuModel.Show(set, value)} (.oml/config.json)", false);
    }
}
