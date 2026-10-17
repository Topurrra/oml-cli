using System.Text.Json;
using OutSystems.Model.Applications;

namespace OmlCli;

static class PackageSelfTest
{
    static string Census(OutSystems.Model.IESpace module)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(Inspect.Census(module)));
        return string.Join(";", doc.RootElement.GetProperty("byType").EnumerateObject().OrderBy(p => p.Name).Select(p => p.Name + "=" + p.Value.GetInt32()));
    }
    internal static void Run(string blank, string dir, List<string> failures)
    {
        var package = Path.Combine(dir, "selftest.oap");
        Packages.Pack(package, "PackageSelfTest", new[] { blank });
        using (var listed = JsonDocument.Parse(JsonSerializer.Serialize(Packages.List(package))))
            if (listed.RootElement.GetProperty("applications")[0].GetProperty("modules").GetArrayLength() != 1)
                failures.Add("package list lost its module");
        var extracted = Path.Combine(dir, "extracted");
        Packages.Extract(package, extracted);
        using (var source = Model.Load(blank))
        using (var target = Model.Load(Directory.GetFiles(extracted, "*.oml").Single()))
            if (Census(source) != Census(target) || Model.Errors(target).Length != Model.Errors(source).Length)
                failures.Add("package extract changed census or validation");
        try { Packages.Extract(package, extracted); failures.Add("package extraction overwrote an existing file"); }
        catch (ModelError) { }

        var services = Model.Services();
        var solutionPath = Path.Combine(dir, "solution.osp");
        using (var app = (IApplication)Reflect.Call(services, "LoadApplication", File.ReadAllBytes(package), null, null)!)
        using (var loose = (OutSystems.Model.Extensions.IExtension)Reflect.Call(services, "CreateExtension", "LooseExtension", "", services.NewKey())!)
        using (var solution = (ISolution)Reflect.Call(services, "CreateSolution", "PackageSolution", services.NewKey(), "", "", "")!) {
            Reflect.Call(solution, "Add", app);
            Reflect.Call(solution, "Add", loose);
            Reflect.Call(solution, "Save", solutionPath);
        }
        Packages.Extract(solutionPath, Path.Combine(dir, "solution-extract"));
        using (var listed = JsonDocument.Parse(JsonSerializer.Serialize(Packages.List(solutionPath))))
            if (listed.RootElement.GetProperty("applications").GetArrayLength() != 1 || listed.RootElement.GetProperty("modules").GetArrayLength() != 2)
                failures.Add("solution list lost an application or module");

        var spec = Path.Combine(dir, "module-settings.json");
        var output = Path.Combine(dir, "module-settings.oml");
        File.WriteAllText(spec, """{"module":{"description":"Package self-test"}}""");
        var (code, report) = Apply.Execute(blank, spec, output);
        if (code != 0) failures.Add("module settings: " + JsonSerializer.Serialize(report));
        else using (var module = Model.Load(output))
            if (((IModule)module).Description != "Package self-test") failures.Add("module description did not persist");
    }
}
