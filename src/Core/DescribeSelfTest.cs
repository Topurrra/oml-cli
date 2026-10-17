using OutSystems.Model;
using System.Text.Json;

namespace OmlCli;

static class DescribeSelfTest
{
    public static void Check(IESpace module, List<string> failures)
    {
        var result = JsonSerializer.SerializeToElement(Describe.Run(module, "FetchOrder", 0, []));
        var text = result.GetProperty("text").GetString()!;
        foreach (var line in new[] { "query GetOrders:", "if GetOrders.List.Empty:", "else:", "for each GetOrders.List:", "call CreateOrUpdateOrder(Source = Rec)  [entity action]", "raise SelfTestError(\"none\")" })
            if (!text.Contains(line, StringComparison.Ordinal)) failures.Add("describe missing: " + line);
        var screen = JsonSerializer.SerializeToElement(Describe.Run(module, "MainFlow/Orders", 1, []));
        if (!screen.GetProperty("text").GetString()!.Contains("OnInitialize -> Init")) failures.Add("describe missing screen lifecycle");
    }
}

