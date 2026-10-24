using System.Globalization;
using System.Text;

namespace OmlCli;

// A small, self-contained parser for the expression language. Its job is to validate expressions cheaply (before an
// apply touches a module, or inside spec checks) and to produce a stable canonical text used by review so whitespace
// and keyword casing do not look like changes.
public static class Expressions
{
    // Error ids keep the parser vocabulary so a message reads like one from the IDE.
    public const string ErrorExpected = "ParserExpectedElement";
    public const string ErrorUnexpected = "ParserUnexpectedElement";
    public const string ErrorUnterminatedString = "ParserUnterminatedStringLiteral";

    public sealed record ExprError(string Id, string Message, int Start, int Length);

    public sealed record Result(bool Ok, string? Text, ExprError? Error)
    {
        public static Result Good(string text) => new(true, text, null);
        public static Result Bad(ExprError error) => new(false, null, error);
    }

    // Canonical text of an expression, or null when it does not parse (the caller then keeps the original text).
    public static string? Canonical(string text)
    {
        var result = TryParse(text);
        return result.Ok ? result.Text : null;
    }

    public static Result TryParse(string text)
    {
        var parser = new Parser(text);
        return parser.Run();
    }

    // ---- lexer ----

    enum TokenKind { End, Ident, String, Int, Decimal, Date, Time, DateTime, Keyword, Op, Open, Close, OpenSquare, CloseSquare, OpenBrace, CloseBrace, Dot, Comma, Colon }

    readonly record struct Token(TokenKind Kind, string Text, int Start, int End)
    {
        public int Length => End - Start;
    }

