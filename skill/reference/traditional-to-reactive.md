# Traditional Web to Reactive

How Traditional Web parts map to Reactive, and how `oml migrate` prepares the move. oml cannot run the Service Studio converter: `oml migrate` copies logic and data definitions, references what must stay, translates screens, web blocks and emails by code where it can, and leaves a worklist for the rest.

## The oml migrate flow

```text
oml migrate plan Orders.oml --name OrdersApp --in C:\producers --template Starter.oml --out migrate\OrdersApp
oml migrate status Orders.oml OrdersApp.oml --plan migrate\OrdersApp
oml migrate check Orders.oml OrdersApp.oml
```

`check` is the last step: the new module validates, every copied action keeps its inputs and outputs, no screen lets in someone the old one did not, and the `TODO migrate` stubs left are counted. Fix each failed check before reporting the migration done.

`plan` reads the old module (it must be Traditional Web) and writes, into a new folder, files for `oml apply`. It changes no module. Its output lists `next` (the commands in order), `notes` and `coverage` (translated, partial and worklist counts). `--template` is a Reactive template module: its layout block (`LayoutTopMenu`, `LayoutSideMenu`, else the first `Layout...`) and placeholders are given to the translation. Without it the screens have no layout block.

| File | What it is |
|---|---|
| `plan.json` | Each element and its decision, the wrappers, references, `screens` (status and todo count), `otherContent`, notes |
| `1-prepare-old.json` | Spec for the OLD module: entities, static entities and roles made public (`exposeReadOnly` stays) and the wrapper actions (also those the screens need) |
| `2-source.json` | `repoint` edits, applied to the prepared module to make `<Old>.migrate-source.oml`, a copy source (never install it) |
| `3-new-module.json` | Spec for the NEW module: `references`, then `copy` from the working copy (also SOAP and integration elements) |
| `4-screens.json` | Spec for the NEW module, applied after 3: `clientVariables`, `blocks`, `screens` as far as translated |
| `4-screens.md` | Summary table, then a section per `partial` or `worklist` element: TODO checkboxes, `screens/<Name>.json` (raw `oml export`) and `screens/<Name>.txt` (`oml describe`) |
| `css/` | One sheet per style sheet of the old module, and `css/README.md` |

Order: apply 1 to a staged copy; the person installs and publishes it; apply 2 to the prepared file; `oml new` a Reactive module from the template; apply 3, then 4; install and publish; work through 4-screens.md. `status --plan` shows what is done by name and how many of each status the new module has.

What stays and what is copied:

- Entities, static entities and roles stay in the old module (a copied role has no users); the new module references them, read only.
- Server and service actions, structures, exceptions, site properties, timers, processes and consumed REST are copied.
- Exposed REST and SOAP services stay in the old module, so their consumers keep working. `next` starts with a question for the person: copy them too? On a yes, plan again with `--copy-exposed`; the person then moves every consumer to the new URL.
- Entity writes: the old module gets a public wrapper (`Order_Create`, `Order_CreateOrUpdate`, `Order_Update`, `Order_Delete`) for each entity action that copied logic or a screen calls, with the same inputs and outputs. `2-source.json` re-points each call in copied logic to its wrapper before the copy. A write the translation missed needs a wrapper added to the old module the same way, and a reference.
- Migrate producers before consumers. Producers under `--in` are referenced by themselves; an `unresolved` one needs its file (`--in`) or Manage Dependencies. Traditional UI is not referenced, except a migrated producer's blocks (from its Reactive module, `--migrated`).
- Session variables (Reactive has none): basic and harmless, a client variable in 4-screens.json; copied server actions that read it get it as an input, passed on by callers (`2-source.json`: `addVariables`, `rewrite`, `setArgs`). Written there, sensitive or a record: the server store, entity `SessionValue` with `GetSession_<Name>`/`SetSession_<Name>` by session key and user. Else `manual` with the reason.
- Style: screens and blocks keep their sheet. A theme based on the template's gets the old CSS variables, fonts and rules for classes the widgets keep; the rest: `css/README.md`.
- Emails, SOAP, entry points: a consumed SOAP or integration element is copied; an email is translated like a screen unless its Preparation loads data (then load it in the sending server action and pass inputs); an entry point is manual (use a screen URL or a redirect screen); anything else is listed by kind and name in the notes.
- You decide: timers and processes exist in both modules (deactivate or delete one); site property values, timer schedules and integration settings in Service Center do not follow a copy. Once its UI is gone, propose making the old module a service module (rule 3).

## Screens worklist

`partial` and `worklist` elements are yours: read `screens/<Name>.txt` and the raw export, finish the TODO stubs, tick the box. Code fills a Form's record (aggregate `onAfterFetch`, input, local) and carries confirm text, icon, prompt, max length, input types, default button, image size, a Dropdown's empty entry, Radio Groups. Todos: Link URL, validation message, image URL, table sort and paging, Combo Box special lists.

