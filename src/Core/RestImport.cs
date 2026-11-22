using System.Reflection;
using OutSystems.Model;

namespace OmlCli;

// "openapi" on a new consumed REST API: Service Studio's own Swagger import (IRestPluginService.CreateRestClientFromSwagger)
// fills it with the methods, parameters and structures Studio's "Add all methods" would make. The plugin service itself
// needs Studio's workspace (it reads workspace.RestService for a favicon and refuses OpenAPI 3 behind a feature flag),
// so this calls the same steps it runs: parse, build the client definition, generate the model. The definition gets no
// REST service, so nothing is fetched: no favicon, and an external $ref is refused before anything is read.
partial class Builder
{
    const string RestPlugin = "ServiceStudio.Plugin.REST.";

    void RestImport(object client, RestSpec s, string label)
    {
        var path = s.OpenApi!;
        if (!File.Exists(path)) throw new ModelError($"openapi file '{path}' not found. Give a path relative to the spec's folder.");
        var json = File.ReadAllText(path).TrimStart('﻿');
        var (parser, version) = RestImportParser(json, path);
        if (Reflect.Get(parser, "ExternalRefURIs") is IEnumerable<Uri> external && external.Any())
            throw new ModelError($"openapi '{path}' has $ref to other files ({string.Join(", ", external.Take(3))}). Put those schemas into the file and apply again.");
        var descriptor = parser.GetType().Assembly.GetType(RestPlugin + "RestClientDescriptor", true)!;
        foreach (var (property, feature) in new[] { ("EnumsAreSupported", "IsRestEnumFeatureSupported"), ("MultipartIsSupported", "IsRestMultipartFeatureSupported") })
            parser.GetType().GetProperty(property)!.SetValue(parser, Static(descriptor, feature, client));
        var definition = Reflect.Call(parser, "CreateClientDefinitionFromSwagger", new object?[] { null })
            ?? throw new ModelError($"Service Studio's importer returned nothing for '{path}'");
        Static(descriptor, "GenerateClientModel", client, definition, Reflect.Get(parser, "ItemsWithTypeArrays"), Reflect.Get(parser, "ItemsWithOneOf"));
        // The importer names the client after info.title; the spec's name wins.
        if (Model.NameOf(client) != s.Name) Reflect.Property(client, "Name", writable: true)!.SetValue(client, s.Name);
        var notes = (Static(descriptor, "GetImportErrorsAndWarnings", client, parser) as System.Collections.IEnumerable)?.Cast<object>() ?? [];
        foreach (var note in notes)
        {
            var text = $"{label}: {version} import: {Flat(Reflect.Get(note, "Title") as string)} {Flat(Reflect.Get(note, "Message") as string)}".TrimEnd();
            if (Reflect.Get(note, "IsError") is true) problems.Add(text); else Warnings.Add(text);
        }
        foreach (var a in RestModel.Actions(client)) created.Add($"{label}.{Model.NameOf(a)}");
        foreach (var structure in Elements(Reflect.Get(client, "Structures"))) created.Add($"REST DTO {Model.NameOf(structure)}");
        foreach (var entity in Elements(Reflect.Get(client, "Entities"))) created.Add($"REST enum {Model.NameOf(entity)}");
        if (!RestModel.Actions(client).Any()) Warnings.Add($"{label}: the {version} document '{path}' has no operations; the REST API has no methods.");
    }

    // Swagger 2.0 first, then OpenAPI 3, as Service Studio tries them.
    static (object parser, string version) RestImportParser(string json, string path)
    {
        if (!json.StartsWith('{'))
            throw new ModelError($"openapi '{path}' is not JSON. Service Studio imports JSON only: convert the YAML to JSON and apply again.");
        var assembly = RestModel.Contract(false).Assembly;
        foreach (var (type, version) in new[] { ("SwaggerParser20", "Swagger 2.0"), ("SwaggerParser30", "OpenAPI 3") })
        {
            var parser = Activator.CreateInstance(assembly.GetType(RestPlugin + "Parser." + type, true)!,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, [json, null], null)!;
            if (Reflect.Call(parser, "IsValid") is true && Reflect.Call(parser, "IsVersionValid") is true) return (parser, version);
        }
        throw new ModelError($"openapi '{path}' is not a Swagger 2.0 or OpenAPI 3 document Service Studio can read. Check that it is valid JSON with \"swagger\": \"2.0\" or \"openapi\": \"3.x\".");
    }

    static object? Static(Type owner, string name, params object?[] args)
    {
        var method = owner.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == args.Length)
            ?? throw new ModelError($"{owner.Name}.{name} is not on this Service Studio build; import the document in Service Studio");
        try { return method.Invoke(null, args); }
        catch (TargetInvocationException e) when (e.InnerException is { } inner) { throw new ModelError($"Service Studio's importer failed: {inner.Message}"); }
    }

    // Studio's notes are several lines with bullets: one line, each part ending in a full stop.
    static string Flat(string? text) => string.Join(" ", (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.Trim().TrimStart('•', ' ').TrimEnd('.', ':', ' ')).Where(l => l.Length > 0).Select(l => l + "."));
}
