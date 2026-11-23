# Third-party notices

`oml` includes adapted or ported work from these sources.
Read the linked license files for their full terms.

| Source | License | License file | Used in |
|---|---|---|---|
| OutSystems `outsystems11-mcp` examples | MIT | [outsystems11-mcp-LICENSE.txt](licenses/outsystems11-mcp-LICENSE.txt) | Adapted in `src/Core/Platform.cs`, `src/Core/Rest.cs`, `src/Core/Sql.cs`, `src/Core/StaticEntities.cs`, and `src/Core/WebUi.cs`. Facts from its skill docs (OutSystems UI block names, inputs, placeholders, enumeration records, design tokens) re-written in our own words in `skill/reference/osui-patterns.md`. |
| OutSystems `outsystems-mcp` architecture template | MIT | [outsystems-mcp-LICENSE.txt](licenses/outsystems-mcp-LICENSE.txt) | Visual style adapted in `src/Core/Architecture/template.html`. |
| OutSystems `outsystems-pipeline` | Apache-2.0 | [outsystems-pipeline-LICENSE.txt](licenses/outsystems-pipeline-LICENSE.txt) | Ported in `src/LifeTime.cs`. |
| Jakub Krehel `skills` (https://github.com/jakubkrehel/skills) | MIT | [jakubkrehel-skills-LICENSE.txt](licenses/jakubkrehel-skills-LICENSE.txt) | Its 16 interface skills, adapted for OutSystems 11 Reactive and Traditional Web in `skill/frontend/` (the license text is also in `skill/frontend/LICENSE.txt`, so project copies of the skill keep it). |
| Simple Icons (https://simpleicons.org) | CC0-1.0 | (public domain, none needed) | The agent logos in the strip on `docs/index.html`. The logos are trademarks of their owners. |
| GitHub Primer Octicons (https://primer.style/foundations/icons), through Simple Icons | MIT | [primer-octicons-LICENSE.txt](licenses/primer-octicons-LICENSE.txt) | The GitHub Copilot logo in the strip on `docs/index.html`. |
| codemode-runtime (the author's own project) | MIT | [codemode/LICENSE](codemode/LICENSE) | Copied into `codemode/` for `oml code`. |

`oml` uses the locally installed Service Studio assemblies. It does not ship those assemblies.
