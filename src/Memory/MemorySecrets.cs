using System.Text;
using System.Text.RegularExpressions;

namespace OmlCli;

// The secret check of oml memory: a record that holds a secret is refused at write, and a secret that reached the file
// anyway is masked at recall. A C# port of voli's disclosure firewall (crate stela, firewall.rs: the structured token
// patterns, Luhn-checked card numbers and structural SSNs), with the same JWT pattern, plus the forms of oml's world:
// the LifeTime bearer token and the value of OML_LIFETIME_TOKEN. voli's crypto-wallet patterns are left out: a long
// CamelCase element name can look like a wallet address, and an OutSystems project has no use for them.
// Deterministic, no model, no network. A record is at most 500 bytes and every pattern is simple, so the ordinary regular
// expression engine is fast; each match has a 2 second limit, and a text that hits it counts as a secret (fail closed).
// (The NonBacktracking engine was tried: building 17 of them added 70 ms to every command.)
static class MemorySecrets
{
    static readonly (string Kind, string Pattern)[] Patterns =
    [
        ("aws access key", @"\b(AKIA|ASIA)[A-Z0-9]{16}\b"),
        ("github token", @"\bgh[opusr]_[A-Za-z0-9]{36,}"),
        ("github fine-grained token", @"\bgithub_pat_[A-Za-z0-9_]{30,}"),
        ("slack token", @"\bxox[abprs]-[A-Za-z0-9-]{10,}"),
        ("slack webhook", @"https://hooks\.slack\.com/services/T[A-Za-z0-9]+/B[A-Za-z0-9]+/[A-Za-z0-9]+"),
        ("stripe key", @"\b(sk|rk)_(live|test)_[A-Za-z0-9]{24,}"),
        ("JWT", @"\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b"),
        ("private key", @"-----BEGIN ([A-Z]+ )*PRIVATE KEY( BLOCK)?-----"),
        ("database url with password", @"\b(postgres|postgresql|mysql|mongodb|mongodb\+srv|redis|amqp|sqlserver)://[^\s:/]+:[^\s@]+@[^\s/]+"),
        ("ethereum private key", @"\b0x[a-fA-F0-9]{64}\b"),
        ("anthropic key", @"\bsk-ant-[A-Za-z0-9_\-]{20,}"),
        ("openai key", @"\bsk-(?:proj-)?[A-Za-z0-9_\-]{20,}"),
        ("npm token", @"\bnpm_[A-Za-z0-9]{36,}"),
        // oml's own world: the LifeTime API v2 takes a bearer token, and oml reads it from OML_LIFETIME_TOKEN.
        ("LifeTime token", @"\bOML_LIFETIME_TOKEN\s*[=:]\s*[""']?[^\s""']{4,}"),
        ("bearer token", @"\bBearer\s+[A-Za-z0-9._~+/=-]{16,}"),
        // name = value, where the name says it is a secret. The value has 6 or more characters, none of them a space, a quote or ';'.
        // A password may be any such value ("Password: hunter2", "P@ssw0rd!"); the other names need a digit in the value ("token: expired" is a sentence).
        ("password value", @"(?<![A-Za-z0-9])(?:password|passwd|pwd)[""']?\s*[:=]\s*[""']?[^\s""';]{6,}"),
        // "the password is Sup3rS3cret!": the value must hold a digit or a symbol ("the password is stored in the vault" is a sentence).
        ("password phrase", @"(?<![A-Za-z0-9])(?:password|passwd|pwd)\s+is\s+[""']?[^\s""';]{6,}"),
        ("secret value", @"(?<![A-Za-z0-9])(?:secret|token|api[_-]?key|account[_-]?key|access[_-]?key|client[_-]?secret|connection[_ -]?string|aws_secret_access_key)[""']?\s*[:=]\s*[""']?[^\s""';]{6,}"),
    ];

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    static readonly (string Kind, Regex Rx)[] Compiled = Patterns.Select(p => (p.Kind, new Regex(p.Pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, Timeout))).ToArray();
    // A long key-like run (base64 or hex): 32 or more letters and digits, with a digit in at least a tenth of them. Element
    // names (few digits), GUIDs with dashes and paths (their parts are short) do not match.
    static readonly Regex LongToken = new(@"(?<![A-Za-z0-9+/=_])[A-Za-z0-9+/=_]{32,}(?![A-Za-z0-9+/=_])", RegexOptions.CultureInvariant, Timeout);
    static readonly Regex Card = new(@"\b(?:\d[ -]?){11,18}\d\b", RegexOptions.CultureInvariant, Timeout);
    static readonly Regex Ssn = new(@"\b(\d{3})-(\d{2})-(\d{4})\b", RegexOptions.CultureInvariant, Timeout);