    static readonly Dictionary<string, string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["or"] = "or", ["and"] = "and", ["like"] = "like", ["not"] = "not", ["true"] = "True", ["false"] = "False",
    };

    sealed class Lexer
    {
        readonly string s;
        int pos;
        public List<Token> Tokens = new();
        public ExprError? Error;

        public Lexer(string text) { s = text; }

        public void Run()
        {
            while (pos < s.Length)
            {
                var c = s[pos];
                if (char.IsWhiteSpace(c)) { pos++; continue; }
                if (c == '"') { LexString(); continue; }
                if (c == '#') { LexDateLiteral(); continue; }
                if (c is '/' && pos + 1 < s.Length && s[pos + 1] == '/') { SkipLineComment(); continue; }
                if (c == '/' && pos + 1 < s.Length && s[pos + 1] == '*') { SkipBlockComment(); continue; }
                if (char.IsDigit(c)) { LexNumber(); continue; }
                if (char.IsLetter(c) || c == '_') { LexIdent(); continue; }
                if (TryOp()) continue;
                if (c == '(') { Punct(TokenKind.Open, "("); continue; }
                if (c == ')') { Punct(TokenKind.Close, ")"); continue; }
                if (c == '[') { Punct(TokenKind.OpenSquare, "["); continue; }
                if (c == ']') { Punct(TokenKind.CloseSquare, "]"); continue; }
                if (c == '{') { Punct(TokenKind.OpenBrace, "{"); continue; }
                if (c == '}') { Punct(TokenKind.CloseBrace, "}"); continue; }
                if (c == '.') { Punct(TokenKind.Dot, "."); continue; }
                if (c == ',') { Punct(TokenKind.Comma, ","); continue; }
                if (c == ':') { Punct(TokenKind.Colon, ":"); continue; }
                Error = new ExprError(ErrorUnexpected, $"unexpected character '{c}'", pos, 1);
                return;
            }
            Tokens.Add(new Token(TokenKind.End, "", s.Length, s.Length));
        }

        void Add(TokenKind kind, string text) => Tokens.Add(new Token(kind, text, pos - text.Length, pos));

        void Punct(TokenKind kind, string ch) { pos++; Add(kind, ch); }

        bool TryOp()
        {
            foreach (var (op, kind) in Ops)
            {
                if (pos + op.Length <= s.Length && string.CompareOrdinal(s, pos, op, 0, op.Length) == 0)
                {
                    pos += op.Length;
                    Tokens.Add(new Token(kind, op, pos - op.Length, pos));
                    return true;
                }
            }
            return false;
        }

        static readonly (string op, TokenKind kind)[] Ops =
        {
            ("<>", TokenKind.Op), ("<=", TokenKind.Op), (">=", TokenKind.Op),
            ("=", TokenKind.Op), ("<", TokenKind.Op), (">", TokenKind.Op),
            ("+", TokenKind.Op), ("-", TokenKind.Op), ("*", TokenKind.Op), ("/", TokenKind.Op),
        };

        void SkipLineComment() { while (pos < s.Length && s[pos] != '\n') pos++; }
        void SkipBlockComment()
        {
            var start = pos;
            pos += 2;
            while (pos + 1 < s.Length && !(s[pos] == '*' && s[pos + 1] == '/')) pos++;
            if (pos + 1 >= s.Length) { Error = new ExprError(ErrorUnexpected, "unterminated block comment", start, s.Length - start); }
            else pos += 2;
        }

        void LexString()
        {
            var start = pos;
            pos++;
            var sb = new StringBuilder();
            while (pos < s.Length)
            {
                var c = s[pos];
                if (c == '"')
                {
                    if (pos + 1 < s.Length && s[pos + 1] == '"') { sb.Append('"'); pos += 2; continue; }
                    pos++;
                    Tokens.Add(new Token(TokenKind.String, sb.ToString(), start, pos));
                    return;
                }
                sb.Append(c);
                pos++;
            }
            Error = new ExprError(ErrorUnterminatedString, "unterminated string literal", start, s.Length - start);
        }

        void LexDateLiteral()
        {
            var start = pos;
            pos++;
            var sb = new StringBuilder();
            while (pos < s.Length && s[pos] != '#')
            {
                if (s[pos] == '\n') break;
                sb.Append(s[pos]);
                pos++;
            }
            if (pos >= s.Length)
            {
                Error = new ExprError(ErrorUnexpected, "unterminated # literal", start, s.Length - start);
                return;
            }
            pos++; // closing '#'
            var inner = sb.ToString();
            var parts = inner.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var kind = parts.Length switch
            {
                0 => TokenKind.Date,
                1 when inner.Contains(':') => TokenKind.Time,
                1 => TokenKind.Date,
                2 => TokenKind.DateTime,
                _ => TokenKind.Date,
            };
            if (!ValidLiteral(inner, kind)) { Error = new ExprError(ErrorUnexpected, $"invalid #{inner}# literal", start, pos - start); return; }
            Tokens.Add(new Token(kind, inner, start, pos));
        }

        static bool ValidLiteral(string inner, TokenKind kind)
        {
            var parts = inner.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;
            var first = parts[0];
            var d = first.Split('-');
            if (kind == TokenKind.Time)
            {
                return d.Length switch { _ => TimeOk(first) };
            }
            if (d.Length != 3 || !int.TryParse(d[0], out var y) || !int.TryParse(d[1], out var mo) || !int.TryParse(d[2], out var day))
                return false;
            if (y < 1 || mo < 1 || mo > 12 || day < 1 || day > 31) return false;
            if (parts.Length == 2) return TimeOk(parts[1]);
            return parts.Length == 1;
        }

        static bool TimeOk(string t)
        {
            var parts = t.Split(':');
            if (parts.Length is < 1 or > 3) return false;
            foreach (var p in parts) if (!int.TryParse(p, out _)) return false;
            return true;
        }

        void LexNumber()
        {
            var start = pos;
            while (pos < s.Length && char.IsDigit(s[pos])) pos++;
            if (pos < s.Length && s[pos] == '.' && pos + 1 < s.Length && char.IsDigit(s[pos + 1]))
            {
                pos++;
                while (pos < s.Length && char.IsDigit(s[pos])) pos++;
                Tokens.Add(new Token(TokenKind.Decimal, s[start..pos], start, pos));
            }
            else Tokens.Add(new Token(TokenKind.Int, s[start..pos], start, pos));
        }

        void LexIdent()
        {
            var start = pos;
            while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_')) pos++;
            var word = s[start..pos];
            if (Keywords.TryGetValue(word, out var kw)) Tokens.Add(new Token(TokenKind.Keyword, kw, start, pos));
            else Tokens.Add(new Token(TokenKind.Ident, word, start, pos));
        }
    }

    // ---- parser (recursive descent, precedence climbing) ----

    sealed class Parser
    {
        readonly string text;
        readonly List<Token> tokens;
        int i;
        ExprError? error;
        readonly StringBuilder output = new();

        public Parser(string source)
        {
            text = source;
            var lexer = new Lexer(source);
            lexer.Run();
            tokens = lexer.Tokens;
            error = lexer.Error;
        }

        public Result Run()
        {
            if (error is not null) return Result.Bad(error);
            Or();
            if (error is null && Peek.Kind != TokenKind.End)
                Fail(ErrorUnexpected, $"unexpected '{Peek.Text}'", Peek.Start, Peek.Length);
            if (error is not null) return Result.Bad(error);
            return Result.Good(output.ToString());
        }

        bool Ok => error is null;

        Token Peek => tokens[i];
        Token Take()
        {
            var t = tokens[i];
            if (t.Kind != TokenKind.End) i++;
            return t;
        }

        // Sets the first error and stops the walk: Fail never throws, so the parser stays exception-free. A batch of
        // bad expressions costs one ExprError per failure, not one exception each.
        void Fail(string id, string message, int start, int length)
        {
            if (error is null) error = new ExprError(id, message, start, length);
        }

        void Expect(TokenKind kind, string what)
        {
            if (Peek.Kind == kind) Take();
            else Fail(ErrorExpected, $"expected {what}", Peek.Start, Peek.Length);
        }

        void Write(string s) => output.Append(s);

        void Or()
        {
            And();
            while (Ok && Peek.Kind == TokenKind.Keyword && Peek.Text == "or")
            {
                Take(); Write(" or ");
                And();
            }
        }

        void And()
        {
            Compare();
            while (Ok && Peek.Kind == TokenKind.Keyword && Peek.Text == "and")
            {
                Take(); Write(" and ");
                Compare();
            }
        }

        void Compare()
        {
            Additive();
            if (!Ok) return;
            if (Peek.Kind == TokenKind.Op || (Peek.Kind == TokenKind.Keyword && Peek.Text == "like"))
            {
                var op = Peek.Text;
                Take();
                Write($" {op} ");
                Additive();
            }
        }

        void Additive()
        {
            Multiplicative();
            while (Ok && Peek.Kind == TokenKind.Op && Peek.Text is "+" or "-")
            {
                var op = Take().Text;
                Write($" {op} ");
                Multiplicative();
            }
        }

        void Multiplicative()
        {
            Unary();
            while (Ok && Peek.Kind == TokenKind.Op && Peek.Text is "*" or "/")
            {
                var op = Take().Text;
                Write($" {op} ");
                Unary();
            }
        }

        void Unary()
        {
            if (!Ok) return;
            if (Peek.Kind == TokenKind.Op && Peek.Text == "-")
            {
                Take(); Write("-");
                Unary();
                return;
            }
            if (Peek.Kind == TokenKind.Keyword && Peek.Text == "not")
            {
                Take(); Write("not ");
                Unary();
                return;
            }
            Postfix();
        }

        void Postfix()
        {
            Primary();
            while (Ok)
            {
                if (Peek.Kind == TokenKind.Open)
                {
                    Take(); Write("(");
                    Arguments();
                    Expect(TokenKind.Close, "')'");
                    Write(")");
                }
                else if (Peek.Kind == TokenKind.OpenSquare)
                {
                    Take(); Write("[");
                    Or();
                    Expect(TokenKind.CloseSquare, "']'");
                    Write("]");
                }
                else if (Peek.Kind == TokenKind.Dot)
                {
                    Take();
                    if (Peek.Kind is not (TokenKind.Ident or TokenKind.Keyword))
                        Fail(ErrorExpected, "expected a name after '.'", Peek.Start, Peek.Length);
                    if (Ok) Write("." + Take().Text);
                }
                else break;
            }
        }

        void Arguments()
        {
            if (!Ok || Peek.Kind == TokenKind.Close) return;
            while (Ok)
            {
                if (Peek.Kind == TokenKind.Ident && i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Colon)
                {
                    Write(Take().Text + ": ");
                    Take(); // colon
                    Or();
                }
                else Or();
                if (Peek.Kind == TokenKind.Comma) { Take(); Write(", "); continue; }
                break;
            }
        }

        void Primary()
        {
            var t = Peek;
            switch (t.Kind)
            {
                case TokenKind.Ident:
                    Take(); Write(t.Text); return;
                case TokenKind.Keyword when t.Text is "True" or "False":
                    Take(); Write(t.Text); return;
                case TokenKind.String:
                    Take(); Write("\"" + t.Text.Replace("\"", "\"\"") + "\""); return;
                case TokenKind.Int:
                case TokenKind.Decimal:
                    Take(); Write(t.Text); return;
                case TokenKind.Date:
                    Take(); Write("#" + t.Text + "#"); return;
                case TokenKind.Time:
                    Take(); Write("#" + t.Text + "#"); return;
                case TokenKind.DateTime:
                    Take(); Write("#" + t.Text + "#"); return;
                case TokenKind.Open:
                    Take(); Write("(");
                    Or();
                    Expect(TokenKind.Close, "')'");
                    Write(")");
                    return;
                case TokenKind.OpenBrace:
                    RecordLiteral();
                    return;
                case TokenKind.OpenSquare:
                    ListLiteral();
                    return;
                default:
                    Fail(ErrorUnexpected, $"unexpected '{t.Text}'", t.Start, t.Length);
                    return;
            }
        }

        void RecordLiteral()
        {
            Take(); Write("{");
            if (Ok && Peek.Kind != TokenKind.CloseBrace)
            {
                while (Ok)
                {
                    if (Peek.Kind == TokenKind.Ident && i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Colon)
                    {
                        Write(Take().Text + ": ");
                        Take(); // colon
                        Or();
                    }
                    else Or();
                    if (Peek.Kind == TokenKind.Comma) { Take(); Write(", "); continue; }
                    break;
                }
            }
            if (Ok) { Expect(TokenKind.CloseBrace, "'}'"); Write("}"); }
        }

        void ListLiteral()
        {
            Take(); Write("[");
            if (Ok && Peek.Kind != TokenKind.CloseSquare)
            {
                while (Ok)
                {
                    Or();
                    if (Peek.Kind == TokenKind.Comma) { Take(); Write(", "); continue; }
                    break;
                }
            }
            if (Ok) { Expect(TokenKind.CloseSquare, "']'"); Write("]"); }
        }
    }
}
