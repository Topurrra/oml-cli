using System.Text;

namespace OmlCli;

// The screen layer of `oml menu`: draws to a TextWriter and reads keys through IKeys, so a test can drive it with
// scripted keys and an in-memory writer. No alternate screen buffer: a screen is a few lines redrawn in place with
// relative cursor movement (up N lines, erase to the end), which behaves the same in Windows Terminal, the VS Code
// terminal and a plain xterm. Without cursor movement (TERM=dumb, redirected output) every screen is a numbered
// prompt ("Type a number"). q or Ctrl+C anywhere throws QuitMenu; the caller restores the cursor.
internal interface IKeys { ConsoleKeyInfo Read(); }

internal sealed class QuitMenu : Exception { }

// Tag carries what the row stands for; Value is a middle column (a setting's value); On is the tick of a tick-list.
internal sealed record Item(string Label, string About = "", object? Tag = null, string Value = "", bool Dim = false, bool On = false);
internal sealed record Picked(Item Item, bool Space);

// Kept by the caller across redraws of one list, so a rebuilt list keeps its cursor and its filter.
internal sealed class PickState
{
    public int Cursor;
    public string Filter = "";
    public bool Filtering; // "/" was typed: j, k and q are text now
    public bool Typing => Filter.Length > 0 || Filtering;
}

internal sealed class Screen(TextWriter w, IKeys keys, bool cursor, Func<(int Width, int Height)> size)
{
    internal const string Hide = "\x1b[?25l", Show = "\x1b[?25h";
    int up; // lines from the cursor up to the first line this screen drew
    public bool Cursor => cursor;
    public TextWriter Out => w;
    public Action Suspend = () => { }; // before a command writes its own output (shows the cursor, Ctrl+C works again)
    public Action Resume = () => { };

    int Cols => Math.Max(24, size().Width - 1);
    int Rows => Math.Max(8, size().Height - 1);
    static bool IsQuit(ConsoleKeyInfo k) => k.Key == ConsoleKey.C && (k.Modifiers & ConsoleModifiers.Control) != 0;
    static string Dim(string t) => TerminalColors.Paint(t, TerminalColors.Dim);
    string Fit(string t, int n) => TerminalRender.Clip(t, Math.Max(1, n));
    static string Pad(string t, int n) => t.Length >= n ? t : t + new string(' ', n - t.Length);

    // ---------------------------------------------------------------- drawing

    // Redraws this screen where it was: up to its first line, each line cleared and written, anything below erased.
    // caretRow puts the cursor on that line at column caretCol (a text input); null leaves it below the last line.
    void Draw(IReadOnlyList<string> lines, int? caretRow = null, int caretCol = 0)
    {
        var sb = new StringBuilder();
        if (up > 0) sb.Append($"\x1b[{up}A");
        sb.Append('\r');
        foreach (var l in lines) sb.Append(l).Append("\x1b[K\n");
        sb.Append("\x1b[J");
        up = lines.Count;
        if (caretRow is { } row)
        {
            sb.Append($"\x1b[{lines.Count - row}A\r");
            if (caretCol > 0) sb.Append($"\x1b[{caretCol}C");
            up = row;
        }
        w.Write(sb.ToString());
        w.Flush();
    }

    // Erases what the last screen drew, so the next one starts at the same place.
    public void Clear()
    {
        if (!cursor || up == 0) return;
        w.Write($"\x1b[{up}A\r\x1b[J");
        w.Flush();
        up = 0;
    }

    public void Restore()
    {
        if (cursor) w.Write(Show);
        w.Flush();
    }

    // ---------------------------------------------------------------- a list

    static List<Item> Shown(IReadOnlyList<Item> items, PickState st, Func<string, IReadOnlyList<Item>>? deep) =>
        st.Filter.Length == 0 ? items.ToList() : deep is not null ? deep(st.Filter).ToList() : items.Where(i => MenuModel.Match(i.Label + " " + i.Value + " " + i.About, st.Filter)).ToList();

