using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli.Qa;

// The app's test accounts, one per role, in .oml/test-accounts.local.json of the project (oml create writes it with an
// example and keeps it out of git). A person fills in the usernames and passwords; agents never read the file (the hook
// refuses it). Browser scripts use an account by role: a "login" step, or "$account:<Role>.username" and
// "$account:<Role>.password" in a fill. Passwords are typed by oml and never printed.
//
//   { "accounts": [ { "role": "Manager", "username": "manager.test", "password": "..." } ] }
static class TestAccounts
{
    internal const string FileName = "test-accounts.local.json";

    internal sealed record Account(string Role, string Username, string Password);

    // The file of the project the current folder belongs to (the folder with .oml/, else the current folder).
    // Set by the browser session for one request: the caller's file, since the session runs in another folder.
    internal static string? FileOverride;

    internal static string PathFor(string? start = null)
    {
        if (start is null && FileOverride is { Length: > 0 } given) return given;
        var from = Path.GetFullPath(string.IsNullOrWhiteSpace(start) ? Environment.CurrentDirectory : start);
        return Path.Combine(ProjectConfig.RootOf(from) ?? from, ".oml", FileName);
    }

    // The template oml create writes: one example account with empty values, which no script can use until filled in.
    internal static string Template() =>
        "{\n" +
        "  \"_about\": \"Test accounts of your app, one per role, for oml browser scripts ({\\\"login\\\": \\\"<role>\\\"}). Fill in username and password; agents never read this file. Keep it out of git.\",\n" +
        "  \"accounts\": [\n" +
        "    { \"role\": \"User\", \"username\": \"\", \"password\": \"\" }\n" +
        "  ]\n" +
        "}\n";

    internal static List<Account> Load(string? start, out string? problem)
    {
        problem = null;
        var file = PathFor(start);
        if (!File.Exists(file)) return [];
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var list = new List<Account>();
            foreach (var a in root?["accounts"] as JsonArray ?? [])
            {
                string Text(string key) => a?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
                if (Text("role") is { Length: > 0 } role) list.Add(new Account(role, Text("username"), a?["password"] is JsonValue p && p.TryGetValue<string>(out var pw) ? pw : ""));
            }
            return list;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = $"{file} cannot be read ({e.Message}).";
            return [];
        }
    }

    // One account by role, any case, with both values filled in; else a usage error that names the roles there are.
    internal static Account Find(string role)
    {
        var accounts = Load(null, out var problem);
        if (problem is not null) throw new QaUsage(problem);
        var file = PathFor();
        var hit = accounts.FirstOrDefault(a => a.Role.Equals(role, StringComparison.OrdinalIgnoreCase))
            ?? throw new QaUsage(accounts.Count == 0
                ? $"No test accounts: {file} is missing or empty. Ask the user to add the role {role} there (agents never read or write that file)."
                : $"No test account for the role '{role}'. Roles: {string.Join(", ", accounts.Select(a => a.Role))} ({file}).");
        if (hit.Username.Length == 0 || hit.Password.Length == 0)
            throw new QaUsage($"The test account for {hit.Role} has no {(hit.Username.Length == 0 ? "username" : "password")} yet. Ask the user to fill it in {file}.");
        return hit;
    }

    // `oml browser accounts`: the roles and usernames, never a password.
    internal static int List()
    {
        var accounts = Load(null, out var problem);
        TerminalOutput.PrintObject(new
        {
            file = PathFor(), error = problem,
            accounts = accounts.Select(a => new { role = a.Role, username = a.Username.Length > 0 ? a.Username : null, ready = a.Username.Length > 0 && a.Password.Length > 0 }),
            next = accounts.Count == 0 ? "The user adds accounts to the file (role, username, password); agents never read or write it."
                : "In a browser script: {\"login\": \"<role>\"} on the login page, or fill \"$account:<role>.username\" and \"$account:<role>.password\".",
        });
        return problem is null ? 0 : 1;
    }

    // The roles for the agent's profile; empty when there are none ready.
    internal static List<string> Roles(string? start) => Load(start, out _).Where(a => a.Username.Length > 0 && a.Password.Length > 0).Select(a => a.Role).ToList();
}
