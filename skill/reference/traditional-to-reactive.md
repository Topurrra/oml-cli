# Traditional Web to Reactive

How Traditional Web parts map to Reactive, and how `oml migrate` prepares the move. oml cannot run the Service Studio converter: `oml migrate` copies the logic and data definitions, references what must stay, and leaves a worklist of screens and blocks for you to rebuild by hand.

## The oml migrate flow

```text
oml migrate plan Orders.oml --name OrdersApp --in C:\producers --out migrate\OrdersApp
oml migrate status Orders.oml OrdersApp.oml
```

`plan` reads the old module (it must be Traditional Web) and writes, into a new folder, files you apply with `oml apply`. It changes no module. Its output lists `next`, the commands in order, and `notes`.

| File | What it is |
|---|---|
| `plan.json` | One entry per element (`stay`, `copy`, `worklist`, `manual`, with why), the wrappers, the references (`add`, `skip-ui`, `unresolved`, `builtin`) and the notes |
| `1-prepare-old.json` | Spec for the OLD module: entities, static entities and roles made public (`exposeReadOnly` stays as it is) and the wrapper actions. Absent when nothing is needed |
| `2-new-module.json` | Spec for the NEW module: `references`, `copy`, then `repoint` edits |
| `3-screens.md` | Your worklist: one checkbox per screen and block, with `screens/<Name>.txt` (`oml describe` of the original) |

Order: apply 1 to a staged copy; the person installs and publishes the old module; `oml new` a Reactive module from a template; apply 2; install and publish it; work through 3. `status` shows by name what is done, what is left, and what exists twice.

What stays and what is copied:

- Entities, static entities and roles stay in the old module (a copied role has no users). The new module references them. They are read only there.
- Server and service actions, structures, exceptions, site properties, timers, processes and REST are copied.
- Entity writes: the old module gets a public wrapper (`Order_Create`, `Order_CreateOrUpdate`, `Order_Update`, `Order_Delete`) for each entity action the copied logic calls, with the same inputs and outputs. `2-new-module.json` re-points each call to its wrapper. A screen you rebuild writes through a wrapper too; if it needs one that is missing, add it to the old module the same way and reference it.
- Producers found under `--in` are referenced by themselves; an `unresolved` reference needs its producer file (`--in`) or Manage Dependencies. Traditional UI references (web blocks, screens, themes) are left out.
- You decide: timers and processes now exist in both modules (deactivate or delete one); site property values, timer schedules and integration settings in Service Center do not follow a copy; session variables are not copied. The old module is best made a service module once its UI is gone (rule 3: propose it).

## Screens worklist

Read the original first (`screens/<Name>.txt`, or `oml describe <old.oml> <Screen>`). Rebuild producers before consumers, in a new Reactive module, not a one-to-one copy.

## Logic

| Traditional | Reactive |
|---|---|
| Preparation | One data action that calls one server action (the Preparation flow, copied: one round trip). Only independent aggregates stay screen aggregates. Post-processing in `onAfterFetch` |
| Aggregate or SQL in Preparation or a screen action | A server action (SQL always on the server) |
| Screen action (server) | Client action that calls one server action; group several server calls into one |
| Session variable | Client variable only when it holds nothing sensitive (the user can read it); else data read again on the server |
| Site property read in the screen | Read it in a server action or data action |
| `CheckRole` | Stays on the server, in the server action; the client check only hides parts |
| Ajax Refresh | Nothing: bound data updates; `refreshData` for aggregates |
| Notify and OnNotify of web blocks | Block `events`, a `triggerEvent` node and a handler on the instance (`oml spec triggerEvent`) |
| Send Email in a screen action | Server action with `sendEmail` of a Reactive email from `emails` (`oml spec emails`) |
| Commit Transaction in screen logic | Server action |

Every server action a screen calls is a public endpoint: start it with identity and role checks (`oml doc security`).

## Widgets

| Traditional | Reactive |
|---|---|
| Button, Link, Container, If, Label, Text, Placeholder, Upload, web block | Same widget |
| Container Display | Visible, or an If (an If removes the content) |
| Combo Box | Dropdown with a list, Options Value and Options Text |
| Radio Button | Radio Group with one Radio Button per choice |
| Edit Record, Show Record | Form with inputs bound to a local record variable |
| Table Records, List Records | Table or List with an aggregate source and a dynamic sort (`"dynamic": true`); the table's On Sort is Service Studio only, so tell the user |
| Expression with escaping off | Avoid; if needed, an unescaped expression block (XSS risk) |
| Editable Table, List Box | Build a Table with inputs, or a list of Checkboxes |
| Link with Method Submit or Ajax Submit | Button or Link with On Click and a client action |
| Validation (Client & Server) | Built-in Validations on the button, `<Input>.Valid` in the client action, checks again on the server |
| Screen entry and Preparation redirects | OnInitialize redirect with a `destination` |

## Data and UI rules that change

- No viewstate: fetch only the fields the screen shows.
- Style Classes is an expression (`"card"`). Layouts differ (`oml doc ui-reactive`).
- Paging: locals `StartIndex` and `MaxRecords` and the Pagination block.
- Dynamic sort text comes from a fixed list, never from user text that reaches SQL.
