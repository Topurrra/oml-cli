# Change walkthroughs (oml show-change)

Read this when a change is more than a small fix and the user should read it before `install`. A walkthrough is a Markdown file that explains the change. `oml show-change` shows it on a dashboard page; the user reads it, opens the elements it names and sends comments back.

## When to write one

- The change has several parts (data, logic, screens), or touches a UI the user can see.
- The user must approve something before it is installed, or asked to "show me first".
- Skip it for one-line fixes: the `oml review` report is enough.

## Where

Write `specs/<short-name>.md` next to the module files, for example `specs/orders-export.md`. Use relative paths in it: they are read from the file's folder.

## Shape

1. **Problem**: two or three sentences, in the user's words. What is wrong or missing, and why it matters.
2. **Phases**: one heading per phase of the change (Data, Logic, Screen). In each phase a **Today** paragraph and a **Proposed** paragraph, then the proof below.
3. **Open questions** at the end: decisions you left to the user.

Proof you can put in a phase:

- A `review` fence with `before: Orders.oml` and `after: Orders.staged.oml` lines: the model's own list of risks, changes and new check findings. Use one per staged file.
- A `flow diff` fence with `before:`, `after:` and `action:` lines for a changed action: the flow diagram, new nodes green, removed nodes red. A `flow` fence with `module:` and `action:` shows one action as it is.
- A `callstack` fence for call order. Rows start with `+` (new), `-` (removed) or a space.
- Element links: write `[[Orders.staged.oml#Customer_Save]]` where you name an element; a click shows `oml describe`. Use the paths that `tree` and `find` print.
- Screenshots: `oml preview <module.oml> <Screen> --screenshot media/save-today.png` for the current module, the same for the staged one as `media/save-proposed.png`. Show them with a gallery:

```text
:::gallery side-by-side
![Today](media/save-today.png)
![Proposed](media/save-proposed.png)
:::
```

Other kinds: `grid` and `carousel` (one at a time). A title goes last: `:::gallery carousel [Saving a record]`. `![Label](media/clip.mp4)` alone on a line shows an image or a video. Media must be below the walkthrough's folder (png, jpg, gif, webp, svg, mp4, webm; 50 MB).

## Run it

```powershell
oml show-change specs/orders-export.md --no-open
```

Give the user the address it prints (the line with `url`). Leave it running: the page follows the file, so you can edit the walkthrough while the user reads. Write the file first and keep it under 2 MB.

## Comments come back

The user selects text, presses Comment and then Copy comments. They paste a numbered list such as `1. In "Logic", on "Trim the name": allow a single letter`. Treat each line as an instruction: change the staged module or the walkthrough, run `oml review` again, say what you did per number, and ask again before `install`.
