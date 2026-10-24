using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace OmlCli;

// The OAP/OSP "Schema 3" manifest, read directly from the package ZIP without loading the model. The reading is
// tolerant: attributes are strings, missing optional nodes become empty lists. This is what makes `oml app list` and
// `oml app extract` fast and model-free.
static class PackageManifest
{
    public const string ManifestEntry = "Manifest.xml";

    public sealed record PublicElement(string Name, string Kind, string Key, string CompatibilitySignatureHash, string FullSignatureHash);

    public sealed record ReferencedModule(string UID, string Kind, string ESpaceType, List<PublicElement> Api);

    public sealed record Dependency(string Name, string Key, decimal ForgeBaseVersion, bool HasLocalChanges, string APIHash, string ContentHash, string ApplicationKind, List<ReferencedModule> Modules);

    public sealed record Module(string UID, string Kind, string ESpaceType, string Name, byte[]? Signature);

    public sealed record Application(string Key, string Name, string Description, decimal ForgeBaseVersion, string APIHash, string ContentHash, string ApplicationKind, List<Module> Modules, List<Dependency> Dependencies);

    public sealed record Solution(string Name, string Description, string ActivationCode, bool IsTransient, string Key, List<Application> Applications, List<Module> References);

    public sealed record Pack(int Schema, string HubServerVersion, string LastUpgradeVersion, bool IsApplicationPack, string UID, DateTime? Creation, List<Solution> Solutions)
    {
        public IEnumerable<Application> Apps => Solutions.SelectMany(s => s.Applications);
        public IEnumerable<Module> AllModules => Apps.SelectMany(a => a.Modules).Concat(Solutions.SelectMany(s => s.References));
    }

    // The zip entry that holds the manifest, or null when the package has none.
    public static ZipArchiveEntry? FindManifestEntry(ZipArchive zip) =>
        zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, ManifestEntry, StringComparison.OrdinalIgnoreCase));

    public static Pack? Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return Read(zip);
    }

    // Reads the manifest from an already-open package archive (so callers that also read module bytes open it once).
    public static Pack? Read(ZipArchive zip)
    {
        var entry = FindManifestEntry(zip);
        if (entry is null) return null;
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        XDocument doc;
        try { doc = XDocument.Load(reader); }
        catch (System.Xml.XmlException) { return null; }
        return Parse(doc.Root);
    }

    // Reads the manifest bytes directly (used by tests and by callers that already have the bytes).
    public static Pack? ReadBytes(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        return Read(zip);
    }

    static Pack? Parse(XElement? root)
    {
        if (root is null || root.Name.LocalName != "Pack") return null;
        var schema = Int(root.Attribute("Schema")?.Value, 0);
        var solutionsEl = root.Element("Solutions");
        var solutions = new List<Solution>();
        foreach (var solEl in solutionsEl?.Elements("Solution") ?? Enumerable.Empty<XElement>())
        {
            var apps = new List<Application>();
            foreach (var appEl in solEl.Element("Applications")?.Elements("Application") ?? Enumerable.Empty<XElement>())
                apps.Add(ReadApplication(appEl));
            var references = new List<Module>();
            foreach (var refEl in solEl.Element("References")?.Elements("Reference") ?? Enumerable.Empty<XElement>())
                references.Add(new Module(Attr(refEl, "UID"), Attr(refEl, "Kind"), Attr(refEl, "ESpaceType"),
                    Attr(refEl, "Name"), null));
            solutions.Add(new Solution(
                Attr(solEl, "Name"), Attr(solEl, "Description"), Attr(solEl, "ActivationCode"),
                Bool(Attr(solEl, "IsTransient")), Attr(solEl, "UID"), apps, references));
        }
        return new Pack(schema, Attr(root, "HubServerVersion"), Attr(root, "LastUpgradeVersion"),
            Bool(Attr(root, "IsApplicationPack")), Attr(root, "UID"), DateTimeLiteral(Attr(root, "Creation")), solutions);
    }

    static Application ReadApplication(XElement appEl)
    {
        var modules = new List<Module>();
        foreach (var modEl in appEl.Element("Modules")?.Elements("Module") ?? Enumerable.Empty<XElement>())
        {
            byte[]? signature = null;
            var sig = modEl.Element("Signature")?.Value;
            if (!string.IsNullOrWhiteSpace(sig))
            {
                try { signature = Convert.FromBase64String(sig); }
                catch (FormatException) { }
            }
            modules.Add(new Module(Attr(modEl, "UID"), Attr(modEl, "Kind"), Attr(modEl, "ESpaceType"),
                Attr(modEl, "Name"), signature));
        }
        var deps = new List<Dependency>();
        foreach (var depEl in appEl.Element("References")?.Elements("ReferencedApplication") ?? Enumerable.Empty<XElement>())
        {
            var refs = new List<ReferencedModule>();
            foreach (var modEl in depEl.Element("ReferencedModules")?.Elements("Module") ?? Enumerable.Empty<XElement>())
            {
                var api = new List<PublicElement>();
                foreach (var peEl in modEl.Element("ReferencedAPI")?.Elements("PublicElement") ?? Enumerable.Empty<XElement>())
                    api.Add(new PublicElement(Attr(peEl, "Name"), Attr(peEl, "Kind"), Attr(peEl, "UID"),
                        Attr(peEl, "CompatibilitySignatureHash"), Attr(peEl, "FullSignatureHash")));
                refs.Add(new ReferencedModule(Attr(modEl, "UID"), Attr(modEl, "Kind"), Attr(modEl, "ESpaceType"), api));
            }
            deps.Add(new Dependency(Attr(depEl, "Name"), Attr(depEl, "UID"), Decimal(Attr(depEl, "ForgeBaseVersion"), -1m),
                Bool(Attr(depEl, "HasLocalChanges")), Attr(depEl, "APIHash"), Attr(depEl, "ContentHash"),
                Attr(depEl, "ApplicationKind"), refs));
        }
        return new Application(Attr(appEl, "UID"), Attr(appEl, "Name"), Attr(appEl, "Description"),
            Decimal(Attr(appEl, "ForgeBaseVersion"), -1m), Attr(appEl, "APIHash"), Attr(appEl, "ContentHash"),
            Attr(appEl, "ApplicationKind"), modules, deps);
    }

    // The module binary inside a package that matches a manifest module (by Normal and then by UID), or null.
    public static byte[]? ModuleBytes(ZipArchive zip, Module module)
    {
        var name = module.Name;
        var entry = zip.Entries.FirstOrDefault(e =>
        {
            var baseName = Path.GetFileName(e.FullName);
            return !e.FullName.EndsWith(ManifestEntry, StringComparison.OrdinalIgnoreCase) && (baseName.Equals(name + ".oml", StringComparison.OrdinalIgnoreCase)
                || baseName.Equals(name + ".xif", StringComparison.OrdinalIgnoreCase));
        });
        if (entry is null) return null;
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    static string Attr(XElement el, string name) => el.Attribute(name)?.Value ?? "";
    static int Int(string? s, int fallback) => int.TryParse(s ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    static decimal Decimal(string? s, decimal fallback) => decimal.TryParse(s ?? "", NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    static DateTime? DateTimeLiteral(string? s) =>
        DateTime.TryParseExact(s ?? "", "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;

    static bool Bool(string? s) => s switch
    {
        null or "" => false,
        _ when bool.TryParse(s, out var b) => b,
        "1" or "Yes" or "yes" or "ON" or "on" => true,
        "0" or "No" or "no" or "OFF" or "off" => false,
        _ => false, // anything else is not a true value
    };
}
