# Spec reference

This file belongs to the `outsystems-oml` skill. It lists every spec key that `oml apply` accepts.

Every top-level key is optional. The parser accepts JSON comments but rejects trailing commas.
Use the documented camelCase spelling, especially inside raw node, widget, and platform objects.
Typed spec records accept case-insensitive property names. Raw object key checks can be case-sensitive.

Creation lists reuse existing names. Listing an existing element: new parts are added under the same rules as addParts; changed settings are refused.
New parts are attributes, static records, action inputs, outputs and locals, screen or block inputs, locals, aggregates, data actions, actions, events and event inputs, REST methods, and lifecycle handlers (such as `onParametersChanged`) the screen or block does not set yet.
A refused setting names the key and the edit op to use; settings of roles, site properties, timers, REST methods and module variables are compared too. A supplied `description` is applied, and an input's `mandatory` is set (rule 4 decides).
An existing flow is compared by node names and node count only (comments do not count). UI settings are replaced (rule 3), and the report's `changed` lists them: a different `styleSheet` of a screen, block, email or theme, a text `title` of a screen, and new `requiredScripts`. Widgets, placeholders, layout, processes and other email settings are not compared: the report's `warnings` name what was not applied and the op that changes it (`replaceWidgets`, `addWidgets`). A restated screen or block aggregate is compared by `sources`, `maxRecords`, `fetch`, `timeout` and `onAfterFetch`; change filters, joins and sorts with edits.
An omitted `public` keeps the element's setting. `"public": true` makes an existing element public (also images, scripts and resources; a theme only through `edits.set`); `false` on any existing public element is refused (rule 4). The report's `changed` lists each existing element made public, by any path (`public of entity Sector`). A server action that becomes public, new or existing, adds a `PublicServerAction` new warning: confirm it with the user (rule 7). Service actions get no warning, because other modules call them by design; exposed REST methods have no public setting. A creation-list spec applied again on its own result saves and changes nothing.
Rule 4 is checked on every path (creation lists, `addVariables`, `addParts`, `set`): on an existing public element apply refuses a new mandatory input, an input made mandatory, a type change, any input change on a public client action or Reactive or Mobile block or screen, a new output except on server and service actions, and a type change of an event input on a public block.
Edits are ordered operations. Repeating them is not an idempotent desired-state update.

Field notation in this reference names a nested key or an accepted value:

```text
kind: aggregate
kind: javaScript
kind: sql
isDefault: true
preparation: [ ...flow nodes... ]
referenceAliases: { "OutSystemsMaps.Type": "MapsType" }
set.properties
variable: expression
template: list / detail
template: list|detail
```

Use the complete JSON examples below when writing a spec.
For example, variable: expression means a variable key whose value contains an OutSystems expression.
A tree Api/Method lookup means the Api/Method path used by the tree command.

### Spec keys

One row per top-level key: what it creates or changes, and the section of this file that describes it. `oml spec` prints this list; `oml spec <key>` prints the section.

| Key | What it creates or changes | Section |
|---|---|---|
| `module` | Description, JavaScript, PNG icon, user provider and tenancy flags, existing default theme. | Module settings |
| `entities`, `structures`, `exceptions` | Data model. New entities get an `Id`. Entity `indexes`, identifier and options. | Data model |
| `staticEntities` | Static entities with records. | Static entities |
| `references`, `referenceAliases`, `removeUnusedDependencies` | Elements from producer modules, local names for them, and removal of unused ones. | References and aliases |
| `serverActions`, `serviceActions`, `clientActions` | Actions with inputs, outputs, locals and a `flow`. | Logic and flows |
| `tidyFlows` | Move nodes in existing flows selected by element paths or name patterns. | Logic and flows |
| `clientVariables`, `sessionVariables`, `systemEvents` | Module state and calls from platform lifecycle events. Module kind limits apply. | Module variables and system events |
| `onException`, `globalExceptionHandler`, flow `exceptionHandler` | Separate exception paths and a Reactive global handler. | Exception handling |
| `flow` nodes `aggregate` | Sources, joins, filters, sorts, group by (with group filters and sorts), calculated attributes, fetch, cache. | Queries |
| `flow` node `sql` | SQL statements with typed parameters and outputs. | SQL |
| `restClients`, `restServices` | Consumed and exposed REST APIs. | REST |
| `timers`, `roles`, `processes`, `siteProperties` | Platform elements, screen `access`. | Platform elements |
| `screens`, `blocks` (Reactive, Mobile) | Screens, blocks, aggregates, actions, events, widgets. | Reactive UI and Mobile UI |
| `events`, `triggerEvent`, `onParametersChanged` | Block events: declare on a block, fire with a node, handle on each block instance. | Block events |
| `emails`, flow node `sendEmail` | Reactive emails and sending them from a server action. | Emails |
| `screens`, `blocks` (Traditional Web) | Web screens with `preparation` and web widgets. | Traditional Web UI |
| `layout`, `placeholders`, `template` | Layout blocks and generated list or detail screens. | Layouts and scaffolding |
| `edits` | Changes to existing elements. | Edits |
| `edits` with `delete` | Deletion of elements created earlier in the same spec. Existing elements are refused (see rules.md). | Deletion |
| `copy` | Clipboard copy from read-only modules; explicit dependencies; IPP checks. | Copy, translations, resources, and folders |
| `locales`, `translate`, `translations` | Locales and module resource translations; file import where the host supports it. | Copy, translations, resources, and folders |
| `keepComments` | Keep comments in `styleSheet` and `javaScript` (removed by default). | Comments in styleSheet and javaScript |
| `images`, `scripts`, `themes`, `resources`, `folders`, `requiredScripts` | Content files, styles, folders, and screen/block script requirements. | Copy, translations, resources, and folders |

### Module settings

`module` changes supported module settings during the data phase. Every field is optional.
It does not rename the module or change its key or activation code.

```json
{
  "module": {
    "description": "Customer portal",
    "javaScript": "console.log('portal');",
    "icon": "assets/portal.png",
    "isUserProvider": false,
    "isMultiTenant": false,
    "defaultTheme": "PortalTheme"
  }
}
```

`description` and `javaScript` are literal strings. The flags are booleans.
`icon` reads PNG bytes from a file path: relative to the spec's folder when the file is there, else to the CLI working directory.
`defaultTheme` is the exact name of an existing theme, including themes from module references.
The CLI selects the Traditional Web or Reactive/Mobile theme property for the module kind.
Missing or ambiguous themes, unsupported fields (including `dbms`, and `styleSheet`: a module has no style sheet, so use a theme), invalid icons, and model refusals are problems.
Any problem prevents saving. `inspect.module` reports the values and icon byte size after reopening.

### Data model

| Scope | Keys | Meaning |
|---|---|---|
| Root | `entities`, `structures` | Arrays of record definitions. |
| Record | `name`, `attributes`, `public`, `description` | Name; attribute array; public (new records: model default, private); optional description. |
| Attribute | `name`, `type`, `length`, `decimals`, `mandatory`, `default`, `description`, `deleteRule` | Type defaults Text; mandatory defaults false; default is an expression. Identifier delete rule accepts Protect, Delete, or Ignore; defaults Protect. |
| Entity | `identifier`, `autoNumber`, `labelAttribute`, `orderByAttribute`, `isActiveAttribute`, `exposeReadOnly`, `updateBehavior` | New entities; on an existing entity they must equal its values. Attribute names, booleans, and `changedAttributes` or `allAttributes`. |
| Entity | `indexes` | Array of `{ "name", "attributes", "unique" }`. Name defaults to `IX_<A>_<B>`; unique defaults false. |
| Root | `exceptions` | Array of user exception names. |
| Variable | `name`, `type`, `mandatory`, `default`, `description` | Shared variable shape. Type defaults Text. An input without `mandatory` is mandatory, as in Service Studio; set `"mandatory": false` for an optional input. Process inputs and extension parameters are optional unless set. Locals ignore `mandatory`. |

New entities get auto-number `Long Integer` `Id`, assigned as `IdentifierAttribute`. On a new entity, an attribute
named `Id` in the spec sets the type of that `Id`; a type other than Integer or Long Integer gets no auto number.
They need at least one other attribute. Entity attributes require basic or Identifier types.
Structures can also contain entity, structure, and list types.

