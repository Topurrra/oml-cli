# Spec reference

This file belongs to the `outsystems-oml` skill. It lists every spec key that `oml apply` accepts.

Every top-level key is optional. The parser accepts JSON comments but rejects trailing commas.
Use the documented camelCase spelling, especially inside raw node, widget, and platform objects.
Typed spec records accept case-insensitive property names. Raw object key checks can be case-sensitive.

Creation lists reuse existing names. Existing attributes are never retyped, and existing static records are not updated.
Existing actions, screens, blocks, integrations, timers, processes, roles, and site properties need explicit edits to change them.
Reused entities, structures, and static entities still apply the spec's `public` value and any supplied `description`.
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
`icon` reads PNG bytes from a path relative to the CLI working directory.
`defaultTheme` is the exact name of an existing theme, including themes from module references.
The CLI selects the Traditional Web or Reactive/Mobile theme property for the module kind.
Missing or ambiguous themes, unsupported fields (including `dbms`), invalid icons, and model refusals are problems.
Any problem prevents saving. `inspect.module` reports the values and icon byte size after reopening.

### Data model

| Scope | Keys | Meaning |
|---|---|---|
| Root | `entities`, `structures` | Arrays of record definitions. |
| Record | `name`, `attributes`, `public`, `description` | Name; attribute array; public defaults false; optional description. |
| Attribute | `name`, `type`, `length`, `decimals`, `mandatory`, `default`, `description`, `deleteRule` | Type defaults Text; mandatory defaults false; default is an expression. Identifier delete rule accepts Protect, Delete, or Ignore; defaults Protect. |
| Root | `exceptions` | Array of user exception names. |
| Variable | `name`, `type`, `mandatory`, `default`, `description` | Shared variable shape. Type defaults Text; mandatory defaults false. |

New entities get auto-number `Long Integer` `Id`, assigned as `IdentifierAttribute`.
They need at least one other attribute. Entity attributes require basic or Identifier types.
Structures can also contain entity, structure, and list types.

Types use Service Studio names: `Text`, `Integer`, `Long Integer`, `Decimal`, `Boolean`, `Date`, `Time`, `Date Time`,
`Email`, `Phone Number`, `Currency`, `Binary Data`, `<Entity> Identifier`, `List of <type>`, or a record name.
An expression's text literal must include its OutSystems quotes:

```json
{
  "entities": [{ "name": "Customer", "attributes": [
    { "name": "Name", "length": 100, "mandatory": true },
    { "name": "Email", "type": "Email", "length": 250 }
  ] }],
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
See reference/model-api.md §13.1 for the API contract.

Names can use `Module.Name` for actions, blocks, screens, entities, structures, roles, and types.
Examples include `CoreServices.Customer Identifier` and `List of CoreServices.Customer`.
Plain names prefer owned elements, then a unique reference. Ambiguity reports qualified candidates and refuses the change.
The module can rename a second same-named reference to `Customer2`; use its actual local name.

Aliases only rename writable referenced elements added by this spec. Renaming a reference already present in the source is refused. They do not change the producer module or replace the reference.
A repeated alias reuses the alias name. Later specs and expressions must use that name.
Refused or silently renamed aliases become problems.


#### Comments in styleSheet and javaScript

`apply` removes comments from every `styleSheet` and `javaScript` value before it builds anything:

- CSS: `/* ... */` comments are removed. `/*! ... */` comments stay (use them for license notices).
- JavaScript: `/* ... */` comments and lines that start with `//` are removed. A `//` after code on the same line stays, because it can be part of a URL or a regular expression.
- Text inside quotes is never changed.
- The report shows `commentsRemoved` (number of characters).
- Set `"keepComments": true` at the top of the spec to keep all comments.

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

`clientVariables` and `sessionVariables` are additive arrays. Each item has `name`, `type` (default `Text`), and optional `default` expression. Existing names are reused without changes. New variables exist before any action expression is bound.

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

`systemEvents` maps event names to action names. `onApplicationReady` and `onApplicationResume` call a client action in Reactive/Mobile. `onSessionStart` and `onBeginWebRequest` call a server action in Traditional Web. Qualified `Module.Action` names work for referenced actions. The CLI creates a Start, Execute and End body for each event. The action must need no mandatory arguments. Existing events are refused; edit their flow instead. Unsupported module kinds, wrong action types and unknown event names report problems and prevent saving. Reactive application events need a Screen to execute at runtime.

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
| `sendEmail` | `email`, `from`, `to`, optional `cc`, `bcc`, `logContent`, `args` | Requires an existing Traditional Web Email screen. Address values are expressions; args map input names to expressions. |

The installed API has no Reactive Email screen or Reactive SendEmail contract. This CLI does not create Email screens. Use a Traditional Web module that already contains the Email screen. Missing screens, unsupported node scopes and model refusals prevent saving. These checks verify the saved model; they do not send email, process a workbook, or download a file at runtime.

### Logic and flows

`apply` automatically lays out every new logic or BPT process flow and every existing flow where
the spec inserts or removes nodes. It moves the nodes of those flows, including exception handler
paths. Other existing flows keep their positions. Comment nodes always keep their positions.
The main path runs down; If false runs down and true runs right, matching the sample Studio flow.
Switch branches use separate columns, loop bodies run to the right, and exception handlers sit beside
the main flow. Shared joins and End nodes sit below their incoming branches. Layout changes positions
through the typed Model API; it keeps the connectors and expressions.

Use the root key `tidyFlows` to move the nodes of selected existing flows without changing their logic:

