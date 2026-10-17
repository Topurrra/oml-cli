# Contributing

## Build

Use Windows, Service Studio 11, and the .NET 8 SDK.
Run commands from the repository root:

```powershell
dotnet build -c Release src/OmlCli.csproj
$env:PATH = "$PWD\src\bin\Release\net8.0;$env:PATH"
oml version
```

The executable is `src/bin/Release/net8.0/oml.exe`. Keep the build output folder together.
For a different Service Studio folder, set the runtime path and build property:

```powershell
$env:OML_SERVICE_STUDIO = 'D:\OutSystems\Service Studio'
dotnet build -c Release src/OmlCli.csproj '-p:SS=D:\OutSystems\Service Studio'
```

The launcher in `src/Program.cs` loads the installed assemblies.
Core uses typed interfaces and reflection to handle build differences.
The self-test checks behavior instead of relying on a supported-build list.

## Run tests

Build first, then run:

```powershell
pwsh -NoProfile -File tests/regress.ps1
```

Exit `0` means pass. The suite creates disposable modules under `tmp/` and replaces its temporary `.oml` files.
It applies JSON fixtures, checks saved models, tests LifeTime against a local mock, and runs the self-test.
LifeTime tests do not contact a real server.
Run a real LifeTime smoke test before relying on deployment behavior.

To run the build capability check alone:

```powershell
oml selftest
```

## Releasing

Build releases locally on Windows with PowerShell 7, Service Studio 11, and the .NET SDK.
Update `src/Directory.Build.props` and add a matching `## <version>` section to `CHANGELOG.md`,
then commit all changes. Configure `origin`; the script checks both local and remote tags.
Set `OML_SERVICE_STUDIO` for a non-default installation and ensure `dotnet` is on PATH.

```powershell
pwsh -NoProfile -File ./release.ps1
```

The script requires a clean working tree on a branch. It cleans and builds Release,
runs the full regression suite, publishes a framework-dependent win-x64 CLI, checks that
no Service Studio files are included, and smoke-tests the extracted archive.
It writes the zip, SHA-256 checksum, and `release-notes.md` to ignored `dist/` and prints
the exact tag, push, and GitHub CLI release commands for you to run.
`-Version` overrides the shared version for the build and package; `-OutDir` changes the output folder.
Use an ignored folder or a folder outside the repository for output.
With `-Publish`, it shows the repository, tag, and files and requires interactive confirmation
before running those commands. This requires an authenticated `gh` CLI. Never use `-Publish` in tests.
If publishing stops partway through, inspect the local and remote tag and release state before retrying.

## Add a feature

1. Add a focused file in `src/Core/`. Extend the shared builder when needed.
2. Use `Reflect` for interface members, setters, and deferred expressions.
   Use `Refs` for element names and references. Do not create separate lookup rules.
3. Add the spec key to its typed record or raw-object handler.
   Define defaults, supported module kinds, and behavior for existing elements.
4. Add a fixture in `tests/`. Check saved behavior after reopening, including targets, types, and arguments.
   Add refusal cases for unsafe or unsupported input.
5. Add a self-test step for the installed API behavior that the feature needs.
6. Add the fixture or checks to the regression suite. Build and run the suite.
7. Document the key, limits, and an example in `skill/reference/spec.md`. Add it to the key index in `skill/SKILL.md` and to `README.md` when users need to know it.
8. Raise the shared release version in `src/Directory.Build.props` when preparing a release.

See [the Model API reference](skill/reference/model-api.md) before changing model construction.

## Rules

- Add no new dependencies.
- Never edit OML XML. Use the typed model API.
- Use generic interface and descriptor discovery instead of hard-coded concrete classes or descriptor GUIDs.
- Never swallow model errors or reflection failures. Report the failed operation and its context.
- Create elements and references before binding expressions.
- Bind arguments by parameter name, never by position.
- Refuse silent renames. Reuse existing elements without changing unrelated content.
- Validate before saving and after reopening. Preserve the source file during development.
- Do not redistribute Service Studio assemblies or bypass IPP and licensing checks.
- Keep credentials out of fixtures, logs, and reports.

## Report a bug

Include:

- The command and its exit code.
- The output of the version command below.
- The spec that reproduces the problem.
- The complete JSON output, including validation errors and warnings.
- The expected result and the actual result.
- The module kind, required references, and a shareable source file if company policy permits.

```powershell
oml version
```

Remove credentials and confidential data before sharing a report.
Keep error message texts and element paths unchanged when possible.
