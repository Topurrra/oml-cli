---
name: state-machine
description: Renders every state of one OutSystems block on a test screen, with realistic mock data and a state switcher, so you can work on each state. User-invoked.
disable-model-invocation: true
---

# State machine

This skill takes one block and renders it on a test screen in every state it can be in. Mock data feeds each state through the block's inputs, and a switcher flips between them; every state is a link. Run it only when the user asks for it.

It is a workbench, not a review. The screen stays while the user works on the visuals. Stacking worst-case scenarios for one look is `break`, exploring designs is `variant` and reviewing finished work is `interface-review`.

Say this before you start: the test screen is a module change. It is built in a staged copy with `oml apply`, the user installs and publishes it to DEV, and once installed only the user can delete it, in Service Studio (rule 3). Until the install, removal is deleting the staged file.

## 1. Scope one block

One block per run: the order history table, not "the orders screen". When the request spans several, list them and ask. Restate it in one sentence: what it shows, where it renders and where its data comes from. Read it with `oml describe <module.oml> <Block>`.

## 2. Find the states in the block

The states are the branches the block already has: its If widgets, Visible and Style Classes expressions, and the inputs they read.

| Kind | States |
| --- | --- |
| Data | Loading (`IsDataFetched` False), empty (`List.Empty`), fetch error (`HasFetchError`), one item, a typical set, a set long enough to page |
| Access | Each role check (`Check<Role>Role`), owner and other user |
| Feature | A switch from a site property or setting, a limit reached, a locked feature |

A state the block cannot reach is not a state: never add a branch to show one. A state the design shows and the block lacks is listed as missing. Write the set down before you build, one line each, in the app's words (`empty`, `manager`, `limit-reached`), grouped by what changes. Combine kinds only where the block branches on the combination.

## 3. Feed each state at the block's inputs

Mock data reaches the block where it receives data, without changing the block:

1. **Block inputs.** The test screen passes a fixture list or record to each input. This is the normal case.
2. **No input for it.** The block fetches its own data with its own aggregates, so a test screen cannot reach that state. Say so and ask before adding an input. On a public Reactive block, inputs never change (rule 4): leave that state out and say why.

Make the data look like the app: real names, dates, amounts and the item counts users really have. Three rows of "Test item" make every state look fine. A loading state holds still: pass the input that means loading and never resolve it with a timer.

## 4. Build the test screen in a staged copy

One spec, one new screen named `States<Block>`, in an existing UI flow, with the same roles as the screens that use the block (never Anonymous, rule 7):

- A Text input `State`. A Reactive screen input is part of the URL, so every state is a link: `.../States<Block>?State=empty`.
- Locals of the block's input types. `OnInitialize` fills them for the current `State` (a Switch on `State` with Assign and ListAppend nodes).
- The block once, in a Container as wide as it is in production.
- The switcher from [switcher.md](switcher.md) (`oml doc state-machine/switcher`): Links to the same screen with each `State` value, grouped under headings, with the switcher's CSS in the test screen's style sheet.

The screen adds the container, the fixtures and the switcher, nothing else: no fonts, styles or themes of its own. Then `oml spec check`, `oml apply <source.oml> <spec.json> <new-staged.oml>` and `oml review`. Ask the user to install and publish to DEV, with the exact command; wait for a yes.

## 5. Confirm every state renders

After the publish, write a browser script that opens each state and takes a screenshot (`oml doc commands#browser`):

```jsonc
{ "baseUrl": "https://<dev>/<Module>/", "steps": [
  { "open": "StatesOrderHistory?State=empty", "expect": { "screen": "StatesOrderHistory" } },
  { "screenshot": "empty" },
  { "open": "StatesOrderHistory?State=typical" },
  { "screenshot": "typical" }
] }
```

`oml browser run states.json --out states` and look at every picture. The block must show the mock data, not real data and not an empty area. Fix the fixtures in a new staged file when one does not show.

Then hand over the URL, the state list and the browser script. Stop there: changing how a state looks is the user's next request.

## Check every state after each change

After every visual change, run the browser script again and look at every state: a fix for one state often breaks another.

## Remove it on the user's word

Before the install: delete the staged file and the spec. After it: tell the user to delete `States<Block>` in Service Studio, then search the module for the screen name (`oml find <module.oml> --text "States<Block>"`) to confirm nothing links to it.

## Before you finish

| Mistake | Fix |
| --- | --- |
| "Item 1", "Test user", three rows | App-shaped data in real counts |
| The switcher in the app's theme colors | Keep it outside the design system |
| A branch added to the block to show a state | Leave the block alone; list the state as missing |
| An input added to a public Reactive block | Not allowed (rule 4); leave the state out |
| The test screen open to Anonymous | The roles of the screens that use the block |
| Installed without a yes | Never; ask with the exact command |
