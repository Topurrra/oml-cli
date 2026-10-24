// The two ready prompts for agents, with placeholders the developer replaces. The texts are also the first lines of
// the prompt builder's output, so the two ways stay in step.
export const PROMPTS = [
  {
    title: 'Small task',
    when: 'one module, one feature or fix, up to about 10 elements. A cheap model is fine.',
    text: String.raw`[oml] Small task
Use oml (the OutSystems 11 CLI) and its skill. Follow the skill's Standard loop and rules.md.

Module to change: <path to the .oml file, for example C:\Work\Orders.oml>
Read-only modules (producers): <paths, or "none">

Task:
<WRITE WHAT YOU WANT: one change, with exact names, types, screens and behaviour>

Done means:
- One staged file next to the module, saved with no new errors, and every new warning fixed or explained.
- UI changes previewed at 1366 and 1920 px (dark or 390 px only if I ask).
- oml review --md of the change, shown to me.
Do not install or publish. Ask me before anything that changes a real module or an environment.`,
  },
  {
    title: 'Architect',
    when: 'several modules, a large feature, or many similar elements. Use a strong model; it leads cheaper worker subagents.',
    text: String.raw`[oml] Architect task
Use oml (the OutSystems 11 CLI) as the architect: read the skill's architect.md and follow it, and rules.md.
Plan lanes, give each worker subagent its brief, check every result yourself, and merge the parts with one oml apply.

Modules: <path - Change or Read only, one per line>
Goal:
<WRITE THE FEATURE: what users can do, the data, the screens, the integrations>
Constraints: <naming, company skills to follow, what must not change, deadlines>
Workers: use the default worker model and effort; ask me once if I want others.

Done means:
- One staged file per changed module that passes validate, oml review --md and oml check.
- A short honest report: what was built, what was verified, what was not.
Do not install or publish without my yes.`,
  },
];
