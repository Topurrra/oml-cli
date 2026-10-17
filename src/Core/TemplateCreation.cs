using OutSystems.Model;
using OutSystems.Model.Applications;
using OutSystems.Model.Enumerations;

namespace OmlCli;

static class TemplateCreation
{
    public static int Run(string output, string template, string? name, string? kind) => Packages.Guard(() => {
        Packages.Fresh(output);
        using var source = Model.Load(template);
        var s = Model.Services();
        var segmentation = kind is null ? source.Kind : Enum.Parse<SegmentationKind>(kind, true);
        using var app = (IApplication)Reflect.Call(s, "CreateApplication", segmentation, "TemplateApplication", "", "", s.NewKey())!;
        // The loaded-object overload lacks an OML header headlessly; bytes preserve the permission checks.
        using var module = (IESpace)Reflect.Call(app, "CreateESpaceFromTemplate", File.ReadAllBytes(template), name ?? Path.GetFileNameWithoutExtension(output), ((IModule)source).ActivationCode, null, null, s.NewKey())!;
        if (Model.NameOf(module) != (name ?? Path.GetFileNameWithoutExtension(output))) throw new ModelError("The template factory renamed the module");
        if (module.Kind != segmentation) throw new ModelError($"Template created kind {module.Kind}; requested {segmentation}");
        if (((IModule)module).Key.ToString() == ((IModule)source).Key.ToString()) throw new ModelError("Template factory kept the source key");
        var baseline = Model.Errors(source);
        var errors = Apply.Subtract(Model.Errors(module), baseline);
        if (errors.Length > 0) { Cli.Emit(new { saved = false, newErrors = Model.Grouped(errors) }); return 1; }
        Model.Save(module, output);
        using var reopened = Model.Load(output);
        errors = Apply.Subtract(Model.Errors(reopened), baseline);
        if (errors.Length > 0) File.Delete(output);
        Cli.Emit(new { saved = errors.Length == 0, created = Path.GetFullPath(output), name = Model.NameOf(reopened), key = ((IModule)reopened).Key.ToString(), kind = reopened.Kind.ToString(), reopenNewErrors = Model.Grouped(errors) });
        return errors.Length == 0 ? 0 : 1;
    });
}
