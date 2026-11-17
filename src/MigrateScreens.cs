// STUB: replaced by the translator agent's file
using System.Text.Json.Nodes;

namespace OmlCli;

static class ScreenTranslator
{
    internal sealed record Context(
        string OldModule,
        IReadOnlyDictionary<string, string> Wrappers,
        IReadOnlySet<string> ClientVariables,
        IReadOnlySet<string> LocalBlocks,
        string? Layout,
        IReadOnlyList<string> LayoutPlaceholders);

    internal sealed record Result(
        JsonObject? Spec,
        string Kind,
        string Status,
        List<string> Todo,
        List<string> EntityActions,
        List<string> SessionVariables);

    internal static Result Translate(JsonObject element, Context context) =>
        new(null, element["kind"]?.GetValue<string>() ?? "Screen", "worklist", ["translator not built in this build"], [], []);

    internal static List<string> SelfTest() => [];
}
