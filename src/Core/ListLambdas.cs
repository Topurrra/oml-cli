using System.Text;
using System.Text.RegularExpressions;
using OutSystems.Model;

namespace OmlCli;

// The Condition of ListFilter, ListIndexOf, ListAny and ListAll, and the By of ListSort, is evaluated once per element of
// the list argument: in Service Studio's model the parameter takes a lambda (its LambdaParameterType is set), and the
// argument's scope holds the element's attributes. So Studio writes `ThreadId = Id` (an attribute of the element),
// `Employee.Name` (a record list) or `Value` (a list of a basic type), as the OutSystems docs show (ListIndexOf(UserList,
// Name = "James")). `Mails.Current.ThreadId` there is valid but means the outer list's current row: the generated
// JavaScript reads `getCurrent(callContext.iterationContext)` and the action picks the wrong rows (kanzo oml#257).
// Apply writes the lambda the way Studio does: `<list>.Current.` written with the list argument's own expression is
// dropped, and a bare `<list>.Current` becomes `Value`. Another list's Current is a real outer reference and stays.
static class ListLambdas
{
    // The parameters of a list action's list argument.
    static readonly string[] ListParameters = ["SourceList", "List"];

    internal static bool IsLambda(object argument) =>
        Reflect.Get(argument, "Parameter") is { } parameter && Reflect.Get(parameter, "LambdaParameterType") is not null;

    // The list argument's expression on this node, once it is set; null when the node has none.
    internal static string? ListText(IEnumerable<object> arguments) =>
        arguments.FirstOrDefault(a => !IsLambda(a) && Reflect.Get(a, "Parameter") is { } p
                && ListParameters.Contains(Model.NameOf(p), StringComparer.OrdinalIgnoreCase)) is IArgument { Value.Text: { Length: > 0 } text }
            ? text.Trim() : null;

    // The lambda form of a condition or sort key written against `<list>.Current`. String literals stay as written.
    internal static string Rewrite(string expression, string? list)
    {
        if (string.IsNullOrWhiteSpace(list) || !expression.Contains("Current", StringComparison.OrdinalIgnoreCase)) return expression;
        // `GetMails.List` also matches `GetMails . List`; not part of a longer name (`OtherMails`, `X.Mails`).
        var name = string.Join(@"\s*\.\s*", list.Split('.').Select(part => Regex.Escape(part.Trim())));
        var current = new Regex(@"(?<![\w.])" + name + @"\s*\.\s*Current\b(?<attribute>\s*\.\s*)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var result = new StringBuilder();
        var start = 0;
        for (var i = 0; i <= expression.Length; i++)
        {
            if (i < expression.Length && expression[i] != '"') continue;
            result.Append(current.Replace(expression[start..i], m => m.Groups["attribute"].Success ? "" : "Value"));
            if (i == expression.Length) break;
            // A text literal: "" inside it is a quote.
            var end = i + 1;
            while (end < expression.Length && !(expression[end] == '"' && (end + 1 >= expression.Length || expression[end + 1] != '"')))
                end += expression[end] == '"' ? 2 : 1;
            end = Math.Min(end + 1, expression.Length);
            result.Append(expression, i, end - i);
            start = end;
            i = end - 1;
        }
        return result.ToString();
    }
}