    // One choice from a list. Arrows or j/k move, Enter or Right chooses, typing filters ("/" first for words that
    // start with j, k or q), Esc or Left clears the filter, then goes back (null); q or Ctrl+C quits. With space,
    // Space chooses too and says so (the settings editor toggles with it). deep: the rows to show for a filter, when
    // they are not just the matching rows (the main list searches every task).
    public Picked? Pick(string title, IReadOnlyList<Item> items, PickState st, string hint, string status = "", bool statusBad = false, bool space = false, Func<string, IReadOnlyList<Item>>? deep = null)
    {
        if (!cursor) return PickPlain(title, items, st, status, deep);
        w.Write(Hide);
        while (true)
        {
            var shown = Shown(items, st, deep);
            st.Cursor = shown.Count == 0 ? 0 : Math.Clamp(st.Cursor, 0, shown.Count - 1);
            Draw(ListLines(title, shown, st, hint, status, statusBad, false));
            var k = keys.Read();
            if (IsQuit(k) || (!st.Typing && k.KeyChar == 'q')) throw new QuitMenu();
            void Move(int d) { if (shown.Count > 0) st.Cursor = (st.Cursor + d + shown.Count) % shown.Count; }
            switch (k.Key)
            {
                case ConsoleKey.UpArrow: Move(-1); break;
                case ConsoleKey.DownArrow: Move(1); break;
                case ConsoleKey.PageUp: st.Cursor = Math.Max(0, st.Cursor - 8); break;
                case ConsoleKey.PageDown: st.Cursor = Math.Max(0, Math.Min(shown.Count - 1, st.Cursor + 8)); break;
                case ConsoleKey.Home: st.Cursor = 0; break;
                case ConsoleKey.End: st.Cursor = Math.Max(0, shown.Count - 1); break;
                case ConsoleKey.Enter or ConsoleKey.RightArrow:
                    if (shown.Count > 0) return new Picked(shown[st.Cursor], false);
                    break;
                case ConsoleKey.Escape or ConsoleKey.LeftArrow:
                    if (!st.Typing) return null;
                    st.Filter = ""; st.Filtering = false; st.Cursor = 0;
                    break;
                case ConsoleKey.Backspace:
                    if (st.Filter.Length > 0) st.Filter = st.Filter[..^1];
                    if (st.Filter.Length == 0) st.Filtering = false;
                    st.Cursor = 0;
                    break;
                case ConsoleKey.Spacebar:
                    if (st.Typing) { st.Filter += ' '; st.Cursor = 0; }
                    else if (space && shown.Count > 0) return new Picked(shown[st.Cursor], true);
                    break;
                default:
                    var c = k.KeyChar;
                    if (!st.Typing && c == 'j') Move(1);
                    else if (!st.Typing && c == 'k') Move(-1);
                    else if (!st.Typing && c == '/') st.Filtering = true;
                    else if (c >= ' ' && c != '\x7f') { st.Filter += c; st.Cursor = 0; }
                    break;
            }
        }
    }

    // A tick-list: Space ticks or unticks, Enter keeps the choice, Esc leaves it as it was (null).
    public bool[]? Tick(string title, IReadOnlyList<Item> items, string hint)
    {
        var on = items.Select(i => i.On).ToArray();
        var st = new PickState();
        if (!cursor) return TickPlain(title, items, on);
        w.Write(Hide);
        while (true)
        {
            Draw(ListLines(title, items.Select((x, i) => x with { On = on[i] }).ToList(), st, hint, "", false, true));
            var k = keys.Read();
            if (IsQuit(k) || k.KeyChar == 'q') throw new QuitMenu();
            switch (k.Key)
            {
                case ConsoleKey.UpArrow: st.Cursor = (st.Cursor + items.Count - 1) % items.Count; break;
                case ConsoleKey.DownArrow: st.Cursor = (st.Cursor + 1) % items.Count; break;
                case ConsoleKey.Spacebar: on[st.Cursor] = !on[st.Cursor]; break;
                case ConsoleKey.Enter: return on;
                case ConsoleKey.Escape or ConsoleKey.LeftArrow: return null;
                default:
                    if (k.KeyChar == 'j') st.Cursor = (st.Cursor + 1) % items.Count;
                    else if (k.KeyChar == 'k') st.Cursor = (st.Cursor + items.Count - 1) % items.Count;
                    break;
            }
        }
    }

