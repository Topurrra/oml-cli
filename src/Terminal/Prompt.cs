namespace OmlCli;

// Questions for a person at a terminal, for commands run without an argument they need (oml create, hooks install,
// config set, team-rules add, studio open). Never for an agent, a script or CI: those get the usage as before. A
// person is: stdin and stdout not redirected, OML_OUTPUT not json, OML_MENU not off, and no agent session variable.
// OML_ASK=1 asks with redirected input too, so the tests can answer through a pipe.
internal static class Prompt
{
    static readonly string[] AgentVariables = ["OML_SESSION", "CLAUDE_SESSION_ID", "CLAUDE_CODE_SESSION_ID", "CODEX_SESSION_ID", "CLAUDECODE", "CI", "GITHUB_ACTIONS"];

    internal static bool Person()
    {
        if (Environment.GetEnvironmentVariable("OML_ASK") == "1") return true;
        if (Console.IsInputRedirected || Console.IsOutputRedirected) return false;
        if (string.Equals(Environment.GetEnvironmentVariable("OML_OUTPUT")?.Trim(), "json", StringComparison.OrdinalIgnoreCase)) return false;
        if (Environment.GetEnvironmentVariable("OML_MENU")?.Trim().ToLowerInvariant() is "0" or "off" or "false" or "no") return false;
        return !AgentVariables.Any(v => Environment.GetEnvironmentVariable(v) is { Length: > 0 });
    }

    // One line of text; Enter keeps `keep`. Null when the input ended (the person stopped).
    internal static string? Ask(string question, string keep = "")
    {
        Console.Write(TerminalColors.Paint("? ", TerminalColors.Cyan) + TerminalColors.BoldText(question)
            + (keep.Length > 0 ? TerminalColors.Paint($" [{keep}]", TerminalColors.Dim) : "") + " ");
        var line = Console.ReadLine();
        return line is null ? null : line.Trim().Length == 0 ? keep : line.Trim().Trim('"');
    }

    // One of `options` by number or by its text, any case; null when the input ended.
    internal static string? Choose(string question, IReadOnlyList<(string Value, string About)> options, string? keep = null)
    {
        for (var i = 0; i < options.Count; i++)
            Console.WriteLine($"  {TerminalColors.Paint((i + 1).ToString(), TerminalColors.Cyan)} {options[i].Value}{(options[i].About.Length > 0 ? TerminalColors.Paint("  " + options[i].About, TerminalColors.Dim) : "")}");
        while (true)
        {
            var answer = Ask(question, keep ?? "");
            if (answer is null) return null;
            if (int.TryParse(answer, out var n) && n >= 1 && n <= options.Count) return options[n - 1].Value;
            if (options.FirstOrDefault(o => o.Value.Equals(answer, StringComparison.OrdinalIgnoreCase)) is { Value: not null } hit) return hit.Value;
            Console.WriteLine(TerminalColors.Paint($"  Type a number from 1 to {options.Count}.", TerminalColors.Yellow));
        }
    }

    // The command line that stands for the answers, so the person learns it.
    internal static void Same(string command) =>
        Console.WriteLine(TerminalColors.Paint("  The same as: ", TerminalColors.Dim) + command);
}