`identifier` makes one of the entity's attributes the identifier and removes the default `Id`; the identifier becomes mandatory.
`autoNumber` sets auto number on the identifier (default: true for `Id`, false otherwise; only Integer or Long Integer).
Entities created by oml get Expose Read Only Yes and Update Behavior All Attributes (the model API defaults on 11.55.93).
The OutSystems docs list No and Changed Attributes as the defaults, so entities made in Service Studio may differ; set both keys when it matters.
With `changedAttributes`, an update writes only the attributes assigned after the record was read; a record built in logic
or copied whole updates nothing. Do not set `exposeReadOnly: false` on a public entity unless the user asks (rule 7).
Index column order is the `attributes` order. Index names use ASCII letters, digits and `_`. Unknown or repeated attributes are problems.
On an existing entity, indexes and attributes are added; an existing index with the same name is reused only when it is equal.
New non-unique indexes are fine. A unique index on an existing entity adds a `UniqueIndexOnExistingEntity` new warning: publish fails if production has
duplicate values, so ask the user to check for duplicates first.
The other entity keys must match the existing values, or apply refuses them (rule 5), also through `edits.set`.

Types use Service Studio names: `Text`, `Integer`, `Long Integer`, `Decimal`, `Boolean`, `Date`, `Time`, `Date Time`,
`Email`, `Phone Number`, `Currency`, `Binary Data`, `<Entity> Identifier`, `List of <type>`, or a record name.
An expression's text literal must include its OutSystems quotes:

```json
{
  "entities": [{ "name": "Customer", "attributes": [
    { "name": "Name", "length": 100, "mandatory": true },
    { "name": "Email", "type": "Email", "length": 250 }
  ], "indexes": [{ "attributes": ["Email"], "unique": true }] }],
  "structures": [{ "name": "OrderDto", "attributes": [
    { "name": "Caption", "default": "\"Hello\"" }
  ] }],
  "exceptions": ["InvalidOrder"]
}
```

### Static entities

| Scope | Keys | Meaning |
|---|---|---|
| Root | `staticEntities` | Static entity definitions. |
| Static entity | `name`, `attributes`, `records`, `public`, `description` | Attributes use the data model shape. |
| Record | `identifier`, `label`, `order`, `isActive`, `values` | Defaults: label = identifier; order = assigned ID; isActive = true. |

The CLI creates mandatory Integer `Id`, Text `Label` of length 50, Integer `Order`, and Boolean `Is_Active`.
All four default attributes are mandatory.
Named attribute definitions can customize defaults during creation. All record types exist before attribute type resolution.
IDs use the first unused positive integer. Existing entities must have `EntityKind == Static`.
Existing attributes and records are reused without retyping or changing values.
`values` maps real attribute names to OutSystems expressions. It cannot override `Id`.
Use `<StaticEntity> Identifier`, such as `Status Identifier`, and record expressions such as `Entities.Status.Active`.
`inspect.staticEntities` reports record identifiers and stored expressions. See `tests/static.json`.

```json
{
  "staticEntities": [{ "name": "Status", "attributes": [{ "name": "Color", "length": 20 }],
    "records": [{ "identifier": "Active", "label": "Active", "order": 1, "isActive": true,
      "values": { "Color": "\"#16a34a\"" } }]
  }]
}
```

### References and aliases

| Scope | Keys | Meaning |
|---|---|---|
| Root | `references` | Import public elements before resolving types and building logic. |
| Import | `producer`, `elements` | Producer path; exact name array, or `"*"` for all supported public elements. |
| Root | `referenceAliases` | Map qualified referenced names to new local names before creating data. |

```json
{
  "references": [{ "producer": "C:\\exports\\Producer.oml",
    "elements": ["Customer", "GetCustomerById", "CustomerCard", "OrderDto", "SupportRole"] }],
  "referenceAliases": { "OutSystemsMaps.Type": "MapsType" }
}
```

To import every supported public element:

```json
{ "references": [{ "producer": "Producer.oml", "elements": "*" }] }
```

Supported kinds include entities, structures, server/service/client actions, blocks, screens, roles, and themes.
User exceptions require a public shareable contract; Service Studio 11.55.92 does not expose one.
Names must be exact and unique in the producer module. Relative paths use the CLI working directory.
Producer files stay read-only. Missing elements extend an existing reference; imported global keys are reported as `existing`.
Existing signatures are not refreshed. Any import refusal prevents saving.
`inspect.references[].elements` lists imported names and kinds.

Use the producer version deployed in the target environment. A mismatch can cause publish failure or stale references.
After publishing the producer, refresh references through Service Studio.
The CLI does no tenant lookup, download, module-signature resolution, or IPP bypass.
See reference/model-api.md section 13.1 for the API contract.

Names can use `Module.Name` for actions, blocks, screens, entities, structures, roles, and types.
Examples include `CoreServices.Customer Identifier` and `List of CoreServices.Customer`.
Plain names prefer owned elements, then a unique reference. Ambiguity reports qualified candidates and refuses the change.
The module can rename a second same-named reference to `Customer2`; use its actual local name.

Aliases only rename writable referenced elements added by this spec. Renaming a reference already present in the source is refused. They do not change the producer module or replace the reference.
A repeated alias reuses the alias name. Later specs and expressions must use that name.
Refused or silently renamed aliases become problems.


#### Comments in styleSheet and javaScript

`apply` removes comments from every inline `styleSheet` and `javaScript` value before it builds anything (a value read from a file keeps them, see Code from files):

- CSS: `/* ... */` comments are removed. `/*! ... */` comments stay (use them for license notices).
- JavaScript: `/* ... */` comments and lines that start with `//` are removed. A `//` after code on the same line stays, because it can be part of a URL or a regular expression.
- Text inside quotes is never changed.
- The report shows `commentsRemoved` (number of characters).
- Set `"keepComments": true` at the top of the spec to keep all comments.

#### Code from files

`javaScript`, `styleSheet`, `sql`, `expression` and `value` can be `{ "file": "relative/path" }` instead of a string. The value is the file's text: read as UTF-8 (or UTF-16 with a BOM), the BOM removed, nothing else changed, so quotes, backslashes, template literals and unicode stay as written. Comments in a file are kept (`keepComments` is only for inline text). Service Studio stores line ends as LF.

```json
{ "clientActions": [{ "name": "RenderChart", "flow": [
    { "kind": "javaScript", "name": "Draw", "javaScript": { "file": "code/chart.js" } } ] }],
  "blocks": [{ "name": "Chart", "styleSheet": { "file": "code/chart.css" } }] }
```

- The path is relative to the spec file's folder. With several specs (`oml apply` with more spec files, or `oml run`), each spec reads its own files.
- The file must be inside the spec's folder or below it. An absolute path, a drive, a `:`, a `..` that leaves the folder, and a symbolic link or junction on the way to the file are refused. A hard link cannot be told from a normal file and is not detected.
- Refused too: a folder, a Windows device name as the base name before the first dot (`NUL`, `nul.js`, `CON`, ...), a file over 2 MB, text that is not valid UTF-8 or UTF-16, and a NUL character (U+0000).
- Keys that start with `_` or `$` are notes; a file value there is not read.
- `oml spec check` reports these problems and a file value on another key. Apply refuses the same before it loads the module.
- Other path keys are different: `images`, `scripts` and `resources` (`file`), `module.icon`, `copy` (`from`) and `translations` (`import`) take a plain path, not `{ "file": ... }`. A relative path there means a file in the spec's folder when one exists, else in the CLI working folder; an absolute path is allowed. When both folders have the file with different content, the spec's is used and `warnings` says so. `references` paths use the working folder only. A theme's `styleSheet` takes CSS text, a plain path or `{ "file": ... }`.

#### Remove unused dependencies

This is the same as **Remove Unused Dependencies** in the Service Studio module context menu.

```json
{ "removeUnusedDependencies": true }
```

- It removes unused referenced elements and references to modules that become empty. Only references to producer modules are removed; elements of this module are never touched. Referenced elements hold no data in this module, so the production rules allow this.
- It runs after all other spec keys, so elements that the same spec starts to use are kept.
- The report has `removedDependencies`: `modules` (modules removed completely) and `elements` (removed elements per module that is still referenced).
- Running it again on the result removes nothing.
- Like every `apply`, the result is a staged file, and nothing is saved when new errors appear.
### Module variables and system events

`clientVariables` and `sessionVariables` are additive arrays. Each item has `name`, `type` (default `Text`), and optional `default` expression. Existing names are reused; a different `type` or `default` is refused. New variables exist before any action expression is bound.

```json
{
  "clientVariables": [{ "name": "Status", "default": "\"Ready\"" }],
  "clientActions": [{ "name": "Init", "flow": [
    { "kind": "assign", "assign": { "Client.Status": "\"Started\"" } }
  ] }],
  "systemEvents": { "onApplicationReady": "Init", "onApplicationResume": "Init" }
}
```

Reactive/Mobile supports client variables. Traditional Web supports session variables, read and written as `Session.Name`. Service Studio 11.55.92 refuses session variables in Reactive and Service modules. Optional `isSecure` is accepted only when the installed variable API exposes that property. This build does not; supplying it reports a problem.

