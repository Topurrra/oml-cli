# Third-party notices

`oml` includes adapted or ported work from these sources.
Read the linked license files for their full terms.

| Source | License | License file | Used in |
|---|---|---|---|
| OutSystems `outsystems11-mcp` examples | MIT | [outsystems11-mcp-LICENSE.txt](licenses/outsystems11-mcp-LICENSE.txt) | Adapted in `src/Core/Platform.cs`, `src/Core/Rest.cs`, `src/Core/Sql.cs`, `src/Core/StaticEntities.cs`, and `src/Core/WebUi.cs`. Facts from its skill docs (OutSystems UI block names, inputs, placeholders, enumeration records, design tokens) re-written in our own words in `skill/reference/osui-patterns.md`. |
| OutSystems `outsystems-mcp` architecture template | MIT | [outsystems-mcp-LICENSE.txt](licenses/outsystems-mcp-LICENSE.txt) | Visual style adapted in `src/Core/Architecture/template.html`. |
| OutSystems `outsystems-pipeline` | Apache-2.0 | [outsystems-pipeline-LICENSE.txt](licenses/outsystems-pipeline-LICENSE.txt) | Ported in `src/LifeTime.cs`. |

`oml` uses the locally installed Service Studio assemblies. It does not ship those assemblies.
