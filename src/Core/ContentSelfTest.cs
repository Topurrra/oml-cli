using System.Text.Json;
using OutSystems.Model;

namespace OmlCli;

static class ContentSelfTest
{
    public static void Run(string blank, string dir, List<string> failures)
    {
        string Write(string name, object value)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, JsonSerializer.Serialize(value));
            return path;
        }
        var source = Path.Combine(dir, "content-source.oml");
        var producerBlank = Path.Combine(dir, "content-producer-blank.oml");
        var services = Model.Services();
        using (var producer = services.CreateESpace(OutSystems.Model.Enumerations.SegmentationKind.CrossDevice, "ContentProducer", "", false, services.NewKey()))
            Model.Save(producer, producerBlank);
        var input = Write("content-source.json", new
        {
            serverActions = new[] { new { name = "ContentAction" } },
            blocks = new[] { new { name = "ContentBlock" } },
            screens = new[] { new { name = "ContentPage" } },
        });
        var (sourceCode, sourceReport) = Apply.Execute(producerBlank, input, source);
        if (sourceCode != 0) { failures.Add("content source: " + JsonSerializer.Serialize(sourceReport)); return; }
        var svg = Path.Combine(dir, "content.svg");
        File.WriteAllText(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"4\" height=\"4\"><rect width=\"4\" height=\"4\"/></svg>");
        var js = Path.Combine(dir, "content.js");
        File.WriteAllText(js, "window.omlContent = true;");
        input = Write("content.json", new
        {
            copy = new[] { new { from = source, elements = new[] { "ContentAction", "MainFlow/ContentPage", "MainFlow/ContentBlock" }, into = "ContentFlow" } },
            images = new[] { new { name = "ContentImage", file = svg } },
            scripts = new[] { new { name = "ContentScript", file = js } },
            resources = new[] { new { name = "ContentFile", file = js, @public = true } },
            themes = new[] { new { name = "ContentTheme", styleSheet = ".content {color:red;}" } },
            locales = new[] { "ka-GE", "en-US" },
            translate = new[] { new { locale = "ka-GE", text = "MandatoryInput", translation = "Required content" } },
            folders = new[] { new { name = "ContentFolder", area = "serverActions", elements = new[] { "ContentAction" } } },
            screens = new[] { new { name = "ContentConsumer", requiredScripts = new[] { "ContentScript" }, widgets = new[] { new { type = "Image", name = "Logo", image = "ContentImage" } } } },
        });
        var output = Path.Combine(dir, "content.oml");
        var (code, report) = Apply.Execute(blank, input, output);
        if (code != 0) { failures.Add("content: " + JsonSerializer.Serialize(report)); return; }
        using var built = Model.Load(output);
        if (!built.ServerActions.Any(a => Model.NameOf(a) == "ContentAction")) failures.Add("clipboard action missing");
        foreach (var path in new[] { "ContentFlow/ContentPage", "ContentFlow/ContentBlock" }) ElementPaths.Resolve(built, path);
        var screen = ElementPaths.Resolve(built, "MainFlow/ContentConsumer");
        var logo = ElementPaths.Resolve(screen, "Logo");
        if (Reflect.Get(logo, "Image") is not { } image || Model.NameOf(image) != "ContentImage") failures.Add("content image binding missing");
        if (!Content.Items(screen, "RequiredScripts").Any(s => Model.NameOf(s) == "ContentScript")) failures.Add("script requirement missing");
        if (!built.Resources.Any(r => Model.NameOf(r) == "ContentFile" && r.Public)) failures.Add("resource missing");
        if (!built.MobileThemes.Any(t => Model.NameOf(t) == "ContentTheme" && t.StyleSheet.Contains("color:red"))) failures.Add("theme missing");
        if (!built.Folders.Any(f => Model.NameOf(f) == "ContentFolder" && Content.Items(f, "Entries").Any(e => Model.NameOf(e) == "ContentAction"))) failures.Add("folder membership missing");
        if (!built.TextResources.Any(t => t.Id == "MandatoryInput" && t.Translations.Any(v => v.Value == "Required content"))) failures.Add("translation missing");
    }
}