`systemEvents` maps event names to action names. `onApplicationReady` and `onApplicationResume` call a client action in Reactive/Mobile. `onSessionStart` and `onBeginWebRequest` call a server action in Traditional Web. Qualified `Module.Action` names work for referenced actions. The CLI creates a Start, Execute and End body for each event. The action must need no mandatory arguments. An existing event that already calls the same action is reused; another one is refused: edit its flow instead. Unsupported module kinds, wrong action types and unknown event names report problems and prevent saving. Reactive application events need a Screen to execute at runtime.

### Exception handling

Use `exceptionHandler` in any supported action `flow`, including screen and data actions:

```json
{
  "serverActions": [{ "name": "Work", "flow": [
    { "kind": "comment", "text": "Main path ends before recovery" },
    { "kind": "exceptionHandler", "name": "OnDatabase", "exception": "DatabaseException",
      "abortTransaction": true, "logError": true, "next": "End" }
  ] }]
}
```

Handlers are separate entry points. Start and ordinary nodes never connect to a handler or comment. A missing `next` continues to the next node in that handler path. The path ends before the next handler. `next: "End"` creates a separate End for each handler. Handler paths must not join the main path or another handler path. Place main-path nodes before the first handler. Use unique names for explicit End nodes. Only one handler per exception is allowed in an action. Use action inputs and locals for recovery data; main-path node outputs are outside the handler path.

`exception` accepts API names such as `AllExceptions` and `DatabaseException`, a local user exception, or an available `Module.Exception`. It is required. `abortTransaction` and `logError` use the installed model defaults when omitted.

To add a handler to an existing action without changing Start:

```json
{ "edits": [{ "op": "addExceptionHandler", "action": "Work", "flow": [
  { "kind": "exceptionHandler", "name": "OnError", "exception": "AllExceptions", "next": "End" }
] }] }
```

The flow must start with `exceptionHandler`. `insertNodes` also accepts handler nodes after the inserted main path. All normal connector and validation rules still apply.

`onException` creates a UI-flow exception action (Reactive, Mobile and Traditional Web). In Traditional Web the handler path cannot end with `end`; end it with a `destination` to a screen. `name` defaults to `OnException`, `flow` defaults to `MainFlow`, `locals` uses the shared variable shape, and `nodes` holds flow nodes. It has no Start. It must contain at least one exception handler:

```json
{
  "onException": { "name": "HandleError", "flow": "MainFlow", "nodes": [
    { "kind": "exceptionHandler", "name": "OnAllErrors", "exception": "AllExceptions" },
    { "kind": "message", "message": "\"Something went wrong\"", "type": "error" }
  ] },
  "globalExceptionHandler": "MainFlow/HandleError"
}
```

One OnException action is allowed per UI flow. An existing same-named action is reused. `globalExceptionHandler` accepts an existing OnException action name or path. It also accepts a module client action name. For a client action, the CLI creates `MainFlow/Handle<ActionName>` with an AllExceptions handler that calls it. An occupied MainFlow handler slot reports a problem. `globalExceptionHandler` requires a Reactive/Mobile module.

### Additional flow nodes

These use the installed typed node API and the normal expression-last order:

| Kind | Keys | Limits on Service Studio 11.55.92 |
|---|---|---|
| `comment` | `text`, optional `isReminder` | No connectors. Text is plain text. A spec name does not persist because the node has no Name or Label. |
| `message` | `message`, `type`: `info`, `success`, `warning`, `error` | Message is an expression. Verified in client actions. |
| `download` | `fileContent`, `fileName`, `mimeType`, optional `saveToDisk` | Expressions; terminal node. Verified in Traditional Web screen actions. |
| `excelToRecordList` | `fileContent`, `sheetName`, `dataType`: `List of <record type>` | Expressions plus list type. Verified in server actions. |
| `recordListToExcel` | `recordList`, `attributeSelection`: array of field paths such as `Label` | At least one field is required. Selections bind after the list expression. |
| `sendEmail` | `email`, `from`, `to`, optional `cc`, `bcc`, `logContent` (Traditional Web only), `args` | Reactive: server actions only. Address values are expressions; args map email input names to expressions. See Emails. |

Missing elements, unsupported node scopes and model refusals prevent saving. These checks verify the saved model; they do not send email, process a workbook, or download a file at runtime.

### Emails

`emails` creates Reactive and Mobile emails in a UI flow (default `Emails`, created if missing):
`[{ "name", "subject", "flow", "inputs", "locals", "widgets", "styleSheet", "description" }]`. `subject` is required and is an expression.

- An email sees only its own inputs and locals. Load data in the server action and pass it as inputs.
- Allowed widgets: Container, Expression, If, Image, Link, List, TableRecords (with HeaderCell and RowCell), Text. Apply refuses any other widget, at any depth.
- Keep the CSS small. Mail clients clip large mails. oml creates no email theme, so the flow uses the module's default theme. In Service Studio, give the email flow a light theme, not the OutSystems UI theme.
- Traditional Web: `emails` is refused. Create the Email screen in Service Studio; `sendEmail` can send it.

`sendEmail` sends an email from a server action. In Reactive and Mobile, the model API cannot place a Send Email node in any server action. oml creates Service Studio's own send action for the email once, `Send<Email>`, with the inputs `From`, `To`, `CC`, `BCC` and the email's inputs. Each `sendEmail` node becomes a call to it. An email input named From, To, CC or BCC is refused.

```json
{ "emails": [{ "name": "Welcome", "subject": "\"Welcome \" + Name", "inputs": [{ "name": "Name", "type": "Text" }],
    "widgets": [{ "type": "Expression", "value": "\"Hello \" + Name" }] }],
  "serverActions": [{ "name": "SendWelcome", "inputs": [{ "name": "To", "type": "Email" }, { "name": "Name", "type": "Text" }],
    "flow": [{ "kind": "sendEmail", "email": "Welcome", "from": "\"team@example.com\"", "to": "To", "args": { "Name": "Name" } }] }] }
```

Sending only queues the mail. SMTP is set up in Service Center. Use a From address of your own mail domain. `oml preview <module> <Email>` renders an email; `describe` shows its subject and widgets. See `recipes/send-email.jsonc`.

### Logic and flows

`apply` automatically lays out every new logic or BPT process flow and every existing flow where
the spec inserts or removes nodes. It moves the nodes of those flows, including exception handler
paths. Other existing flows keep their positions. Comment nodes always keep their positions.
The main path runs down; If false runs down and true runs right, matching the sample Studio flow.
Switch branches use separate columns, loop bodies run to the right, and exception handlers sit beside
the main flow. Shared joins and End nodes sit below their incoming branches. Layout changes positions
through the typed Model API; it keeps the connectors and expressions.

#### tidyFlows

Use the root key `tidyFlows` to move the nodes of selected existing flows without changing their logic:

```json
{ "tidyFlows": ["Fx*", "Showcase/CopyText"] }
```

Each entry is an element path to an editable action, preparation, process or other logic flow,
or a case-sensitive name/path pattern with `*`. `"*"` selects all local editable flows, including
nested flows. Use the qualified paths from `oml tree` when names are ambiguous. Empty entries,
paths to non-flow elements, and patterns with no matches are errors. This is an explicit request
to replace the selected flows' node positions. Comments stay fixed; nodes avoid their default bounds.
Layout uses Studio's default `(3196, 800)` origin, `2660` spacing and `914 x 928` node bounds,
with extra room for labels. The typed API does not expose rendered text bounds, so very long labels
or expanded comments can extend beyond the reserved space.

#### Actions and flow nodes

| Scope | Keys | Meaning |
|---|---|---|
| Root | `serverActions`, `serviceActions`, `clientActions` | Action arrays. Module kinds must support the action type. |
| Action | `name`, `public`, `function`, `description`, `inputs`, `outputs`, `locals`, `flow` | Public and function default false. Variables use the shared variable shape. |
| Every node | `kind`, `name`, `next` | Kind required; name defaults to kind, except execute defaults to the callee name. Next defaults to the following node, then End. |
| Assign | `assign` | Map variable expressions to value expressions. |
| If | `condition`, `true`, `false` | Boolean expression and required branch names. |
| For Each | `recordList`, `cycle`, `next` | Source list, required body target, and target after the loop. |
| Switch | `cases`, `otherwise` | Ordered Boolean cases; otherwise required. Each case has `condition` and `next`. |
| Execute | `action`, `args` | Action name; parameter-name-to-expression map. |
| Refresh Data | `dataSource`, `args` | Aggregate or data action in the owning screen/block; optional data-action inputs. |
| Destination | `screen`, `args` | Navigation target and inputs; terminates the path. |
| Raise Exception | `exception`, `message` | Exception reference and message expression; terminates the path. |
| JSON nodes | `dataType`, `jsonString`, `variable` | Reflected properties for jsonDeserialize / jsonSerialize, where supported by the installed node. |
| JavaScript | `javaScript`, `inputs`, `outputs`, `args` | Raw script, typed variables, and input expressions. |

