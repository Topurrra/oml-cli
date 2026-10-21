# Best practices

OutSystems best practices that the other topics do not hold: read them before you design new elements, and check them before you report. Rules 1 to 11 come first: never change existing elements only to follow a line here. If the user asks otherwise, do it and mention the practice. `(check)` marks what `oml check` finds; the rest only you can check.

## Data model

- Names: English, PascalCase, no short forms (`Customer`, not `Cstr`). A reference attribute ends in `Id` (`CustomerId`).
- A fixed list of values is a static entity, not text or numbers written in logic.
- Prefer a soft delete: an `IsActive` attribute set to False keeps the history.
- Values that lists and dashboards show are computed when the data is written, or by a timer, not on each screen load.
- New references: Protect and Delete run extra queries for each deleted parent. Ignore is the fastest but needs a clean-up plan. Keep Protect unless the user chooses.
- An index on several attributes helps queries that filter in the same order.
- A large Excel upload: save the file in an entity and let a timer read it in chunks. A request has a time limit.

## Queries

- Prefer aggregates. Use SQL only for what an aggregate cannot do, and keep business rules out of SQL.
- A few attributes of one record: an aggregate filtered by its Id. `Get<Entity>` reads every column.
- An action whose output is entity records reads every column, also when it only wraps one aggregate. Put the aggregate where its result is used, or return a structure.
- A second aggregate that filters on the result of the first: one aggregate with a join.
- Change a record with `Update` or `CreateOrUpdate`; never delete it and create it again.
- SQL row limit (SQL Server): `TOP (@MaxRows)`. The parentheses keep the parameter an Integer; without them it needs Expand Inline.
- `.Count` of a SQL query runs the whole query again without the limit. Write a separate, simpler count query.
- On the server, loop over a query result once. A second loop or `List[i]` keeps the whole result in memory.

## Server logic

- Site properties hold configuration that admins set. Never assign them in logic: each change clears the cached values in that module and in every module that uses it. Keep changing values in an entity. (check)
- Raise exceptions for exceptional cases only, not to steer normal logic.
- A public action that runs `CommitTransaction` or `AbortTransaction` says so in its description, for example `[commit transaction]`: it commits or rolls back the caller's work too.
- Long flows: move parts to actions or add comments (check). Do not write If branches whose condition is a fixed True or False.
- Timer actions start with `Timer_`. Describe public elements and their parameters (check).
- Session variables hold small values only (check, Traditional Web). Large data goes in an entity keyed by the session Id.

## Reactive and Mobile screens

- Server fetches that wait for each other (one OnAfterFetch starts the next): one data action that runs them in order, in one request (check).
- Several simple screens are faster than one screen that handles every case.
- Screen inputs: identifiers and short values only, never sensitive data. They can show in the URL.
- While data loads (`not <Agg>.IsDataFetched`), show a placeholder of the same size (a skeleton or grey lines), so the layout does not jump. BlankSlate is for the empty state only.
- Keep server request timeouts at 10 seconds or less (module default 10 s; `timeout` on aggregates and data actions).
- Set width and height on Image widgets to the final size.
- Images: resize to the shown size, compress, and use a separate thumbnail. Aim for less than about 150 KB (Mobile) or 500 KB (Web) and at most 1024 px.
- List items stay simple: no maps or heavy JavaScript per item, no "show more" text inside an item (use MasterDetail).

## JavaScript and CSS

- Low-code first; JavaScript only when the platform cannot do it. Use only the public JavaScript API (`$public`, `$actions`); internal APIs change on upgrade.
- Do not change the DOM that the platform renders. Change classes with a Style Classes expression, not in JavaScript.
- A JavaScript library goes in a Script element, not in JavaScript nodes. JavaScript used in several places goes in one client action.

## Traditional Web

- Screen actions: do not read Preparation results or large inputs or locals (records, lists). They travel in the viewstate of every request of that screen. Pass an Id as a parameter and fetch again.
- Keep the Preparation short. No queries inside If branches of the Preparation: assign locals instead.

## Mobile

- Splash screen: no heavy logic, few blocks. Fetch server data on the default screen (aggregates or one data action), not on the splash screen.
- Local storage: only the attributes and records a screen needs, denormalized. Sync in small chunks, most important data first.
- Offline sync: put the actions that update local entities in the OfflineDataSync client action and start it with TriggerOfflineDataSync, so it runs in the background. Its SyncUnit input limits what is synced; update the UI in OnSyncComplete.

## Accessibility

- A custom interactive element answers Enter, Space and Escape like a native one. Set the focus order with the widget order, never a positive `tabindex`.

More: `oml doc processes-timers-email#process-design-and-change`, `oml doc security`, `oml doc ui-reactive`.
