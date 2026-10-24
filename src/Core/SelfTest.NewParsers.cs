using System.IO.Compression;
using System.Text;

namespace OmlCli;

// Pure self-tests for the pieces that need no model: the expression parser, the header reader and the package manifest
// reader. They run inside the main self-test; they only touch temp files and the module zip.
static partial class SelfTest
{
    internal static void NewParsersTest(List<string> failures)
    {
        try
        {
            ExpressionsTest(failures);
            HeaderTest(failures);
            ManifestTest(failures);
        }
        catch (Exception e) when (e is not CliError)
        {
            failures.Add($"NewParsersTest: {e.GetType().Name}: {e.Message}");
        }
    }

    static void ExpressionsTest(List<string> failures)
    {
        // Valid expressions parse and canonicalize keyword casing and spacing.
        foreach (var (input, expected) in new[]
        {
            ("A + B", "A + B"),
            ("A+B", "A + B"),
            ("NOT A AND B", "not A and B"),
            ("GetOrders.List.Current.Order.Total", "GetOrders.List.Current.Order.Total"),
            ("Order.Id = OrderId", "Order.Id = OrderId"),
            ("Count >= 0 AND Count <= 10", "Count >= 0 and Count <= 10"),
            ("Name like \"A%\"", "Name like \"A%\""),
            ("x[1] + x[2]", "x[1] + x[2]"),
            ("f(1, x: 2)", "f(1, x: 2)"),
            ("#2026-10-5#", "#2026-10-5#"),
            ("#10:30#", "#10:30#"),
            ("#2026-10-5 10:30:0#", "#2026-10-5 10:30:0#"),
            ("{Name: \"a\", Count: 1}", "{Name: \"a\", Count: 1}"),
            ("[1, 2, 3]", "[1, 2, 3]"),
            ("NullIdentifier()", "NullIdentifier()"),
            ("-Amount", "-Amount"),
            ("EncodeHtml(Name)", "EncodeHtml(Name)"),
        })
        {
            var result = Expressions.TryParse(input);
            if (!result.Ok) { failures.Add($"expr parse failed: {input}: {result.Error}"); continue; }
            if (result.Text != expected) failures.Add($"expr canonical mismatch: '{input}' -> '{result.Text}', expected '{expected}'");
        }

        // Invalid expressions report an error with the parser vocabulary.
        foreach (var input in new[] { "A +", "f(", "Name =", "\"unterminated", "#2026-13-1#", "A B", "(", "x[", "like" })
        {
            var result = Expressions.TryParse(input);
            if (result.Ok) failures.Add($"expr accepted invalid: {input} -> '{result.Text}'");
        }
    }