    List<string> ListLines(string title, IReadOnlyList<Item> shown, PickState st, string hint, string status, bool statusBad, bool ticks)
    {
        var cols = Cols;
        var lines = new List<string> { TerminalColors.BoldText(Fit(title, cols)) };
        lines.Add(st.Typing ? TerminalColors.Paint("Filter: " + Fit(st.Filter, cols - 10) + "_", TerminalColors.Cyan) : Dim(Fit(hint, cols)));
        var visible = Math.Max(3, Math.Min(shown.Count, Rows - 5));
        var top = Math.Clamp(st.Cursor - visible / 2, 0, Math.Max(0, shown.Count - visible));
        var lw = Math.Min(30, shown.Select(i => i.Label.Length).DefaultIfEmpty(0).Max());
        var vw = Math.Min(24, shown.Select(i => i.Value.Length).DefaultIfEmpty(0).Max());
        for (var i = top; i < Math.Min(shown.Count, top + visible); i++)
        {
            var it = shown[i];
            var left = (i == st.Cursor ? "> " : "  ") + (ticks ? (it.On ? "[x] " : "[ ] ") : "") + Pad(Fit(it.Label, lw), lw) + (vw > 0 ? "  " + Pad(Fit(it.Value, vw), vw) : "");
            var about = Fit(it.About, cols - left.Length - 2);
            lines.Add(i == st.Cursor ? TerminalColors.Paint(left, TerminalColors.Cyan + TerminalColors.Bold) + "  " + Dim(about)
                : it.Dim ? Dim(left + "  " + about) : left + "  " + Dim(about));
        }
        if (shown.Count == 0) lines.Add(Dim("  Nothing matches. Esc clears the filter."));
        else if (shown.Count > visible) lines.Add(Dim($"  {top + 1}-{Math.Min(shown.Count, top + visible)} of {shown.Count}"));
        if (status.Length > 0) lines.Add(TerminalColors.Paint(Fit(status, cols), statusBad ? TerminalColors.Yellow : TerminalColors.Green));
        return lines;
    }

    // ---------------------------------------------------------------- one line of text

