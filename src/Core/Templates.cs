using OutSystems.Model;

namespace OmlCli;

static class Templates
{
    static IEnumerable<string> Candidates()
    {
        var roots = new[] { Model.ServiceStudioDir,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OutSystems"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OutSystems") };
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                if (new[] { ".oml", ".oap", ".osp" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
                    || Path.GetFileName(file) is "Templates.dat" or "TemplateService.dat") yield return file;
    }

    // Installed files are read only: inspect/clone a private copy, including .dat files containing OML bytes.
    static T Copy<T>(string path, Func<string, T> use)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "oml-template-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
        try { File.Copy(path, scratch); return use(scratch); }
        finally { File.Delete(scratch); }
    }

    public static object List()
    {
        return Candidates().Select(path => Copy(path, copy =>
        {
            try
            {
                if (Path.GetExtension(path).ToLowerInvariant() is ".oap" or ".osp")
                    return (object)new { name = Path.GetFileNameWithoutExtension(path), path, available = false, problem = "Application/solution bundle; export its Reactive module as .oml first." };
                using var m = Model.Load(copy);
                return new { name = Path.GetFileNameWithoutExtension(path), path, available = true, kind = m.Kind.ToString(),
                    templateBundle = Reflect.Get(m, "IsTemplateBundle"),
                    theme = Reflect.Get(m, "DefaultMobileTheme") is { } theme ? Model.NameOf(theme) : null,
                    references = m.References.Select(r => r.Name).ToArray(),
                    blocks = m.MobileFlows.SelectMany(f => f.Nodes).Where(n => n is OutSystems.Model.UI.Mobile.IMobileBlockSignature).Select(Model.NameOf).ToArray() };
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or ModelError or NotSupportedException or NotImplementedException)
            { return new { name = Path.GetFileNameWithoutExtension(path), path, available = false, problem = e.Message }; }
        })).ToArray();
    }

    public static int New(string output, string template, string? name)
    {
        output = Path.GetFullPath(output);
        if (File.Exists(output)) throw new CliError($"Output already exists: {output}. Use a new output path, or delete that file first.", 2);
        var matches = File.Exists(template) ? new[] { Path.GetFullPath(template) }
            : Candidates().Where(p => Path.GetFileNameWithoutExtension(p).Equals(template, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new CliError($"Template '{template}' {(matches.Length == 0 ? "not found" : "is ambiguous; use its full path")}. Run 'oml templates', or provide an exported .oml module.", 2);
        if (Path.GetExtension(matches[0]).ToLowerInvariant() is ".oap" or ".osp")
            throw new CliError("Export the bundle's Reactive module as .oml before using --template.", 2);
        try
        {
            return Copy(matches[0], copy =>
            {
                var services = Model.Services();
                using var source = Model.Load(copy);
                var baseline = Model.Errors(source);
                using var module = (IESpace)Reflect.Call(services, "CloneESpace", File.ReadAllBytes(copy),
                    Reflect.Get(source, "ActivationCode"), null, null, services.NewKey())!;
                Reflect.Property(module, "Name", writable: true)!.SetValue(module, name ?? Path.GetFileNameWithoutExtension(output));
                var errors = Apply.Subtract(Model.Errors(module), baseline);
                if (errors.Length > 0) { Cli.Emit(new { saved = false, newErrors = Model.Grouped(errors) }); return 1; }
                Model.Save(module, output);
                using var reopened = Model.Load(output);
                var reopenErrors = Apply.Subtract(Model.Errors(reopened), baseline);
                if (reopenErrors.Length > 0) File.Delete(output);
                Cli.Emit(new { saved = reopenErrors.Length == 0, created = output, name = Model.NameOf(module), template = matches[0], baselineErrors = baseline.Length, reopenNewErrors = Model.Grouped(reopenErrors) });
                return reopenErrors.Length == 0 ? 0 : 1;
            });
        }
        catch (Exception e) when (e is ModelError or InvalidOperationException or ArgumentException or NotSupportedException or NotImplementedException)
        { throw new CliError($"Cannot clone template '{template}': {e.Message}"); }
    }
}