    sealed record Finding(string Kind, int Start, int End);

    // The value of OML_LIFETIME_TOKEN, when it is set and long enough to be a token (a short value would match words).
    static string? EnvToken => Environment.GetEnvironmentVariable("OML_LIFETIME_TOKEN") is { Length: >= 8 } t ? t : null;

    static List<Finding> Scan(string text)
    {
        try { return ScanText(text); }
        catch (RegexMatchTimeoutException) { return [new Finding("unscannable text", 0, text.Length)]; }
    }

    static List<Finding> ScanText(string text)
    {
        var found = new List<Finding>();
        foreach (var (kind, rx) in Compiled)
            foreach (Match m in rx.Matches(text))
                if (ValueLooksSecret(kind, m.Value)) found.Add(new Finding(kind, m.Index, m.Index + m.Length));
        foreach (Match m in LongToken.Matches(text))
        {
            var digits = m.Value.Count(char.IsAsciiDigit);
            if (digits > 0 && m.Value.Any(char.IsAsciiLetter) && digits * 10 >= m.Value.Length) found.Add(new Finding("long token", m.Index, m.Index + m.Length));
        }
        foreach (Match m in Card.Matches(text))
            if (Luhn(new string(m.Value.Where(char.IsAsciiDigit).ToArray()))) found.Add(new Finding("card number", m.Index, m.Index + m.Length));
        foreach (Match m in Ssn.Matches(text))
            if (ValidSsn(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)) found.Add(new Finding("social security number", m.Index, m.Index + m.Length));
        if (EnvToken is { } token)
            for (var at = text.IndexOf(token, StringComparison.Ordinal); at >= 0; at = text.IndexOf(token, at + token.Length, StringComparison.Ordinal))
                found.Add(new Finding("LifeTime token", at, at + token.Length));
        return found;
    }

    // The name-value patterns match any value of 6 characters; this keeps the ones that look like a secret.
    static bool ValueLooksSecret(string kind, string match)
    {
        if (kind is not ("secret value" or "password phrase")) return true;
        var value = kind == "secret value" ? match[(match.IndexOfAny([':', '=']) + 1)..] : match[(match.LastIndexOf(' ') + 1)..];
        return kind == "secret value" ? value.Any(char.IsAsciiDigit) : value.Any(c => !char.IsAsciiLetter(c) && c is not ('"' or '\''));
    }

    // The kind of the first secret in the text (never the value), or null.
    public static string? Find(string text) => Scan(text).OrderBy(f => f.Start).FirstOrDefault()?.Kind;

    // The text with every secret replaced by a short mask. Back to front, so the offsets stay valid; overlaps are skipped.
    public static string Mask(string text)
    {
        var found = Scan(text).OrderBy(f => f.Start).ThenByDescending(f => f.End).ToList();
        if (found.Count == 0) return text;
        var sb = new StringBuilder(text);
        var lastStart = text.Length;
        for (var i = found.Count - 1; i >= 0; i--)
        {
            var f = found[i];
            if (f.End > lastStart || f.Start >= f.End) continue;
            sb.Remove(f.Start, f.End - f.Start).Insert(f.Start, MaskOf(f.Kind, text[f.Start..f.End]));
            lastStart = f.Start;
        }
        return sb.ToString();
    }

    static string MaskOf(string kind, string raw)
    {
        if (kind == "card number")
        {
            var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
            return "**** **** **** " + digits[^4..];
        }
        if (kind is "private key") return "[PRIVATE KEY]";
        // A name and its value ("Password: hunter2"): a prefix and a tail would show half of a short value.
        if (kind is "unscannable text" or "password value" or "password phrase" or "secret value") return "[REDACTED]";
        if (raw.Length <= 8) return "[REDACTED]";
        return raw[..4] + "***" + raw[^4..];
    }

    static bool Luhn(string digits)
    {
        if (digits.Length is < 12 or > 19) return false;
        var sum = 0;
        var doubled = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubled) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
            doubled = !doubled;
        }
        return sum % 10 == 0;
    }

    // Structural rules of the SSA: no area 000, 666 or 9xx, no group 00, no serial 0000.
    static bool ValidSsn(string area, string group, string serial) =>
        !(area == "000" || area == "666" || area.StartsWith('9') || group == "00" || serial == "0000");
}