    static void HeaderTest(List<string> failures)
    {
        var dir = Path.Combine(Path.GetTempPath(), "oml-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var fields = new[]
            {
                "11.0.415.100", "11.55.91.65258", "155", "aaaaaaaaaaaaaaaaaaaaaaaaaa", "", "True",
                "HeaderTest", "a module", "2026-10-11 09:00:00", "AAAAAAAAAAAAAAAAAAAAAAAAAA", "", "",
                "AAAAAAAAAAAAAAAAAAAAAAAAAA", "13", "False", "Signaturely", "98", "someone", "", "",
                "False", "", "", "False", "", "", "", "", "", "", "", "CrossDevice", "False", "", "11.0.415.100",
            };
            var header = string.Join("|", fields);
            var bytes = new List<byte> { 79, 77, 76 };
            var utf8 = Encoding.UTF8.GetBytes(header);
            bytes.AddRange(BitConverter.GetBytes(utf8.Length));
            bytes.AddRange(utf8);
            var path = Path.Combine(dir, "header.oml");
            File.WriteAllBytes(path, bytes.ToArray());

            var info = ModuleHeader.Read(path);
            if (info is null) { failures.Add("header read returned null for a synthetic module"); return; }
            if (info.Name != "HeaderTest" || info.Version != "11.55.91.65258")
                failures.Add("header name/version mismatch");
            if (info.LastUpgradeVersion != "11.0.415.100") failures.Add("header lastUpgradeVersion mismatch");
            if (info.ESpaceType != "CrossDevice") failures.Add("header eSpaceType mismatch");
            if (info.ContentHash != "") failures.Add("header contentHash mismatch");
            if (info.Key != "aaaaaaaaaaaaaaaaaaaaaaaaaa") failures.Add("header key mismatch");
            if (!info.HasLuvUpgrade) failures.Add("header hasLuvUpgrade should be true (lastUpgrade < version)");
            var guid = Guid.NewGuid();
            var b64 = Convert.ToBase64String(guid.ToByteArray()).Replace('/', '_').Replace("=", "");
            if (ModuleHeader.NormalizeKey(b64) != ModuleHeader.NormalizeKey(guid.ToString()))
                failures.Add("key normalization across formats failed");

            var not = ModuleHeader.Read(Path.Combine(dir, "nonexistent.oml"));
            if (not is not null) failures.Add("header read of a missing file did not return null");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    static void ManifestTest(List<string> failures)
    {
        var dir = Path.Combine(Path.GetTempPath(), "oml-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string manifest = """
                <?xml version="1.0" encoding="utf-8"?>
                <Pack Schema="3" HubServerVersion="11.55.91" LastUpgradeVersion="11.0.415.100" IsApplicationPack="True" UID="aaaa" Creation="2026-10-11 09:00:00">
                  <Solutions>
                    <Solution UID="bbbb" Name="S" Description="D" ActivationCode="" IsTransient="False" Schema="3">
                      <References/>
                      <Applications>
                        <Application UID="cccc" Name="App" Description="" FrontOfficeEspaceName="App" ForgeBaseVersion="1.0" APIHash="" ContentHash="" ApplicationKind="WebResponsive">
                          <Modules>
                            <Module UID="dddd" Kind="eSpace" Name="App" ESpaceType="CrossDevice"><Signature/></Module>
                          </Modules>
                          <References>
                            <ReferencedApplication UID="eeee" Name="Producer" ForgeBaseVersion="1.0" ApplicationKind="WebResponsive" HasLocalChanges="False">
                              <ReferencedModules>
                                <Module UID="ffff" Kind="eSpace" ESpaceType="WebResponsive">
                                  <ReferencedAPI>
                                    <PublicElement UID="1111" Kind="Action" Name="Ping" CompatibilitySignatureHash="hash1" FullSignatureHash="hash2"/>
                                  </ReferencedAPI>
                                </Module>
                              </ReferencedModules>
                            </ReferencedApplication>
                          </References>
                        </Application>
                      </Applications>
                      <Version UID="gggg" Name="V" Creation="2026-10-11 09:00:00"><References/></Version>
                    </Solution>
                  </Solutions>
                </Pack>
                """;
            var path = Path.Combine(dir, "sample.oap");
            using (var fs = File.Create(path))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("Manifest.xml");
                using var w = new StreamWriter(e.Open(), Encoding.UTF8);
                w.Write(manifest);
            }

            var pack = PackageManifest.Read(path);
            if (pack is null) { failures.Add("manifest read returned null for a synthetic package"); return; }
            if (pack.Schema != 3 || !pack.IsApplicationPack) failures.Add("manifest root attributes mismatch");
            var app = pack.Apps.SingleOrDefault();
            if (app is null) { failures.Add("manifest applications missing"); return; }
            if (app.Name != "App" || app.Modules.Count != 1 || app.Modules[0].Name != "App") failures.Add("manifest application/module mismatch");
            var dep = app.Dependencies.SingleOrDefault();
            if (dep is null) { failures.Add("manifest dependency missing"); return; }
            if (dep.Name != "Producer" || dep.Modules.Count != 1 || dep.Modules[0].Api.Count != 1) failures.Add("manifest dependency modules/elements mismatch");
            var pe = dep.Modules[0].Api[0];
            if (pe.Name != "Ping" || pe.CompatibilitySignatureHash != "hash1") failures.Add("manifest public element mismatch");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }
}
