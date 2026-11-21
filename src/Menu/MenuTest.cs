using System.Text.RegularExpressions;

namespace OmlCli;

// Keys from a script, for tests: "Down Down Enter Esc q t:hello". A word of one character is that key; t:<text> types
// the text; Space, Enter, Esc, Up, Down, Left, Right, Home, End, Back, Del, CtrlC are the named keys.
internal sealed class ScriptedKeys : IKeys
{
    readonly Queue<ConsoleKeyInfo> queue = new();
    public ScriptedKeys(string script)
    {
        foreach (var word in script.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.StartsWith("t:")) { foreach (var c in word[2..]) queue.Enqueue(Char(c)); }
            else if (word.Length == 1) queue.Enqueue(Char(word[0]));
            else queue.Enqueue(word switch
            {
                "Space" => Char(' '),
                "CtrlC" => new ConsoleKeyInfo('\x03', ConsoleKey.C, false, false, true),
                _ => new ConsoleKeyInfo('\0', Enum.Parse<ConsoleKey>(word switch { "Esc" => "Escape", "Up" => "UpArrow", "Down" => "DownArrow", "Left" => "LeftArrow", "Right" => "RightArrow", "Back" => "Backspace", "Del" => "Delete", _ => word }), false, false, false),
            });
        }
    }
    public int Left => queue.Count;
    static ConsoleKeyInfo Char(char c) => new(c, c == ' ' ? ConsoleKey.Spacebar : char.IsAsciiLetter(c) ? Enum.Parse<ConsoleKey>(char.ToUpperInvariant(c).ToString()) : ConsoleKey.NoName, false, false, false);
    public ConsoleKeyInfo Read() => queue.Count > 0 ? queue.Dequeue() : throw new InvalidOperationException("the script of keys ran out");
}

// `oml menu-test` (hidden): the menu without a terminal. Scripted keys, an in-memory writer, a temp folder; "ok" or the failures.
internal static class MenuTest
{
    static readonly Regex Ansi = new("\x1b\\[[0-9;?]*[A-Za-z]");

    static (Screen Screen, StringWriter Text, ScriptedKeys Keys) Make(string script, bool cursor = true)
    {
        var text = new StringWriter();
        var keys = new ScriptedKeys(script);
        return (new Screen(text, keys, cursor, () => (100, 30)), text, keys);
    }

    static Item[] Rows(params string[] labels) => labels.Select(l => new Item(l, l + " row")).ToArray();

