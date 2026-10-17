namespace OmlCli;

static class AssetFiles
{
    internal sealed record Options(List<string> Paths, Dictionary<string, string> Values, HashSet<string> Flags)
    {
        public string? Get(string key) => Values.GetValueOrDefault(key);
        public bool Has(string key) => Flags.Contains(key);
        public int Number(string key, int fallback, int min, int max)
        {
            if (Get(key) is not { } text) return fallback;
            if (!int.TryParse(text, out var n) || n < min || n > max)
                throw new ArgumentException($"{key} must be between {min} and {max}.");
            return n;
        }
    }

    internal static Options Parse(string[] args, string[] values, params string[] flags)
    {
        var result = new Options([], new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (flags.Contains(arg)) { if (!result.Flags.Add(arg)) throw new ArgumentException($"Repeated {arg}."); }
            else if (values.Contains(arg))
            {
                if (++i >= args.Length || args[i].StartsWith("--")) throw new ArgumentException($"{arg} needs a value.");
                if (!result.Values.TryAdd(arg, args[i])) throw new ArgumentException($"Repeated {arg}.");
            }
            else if (arg.StartsWith("--")) throw new ArgumentException($"Unknown option {arg}.");
            else result.Paths.Add(arg);
        }
        return result;
    }

    internal static string Output(string input, string? output, string extension) => Path.GetFullPath(output ??
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, Path.GetFileNameWithoutExtension(input) + ".optimized." + extension));

    internal static void CheckOutput(string output, IEnumerable<string> inputs, bool force)
    {
        var resolvedOutput = ResolvePath(output);
        if (inputs.Any(p => string.Equals(ResolvePath(p), resolvedOutput, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Never overwrite an input; choose a separate output path.");
        // A hard link or symlink can alias an input even when its path differs. Never truncate existing files.
        if (File.Exists(output) && !force) throw new IOException($"Output exists: {output}. Use --force to replace it.");
        if (File.Exists(output) && File.GetAttributes(output).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Output is a link; choose a regular output file.");
    }

    static string ResolvePath(string path)
    {
        var full = Path.GetFullPath(path); var current = Path.GetPathRoot(full)!;
        foreach (var component in full[current.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, component);
            FileSystemInfo? info = Directory.Exists(current) ? new DirectoryInfo(current) : File.Exists(current) ? new FileInfo(current) : null;
            if (info is not null && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) current = info.ResolveLinkTarget(true)?.FullName ?? current;
        }
        return current;
    }

    internal static void Write(string output, byte[] data, bool force)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var staged = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, ".oml-asset-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(staged, data);
            File.Move(staged, output, force);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    internal static double Ratio(long output, long input) => Math.Round((double)output / Math.Max(1, input), 4);
}
