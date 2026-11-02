using System.Net;
using System.Text.Json.Nodes;

namespace OmlCli;

// `oml dif <file.md>`: a Markdown change walkthrough an agent writes for a person (the idea of diffmap, MIT, rebuilt for
// OutSystems), shown on the dashboard's Dif page. The page renders the Markdown and three oml fences: `callstack` (rows
// marked + new, - removed), `review` (oml review of a module and its staged copy, from the model, not the agent) and
// element links [[Orders.oml#MainFlow/Orders/LoadData]] that open describe. Comments stay in the browser and copy as a
// list for the agent. The dashboard only reads the file; relative paths in it are read from the file's folder.
static partial class Dashboard
{
    const string DifUsage = "oml dif <file.md> [--port N] [--no-open]";
    const long MaxDifBytes = 2 * 1024 * 1024;
    static string? difFile;

    // `oml dif <file.md> [--port N] [--no-open]`: the dashboard, opened on the Dif page for that file.
    public static int Dif(string[] args)
    {
        var file = args.FirstOrDefault(a => !a.StartsWith("--") && !int.TryParse(a, out _));
        if (file is null || args.Count(a => !a.StartsWith("--") && !int.TryParse(a, out _)) != 1)
        {
            Console.Error.WriteLine($"Usage: {DifUsage}");
            return 2;
        }
        return Run([.. args.Where(a => a != file), "--dif", file]);
    }

    // Checked when the dashboard starts: an existing Markdown file of at most 2 MB.
    static string? DifProblem(string path) =>
        !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? $"oml dif needs a Markdown (.md) file: {path}"
        : !File.Exists(path) ? $"File not found: {path}"
        : new FileInfo(path).Length > MaxDifBytes ? $"{path} is larger than 2 MB." : null;

    // GET dif: the file oml dif started with. POST dif {path}: another Markdown file the person typed.
    static Reply DifRead(string? typed)
    {
        string path;
        if (typed is null)
        {
            if (difFile is null) return new Reply(200, new JsonObject { ["file"] = null, ["hint"] = "Start it with oml dif <file.md>, or type the path of a Markdown file." });
            path = difFile;
        }
        else path = UserPath(typed, "a Markdown (.md) file");
        if (DifProblem(path) is { } problem) throw new BadInput(problem);
        var info = new FileInfo(path);
        return new Reply(200, new JsonObject
        {
            ["file"] = path, ["folder"] = info.DirectoryName, ["markdown"] = File.ReadAllText(path),
            ["modified"] = info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), ["bytes"] = info.Length,
        });
    }

    // ---- media next to the walkthrough: GET /dif-media?key=&md=<the .md file>&path=<relative path> ----
    const long MaxMediaBytes = 50L * 1024 * 1024;
    static readonly Dictionary<string, string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml", [".mp4"] = "video/mp4", [".webm"] = "video/webm",
    };

    // The file for a relative media path, or the reason it is refused. Only a path below the .md file's folder, with
    // a media extension, no ".." and no reparse point (link or junction) on the way; at most 50 MB.
    internal static (int status, string text) DifMediaFile(string? md, string? rel)
    {
        if (string.IsNullOrWhiteSpace(md) || string.IsNullOrWhiteSpace(rel)) return (400, "Needs md and path.");
        string mdFull;
        try { mdFull = Path.GetFullPath(md); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return (400, "Not a valid walkthrough path."); }
        if (DifProblem(mdFull) is { } problem) return (404, problem);
        // Drive letters and streams (':'), absolute and UNC paths (leading slash), NUL and control characters.
        if (rel.Any(c => c < 32) || rel.Contains(':') || rel.StartsWith('/') || rel.StartsWith('\\')) return (403, "Only a relative path below the walkthrough's folder is served: " + rel);
        var parts = rel.Split('/', '\\').Where(p => p != ".").ToArray();
        if (parts.Length == 0 || parts.Any(p => p.Length == 0 || p.TrimEnd(' ', '.').Length == 0)) return (403, "Not a usable media path: " + rel);
        if (!MediaTypes.ContainsKey(Path.GetExtension(parts[^1]))) return (403, "Not a media file (png, jpg, jpeg, gif, webp, svg, mp4, webm): " + rel);
        var folder = Path.GetDirectoryName(mdFull)!;
        var prefix = folder.EndsWith('\\') ? folder : folder + "\\";
        string full;
        try { full = Path.GetFullPath(Path.Combine(folder, string.Join('\\', parts))); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return (400, "Not a valid media path: " + rel); }
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return (403, "Outside the walkthrough's folder: " + rel);
        var current = folder;
        foreach (var part in full[prefix.Length..].Split('\\'))
        {
            current = Path.Combine(current, part);
            try { if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return (403, "Links and junctions are not followed: " + rel); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (404, "File not found: " + rel); }
        }
        if (!File.Exists(full)) return (404, "File not found: " + rel);
        if (new FileInfo(full).Length > MaxMediaBytes) return (413, "Larger than 50 MB: " + rel);
        return (200, full);
    }

    static async Task SendDifMedia(HttpListenerResponse response, string? md, string? rel, string? range)
    {
        var (status, text) = DifMediaFile(md, rel);
        if (status != 200) { await Text(response, status, text); return; }
        // A script inside an SVG must never run, even if the address is opened on its own.
        response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
        response.ContentType = MediaTypes[Path.GetExtension(text)];
        response.Headers["Accept-Ranges"] = "bytes";
        await using var stream = new FileStream(text, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = stream.Length, from = 0, to = length - 1;
        // One byte range (video seeking): bytes=a-b, bytes=a- or bytes=-n. Anything else gets the whole file.
        if (range is not null && length > 0 && System.Text.RegularExpressions.Regex.Match(range, @"^bytes=(\d{0,15})-(\d{0,15})$") is { Success: true } m
            && (m.Groups[1].Length > 0 || m.Groups[2].Length > 0))
        {
            var (a, b) = (m.Groups[1].Value, m.Groups[2].Value);
            if (a.Length == 0) from = Math.Max(0, length - long.Parse(b));
            else { from = long.Parse(a); if (b.Length > 0) to = Math.Min(to, long.Parse(b)); }
            if (from > to || from >= length)
            {
                response.StatusCode = 416;
                response.Headers["Content-Range"] = $"bytes */{length}";
                return;
            }
            response.StatusCode = 206;
            response.Headers["Content-Range"] = $"bytes {from}-{to}/{length}";
        }
        response.ContentLength64 = Math.Max(0, to - from + 1);
        stream.Seek(from, SeekOrigin.Begin);
        var buffer = new byte[81920];
        for (long left = to - from + 1; left > 0;)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)));
            if (n == 0) break;
            await response.OutputStream.WriteAsync(buffer.AsMemory(0, n));
            left -= n;
        }
    }
}