Node kinds include `assign`, `if`, `forEach`, `switch`, `aggregate`, `sql`, `execute`, `refreshData`, `destination`,
`raiseException`, `end`, `jsonDeserialize`, `jsonSerialize`, and `javaScript`.
Other installed kinds resolve as `I<Kind>Node`. Other node keys use installed setters or writable properties.
Check them before use:

```powershell
oml api "OutSystems.Model.Logic.Nodes.I*Node"
```

Start and End are created when needed. An End named `End` reuses the implicit End.
On Service Studio 11.55.92, switch otherwise must differ from case targets. Use a separate End or branch node.
Assignment pairs are applied in object order. Execute selects client/server node types for ordinary, service, CRUD, or referenced actions.
For Each expressions use the source list's `.Current` and `.CurrentRowNumber`, never the loop label.
All action signatures exist before flows. Generated CRUD actions can be called within the same apply run.
The model can rename GetOrders to `GetOrdersByCustomerId` after source/filter assignment.
The CLI restores aggregate names before binding downstream expressions.

```json
{
  "serverActions": [{ "name": "CreateOrderFor",
    "inputs": [{ "name": "CustomerId", "type": "Customer Identifier" }, { "name": "Total", "type": "Decimal" }],
    "outputs": [{ "name": "OrderId", "type": "Order Identifier" }],
    "locals": [{ "name": "Rec", "type": "Order" }],
    "flow": [
      { "kind": "if", "name": "ValidTotal", "condition": "Total >= 0", "true": "Prepare", "false": "Invalid" },
      { "kind": "assign", "name": "Prepare", "assign": { "Rec.CustomerId": "CustomerId", "Rec.Total": "Total" } },
      { "kind": "execute", "action": "CreateOrder", "args": { "Source": "Rec" } },
      { "kind": "assign", "assign": { "OrderId": "CreateOrder.Id" }, "next": "End" },
      { "kind": "raiseException", "name": "Invalid", "exception": "InvalidOrder", "message": "\"Total must be >= 0\"" }
    ]
  }]
}
```

This example requires Customer, Order with CustomerId/Total, and InvalidOrder in the source or the same spec.

```json
{ "kind": "refreshData", "dataSource": "JoinedOrders" }
```

JavaScript validation does not run the script or test browser behavior.
Scope DOM work to a block root ID, respect reduced motion, and clean up work when removed.
Avoid global listeners.

```json
{ "kind": "javaScript", "name": "Animate", "javaScript": "console.log($parameters.Caption);",
  "inputs": [{ "name": "Caption", "type": "Text" }], "args": { "Caption": "\"Ready\"" } }
```

### Queries

Use these keys on a flow aggregate or screen aggregate:

| Key | Shape or rule |
|---|---|
| `name` | Aggregate name. Existing aggregates remain unchanged through creation lists. |
| `sources` | Array of entity names. |
| `joins` | Array with type, left, right, condition. Endpoints name entries in sources. |
| `filters` | Array of Boolean expressions. |
| `sorts` | Array with attribute expression, optional descending and dynamic booleans. |
| `groupBy` | Array of expressions, or `{ "attribute", "as" }` objects to name the group. |
| `aggregates` | Output definitions with name, function, attribute. Functions: Count, Sum, Avg, Min, Max; case-insensitive. |
| `calculated` | Pre-group attributes with name and expression. |
| `groupFilters`, `groupSorts`, `groupCalculated` | After grouping: filters (HAVING), sorts, and calculated attributes. They use group and aggregate names. |
| `maxRecords`, `startIndex` | Number or expression string. |
| `fetch` | Screen and block aggregates: `atStart` (default) or `onDemand` (runs only on refreshData). |
| `onAfterFetch` | Screen and block aggregates: name of a screen action of the same screen or block to run after each fetch. |
| `cacheInMinutes` | Whole number of minutes the server keeps the result. Aggregates in server actions only; screen and block aggregates refuse it. |
| `timeout` | Whole number of seconds; any aggregate. |

`joins.type`: `only with` = Inner; `with or without` = Left; `with` = Full.
Joins are explicit. Even Count needs an attribute, such as `Order.Id`.
Grouped output comes from groupBy and aggregates. Output names must be unique; automatic renames become problems.
A group's name (ASCII letters, digits and `_`) is the attribute name of a plain path (`Order.CustomerId` gives `CustomerId`). For any other
expression it is the first and last names in it (`Year(Order.PlacedOn)` gives `YearPlacedOn`); set `as` for a better name.
Read grouped rows by name: `GetTotals.List.Current.BirthYear`. A dynamic sort is a Text expression evaluated at run time,
such as a local `SortBy` holding `"Order.Total DESC"`; a plain sort names an attribute.
The CLI binds source and join references before query expressions.

```json
{
  "kind": "aggregate", "name": "JoinedOrders", "sources": ["Order", "Customer"],
  "joins": [{ "type": "only with", "left": "Order", "right": "Customer", "condition": "Order.CustomerId = Customer.Id" }],
  "filters": ["Order.Total >= 0"], "sorts": [{ "attribute": "Order.CustomerId", "descending": true }],
  "groupBy": ["Order.CustomerId"],
  "aggregates": [{ "name": "Total", "function": "Sum", "attribute": "Order.Total" }],
  "calculated": [{ "name": "Label", "expression": "Customer.Name" }], "maxRecords": 50
}
```

```json
{
  "kind": "aggregate", "name": "ByYear", "sources": ["Order"], "cacheInMinutes": 10,
  "groupBy": [{ "attribute": "Year(Order.PlacedOn)", "as": "Year" }],
  "aggregates": [{ "name": "Orders", "function": "Count", "attribute": "Order.Id" }],
  "groupFilters": ["Orders > 1"], "groupSorts": [{ "attribute": "Year", "descending": true }]
}
```

Change an existing aggregate with the edit ops `addToAggregate`, `set` and `delete` (see Edits).

Tree includes join endpoints/types, aggregate functions, refresh/navigation/event ref, and argument parameter ref.
Switch connector targets come from Link. Tree uses node labels when Name is absent.

### SQL

SQL nodes work where the installed model allows `ISQLNode`, including server flows and Traditional Web Preparation.
They also work in `edits.insertNodes`.

Statements containing DELETE, UPDATE, TRUNCATE, DROP, ALTER, or MERGE as whole SQL keywords are refused, case-insensitively. String literals, quoted identifiers, and comments are ignored. SELECT and INSERT are allowed. This applies to new, edited, and copied SQL nodes. SQL that was already in the module and is not changed is left alone. There is no override.

| Key | Meaning |
|---|---|
| `kind`, `name`, `sql` | Use sql as kind; SQL statement string. |
| `params` | Array with name, type, value, optional expandInline. Basic/Identifier types; default Text. Value is required. |
| `outputs` | Entity/structure names in SQL column order. |
| `maxRecords` | Number or expression string. |

```json
{ "kind": "sql", "name": "GetTopCustomers",
  "sql": "SELECT {Customer}.* FROM {Customer} WHERE {Customer}.[Name] LIKE @Pattern",
  "params": [{ "name": "Pattern", "type": "Text", "value": "\"%\" + Search + \"%\"", "expandInline": false }],
  "outputs": ["Customer"], "maxRecords": 50 }
```

Read rows as `GetTopCustomers.List.Current.Customer.Name`. Structure outputs use their structure name in the same position.
The CLI creates inputs and ordered outputs before statements, arguments, and downstream expressions.
`expandInline` uses the installed API property. True inserts raw SQL; use only trusted or sanitized values.
Model validation does not execute SQL or verify database-specific results.

### REST

