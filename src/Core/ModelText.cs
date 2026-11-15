using OutSystems.Model;

namespace OmlCli;

// The text of a model property read by reflection: an expression's text, a plain string as it is, anything else empty.
static class ModelText
{
    internal static string Of(object? o) => o is IExpression e ? e.Text : o as string ?? "";
}
