using System.Security.Cryptography;
using OutSystems.Model.Enumerations;

namespace OmlCli;

// Commands that create or change a module file: new, apply, install.
public static partial class Cli
{
    static int? CmdNew(List<string> args)
    {
        if (args.Count < 2) return null;
        if (args.Contains("--from-template"))
        {
            if (Opt(args, "--from-template") is not { } from || from.StartsWith("--") || args.Contains("--template"))
                throw new CliError("--from-template requires a .oml path and cannot be combined with --template", 2);
            return TemplateCreation.Run(args[1], from, Opt(args, "--name"), Opt(args, "--kind"));
        }
        if (args.Contains("--template") && Opt(args, "--template") is null)
            throw new CliError("--template requires a template name or exported .oml path.", 2);
        return Opt(args, "--template") is { } template ? Templates.New(args[1], template, Opt(args, "--name"))
            : New(args[1], Opt(args, "--name"), Opt(args, "--kind"));
    }

    static int? CmdApply(List<string> args)
    {
        if (args.Count < 4) return null;
        var positional = new List<string>();
        var describe = new List<string>();
        var describeCreated = false;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] == "--describe" && i + 1 < args.Count) describe.AddRange(args[++i].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            else if (args[i] == "--describe-created") describeCreated = true;
            else if (args[i].StartsWith("--")) throw new CliError($"apply accepts --describe <A,B,...> and --describe-created, not '{args[i]}'.", 2);
            else positional.Add(args[i]);
        }
        if (positional.Count < 3) throw new CliError("apply needs <source.oml> <spec.json>... <staged-out.oml>.", 2);
        return Apply.Run(positional[0], positional.Skip(1).SkipLast(1).ToList(), positional[^1], new ApplyOptions(describe, describeCreated));
    }

    static int? CmdInstall(List<string> args) => args.Count == 3 ? Install(args[1], args[2]) : null;

    static int New(string outPath, string? name, string? kind)
    {
        outPath = Path.GetFullPath(outPath);
        if (File.Exists(outPath)) throw new CliError($"Output already exists: {outPath}. Use a new output path, or delete that file first.", 2);
        var services = Model.Services();
        var segmentation = Enum.Parse<SegmentationKind>(kind ?? "WebResponsive", ignoreCase: true);
        name ??= Path.GetFileNameWithoutExtension(outPath);
        using var module = services.CreateESpace(segmentation, name, "", false, services.NewKey());
        Model.Save(module, outPath);
        return Emit(new { created = outPath, name, kind = segmentation.ToString() });
    }

    // Validate, backup, copy, verify, restore on failure (skill section 16). The installed file is verified by its
    // SHA-256 against the exact bytes that were validated, so loading those same bytes again would add nothing.
    static int Install(string staged, string target)
    {
        staged = Path.GetFullPath(staged);
        target = Path.GetFullPath(target);
        if (!File.Exists(staged) || !File.Exists(target)) throw new CliError("Both staged and target must exist.", 2);
        if (string.Equals(staged, target, StringComparison.OrdinalIgnoreCase)) throw new CliError("Staged and target are the same file.", 2);

        var bytes = File.ReadAllBytes(staged);
        var validatedHash = Convert.ToHexString(SHA256.HashData(bytes));
        using (var module = Model.Load(bytes, "load staged", staged))
        {
            var stagedErrors = Model.Errors(module, "validate staged");
            if (stagedErrors.Length > 0)
                throw new CliError($"Staged module has {stagedErrors.Length} validation errors; refusing to install.");
        }

        if (new FileInfo(target).IsReadOnly) throw new CliError($"Target is read-only: {target}. Nothing installed.", 2);
        var backup = $"{target}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        if (File.Exists(backup)) throw new CliError($"Backup already exists: {backup}", 2);
        var targetHash = Timings.Measure("backup", () =>
        {
            var hash = Hash(target);
            File.Copy(target, backup);
            if (Hash(backup) != hash) throw new CliError("Backup hash mismatch; nothing installed.", 2);
            return hash;
        });

        Timings.Measure("copy", () =>
        {
            string? failure = null;
            try
            {
                File.WriteAllBytes(target, bytes);
                if (Hash(target) != validatedHash) failure = "the installed hash differs from the validated staged file";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failure = e.Message; }
            if (failure is null) return;
            // A write that failed before it changed anything (a locked file) leaves the target as it was: say so.
            if (TryHash(target) == targetHash)
                throw new CliError($"Nothing installed: {failure} The target is unchanged; backup kept at {backup}.", 2);
            try { File.Copy(backup, target, overwrite: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { throw new CliError($"Install failed ({failure}), and restoring the backup failed too ({e.Message}). Copy {backup} over {target} by hand.", 2); }
            throw new CliError($"Install failed ({failure}). The target was restored from {backup}.");
        });
        return Emit(new { installed = target, backup, sha256 = validatedHash });
    }

    static string? TryHash(string path)
    {
        try { return Hash(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