Export exposed API documentation with `oml openapi`; see [command details](commands.md#openapi).

| Scope | Keys | Meaning |
|---|---|---|
| Root | `restClients`, `restServices` | Consumed and exposed REST integrations. |
| Integration | `name`, `baseUrl`, `actions` | Optional base URL; method definitions. |
| Method | `name`, `method`, `url`, `inputs`, `outputs`, `flow` | HTTP verb defaults GET. Exposed methods support action flows. |
| Parameter | `name`, `type`, `location`, `headerName`, `mandatory` | Type defaults Text; mandatory defaults false, except URL inputs. |

Input locations: url, query (default), header, body. Output locations: body (default), header.
URL inputs need matching `{Name}` placeholders and become mandatory.
Consumed query inputs append `Name={Name}` unless the placeholder exists; exposed query inputs use Receive In = URL without a path token. Headers use the wire name from headerName.
Consumed body formats: Text = plain text; Binary Data = binary; structure/list = JSON.
Use one body parameter. Wrap multiple fields or other scalar types in a structure.

```json
{
  "structures": [{ "name": "ForecastDto", "attributes": [{ "name": "Temperature", "type": "Decimal" }] }],
  "restClients": [{ "name": "Weather", "baseUrl": "https://api.example.com", "actions": [
    { "name": "GetForecast", "method": "GET", "url": "/forecast/{City}",
      "inputs": [{ "name": "City", "location": "url" }, { "name": "Days", "type": "Integer", "location": "query" },
        { "name": "ApiKey", "location": "header", "headerName": "X-Api-Key" }],
      "outputs": [{ "name": "Response", "type": "ForecastDto" }] }
  ] }],
  "restServices": [{ "name": "StatusApi", "actions": [
    { "name": "GetStatus", "method": "GET", "url": "/status/{Id}",
      "inputs": [{ "name": "Id", "type": "Long Integer", "location": "url" }],
      "outputs": [{ "name": "Status", "type": "Text" }],
      "flow": [{ "kind": "assign", "assign": { "Status": "\"ok\"" } }] }
  ] }]
}
```

Other installed HTTP verbs, including POST, PUT, and DELETE, are supported.
Types resolve after the data phase, including same-spec structures.
On Service Studio 11.55.92, consumed structures must be client-owned.
The CLI recursively copies structures and lists as `<Client><Structure>` DTOs and refuses global name clashes.
Exposed parameters use resolved types directly.
Call consumed methods with execute using `Client.Method`; the default node name remains `Method`.
A unique method name works if no ordinary action has that name.

Existing same-kind integrations stay unchanged, including methods and base URL.
Wrong-kind clashes, duplicate methods, unknown types, unsupported placements, model refusals, and new errors prevent saving.
Inspect reports integrations with name, baseUrl, and methods with name, method, url.
Inspect an exposed method's flow with:

```text
oml tree <module.oml> Api/Method
```

Exposed APIs retain plugin defaults, including Authentication=None.
Authentication callbacks, Swagger import, and other REST settings are outside these spec keys.
Creation does not contact the remote API.

### Platform elements

Verified on Service Studio `11.55.92.65269` in a fresh CrossDevice module. See `tests/platform.json`.

| Element | Keys | Rules |
|---|---|---|
| `roles` | name, description, persistent; other installed properties | Persistent maps to IsPersistent. |
| `siteProperties` | name, type, default, description, multiTenant; other installed properties | Basic type; defaults to Text. Default is an expression. |
| `timers` | name, action, schedule, timeout, priority, description; other installed properties | Action must be a module-owned server action. Timeout maps to TimeoutInMinutes. |
| `processes` | name, launchOn, inputs, flow, detail; other installed properties | Launch On names a module entity's Create event. |
| Screen `access` | anonymous, registered, roles | Replaces memberships. Flags default false; roles defaults empty. |
| Process node | kind, name, next; installed properties | Resolves `OutSystems.Model.Processes.Nodes.I<Kind>Node`. |
| Human activity | destination, args, roles, instructions, detail, dueDate | Supplied roles replace Registered. |
| Automatic activity | flow, or action with args | Nested ordinary flow, or a single-call convenience. |
| Decision | condition, true, false | Required Boolean condition and Yes/No targets. |

Roles and site properties are created before logic. Timers and processes follow actions and screens.
Timer defaults are 20 minutes and Normal priority. Prefer actions without inputs; required inputs need valid defaults.
Timer schedules are raw strings:

```json
{ "timers": [{ "name": "NightlyCleanup", "action": "Timer_CleanupOldOrders", "schedule": "WhenPublished", "timeout": 20, "priority": "Normal" }] }
```

`"WhenPublished"` schedules on publish. `""` means unscheduled and reads back null.
`"0,15,30,45 * * * *"` round-trips as five-field cron.
On Service Studio 11.55.92, `"*/15 * * * *"` and `"Every 5 minutes"` also save without errors.
The headless API does not validate scheduler grammar. Verify custom schedules in Service Studio. Timer execution was not tested.

Access uses `AnonymousRole` membership, not `AnonymousAccess`.
On save/reopen, Registered includes all custom module roles. Anonymous also restores Registered.
For an exact custom-role gate, set both flags false:

```json
{ "screens": [{ "name": "ReviewOrder", "access": { "anonymous": false, "registered": false, "roles": ["OrderManager"] } }] }
```

Without access, keep factory defaults.
Role actions `CheckOrderManagerRole`, `GrantOrderManagerRole`, and `RevokeOrderManagerRole` exist immediately after CreateRole.
Owned and referenced role actions resolve through ordinary execute nodes.

Launch On creates mandatory `<Entity>Id`. Do not repeat it in explicit inputs.
Explicit process inputs default optional. Only the generated input can be mandatory with Launch On.
Process Start is implicit unless supplied. Next defaults to the following node, then End.
Start/End names are construction aliases; they have no persisted Name on Service Studio 11.55.92.
Automatic activities reuse their seeded logic Start. Process connectors use `Targets` and decision connectors.
Other kinds and properties use the installed API; refusals become problems.
CrossDevice supports BPT. ReactiveLibrary refuses CreateProcess with "Process objects can't be children of Module objects".

```json
{ "processes": [{ "name": "ApproveOrder", "launchOn": "Order", "detail": "\"Order \" + OrderId",
  "inputs": [{ "name": "Approved", "type": "Boolean", "default": "True" }],
  "flow": [
    { "kind": "humanActivity", "name": "Review", "destination": "ReviewOrder", "args": { "OrderId": "OrderId" }, "roles": ["OrderManager"], "instructions": "Review this order" },
    { "kind": "automaticActivity", "name": "RecordReview", "action": "CleanupOldOrders" },
    { "kind": "decision", "name": "WasApproved", "condition": "Approved", "true": "ApprovedEnd", "false": "RejectedEnd" },
    { "kind": "end", "name": "ApprovedEnd" }, { "kind": "end", "name": "RejectedEnd" }
  ] }] }
```

The example needs Order, ReviewOrder with OrderId, OrderManager, and CleanupOldOrders.
Site property multiTenant true/false maps to Yes/No; omission retains Inherited.
All site properties exist before default binding. The factory sets `IsReadOnly` false.
Read server-side values as `Site.MaxItems`.
Inspect reports platform names and key fields, including generated role actions. Tree reports access and BPT connector endpoints.

### Reactive UI and Mobile UI

| Scope | Keys | Meaning |
|---|---|---|
| Root | `screens`, `blocks` | UI elements in a named flow. |
| Screen/block | name, flow, title, inputs, locals, aggregates, dataActions, actions, widgets, public | Flow defaults MainFlow and is created if missing. String title is an expression. `public` makes a block usable by other modules. |
| Lifecycle | onInitialize, onReady, onRender, onDestroy; block only: onParametersChanged | Names of actions owned by the screen/block. On an existing screen or block (listed again or `addParts`), an unset handler is set; a creation list reports it in `changed`. Changing a set one in a creation list is refused (use `set`). |
| Block events | events (block), triggerEvent (flow node), events (BlockInstance) | See Block events below. |
| Styling and access | styleSheet, isDefault, access | Raw CSS; true selects the landing screen; access uses the platform shape. |
| Layout/scaffold | layout, placeholders, template, entity | See layouts below. |
| Widget | type, name, children, content area arrays | Built-in type or registered descriptor type. |
| Widget property | value, variable, source, style, visible, enabled, mandatory, prompt, text, sourceBlock | Uses installed setters/properties. Expression properties need OutSystems expressions. |
| Widget event | onClick, onChange, other installed events; args | Action name, screen name, or destination object. Arguments bind by parameter name. |
| BlockInstance | sourceBlock, args, placeholders, events | Source block, input expressions, named placeholder content, event handlers. |
| Label | targetWidget | Name of the input it describes, in the same screen, block or email (`""` clears it; also in `edits.set`). See Edits. |

Data actions and screen actions use the action shape. Data-action outputs replace the seeded Out1.
Data actions also take `fetch` (`atStart` or `onDemand`), `timeout` (seconds) and `onAfterFetch` (a screen action of the same screen or block). See `aggregate-on-demand-detail.jsonc`.
Preparation is refused for Reactive/Mobile. Use aggregates or lifecycle actions.
Explicit layout or widgets replaces starter widgets on newly created elements.
`styleSheet` also works through `edits.set`. `styleSheet` replaces the whole sheet: read the current one with `oml describe <module.oml> <Screen> --css` and edit it (apply warns when a sheet loses more than half or drops selectors). JavaScript flow nodes use the logic keys above.

Built-ins include If, Text, and BlockInstance.
Registered Reactive types include Container, Expression, Input, TextArea, Checkbox, Switch, Dropdown, Button, Link, Form,
Label, Image, Icon, List, ListItem, TableRecords, HeaderCell, RowCell, Popup, and Upload.
Discover available types with:

```powershell
oml widgets
```

Children use the widget or its first content placeholder.
Other array keys name content areas, including `trueBranch`, `falseBranch`, `headerRow`, and `row`.
Text is plain text. Expression properties need quoted expressions for literals:

```json
{ "type": "Container", "style": "\"card\"", "children": [{ "type": "Text", "text": "Orders" }] }
```

For string events, an owned screen action wins over a screen with the same name.
Widget args bind OnClick parameters when onClick is a string; block-instance args bind block inputs.
Object events explicitly select a destination:

```json
{ "type": "Link", "onClick": { "destination": "OrderDetail", "args": { "OrderId": "FetchOrderRecords.List.Current.Order.Id" } },
  "children": [{ "type": "Text", "text": "Open" }] }
```

#### Block events

A block tells its parent that something happened with an event. Reactive and Mobile only.

- `events` on a block: `[{ "name", "mandatory" (default true), "inputs", "description" }]`. Inputs carry data to the parent.
- `triggerEvent` flow node: `{ "kind": "triggerEvent", "event": "<event>", "args": { ... } }`. Only in a client action of the block that owns the event; anywhere else apply refuses it.
- `events` on a BlockInstance: `{ "<event>": "<action>" }` or `{ "<event>": { "action": "<action>", "args": { ... } } }`. The handler is a client action of the screen or block that holds the instance. A handler input with no arg gets the event input of the same name. Args are expressions; they can use the event's inputs. A handler input that is neither in args nor an event input is refused.
- A mandatory event must have a handler on every instance. Apply refuses an instance without one and names the event.
- Existing blocks (`addParts` edit): a new event on an existing public block is refused (rule 4: it changes the block's signature for other modules). On an existing block that is not public, a new event must be `"mandatory": false`, because its existing instances have no handler. Renaming or deleting an existing event, and making an existing optional event mandatory, are refused.
- `onParametersChanged` on a block names a block action. It runs when the parent changes an input of the block, not on the first render: also call the action from `onReady`.

```json
{ "blocks": [{ "name": "Picker", "inputs": [{ "name": "ItemId", "type": "Integer" }],
    "events": [{ "name": "OnSelect", "inputs": [{ "name": "Id", "type": "Integer", "mandatory": true }] }],
    "actions": [{ "name": "Select", "flow": [{ "kind": "triggerEvent", "event": "OnSelect", "args": { "Id": "ItemId" } }] }],
    "widgets": [{ "type": "Button", "onClick": "Select", "children": [{ "type": "Text", "text": "Pick" }] }] }],
  "screens": [{ "name": "Home", "locals": [{ "name": "Picked", "type": "Integer" }],
    "actions": [{ "name": "PickedItem", "inputs": [{ "name": "Id", "type": "Integer" }], "flow": [{ "kind": "assign", "assign": { "Picked": "Id" } }] }],
    "widgets": [{ "type": "BlockInstance", "sourceBlock": "Picker", "args": { "ItemId": "7" }, "events": { "OnSelect": "PickedItem" } }] }] }
```

Re-run `SetX` when input `Status` changes. `SetX` can hold an `assign` (as here) or a `javaScript` node. For an existing block, list it again with the new action and handler, or use `addParts`:

```json
{ "blocks": [{ "name": "StatusBadge", "inputs": [{ "name": "Status", "type": "Text" }], "locals": [{ "name": "Css", "type": "Text" }],
    "onReady": "SetX", "onParametersChanged": "SetX",
    "actions": [{ "name": "SetX", "flow": [{ "kind": "assign", "assign": { "Css": "If(Status = \"Done\", \"badge-green\", \"badge-grey\")" } }] }],
    "widgets": [{ "type": "Container", "style": "Css", "children": [{ "type": "Expression", "value": "Status" }] }] }] }
```

Events of referenced blocks work the same way. The OutSystems UI `Pagination` block has the event `OnNavigate` with input `NewStartIndex`:
`{ "type": "BlockInstance", "sourceBlock": "OutSystemsUI.Pagination", "args": { "StartIndex": "StartIndex", "MaxRecords": "PageSize", "TotalCount": "LongIntegerToInteger(FetchCustomers.Count)" }, "events": { "OnNavigate": "GoToPage" } }`
with a screen action `GoToPage` (input `NewStartIndex`, Integer) that assigns `StartIndex` and refreshes the aggregate. oml's tests do not cover OutSystems UI. To read the block's inputs and events, describe it in the producer module file (`oml describe OutSystemsUI.oml Pagination`); `describe` of a referenced block in the consumer is not supported. `describe` shows events, `trigger` lines and `event` handlers on instances. See `recipes/block-popup-event.jsonc`.

#### Known model behaviour (Reactive)

Checked while building the recipes in `skill/recipes/`. Follow these to avoid failed applies and warnings:
- Form validation: in a client action, an `assign` node can set `<Input>.Valid` to `False` and `<Input>.ValidationMessage` to a text. The input then shows the message. `<Form>.Valid` is `False` when any input in the form is invalid. `<Form>.Valid` is read-only: assigning it is a model error. oml leaves a Button's On Click Built-in Validations at Yes (the default), so empty mandatory inputs and values of the wrong type are marked before the action runs. See `form-with-validation.jsonc`.
- `GetExceptionMessage()` does not exist. In an exception handler, read `<HandlerName>.ExceptionMessage`.
- `List`/`ListItem` can fail with "On Click must be set" on the seeded `rightActions`. Use `TableRecords` for simple lists.
- An aggregate accepts `startIndex` together with `maxRecords` for paging.
- A role creates `Check<Role>Role`, which takes `UserId` and returns `HasRole`.
- Calling entity CRUD actions from a client action gives a security warning. Call a server action that calls them.
- Comparing a Long Integer with an entity identifier gives a warning. Convert with `LongIntegerToIdentifier`.

### Traditional Web UI

WebResponsive and WebResponsiveLibrary use the same screens and blocks keys in WebFlows.
Inputs, locals, server-side screen actions, block instances, layouts, access, and placeholders use the same shapes.

| Key or widget | Traditional Web behavior |
|---|---|
| `preparation` | Array of ordinary flow nodes. Use aggregate nodes to fetch data. |
| `aggregates`, `dataActions`, lifecycle keys | Refused. Use Preparation and server-side actions. |
| `template` | list/detail scaffolding is refused. Use explicit widgets. |
| `source` | SourceRecordList for tables/lists; SourceRecord for record widgets. |
| TableRecords `headerRow`, `row` | Fill existing HeaderRow/DataRow with HeaderCell/RowCell mapped to Web cells. |
| Form / EditRecord | Form aliases IEditRecordWidget; no IFormWidget on Service Studio 11.55.92. Use rows with cells. |
| Button | Text children become its label expression. Other child types are refused. Use label for an expression. |
| Text | Plain text. |
| `styleClasses` | Plain CSS classes. |
| `style` | Plain classes or a quoted literal; maps to StyleClasses, not a dynamic expression. |

Other widget properties use installed setters/properties. Events and args keep action-first, screen-second lookup.
Verified on Service Studio 11.55.92: Container, Expression, Text, Input, Button, Link, If, TableRecords,
ListRecords, EditRecord, and BlockInstance.
Widget interfaces resolve under `OutSystems.Model.UI.Web.Widgets`:

```powershell
oml api "OutSystems.Model.UI.Web.Widgets.I*"
```

```json
{ "screens": [{ "name": "Orders",
  "preparation": [{ "kind": "aggregate", "name": "FetchOrders", "sources": ["Order"] }],
  "widgets": [{ "type": "TableRecords", "source": "FetchOrders.List",
    "headerRow": [{ "type": "HeaderCell", "children": [{ "type": "Text", "text": "Total" }] }],
    "row": [{ "type": "RowCell", "children": [{ "type": "Expression", "value": "FetchOrders.List.Current.Order.Total" }] }] }] }] }
```

Form content shape:

```json
{ "type": "Form", "rows": [{ "type": "Row", "cells": [{ "type": "Cell", "children": [{ "type": "Text", "text": "Details" }] }] }] }
```

A fresh WebResponsive regression module saved and reopened with zero errors, including no theme/grid errors.
Existing theme errors still use baseline comparison. New errors are never ignored.
Inspect lists Web screens. Tree shows Preparation connectors, CRUD and event targets, blocks, and argument references.

### Layouts and scaffolding

| Key | Meaning |
|---|---|
| `layout` | Block name in this module or its references. |
| `widgets` | Fills MainContent when a layout is selected. |
| `title` | Widget array fills Title; string sets the screen title expression. |
| `placeholders` | Dictionary of actual placeholder names to widget arrays. Overrides convenience aliases. |
| `template` | list or detail; requires entity. |
| `entity` | Existing entity, or one created in the same spec. |

Unknown layout blocks or placeholders prevent saving.
BlockInstance also accepts placeholders. The CLI sets SourceBlock before filling PlaceholdersContent.

```json
{ "screens": [
  { "name": "Orders", "template": "list", "entity": "Order", "layout": "LayoutTopMenu",
    "title": [{ "type": "Text", "text": "Orders" }],
    "placeholders": { "Actions": [{ "type": "Text", "text": "Order management" }] } },
  { "name": "OrderDetail", "template": "detail", "entity": "Order", "layout": "LayoutTopMenu" }
] }
```

| Template | Generated elements |
|---|---|
| list | `Fetch<Entity>Records`, table with every attribute except the identifier, empty state, link from the first column to the first requested detail screen for that entity. |
| detail | Optional `<Entity>Id` input, single-record filtered aggregate, labels and mandatory inputs, Checkbox for Boolean, Save calling `CreateOrUpdate<Entity>`. |

Save navigates to the first requested list screen for the entity; otherwise it ends.
Explicit inputs, aggregates, actions, widgets, and title replace generated values, including empty arrays.
Generated titles fill Title. Use placeholders for layouts with other names.
Existing screens are reused and never regenerated.

Themed modules or UI flows use `table`, `card`, and `btn btn-primary` classes. Templates also validate without a theme.
They do not install OutSystemsUI or create its CSS.
Binary Data displays `(binary data)`; editing needs explicit widgets such as Upload.
Relationships use Dropdown and a lookup aggregate. Labels use the first Text attribute, or the identifier if none exists.
Add authorization, form validation, and error handling. Publish and test in the target environment.

For OutSystemsUI, export your own Reactive module with the needed layouts, transitive references, and a configured theme.
Use it as the apply source or clone it with a template.
The shorthand `new --template <export.oml>` still needs the output path shown in usage.
The CLI cannot download OutSystemsUI, authenticate an IPP-protected export, or invent producer signatures.

Service Studio 11.55.92.65269 template availability is installation-specific:

| Source | Status on the checked installation |
|---|---|
| `%LOCALAPPDATA%\\OutSystems`, `%APPDATA%\\OutSystems`, installation folders | No Reactive template or OutSystemsUI module. No .oap or .osp bundles. |
| `TemplateService.dat` | Loadable Service module referencing System; cloning and renaming verified. |
| `TutorialResources\\NoLockIn.oml` | Traditional Web ContactManager sample; references System, HTTPRequestHandler, Text. |
| `Templates.dat` | Protected system module; refuses with "System modules cannot be loaded". |

Discovery reads private scratch copies. Bundle discovery does not make the contained module directly usable.
The scaffold fixture verifies a local layout on bare CrossDevice. The self-test verifies a referenced layout and themed classes after reopening.

### Edits

Edits run after creation in list order. Expression writes run after edits; expressions must use final names.
Each problem prevents the final staged save.

Existing means present in the source module before this spec started. Elements created in this spec remain editable and deletable, including after the internal data-model reload.
Existing entities (regular or static), their attributes and static records, structures and attributes, server/service/client actions and input/output parameters, screens, blocks, roles, exceptions, site properties, timers, REST clients/services and methods, and processes cannot be renamed or deleted. Setting Name or a static record Identifier cannot bypass this rule.
Existing entity, static entity, and structure attributes cannot change DataType, Length, Decimals, or IsMandatory through edits. Creation lists skip existing attributes. New attributes and parameters can be added. Flow nodes, widgets, and local variables remain editable and removable.

| Operation | Keys | Behavior |
|---|---|---|
| `insertNodes` | action, after, connector, flow | Insert a nonempty flow at an outgoing connector. Final implicit next inherits the old successor. |
| `addExceptionHandler` | action, flow | Add a separate handler entry and path. Flow must start with exceptionHandler. |
| `addWidgets` | screen, parent, area, index, widgets | Append by default. Parent is relative to screen/block; area defaults children. Index is zero-based. |
| `replaceWidgets` | at, area, widgets | Replaces all widgets inside the element at `at` (a screen, block, email, widget or placeholder path); that element stays. Same widget spec as creation. Area as in addWidgets. |
| `addParts` | screen, inputs, locals, aggregates, dataActions, actions, lifecycle keys; other supported screen parts | Adds through the shared screen builder. Names must be free. Block `events` follow the existing-block rule in Block events. |
| `addVariables` | action, inputs, outputs, locals | Adds variables; names must be free. |
| `setAccess` | screen, anonymous, registered, roles | Replaces access memberships. Omitted flags false, roles empty. |
| `set` | element, properties | Property map uses interface-first reflection, expression setters, and reference lookup. |
| `rename` | element, to | Refuses existing protected elements, occupied names, or model renaming. Use instead of setting Name. |
| `delete` | element, allowDataLoss for data | See deletion rules below. |
| `addToAggregate` | aggregate, sources, joins, filters, sorts, groupBy, aggregates, calculated, group keys | Adds parts to an existing aggregate with the query keys. |
| `setArgs` | element, args | Sets arguments of a call, navigation or other node by parameter name. A number or true/false is used as the expression. |

For If or For Each insertion, connector is required: true, false, cycle, or next (alias sequence), as supported.
Explicit targets can name existing or inserted nodes. End and Raise Exception terminate normally.
Widget area can be children, trueBranch, falseBranch, row, or headerRow; indexed insertion keeps list order. Absent parent means the screen/block root; for plugin widgets, children uses the first placeholder.

```json
{ "edits": [
  { "op": "addVariables", "action": "CreateOrderFor", "locals": [{ "name": "AuditTotal", "type": "Decimal" }] },
  { "op": "insertNodes", "action": "CreateOrderFor", "after": "Prepare",
    "flow": [{ "kind": "assign", "name": "Audit", "assign": { "AuditTotal": "Total" } }] },
  { "op": "addParts", "screen": "Orders", "locals": [{ "name": "Caption", "default": "\"Ready\"" }],
    "actions": [{ "name": "RefreshCaption", "flow": [] }] },
  { "op": "addWidgets", "screen": "Orders", "parent": "Header", "area": "children", "index": 1,
    "widgets": [{ "type": "Expression", "name": "CaptionText", "value": "Caption" }] },
  { "op": "setAccess", "screen": "Orders", "roles": ["OrderManager"] },
  { "op": "set", "element": "Orders/CaptionText", "properties": { "value": "\"Orders: \" + GetOrders.Count" } },
  { "op": "rename", "element": "CreateOrderFor/Audit", "to": "RememberTotal" },
  { "op": "delete", "element": "CreateOrderFor/RememberTotal" }
] }
```

The example assumes the named action, nodes, screen, Header container, aggregate, and role already exist.

`replaceWidgets` keeps the element at `at` (its name, style and events) and replaces every widget inside it. Use it to restyle or rebuild part of an existing screen without a rebuild; a deeper `at` replaces less. A layout placeholder's path has its name, for example `ContactForm/MainContent` (copy it from `oml tree`). The report's `changed` lists `widgets of <at>`.
It is refused, before anything changes, when a removed widget is used outside the replaced part, for example `NameInput.Valid` in a screen action; the problem names each widget and where it is used. An expression finds a widget by name, so a new widget with the same name keeps that usage working: keep the name in `widgets`, or change the usage first. The new widget must be the same kind (an Input stays an Input): `NameInput.Valid` would mean something else, so a used name given to another kind is refused ("NameInput was an Input, now a Container"). Other references to a removed widget point at the widget itself and are always refused, for example a Label's target widget: first point the Label at another widget.
A Label takes `"targetWidget": "<widget name>"` (`""` clears it), the input it describes, in a widget spec and in `set` (`{ "op": "set", "element": "Home/NameLabel", "properties": { "targetWidget": "NameInput" } }`). The name is looked up in the same screen, block or email, after all its widgets exist. `describe` and `tree` show it. When replaceWidgets removes a Label with a target and no new Label names that target, `warnings` says so.

```json
{ "edits": [
  { "op": "replaceWidgets", "at": "ContactForm/ContactFormBody", "widgets": [
    { "type": "Container", "style": "\"columns2\"", "children": [
      { "type": "Input", "name": "NameInput", "variable": "NewContact.Name", "mandatory": "True" },
      { "type": "Input", "name": "EmailInput", "variable": "NewContact.Email", "inputType": "Email" } ] },
    { "type": "Button", "onClick": "Save", "style": "\"btn btn-primary\"", "children": [{ "type": "Text", "text": "Save" }] } ] }
] }
```

Change aggregate parts with `set` on their tree paths: Filter `condition`, AttributeSort `attribute` or `sortDirection`
(Ascending, Descending), JoinCondition `condition` or `joinType` (Inner, Left, Full), CalculatedAttribute `value`.
Remove them with `delete`. Parts added by `addToAggregate` exist only at the end of the apply; change them in a later apply.
Group-level parts sit under a second CombineSources (for example `Q/CombineSources1/Filter[0]`); copy the path from `oml tree`.
Deleting a filter, sort or group of a grouped aggregate can drop its source; oml refuses such a delete (the model
removes the source without a validation error). A delete of internal structure (AddSource, CombineSources) is refused,
and a delete the model fails on is a problem. Do these in Service Studio.

```json
{ "edits": [
  { "op": "addToAggregate", "aggregate": "GetOrderTotals/Totals", "filters": ["Order.Total > 0"],
    "sorts": [{ "attribute": "Order.PlacedOn", "descending": true }] },
  { "op": "set", "element": "GetOrderTotals/Totals/Orders/JoinCondition[0]", "properties": { "joinType": "Left" } },
  { "op": "setArgs", "element": "LoadReport/GetOrderTotals", "args": { "Year": "Year(CurrDate())" } }
] }
```

Paths use slash-separated scopes: `Action/Node`, `Screen/Widget`, `Screen/ScreenAction/Node`, or `Flow/Screen/...`.
Each segment searches breadth-first within its parent. From the module, this module's own top-level element wins;
when it does not hold the rest of the path, the path fails (it does not fall through to a deeper or referenced element).
`Kind:Name` (`Screen:Orders`) and `Module.Name` qualify a name. An ambiguous path is refused with an exact path for
every candidate; copy one. At the end of a path, directly under an action, an input or output parameter wins over a flow node
of the same name (`Pick/Response` is the parameter); write `Pick/Assign:Response` for the node. Edits refuse elements of referenced modules. Tree accepts the same paths. See "Element paths" in `commands.md`.
Unnamed elements use `Kind[index]`, indexed from zero among siblings of that kind.
For example: `Orders/Header/Button[0]/Text[0]`. Copy the emitted path; indexes can change after edits. Flow nodes and action or instance arguments have a fixed order on every load.
Labels persist where supported. Raise Exception on Service Studio 11.55.92 needs an index path after reopening.

### Deletion

Existing entities of every kind, attributes, static records, and other protected elements cannot be deleted. Deleting a parent that contains a protected element is also refused. Do the deletion manually in Service Studio if required. There is no override; allowDataLoss does not permit deletion of existing data definitions.

For entities and attributes created in the same spec:

- Data deletion requires `allowDataLoss: true`. Identifier attributes cannot be deleted alone.
- Referenced-module data cannot be deleted.
- Data deletions run in edit order after expression binding.
- The reference graph checks aggregates, SQL, variable/parameter/attribute types, foreign keys, widgets, and generated CRUD calls. Any usage refuses deletion and lists consumer paths. Remove or change consumers explicitly first.
- Attribute checks also cover whole-row queries and CRUD calls on the owning entity.
- Public/static data deletion emits warnings.

A flow node is reconnected only with exactly one incoming and one outgoing connector. A comment arrow is a note, not flow: an arrow that points at the deleted node goes with it (the report has a warning), and other arrows stay. The delete of a node with outputs (aggregate, SQL, action call) is refused while an expression in the same action, per-field record mappings included, still reads `Node.`; the refusal lists each reader. A node without outputs (Assign, If, Switch, Raise exception, Refresh, End) is not checked. Set those expressions first: `set` before `delete` in one edits list works. Branches, junctions, and Start/End deletion are refused. Widgets and local variables can be removed from existing actions and screens.

### Copy, translations, resources, and folders

These keys use the installed typed API. A relative file path means a file in the spec's folder when one exists, else in the CLI working folder; an absolute path is allowed. The example's `assets/...` paths are next to the spec.
Copy runs after explicit reference imports and before data and logic creation. Resources and themes also exist before widgets are built.
Locales, translation imports, translation writes, and folders run after logic and edits, in that order.

```json
{
  "copy": [{ "from": "Source.oml", "elements": ["CheckInput", "MainFlow/Welcome"], "into": "MainFlow" }],
  "locales": ["ka-GE", "en-US"],
  "translate": [{ "locale": "ka-GE", "text": "MandatoryInput", "translation": "Required value" }],
  "images": [{ "name": "Logo", "file": "assets/logo.svg" }],
  "scripts": [{ "name": "Helper", "file": "assets/helper.js" }],
  "themes": [{ "name": "AppTheme", "styleSheet": ".title { color: navy; }" }],
  "resources": [{ "name": "Payload", "file": "assets/payload.json", "public": false }],
  "folders": [{ "name": "Validation", "area": "serverActions", "elements": ["CheckInput"] }],
  "screens": [{ "name": "Home", "requiredScripts": ["Helper"], "widgets": [{ "type": "Image", "image": "Logo" }] }]
}
```

| Key | Behavior and limits |
|---|---|
| `copy` | `from` is a read-only source module. `elements` uses the same paths as `tree`. Paste uses `Serialize` and `DeserializeInto`. Module elements paste into the module. Screens and blocks paste into `into`, default `MainFlow`; a missing flow is created. The `created` report lists pasted names. Clashes, missing pasted elements, and new producer signatures are problems and prevent saving. Add dependencies explicitly with `references`. Clipboard IPP checks remain active. |
| `locales` | Culture names map to the installed `Culture` enum (`ka-GE` maps to `KA_GE`). Unknown values are problems. Existing locales are reused. |
| `translate` | Calls `CreateOrUpdateTranslation` on the module. `text` is the API resource ID, such as `MandatoryInput`; it is not a widget path or a search for displayed text. Creates the locale if needed. Values survive save/reopen and appear under `inspect.content.translations`. |
| `translations` | `{ "import": "ModuleLanguage.ka-GE.resX" }` calls `LoadTextResources`. Refusals prevent saving. On 11.55.92 the headless host refuses ResX format detection, so file round trips are unavailable. Use `translate` for module resource IDs. |
| `images` | `name`, `file`, optional `public` (false). Uses `CreateImage`. SVG and PNG were verified; the API also lists Jpg, Gif, and Ico. Invalid files are problems. Image widgets can reference the image in the same spec. |
| `scripts` | `name`, `file`, optional `public` (false). Sets `JavaScript` to the file text. Screens and blocks accept `requiredScripts`, an array of script names. |
| `themes` | `name`, optional `styleSheet` and `baseTheme`. `styleSheet` is CSS text, an existing file path, or `{ "file": ... }`; a value that looks like a path (`theme.css`, `css/app.css`) but names no file is refused. A new theme without `styleSheet` starts empty. Creates a Web theme in Traditional Web modules, otherwise a Mobile theme. Does not select the module default theme. |
| `resources` | `name`, `file`, optional `public` (false). Stores the exact file bytes. |
| `folders` | `name`, `area`, `elements` paths. Reuses folders with the same area and name. Moves elements by their typed `Folder` property. `entities` is an alias for `ServerEntities`. Other areas match installed `ESpaceTreeFolder` names without case sensitivity: `serverActions`, `clientActions`, `serviceActions`, `structures`, `images`, `resources`, `scripts`, `themes`, `siteProperties`, `timers`, `roles`, `processes`, and others listed by `oml api ESpaceTreeFolder`. Rejects elements outside the requested area. |

Image, script and resource creation lists reuse existing names without updating their content. An existing theme takes a different `styleSheet` and `baseTheme` (UI, rule 3); the report's `changed` lists `styleSheet of theme <name>` and `baseTheme of theme <name>`. Left out, they keep the theme's values. A module has no style sheet of its own: put module-wide CSS in a theme.
`inspect.content` lists content, locales, module translations, and folder entries. `tree` shows `folder`, `requiredScripts`, and image `ref` values.

Export text resources with:

```text
oml translations export App.oml translations.xlsx --format Excel
oml translations export App.oml translations-resx --format ResX
```

The installed enum supports `Excel` and `ResX`; `xlsx` is an alias for `Excel`. Default is Excel, or ResX for a `.resx` output name.
The API takes an output folder for both formats. The CLI stages that folder, then writes the Excel file to the requested path, or moves the ResX folder to that path.
Existing outputs are refused. Model failures return `exported: false`, `problems`, and exit 1. Failed export does not create the requested output.
On 11.55.92 the ResX writer throws `NotImplementedException` and the API wraps it in `AbortCommandException`. Excel export also refuses in the headless host.
Export and import are exposed for builds that implement them. A file round trip was not possible on 11.55.92; direct translation storage was verified instead.
A failed ResX writer can leave locked files in the scratch folder until the CLI exits. Cleanup problems include that folder path.
