using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// `oml library`: a local copy of the public OutSystems 11 docs, for `oml doc --search "<words>" --library`. Sources are the
// GitHub repositories OutSystems/docs-product, docs-support and docs-howtos. Only the Markdown under src/ and the LICENSE
// are copied, unchanged. The docs are CC BY-NC-ND 4.0 (c) OutSystems: oml never ships them; each user downloads them for
// their own use. The copy lives in %LOCALAPPDATA%\oml-cli\library (OML_LIBRARY overrides it, for tests).
static class Library
{
    const string Usage = "Usage: oml library [status]\n       oml library fetch [--source product|support|howtos]...\n       oml library fetch --source <name> --from <folder of an unzipped repository>";
    internal static readonly (string Name, string Repo)[] Sources = [("product", "docs-product"), ("support", "docs-support"), ("howtos", "docs-howtos")];
    const string Owner = "OutSystems";
    const int Parallel = 8;
    const long MaxFileBytes = 2 * 1024 * 1024;
    const string LicenseNote = "OutSystems documentation, CC BY-NC-ND 4.0 (c) OutSystems. Downloaded for your own use; do not share or change it.";

    public static string Root => Environment.GetEnvironmentVariable("OML_LIBRARY") is { Length: > 0 } root
        ? Path.GetFullPath(root)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "oml-cli", "library");

    // The folders of the sources that were fetched (each has a source.json).
    public static List<string> Folders() =>
        Sources.Select(s => Path.Combine(Root, s.Repo)).Where(d => File.Exists(Path.Combine(d, "source.json"))).ToList();

    static int Fail(string message, int code = 2)
    {
        TerminalOutput.PrintObject(new { error = message });
        return code;
    }

    public static int Run(string[] args)
    {
        if (args is [] or ["status"]) return Status();
        if (args[0] != "fetch") return Fail($"Unknown library command '{args[0]}'. {Usage}");
        var names = new List<string>();
        string? from = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--source" && i + 1 < args.Length) names.Add(args[++i]);
            else if (args[i] == "--from" && i + 1 < args.Length) from = args[++i];
            else return Fail($"Unknown option {args[i]}. {Usage}");
        }
        var unknown = names.FirstOrDefault(n => !Sources.Any(s => s.Name == n));
        if (unknown is not null) return Fail($"Unknown source '{unknown}'. Sources: {string.Join(", ", Sources.Select(s => s.Name))}.");
        var chosen = names.Count == 0 ? Sources : Sources.Where(s => names.Contains(s.Name)).ToArray();
        if (from is not null)
        {
            if (chosen.Length != 1) return Fail("--from needs exactly one --source: the repository that the folder holds.");
            if (!Directory.Exists(Path.Combine(from, "src"))) return Fail($"No src folder in {from}. Give the top folder of the unzipped repository.");
        }

        Directory.CreateDirectory(Root);
        var results = new JsonArray();
        var failed = false;
        foreach (var (name, repo) in chosen)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var temp = Path.Combine(Root, $"{repo}.tmp-{Guid.NewGuid():N}");
            try
            {
                var (commit, files, bytes) = from is null ? Download(repo, temp) : Copy(from, temp);
                var info = new JsonObject
                {
                    ["source"] = name, ["repo"] = $"{Owner}/{repo}", ["commit"] = commit, ["fetchedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ["files"] = files, ["kb"] = bytes / 1024, ["license"] = LicenseNote,
                };
                File.WriteAllText(Path.Combine(temp, "source.json"), JsonOutput.Write(info));
                // Replace the old copy only now, so a failed fetch keeps the last good one.
                var target = Path.Combine(Root, repo);
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.Move(temp, target);
                LibraryIndex.Delete();
                info["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 1);
                results.Add(info);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                failed = true;
                results.Add(new JsonObject { ["source"] = name, ["repo"] = $"{Owner}/{repo}", ["error"] = e.Message });
            }
            finally { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
        }

        var output = new JsonObject { ["library"] = Root, ["sources"] = results, ["license"] = LicenseNote, ["next"] = "oml doc --search \"<words>\" --library" };
        // Build the saved search index now, so the first search is fast too.
        if (Folders() is { Count: > 0 } fetched) output["indexed"] = DocSearch.LibraryIndex(fetched, []).Count;
        if (failed)
            output["hint"] = "No access to GitHub? On a machine with access, download the repository as a ZIP (github.com/OutSystems/<repo>: Code > Download ZIP), unzip it here, then run: oml library fetch --source <name> --from <unzipped folder>";
        TerminalOutput.PrintNode(output);
        return failed ? 2 : 0;
    }

    static int Status()
    {
        var sources = new JsonArray();
        foreach (var folder in Folders())
            sources.Add(JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "source.json"))));
        var output = new JsonObject { ["library"] = Root, ["sources"] = sources };
        if (sources.Count == 0) output["hint"] = "No library yet. Run: oml library fetch";
        TerminalOutput.PrintNode(output);
        return 0;
    }

    // The Markdown under src/ of the newest commit of the default branch: 2 GitHub API calls (the commit and its file
    // tree), then each file from raw.githubusercontent.com at that commit. The system proxy settings apply.
    static (string commit, int files, long bytes) Download(string repo, string temp)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("oml-cli", "1"));
        var api = $"https://api.github.com/repos/{Owner}/{repo}";
        var commit = Get(http, $"{api}/commits/HEAD", "application/vnd.github.sha").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(commit, "^[0-9a-f]{40}$")) throw new InvalidDataException($"GitHub gave no commit id for {repo}.");
        var tree = JsonNode.Parse(Get(http, $"{api}/git/trees/{commit}?recursive=1", "application/vnd.github+json"))!;
        if (tree["truncated"]?.GetValue<bool>() == true) throw new InvalidDataException($"GitHub cut the file list of {repo}; the library cannot be complete.");
        var paths = tree["tree"]!.AsArray()
            .Where(e => e!["type"]?.GetValue<string>() == "blob" && (e["size"]?.GetValue<long>() ?? 0) <= MaxFileBytes)
            .Select(e => e!["path"]!.GetValue<string>())
            .Where(p => p.StartsWith("src/", StringComparison.Ordinal) && p.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && Safe(p))
            .Append("LICENSE").ToList();
        long bytes = 0;
        using var bar = new ProgressBar(repo, paths.Count);
        System.Threading.Tasks.Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = Parallel }, path =>
        {
            var url = $"https://raw.githubusercontent.com/{Owner}/{repo}/{commit}/{Uri.EscapeDataString(path).Replace("%2F", "/")}";
            var data = GetBytes(http, url);
            var file = Path.Combine(temp, path == "LICENSE" ? "LICENSE.txt" : path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, data);
            bar.Step(Interlocked.Add(ref bytes, data.Length));
        });
        return (commit, paths.Count - 1, bytes);
    }

    // A bar on stderr while files download, only when a person watches a console: an agent's redirected output stays
    // clean JSON. It redraws at most 10 times a second; Dispose prints the last state and a line end.
    sealed class ProgressBar(string label, int total) : IDisposable
    {
        const int Width = 24;
        readonly bool shown = !Console.IsErrorRedirected;
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        readonly object gate = new();
        int done;
        long bytes, last = -1000;

        public void Step(long totalBytes)
        {
            if (!shown) return;
            lock (gate)
            {
                done++;
                bytes = Math.Max(bytes, totalBytes);
                if (done < total && clock.ElapsedMilliseconds - last < 100) return;
                last = clock.ElapsedMilliseconds;
                Draw();
            }
        }

        void Draw()
        {
            var filled = total == 0 ? Width : done * Width / total;
            Console.Error.Write($"\r{label,-13} {new string('\u2588', filled)}{new string('\u2591', Width - filled)} {done,5}/{total,-5} {bytes / 1048576.0,5:0.0} MB {clock.Elapsed.TotalSeconds,4:0} s ");
        }

        public void Dispose()
        {
            if (!shown) return;
            lock (gate) { Draw(); Console.Error.WriteLine(); }
        }
    }

    // An unzipped repository from GitHub's "Download ZIP", for machines without access to GitHub.
    static (string commit, int files, long bytes) Copy(string from, string temp)
    {
        var root = Path.GetFullPath(from);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System };
        int files = 0;
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.md", options))
        {
            var info = new FileInfo(file);
            if (info.Length > MaxFileBytes) continue;
            var target = Path.Combine(temp, Path.GetRelativePath(root, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            files++;
            bytes += info.Length;
        }
        if (files == 0) throw new InvalidDataException($"No .md files under {Path.Combine(root, "src")}.");
        var license = Path.Combine(root, "LICENSE");
        if (File.Exists(license)) File.Copy(license, Path.Combine(temp, "LICENSE.txt"));
        return ("local copy", files, bytes);
    }

    // A tree path is written under the temp folder: no parent steps, no drive or stream marks, no empty parts.
    internal static bool Safe(string path) =>
        !path.Contains(':') && !path.Contains('\\') && path.Split('/').All(p => p.Length > 0 && p != "." && p != "..");

    static string Get(HttpClient http, string url, string accept)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd(accept);
        using var response = http.Send(request);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase} for {url}");
        return new StreamReader(response.Content.ReadAsStream()).ReadToEnd();
    }

    // One file; a failed try is repeated twice (raw.githubusercontent.com sometimes drops a connection).
    static byte[] GetBytes(HttpClient http, string url)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = http.Send(new HttpRequestMessage(HttpMethod.Get, url));
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} for {url}");
                using var stream = new MemoryStream();
                response.Content.ReadAsStream().CopyTo(stream);
                return stream.ToArray();
            }
            catch (Exception e) when (attempt < 3 && e is HttpRequestException or TaskCanceledException or IOException) { Thread.Sleep(500 * attempt); }
        }
    }
}