    // A line with editing: Backspace, Delete, Left, Right, Home, End, typing inserts. Enter returns it, Esc null.
    // header: lines above the prompt (usage lines, what the setting does).
    public string? ReadLine(string prompt, string initial = "", IReadOnlyList<string>? header = null)
    {
        header ??= [];
        if (!cursor) return ReadPlain(prompt, header);
        var text = new StringBuilder(initial);
        var pos = text.Length;
        w.Write(Show);
        while (true)
        {
            var avail = Math.Max(8, Cols - prompt.Length - 1);
            var start = Math.Max(0, pos - avail + 1);
            var shown = text.ToString(start, Math.Min(avail, text.Length - start));
            var lines = header.Take(8).Select(h => Dim(Fit(h, Cols))).Append(prompt + shown).Append(Dim("Enter accepts   Esc cancels   Left, Right, Home, End move")).ToList();
            var row = lines.Count - 2;
            Draw(lines, row, prompt.Length + pos - start);
            var k = keys.Read();
            if (IsQuit(k)) throw new QuitMenu();
            switch (k.Key)
            {
                case ConsoleKey.Enter: return text.ToString();
                case ConsoleKey.Escape: return null;
                case ConsoleKey.LeftArrow: pos = Math.Max(0, pos - 1); break;
                case ConsoleKey.RightArrow: pos = Math.Min(text.Length, pos + 1); break;
                case ConsoleKey.Home: pos = 0; break;
                case ConsoleKey.End: pos = text.Length; break;
                case ConsoleKey.Backspace: if (pos > 0) text.Remove(--pos, 1); break;
                case ConsoleKey.Delete: if (pos < text.Length) text.Remove(pos, 1); break;
                default:
                    if (k.KeyChar >= ' ' && k.KeyChar != '\x7f') text.Insert(pos++, k.KeyChar);
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- confirm and wait

    // Lines, then Enter (true) or Esc and Left (false); q or Ctrl+C quits. Plain: "Press Enter, or type n".
    public bool Confirm(string title, IReadOnlyList<string> lines, string hint)
    {
        if (!cursor)
        {
            w.WriteLine(); w.WriteLine(title);
            foreach (var l in lines) w.WriteLine("  " + l);
            var answer = ReadPlain("Press Enter to continue, or type n to go back: ", []);
            return answer is not null && !answer.Trim().StartsWith('n') && !answer.Trim().StartsWith('N');
        }
        w.Write(Hide);
        var cols = Cols;
        Draw(lines.Select(l => "  " + Fit(l, cols - 2)).Prepend(TerminalColors.BoldText(Fit(title, cols))).Append(Dim(Fit(hint, cols))).ToList());
        while (true)
        {
            var k = keys.Read();
            if (IsQuit(k) || k.KeyChar == 'q') throw new QuitMenu();
            if (k.Key == ConsoleKey.Enter) return true;
            if (k.Key is ConsoleKey.Escape or ConsoleKey.LeftArrow) return false;
        }
    }

    // "Press any key to go back", after a command has written its own output below the menu.
    public void Wait()
    {
        w.WriteLine();
        w.Write("Press any key to go back");
        w.Flush();
        var k = keys.Read();
        w.WriteLine();
        if (IsQuit(k)) throw new QuitMenu();
        up = 0; // what the command printed is not ours to erase
    }

    // ---------------------------------------------------------------- without cursor movement

    // Plain input: echo each key; Backspace erases one; Esc gives null.
    string? ReadPlain(string prompt, IReadOnlyList<string> header)
    {
        foreach (var h in header.Take(8)) w.WriteLine(h);
        w.Write(prompt);
        w.Flush();
        var text = new StringBuilder();
        while (true)
        {
            var k = keys.Read();
            if (IsQuit(k)) throw new QuitMenu();
            if (k.Key == ConsoleKey.Enter) { w.WriteLine(); return text.ToString(); }
            if (k.Key == ConsoleKey.Escape) { w.WriteLine(); return null; }
            if (k.Key == ConsoleKey.Backspace) { if (text.Length > 0) { text.Length--; w.Write("\b \b"); } }
            else if (k.KeyChar >= ' ' && k.KeyChar != '\x7f') { text.Append(k.KeyChar); w.Write(k.KeyChar); }
            w.Flush();
        }
    }

    Picked? PickPlain(string title, IReadOnlyList<Item> items, PickState st, string status, Func<string, IReadOnlyList<Item>>? deep)
    {
        while (true)
        {
            var shown = Shown(items, st, deep);
            w.WriteLine(); w.WriteLine(title);
            for (var i = 0; i < shown.Count; i++) w.WriteLine($"  {i + 1,2}. {shown[i].Label}{(shown[i].Value.Length > 0 ? "  " + shown[i].Value : "")}  {shown[i].About}");
            if (shown.Count == 0) w.WriteLine("  Nothing matches.");
            if (status.Length > 0) w.WriteLine(status);
            var line = ReadPlain("Type a number (Enter goes back, q quits, other words filter): ", [])?.Trim();
            if (string.IsNullOrEmpty(line))
            {
                if (st.Filter.Length == 0) return null;
                st.Filter = "";
            }
            else if (line == "q") throw new QuitMenu();
            else if (int.TryParse(line, out var n) && n >= 1 && n <= shown.Count) return new Picked(shown[n - 1], false);
            else st.Filter = line;
            status = "";
        }
    }

    bool[]? TickPlain(string title, IReadOnlyList<Item> items, bool[] on)
    {
        while (true)
        {
            w.WriteLine(); w.WriteLine(title);
            for (var i = 0; i < items.Count; i++) w.WriteLine($"  {i + 1,2}. [{(on[i] ? "x" : " ")}] {items[i].Label}  {items[i].About}");
            var line = ReadPlain("Type a number to tick or untick it (Enter keeps the choice, Esc leaves it): ", []);
            if (line is null) return null;
            if (line.Trim().Length == 0) return on;
            if (int.TryParse(line.Trim(), out var n) && n >= 1 && n <= items.Count) on[n - 1] = !on[n - 1];
        }
    }
}
