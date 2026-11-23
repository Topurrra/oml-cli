# Frontend skills

UI design rules and UI workflows for OutSystems 11 Reactive and Traditional Web apps. They add to `oml doc ui-checklist` and golden rule 1; they never override the 11 rules.

Read one with `oml doc <name>` (or `skill/frontend/<name>/SKILL.md`). A linked file is `oml doc <name>/<file>`, such as `oml doc better-motion/recipes`. A skill named in backticks in another skill is read the same way.

## Design rules: read the one that matches the UI work

| Skill | Read it when you |
| --- | --- |
| `better-interface` | Review a screen, flow or app across every UI domain; it runs the skills below and writes one ranked report |
| `better-accessibility` | Build or check keyboard and focus, names, labels, forms, announcements, motion and zoom (WCAG 2.2) |
| `better-layout` | Set grouping, alignment, spacing, responsive structure (OutSystems UI Columns, device classes) and room for long text |
| `better-writing` | Write or check labels, button texts, messages, validation texts, empty states and confirmations |
| `better-typography` | Set or check the type scale, line height, fonts, wrapping, truncation and punctuation |
| `better-colors` | Map colors to theme tokens, add role tokens, measure contrast or build a dark theme |
| `better-ui` | Polish radius, shadows, image outlines, hover and icons |
| `better-motion` | Decide whether something animates and build it in CSS with exact values |
| `build-design` | Build a screen or block from a Figma file or a design image |

## Workflows: run only when the user asks for one

| Skill | What it does |
| --- | --- |
| `interface-review` | Reviews a change (a staged copy against its source, two exports or two server versions) for the UI problems it introduced |
| `explain-interface` | Explains how a screen, an effect or an animation was built, from a URL or a screenshot |
| `break` | Stress tests one block with worst-case content, injected into the DEV page with `oml browser try` |
| `state-machine` | Shows every state of one block on a test screen with mock data and a switcher |
| `variant` | Previews three variants of one piece in the DEV page with a picker, then builds the chosen one |
| `afk` | Improves the app while the user is away: staged copies and review files, never an install or publish |
| `upskill` | Studies past agent sessions and oml's logs, proposes team rules, hooks, checks and settings, builds the ones picked |

## Shared ground

- Changes go through a spec, `oml apply` to a new staged file and `oml review`; the user installs and publishes. A preview in the running page (`oml browser try`) saves nothing.
- Fixes are written in the app's idiom: theme or block CSS, Style Classes, widget properties and attributes, OutSystems UI patterns. Never React, Tailwind or a new library.
- Each rule lives in one skill; others name it. Severity and the verdict of a full review belong to `better-interface`.

Adapted for OutSystems from the skills by Jakub Krehel (https://github.com/jakubkrehel/skills), MIT License, Copyright (c) 2026 Jakub Krehel: [LICENSE.txt](LICENSE.txt).