## UI libraries

OutSystemsUIWeb, RichWidgets and OutSystemsChartsWeb blocks are translated by a map from their real signatures and the OutSystems docs: a block with a checked Reactive counterpart becomes it (OutSystemsUI.Card, Pagination, Columns2, ...), with inputs and placeholders renamed; loaders, the Ajax wait box and the message area are dropped; RichWidgets.Icon becomes the Icon widget; `Feedback_Message` becomes a message node. Everything else is a Todo with the map's note. `ui-libraries.md` in the plan folder lists the library elements the module uses, what each becomes and what to check (renamed static entity values, default values that differ). The new module must reference the Reactive libraries it lists before step 3.

## Logic

| Traditional | Reactive |
|---|---|
| Preparation | Aggregates stay screen aggregates. Logic with SQL or server calls becomes the data action `Preparation` (Traditional order, one request) with the aggregates it reads; assigned locals return as `<Local>Value` in `onAfterFetch`, queries the screen reads as `Preparation.<Query>List`. Other logic: `onInitialize` and an aggregate's `onAfterFetch` if provable, else the data action. Navigating: a Todo (code) |
| Aggregate or SQL in a screen action | Joins the server action below; later nodes read `<Query>_List` (`.Current.X`) and `<Query>_Count` (code). Reading a widget or multi-entity records: a stub |
| Screen action (server) | Client action; two or more server calls and the Assigns and Ifs between them become one server action `<Screen>_<Action>` with the screen's access check (`AccessDenied`) (code) |
| Session variable | A client variable when harmless (the user can read it), else read on the server (above) |
| Site property read in the screen | `ServerValues.X`, an output of the data action `ServerValues`; aggregates keep `Site.X`; a secret one is a Todo (code) |
| Server function in a screen | A client copy, same name (code); with a query, server call, `Site`, `Session` or role check: a Todo |
| `Get<Static>(id).<Static>.<Attr>` in a screen | The value, or a client function `Get<Static><Attr>(Id)` (code) |
| `CheckRole` | `Check<Role>Role()` in the screen is `ServerValues.Has<Role>Role` (code); it only hides parts, the server action checks again |
| Ajax Refresh | Nothing: bound data updates; `refreshData` for the aggregates behind a refreshed table or list the export names, else a Todo (code) |
| Notify and OnNotify of web blocks | The block's `Notify` event (input `Message`) fired by `triggerEvent`, handled on the instance (code) |
| JavaScript of a screen or block | A script of the module that it requires (code); jQuery and `osjs` code must be rewritten |
| Send Email in a screen action | Server action with `sendEmail` of a Reactive email (`oml spec emails`) |
| Commit Transaction in screen logic | Server action; a Todo, like other page-only System actions (`RunJavaScript`) and unmapped UI library actions |
| Download in a screen action | Download node (code) |
| UI flow OnException | The flow's `onException` (code); not if the template's flow has one or a screen is missing |

Every server action a screen calls is a public endpoint: check identity and roles in it (`oml doc security`).

## Widgets

| Traditional | Reactive |
|---|---|
| Button, Link, Container, If, Label, Text, Placeholder, Upload, web block | Same widget |
| Container Display | Visible (the translator sets it), or an If, which removes the content |
| Layout HTML Table, Width, Align, `InWebBlockPreview`, Example | The translator: HTML elements, CSS and classes; preview and design-time parts go |
| Combo Box | Dropdown with a list, Options Value and Options Text; a Source Entity: an aggregate of it (code) |
| Radio Button | Radio Group of the variable, one Radio Button per choice (code) |
| Edit Record, Show Record | Form; on an aggregate's whole record its inputs bind to that record, else to a local record variable |
| Table Records, List Records | Table or List on the aggregate. List_Navigation, List_Counter and List_SortColumn are translated by code (OutSystems UI Pagination, header sort, locals, handlers) |
| Expression with escaping off | Avoid; else an unescaped expression block (XSS risk) |
| Editable Table, List Box | Build a Table with inputs, or a list of Checkboxes |
| Link with Method Submit, Ajax Submit or Ajax | Button or Link with On Click and a client action |
| Link to an External URL | HTML element `a` with `href`; a button calls `RedirectToURL` (code) |
| Validation (Client & Server) | Built-in Validations on the button, `<Input>.Valid` in the client action, checks again on the server |
| Screen entry and Preparation redirects | OnInitialize redirect with a `destination`; an External URL is `RedirectToURL`; `AddFaviconTag` goes |

## Data and UI rules that change

- No viewstate: fetch only the fields the screen shows.
- Style Classes is an expression (`"card"`); the translator quotes Traditional classes. Layouts differ (`oml doc ui-reactive`).
- Dynamic sort text comes from a fixed list, never from user text that reaches SQL.