    public static string Run()
    {
        var bad = new List<string>();
        void Check(bool ok, string what) { if (!ok) bad.Add(what); }
        void Try(string what, Action test)
        {
            try { test(); }
            catch (Exception e) { bad.Add($"{what}: {e.GetType().Name} {e.Message}"); }
        }
        var tmp = Path.Combine(Path.GetTempPath(), "oml-menu-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            // 1. Every task builds a command of the table or the usage text, with options of that command's usage lines only.
            Try("tasks", () =>
            {
                var names = CommandTable.All.Select(c => c.Name).Concat(MenuModel.Commands().Select(c => c.Name)).ToHashSet();
                foreach (var (group, task) in MenuModel.AllTasks())
                    foreach (var args in new[] { task.Before, task.Build(MenuModel.Sample(task)) }.Where(a => a is not null))
                    {
                        var line = MenuModel.Line(args!);
                        Check(names.Contains(args![0]), $"{task.Title}: {args[0]} is not a command ({line})");
                        var usage = string.Join("\n", MenuModel.UsageLines().Where(l => l == "oml " + args[0] || l.StartsWith("oml " + args[0] + " "))) + (args[0] == "lifetime" ? LifeTime.Usage : ""); // lifetime lists its options in its own text
                        foreach (var option in args.Where(a => a.StartsWith("--")))
                            Check(usage.Contains(option), $"{task.Title}: {option} is not in the usage of {args[0]} ({line})");
                        Check(MenuModel.Refusal(args) is null, $"{task.Title}: asks for a yes, so it must not be in the menu ({line})");
                    }
                Check(MenuModel.Groups.SelectMany(g => g.Tasks).All(t => t.Needs is null || MenuModel.Offered(t) == MenuModel.UsageLines().Any(l => l.StartsWith("oml " + t.Needs))), "Needs follows the usage text");
                Check(MenuModel.Words("describe \"a b.oml\" X") is ["describe", "a b.oml", "X"], "Words keeps quoted parts together");
                Check(MenuModel.Refusal(["install", "a.oml", "b.oml"]) is not null && MenuModel.Refusal(["lifetime", "deploy", "A"]) is not null && MenuModel.Refusal(["lifetime", "apps"]) is null, "Refusal");
            });

            // 2. Filtering finds a task by a word of its description (and the words of a query all have to match).
            Try("filter", () =>
            {
                foreach (var (_, task) in MenuModel.AllTasks())
                {
                    var word = task.About.Split(' ', ',', '.', '(', ')', ':').OrderByDescending(w => w.Length).First();
                    Check(MenuModel.Search(word).Any(x => x.Task == task), $"search '{word}' does not find '{task.Title}'");
                }
                Check(MenuModel.Search("favicon").Any(x => x.Task.Title == "Make an ICO"), "search favicon finds Make an ICO");
                Check(MenuModel.Search("favicon zzzz").Count == 0, "all words must match");
                var (s, text, keys) = Make("t:ico Enter");
                var shown = s.Pick("T", Rows("Make an ICO", "Check it", "Other"), new PickState(), "hint", deep: q => MenuModel.Search(q).Select(x => new Item(x.Task.Title, "", x.Task)).ToList());
                Check(shown?.Item.Label == "Make an ICO", "typing ico then Enter picks the ICO task");
            });

            // 3. Keys: j/k and arrows move, Enter chooses, Esc goes back, q quits, "/" lets a word start with q.
            Try("keys", () =>
            {
                var items = Rows("one", "two", "three");
                Check(Make("j j Enter").Screen.Pick("T", items, new PickState(), "h")?.Item.Label == "three", "j j Enter picks the third row");
                Check(Make("Down Down Up Enter").Screen.Pick("T", items, new PickState(), "h")?.Item.Label == "two", "Down Down Up Enter picks the second row");
                Check(Make("Esc").Screen.Pick("T", items, new PickState(), "h") is null, "Esc goes back");
                Check(Make("Left").Screen.Pick("T", items, new PickState(), "h") is null, "Left goes back");
                Check(Make("t:thr Esc Esc").Screen.Pick("T", items, new PickState(), "h") is null, "Esc clears a filter first, then goes back");
                var quit = false;
                try { Make("q").Screen.Pick("T", items, new PickState(), "h"); } catch (QuitMenu) { quit = true; }
                Check(quit, "q quits");
                quit = false;
                try { Make("CtrlC").Screen.Pick("T", items, new PickState(), "h"); } catch (QuitMenu) { quit = true; }
                Check(quit, "Ctrl+C quits");
                Check(Make("/ q Enter").Screen.Pick("T", Rows("quiet", "loud"), new PickState(), "h")?.Item.Label == "quiet", "/ then q filters instead of quitting");
                Check(Make("2 Enter", cursor: false).Screen.Pick("T", items, new PickState(), "h")?.Item.Label == "two", "plain mode: Type a number");
                var line = Make("t:abcd Left Left Back t:X Home Del Enter").Screen.ReadLine("> ", "");
                Check(line == "Xcd", $"line editing gave '{line}', want Xcd");
                Check(Make("t:abc Esc").Screen.ReadLine("> ") is null, "Esc cancels a line");

                // The main loop: Enter (Start), Enter (Check my setup), Enter (run it), a key, Esc back, q.
                var calls = new List<string>();
                var (s, text, keys) = Make("Enter Enter Enter Enter Esc q");
                MenuApp.Loop(s, a => { calls.Add(string.Join(' ', a)); return 0; }, tmp);
                Check(calls.SequenceEqual(["doctor --text"]), "the main loop ran: " + string.Join("; ", calls));
                Check(keys.Left == 0, "the main loop used every key");
                Check(Ansi.Replace(text.ToString(), "").Contains("oml doctor --text"), "the command line is shown before it runs");
                var (s2, _, keys2) = Make("Enter Esc Esc");
                MenuApp.Loop(s2, a => 0, tmp);
                Check(keys2.Left == 0, "Esc leaves the group and then the menu");
            });

            // 4. The settings editor: toggle, cycle, tick-list, text, locked; the file reads back with no problems.
            Try("settings", () =>
            {
                var empty = Path.Combine(tmp, "empty");
                Directory.CreateDirectory(empty);
                var (c0, _, _) = Make("Enter Esc");
                MenuApp.Settings(c0, empty);
                Check(File.Exists(ProjectConfig.PathIn(empty)), "the editor creates the profile when it is missing");

                var root = Path.Combine(tmp, "project");
                Directory.CreateDirectory(root);
                ProjectConfig.Save(root, new ProjectConfig.Values());
                var shown = "";
                void Edit(string script)
                {
                    var (s, text, keys) = Make(script);
                    MenuApp.Settings(s, root);
                    shown = Ansi.Replace(text.ToString(), "");
                    Check(keys.Left == 0, $"settings script '{script}' left {keys.Left} keys");
                }
                Check(ProjectConfig.Load(root).On("checks.browser"), "checks.browser starts on");
                Edit("t:checks.browser Enter Esc Esc");
                Check(!ProjectConfig.Load(root).On("checks.browser"), "Enter toggles a boolean off");
                Check(shown.Contains("Saved: checks.browser = off"), "the screen says it saved");
                Edit("t:checks.browser Enter Esc Esc");
                Check(ProjectConfig.Load(root).On("checks.browser"), "Enter toggles it back on");
                Edit("t:Who Enter Esc Esc");
                Check(ProjectConfig.Load(root).Get("browser") == "agent", "Enter cycles a choice");
                Edit("t:Who Enter Esc Esc");
                Check(ProjectConfig.Load(root).Get("browser") == "oml", "a choice wraps around");
                Edit("t:hooks.omlWrites Enter Esc Esc");
                Check(shown.Contains("locked") && ProjectConfig.Load(root).On("hooks.omlWrites"), "a locked setting is refused");
                Edit("t:autoApprove.install Enter Esc Esc");
                Check(ProjectConfig.Load(root).On("autoApprove.install") && ProjectConfig.Profile(ProjectConfig.Load(root)).Any(l => l.StartsWith("Do not ask before install")),
                    "autoApprove.install turns on and the profile says not to ask");
                Edit("t:ui.targets Enter Space Enter Esc Esc");
                Check(ProjectConfig.Load(root).Get("ui.targets") == "tablet,phone", "a tick-list changes a list setting: " + ProjectConfig.Load(root).Get("ui.targets"));
                Edit("t:ui.themes Enter Space Space Enter Esc Esc");
                Check(ProjectConfig.Load(root).Get("ui.themes") == "light,dark", "a tick-list left as it was");
                Edit("t:dev.example Enter t:https://dev.example.com Enter Esc Esc");
                Check(ProjectConfig.Load(root).Get("app.devUrl").StartsWith("https://dev.example.com"), "a text prompt saves an address: " + shown[^Math.Min(shown.Length, 300)..]);
                Edit("t:outsystems.odcMcpServer Enter t:http://example.com Enter Esc Esc");
                Check(shown.Contains("https") && ProjectConfig.Load(root).Get("outsystems.odcMcpServer") == "", "a bad address is refused with the reason");
                var back = ProjectConfig.Load(root);
                Check(back.Error is null && back.Problems.Count == 0, "the saved file reads back with no problems: " + string.Join("; ", back.Problems));

                // The older file name is read, a replaced key is named, and the next save writes the new name only.
                var legacy = Path.Combine(tmp, "legacy");
                Directory.CreateDirectory(Path.Combine(legacy, ".oml"));
                File.WriteAllText(Path.Combine(legacy, ".oml", ProjectConfig.LegacyFileName), """{ "checks": { "qa": false }, "ask": { "install": true } }""");
                var old = ProjectConfig.Load(legacy);
                Check(!old.On("checks.browser") && old.Problems.Any(p => p.Contains("autoApprove.install")), "the older config.json is read and ask.install points to autoApprove: " + string.Join("; ", old.Problems));
                ProjectConfig.Save(legacy, old);
                Check(File.Exists(ProjectConfig.PathIn(legacy)) && !File.Exists(Path.Combine(legacy, ".oml", ProjectConfig.LegacyFileName)) && !ProjectConfig.Load(legacy).On("checks.browser"),
                    "a save writes oml-config.json with the old values and removes config.json");
            });

            // 5. The file picker lists files of this folder and modules/, newest first, and folders.
            Try("picker", () =>
            {
                var work = Path.Combine(tmp, "work");
                Directory.CreateDirectory(Path.Combine(work, "modules"));
                File.WriteAllText(Path.Combine(work, "a.oml"), "");
                File.WriteAllText(Path.Combine(work, "notes.txt"), "");
                File.WriteAllText(Path.Combine(work, "modules", "b.oml"), "");
                File.SetLastWriteTime(Path.Combine(work, "a.oml"), DateTime.Now.AddDays(-2));
                var files = MenuModel.Files(work, [".oml"]).Select(f => f.Path).ToArray();
                Check(files.Length == 2 && files[0] == Path.Combine("modules", "b.oml") && files[1] == "a.oml", "files, newest first: " + string.Join(", ", files));
                Check(MenuModel.Folders(work).Select(f => f.Path).SequenceEqual(["modules"]), "folders");
                var (s, _, _) = Make("Enter");
                Check(MenuApp.PickPath(s, "Which?", [".oml"], false, work) == Path.Combine("modules", "b.oml"), "Enter picks the newest file");
                var typed = Path.Combine(work, "a.oml");
                var (s2, _, _) = Make("Down Down Enter t:" + typed + " Enter");
                Check(MenuApp.PickPath(s2, "Which?", [".oml"], false, work) == typed, "Type a path... takes a path");
            });

            // 6. A bare oml opens the menu only for a person at a terminal.
            Try("bare", () =>
            {
                Func<string, string?> Env(params string[] set) => n => set.Contains(n) ? "1" : n == "OML_OUTPUT" && set.Contains("json") ? "json" : null;
                Check(MenuModel.Bare(false, false, Env()) == BareChoice.Menu, "a terminal gets the menu");
                Check(MenuModel.Bare(true, false, Env()) == BareChoice.Overview, "redirected input gets the overview");
                Check(MenuModel.Bare(false, true, Env()) == BareChoice.Overview, "redirected output gets the overview");
                Check(MenuModel.Bare(false, false, n => n == "OML_OUTPUT" ? "json" : null) == BareChoice.Overview, "OML_OUTPUT=json gets the overview");
                Check(MenuModel.Bare(false, false, n => n == "OML_MENU" ? "0" : null) == BareChoice.Overview, "OML_MENU=0 gets the overview");
                foreach (var v in new[] { "OML_SESSION", "CLAUDE_SESSION_ID", "CLAUDE_CODE_SESSION_ID", "CODEX_SESSION_ID", "CI", "GITHUB_ACTIONS" })
                    Check(MenuModel.Bare(false, false, Env(v)) == BareChoice.Overview, $"{v} gets the overview");
            });
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return bad.Count == 0 ? "ok" : string.Join(Environment.NewLine, bad.Select(b => "FAIL " + b));
    }
}
