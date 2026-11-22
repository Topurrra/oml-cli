using System.Text.Json.Nodes;

namespace OmlCli.Qa;

// Screenshots of oml browser (page, look, try, sweep and run steps), shrunk to --max-width / --max-height (see ScreenshotSize).
// The OML_SCREENSHOT_MAX_WIDTH and OML_SCREENSHOT_MAX_HEIGHT variables set a default; the option wins, and 0 means no limit.
// Each screenshot in the output has "screenshotSize": { width, height, tokens, scaledFrom } next to the file.
static class QaShot
{
    public readonly record struct Limit(int? Width, int? Height);
    public sealed record Shot(string File, int Width, int Height, int FromWidth, int FromHeight);

    static readonly (string Option, string Variable)[] Limits = [("--max-width", "OML_SCREENSHOT_MAX_WIDTH"), ("--max-height", "OML_SCREENSHOT_MAX_HEIGHT")];

    // The variables as options, so a running session (which has its own environment) gets the caller's limits.
    public static string[] WithVariables(string[] args)
    {
        var added = args.ToList();
        foreach (var (option, variable) in Limits)
            if (!args.Contains(option) && Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
            {
                if (!int.TryParse(value, out var n) || n < 0) throw new QaUsage($"{variable} takes a number of pixels; 0 means no limit.");
                added.AddRange([option, value]);
            }
        return [.. added];
    }

    public static Limit From(QaCommand.Settings o) => new(Read(o, "--max-width"), Read(o, "--max-height"));

    static int? Read(QaCommand.Settings o, string name)
    {
        if (o.Get(name) is not { } text) return null;
        if (!int.TryParse(text, out var n) || n < 0) throw new QaUsage($"{name} takes a number of pixels; 0 means no limit.");
        return n == 0 ? null : n;
    }

    // Takes the screenshot and adds "screenshot" (the file) and "screenshotSize" to the result.
    public static async Task Take(IQaEngine browser, string file, Limit limit, JsonObject result)
    {
        var shot = await browser.Screenshot(file, limit);
        Add(shot, result);
    }

    public static void Add(Shot shot, JsonObject result)
    {
        result["screenshot"] = shot.File;
        result["screenshotSize"] = ScreenshotSize.Report(shot.Width, shot.Height, shot.FromWidth, shot.FromHeight);
    }

    // Writes the browser's PNG, shrunk to the limit, and returns its size and the capture's.
    public static async Task<Shot> Write(string file, byte[] png, Limit limit)
    {
        var (fromWidth, fromHeight) = ScreenshotSize.PngSize(png);
        png = ScreenshotSize.ShrinkPng(png, limit.Width, limit.Height);
        var full = Path.GetFullPath(file);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, png);
        var (width, height) = ScreenshotSize.PngSize(png);
        return new Shot(full, width, height, fromWidth, fromHeight);
    }
}
