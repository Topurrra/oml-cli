# Traditional Web to Reactive

How Traditional Web parts map to Reactive when you rebuild a screen or module by hand with oml. oml cannot run the Service Studio converter; it builds the Reactive version from a spec.

## Plan

- Rebuild producers before consumers.
- For a big redesign, build a new Reactive app instead of a one-to-one copy.
- Keep the Traditional module until the new one is in use. Removing its UI later is a manual delete in Service Studio (rule 3): propose it to the user.
- Read the Traditional screen first: `oml describe <module.oml> <Screen>`.

## Logic

| Traditional | Reactive |
|---|---|
| Preparation | Aggregates and data actions on the screen; post-processing in `onAfterFetch` |
| Aggregate or SQL in Preparation or a screen action | A server action (SQL always on the server), called from a client action or a data action |
| Screen action (server) | Client action; server work moves to one server action it calls |
| Session variable | Client variable (visible to the user: no secrets) or data read again on the server |
| Site property read in the screen | Read it in a server action or data action |
| `CheckRole` in the screen | Server check in the server action; the client check only hides parts |
| Ajax Refresh | Nothing: widgets update when bound data changes; use `refreshData` for aggregates |
| Notify and OnNotify of web blocks | Block `events`, a `triggerEvent` node and a handler on the instance (`oml spec triggerEvent`) |
| Send Email in a screen action | Server action with `sendEmail` of a Reactive email from `emails` (`oml spec emails`) |
| Commit Transaction in screen logic | Server action |

- Every server action that a screen calls is a public endpoint: start it with identity and role checks (`oml doc security`).
- Group several server calls of one client action into one server action.

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
| Editable Table, List Box | No Reactive widget: build a Table with inputs, or a list of Checkboxes |
| Link with Method Submit or Ajax Submit | Button or Link with On Click and a client action |
| Validation (Client & Server) | Built-in Validations on the button, `<Input>.Valid` in the client action, checks again on the server |
| Screen entry and Preparation redirects | OnInitialize redirect with a `destination` |

## Data and UI rules that change

- No viewstate: screen data lives in the browser. Fetch only the fields the screen shows.
- Styles: Style Classes is an expression (`"card"`), not a plain class list.
- Layouts: Reactive layout blocks and placeholders differ (`oml doc ui-reactive`).
- Paging: locals `StartIndex` and `MaxRecords` and the Pagination block, not screen inputs.
- Dynamic sort text must come from a fixed list, never from user text that reaches SQL.

More: `oml doc ui-reactive`, `oml doc logic-rules`.
