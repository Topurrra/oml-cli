# Data rules

Entities, types, static entities, delete rules, indexes, aggregates, SQL and transactions. Spec shape: `oml spec entities`, `oml spec aggregate`, `oml spec sql`.

## Entities and attributes

- An entity is a database table. A new entity gets `Id` (Long Integer, auto number) as its identifier.
- One identifier per entity; no composite keys (use a unique index). At most one auto-number attribute. The identifier is Integer, Long Integer, Text, or the Identifier of another entity (one-to-one).
- An entity needs an attribute besides an auto-number `Id` (`InvalidEntity_OnlyAuto`).
- At most one Binary Data attribute per entity. Put files and long texts in a 1:1 entity; wide rows slow every update.
- Text up to 2000 is a normal column; above 2000 is a large text column that cannot be indexed and cannot shrink back to 2000 or less.
- Defaults: Text length 50; Decimal 37 digits with 8 decimals. Decimals must not exceed Length.
- Mandatory is checked by forms and validation, not by the database.
- Attribute and variable defaults must be literals (or `NullDate()`, `NullIdentifier()`, `CurrDate()`, `CurrDateTime()` and similar).
- Update Behavior "Changed Attributes" (entity `updateBehavior`) writes only attributes assigned after the record was read. A record built from scratch or copied whole can update nothing; assign attributes one by one.
- A rename or delete leaves the old column and data in the database; a type change that cannot convert fails at publish. oml refuses both (rules 3 and 5).
- A new attribute gets the type default in existing rows.

## Types and null

- No null except identifiers. Defaults: `""`, `0`, `False`, `#1900-01-01#`, `#00:00:00#`.
- Test "not set" with `NullIdentifier()`, `NullDate()`, `NullTextIdentifier()`, `NullBinary()`.
- Integer is 32-bit, Long Integer 64-bit. Decimal keeps 8 decimal places. Dates run from 1900-01-01 to 3000-12-31.
- The client shows Date Time in the user's time zone. Use Date when the day must not shift.

## Static entities

- A fixed list of named records, like an enum. Read a record as `Entities.Status.Active`. Data changes only at design time.
- Default attributes: `Id`, `Label`, `Order`, `Is_Active`. Use Integer for a static `Id`, never auto number.
- A static entity can reference only other static entities.

## Relationships, delete rules, indexes

- A relationship is an attribute of type `<Entity> Identifier` on the "many" side. Many-to-many: a junction entity with two identifiers and a unique index on the pair (entity `indexes`).
- Protect (default): deleting a parent with children fails with a database exception. Delete: children are deleted too. Ignore: no foreign key, no automatic index, children keep a dead id.
- References to extension entities or to entities not in the module's references must use Ignore. A self-reference cannot use Delete.
- On SQL Server, two Delete chains that reach the same table pass validation but fail at publish (multiple cascade paths). Set one to Ignore or Protect.
- Index attributes used in filters and joins, most selective first. A reference with Ignore has no index: add one if you filter on it. Every index slows writes. No Binary Data in an index.

## Aggregates

- An aggregate reads only the attributes used. Returning its rows from an action fetches every column: pass a small structure.
- Joins: "only with" = inner; "with or without" = left (unmatched columns hold defaults); "with" = full; no join = cross product (warning).
- After any group by or aggregate function, only grouped columns and aggregate outputs remain. Sum and Average need numbers; Count works on any type.
- Dynamic sort: a sort with `"dynamic": true` and a Text expression, such as a local holding `"Order.CreatedOn DESC"`.
- `<Agg>.Count` runs a second count query without the Max Records limit. Use `<Agg>.List.Empty` to test for rows.
- Never run an aggregate or SQL inside a For Each. Join in one query instead.
- Detail panels: master fetched at start, detail with `fetch: "onDemand"`, then `refreshData` after a row is chosen.

## SQL

- Server logic only. Entities as `{Order}`, attributes as `[Total]`, parameters as `@Name`. An output structure is required and must match the SELECT columns in order.
- `maxRecords` on SQL does not limit the query; it cuts the result afterwards. Limit inside the SQL (`TOP`, `FETCH FIRST`).
- `SELECT *` cannot be checked against the output (warning). Use `SELECT {Order}.*`.
- Optional filter without Expand Inline (SQL Server syntax): `(@Name = '' OR {Customer}.[Name] LIKE '%' + @Name + '%')`.
- Expand Inline pastes text into the SQL (injection risk). Only for fixed text chosen by the app. `IN` lists: `BuildSafe_InClauseIntegerList` or `BuildSafe_InClauseTextList`. A parameter can never be a column, a table or `ORDER BY` text.
- Never touch tables named `OSSYS_`, `OSUSR_` or `OSLOG_` directly.
- oml refuses SQL with DELETE, UPDATE, TRUNCATE, DROP, ALTER or MERGE (rule 6); SELECT and INSERT are allowed. Tell the user when a task needs more.

## Transactions

- The server logic of a request shares one main transaction that commits when the request ends. An unhandled exception rolls it back.
- `CommitTransaction` and `AbortTransaction` end the transaction and start a new one.
- On Oracle, PostgreSQL and DB2, commit before launching a process or calling a web service that must see new rows.
- A rollback does not undo calls to other systems.

More: `oml doc logic-rules`, `oml doc security`.