```json
{ "tidyFlows": ["Fx*", "Showcase/CopyText"] }
```

Each entry is an element path to an editable action, preparation, process or other logic flow,
or a case-sensitive name/path pattern with `*`. `"*"` selects all local editable flows, including
nested flows. Use the qualified paths from `oml tree` when names are ambiguous. Empty entries,
paths to non-flow elements, and patterns with no matches are errors. This is an explicit request
to replace the selected flows' node positions. Comments stay fixed; nodes avoid their default bounds.
Layout uses Studio's default `(3196, 800)` origin, `2660` spacing and `914 × 928` node bounds,
with extra room for labels. The typed API does not expose rendered text bounds, so very long labels
or expanded comments can extend beyond the reserved space.

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
| `sorts` | Array with attribute expression and optional descending boolean. |
| `groupBy` | Array of attribute expressions. |
| `aggregates` | Output definitions with name, function, attribute. Functions: Count, Sum, Avg, Min, Max; case-insensitive. |
| `calculated` | Pre-group attributes with name and expression. |
| `maxRecords` | Number or expression string. |

`joins.type`: `only with` = Inner; `with or without` = Left; `with` = Full.
Joins are explicit. Even Count needs an attribute, such as `Order.Id`.
Grouped output comes from groupBy and aggregates. Output names must be unique; automatic renames become problems.
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
{ "timers": [{ "name": "NightlyCleanup", "action": "CleanupOldOrders", "schedule": "WhenPublished", "timeout": 20, "priority": "Normal" }] }
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
| Screen/block | name, flow, title, inputs, locals, aggregates, dataActions, actions, widgets | Flow defaults MainFlow and is created if missing. String title is an expression. |
| Lifecycle | onInitialize, onReady, onRender, onDestroy | Names of actions owned by the screen/block. |
| Styling and access | styleSheet, isDefault, access | Raw CSS; true selects the landing screen; access uses the platform shape. |
| Layout/scaffold | layout, placeholders, template, entity | See layouts below. |
| Widget | type, name, children, content area arrays | Built-in type or registered descriptor type. |
| Widget property | value, variable, source, style, visible, enabled, mandatory, prompt, text, sourceBlock | Uses installed setters/properties. Expression properties need OutSystems expressions. |
| Widget event | onClick, onChange, other installed events; args | Action name, screen name, or destination object. Arguments bind by parameter name. |
| BlockInstance | sourceBlock, args, placeholders | Source block, input expressions, named placeholder content. |

Data actions and screen actions use the action shape. Data-action outputs replace the seeded Out1.
Preparation is refused for Reactive/Mobile. Use aggregates or lifecycle actions.
Explicit layout or widgets replaces starter widgets on newly created elements.
`styleSheet` also works through `edits.set`. JavaScript flow nodes use the logic keys above.

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
| `addParts` | screen, inputs, locals, aggregates, dataActions, actions, lifecycle keys; other supported screen parts | Adds through the shared screen builder. Names must be free. |
| `addVariables` | action, inputs, outputs, locals | Adds variables; names must be free. |
| `setAccess` | screen, anonymous, registered, roles | Replaces access memberships. Omitted flags false, roles empty. |
| `set` | element, properties | Property map uses interface-first reflection, expression setters, and reference lookup. |
| `rename` | element, to | Refuses existing protected elements, occupied names, or model renaming. Use instead of setting Name. |
| `delete` | element, allowDataLoss for data | See deletion rules below. |

For If or For Each insertion, connector is required: true, false, cycle, or next, as supported.
The sequence connector alias also means next.
Explicit targets can name existing or inserted nodes. End and Raise Exception terminate normally.
Widget area can be children, trueBranch, falseBranch, row, or headerRow. Indexed insertion preserves list order.
Absent parent means the screen/block root. For plugin widgets, children uses the first placeholder.

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

Paths use slash-separated scopes: `Action/Node`, `Screen/Widget`, `Screen/ScreenAction/Node`, or `Flow/Screen/...`.
Each segment searches breadth-first within its parent. Multiple matches at the shallowest level are refused.
Qualify the parent to resolve ambiguity. Tree accepts the same paths.
Unnamed elements use `Kind[index]`, indexed from zero among siblings of that kind.
For example: `Orders/Header/Button[0]/Text[0]`. Copy the emitted path; indexes can change after edits.
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

A flow node is reconnected only with exactly one incoming and one outgoing connector. Branches, junctions, and Start/End deletion are refused. Widgets and local variables can be removed from existing actions and screens.

### Copy, translations, resources, and folders

These keys use the installed typed API. All file paths are relative to the CLI working directory, or absolute.
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
| `themes` | `name`, `styleSheet`, optional `baseTheme`. `styleSheet` is CSS text or an existing file path. Creates a Web theme in Traditional Web modules, otherwise a Mobile theme. Does not select the module default theme. |
| `resources` | `name`, `file`, optional `public` (false). Stores the exact file bytes. |
| `folders` | `name`, `area`, `elements` paths. Reuses folders with the same area and name. Moves elements by their typed `Folder` property. `entities` is an alias for `ServerEntities`. Other areas match installed `ESpaceTreeFolder` names without case sensitivity: `serverActions`, `clientActions`, `serviceActions`, `structures`, `images`, `resources`, `scripts`, `themes`, `siteProperties`, `timers`, `roles`, `processes`, and others listed by `oml api ESpaceTreeFolder`. Rejects elements outside the requested area. |

Image, script, resource, and theme creation lists reuse existing names without updating their content.
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
