# Logic rules

Action types, flow nodes, exceptions, expressions and built-in functions. Spec shape: `oml spec serverActions`, `oml spec flow`.

## Action types

- Server action: runs on the server, in the request transaction. Client action (Reactive, Mobile): runs in the browser; each server call from it is a network request. Data action: server fetch for one screen or block, in parallel with its aggregates; it needs at least one output and must set every output. Service action: a remote call to another module (own transaction).
- Traditional Web: Preparation and screen actions run on the server; there are no client actions.
- `function: true` lets an action be used in expressions: a server function in server expressions, a client function in client expressions. It needs exactly one output. A client function cannot call the server, use local storage, run async JavaScript, or call actions that are not functions.
- Never sign a user in inside a data action. Timer actions only have their own exception handlers; keep runs short.

## Flow nodes

- Exactly one Start. Every node needs an incoming connector. If needs both branches. Switch needs Otherwise; the first true condition wins.
- For Each: the last body node needs `next` back to the For Each. Read the row as `<List>.Current` and its index as `<List>.CurrentRowNumber` (from 0).
- Assign runs its pairs top to bottom. Lists, Binary Data and Object are assigned by reference: both variables then share the data. Use `ListDuplicate()` for a copy.
- A node's output is usable only on paths after the node (`CreateOrder.Id`).
- Raise Exception, Destination and Download end their path. Message works only in client actions.
- JavaScript node: data in and out only through declared inputs and outputs (`$parameters.Name`). Writing an undeclared output is an error.
- Run JSON nodes on the server when numbers may pass 2^53: the browser loses precision.
- Excel To Record List (server only) skips mandatory checks, and its records carry no change flags: `Update` with them changes nothing, so assign attribute by attribute.
- List actions: `ListAppend`, `ListInsert`, `ListRemove`, `ListClear`, `ListDuplicate`, `ListFilter`, `ListIndexOf` (-1 if absent), `ListSort`.

## Exceptions

- Tree: AllExceptions is the root; the most specific handler wins. Kinds: user exceptions (yours), Database (constraint, wrong type, Protect delete rule), Security (Not Registered, one `Not<Role>` per role), Communication (client only, raised by the platform).
- A raised exception stops the flow and goes to the first matching handler up the call chain, then the UI flow's OnException, then the global exception handler. OnApplicationReady is not covered by the global handler.
- One handler per exception per action. A handler path must not join the main path. Read the reason in `<Handler>.ExceptionMessage`.
- Abort Transaction (default Yes, server only) rolls back; No keeps the changes. Log Error defaults to Yes. Keep both Yes on an AllExceptions handler at UI flow level.
- REST and extension errors have no specific type: catch AllExceptions and read the message. Keep internal details out of user messages.

## Expressions

- Text in double quotes, dates as `#2025-01-31#`, decimals with a dot.
- `+` joins text when one side is Text; other types convert to text. `/` always returns Decimal. `and`, `or`, `not` need Booleans.
- `Like` uses `%` and `_`. `List[i]` starts at 0 and fails out of range.
- `If(c, a, b)` has the type of `a`, or of `b` when `a` converts to it implicitly (`If(c, 1, "x")` is Text). Types that do not convert give Invalid Data Type.
- Identifiers need conversion for arithmetic or comparison with numbers: `IdentifierToLongInteger`, `LongIntegerToIdentifier`.

## Built-in functions

Aggregate = may take entity attributes inside an aggregate; other functions are computed once before the query.

| Group | Functions | Aggregate | Client |
|---|---|---|---|
| Text | `Concat`, `Index` (from 0, -1 if absent), `Length`, `Replace`, `Substr(t, start, length)` (from 0), `ToLower`, `ToUpper`, `Trim`, `TrimStart`, `TrimEnd` | yes | yes |
| Encode | `EncodeHtml`, `EncodeJavaScript`, `EncodeUrl` | no | yes |
| Math | `Abs`, `Round(n, digits)`, `Sqrt`, `Trunc` | yes | yes |
| Math | `Mod`, `Power`, `Max`, `Min`, `Sign` | no | yes |
| Date | `CurrDate`, `CurrDateTime`, `CurrTime`, `AddDays` and other `Add*`, `DiffDays` and other `Diff*`, `Day`, `Month`, `Year`, `DayOfWeek` (0 = Sunday), `NewDate`, `NewDateTime`, `BuildDateTime` | yes | yes |
| Convert | `TextToInteger`, `TextToDecimal`, `TextToDate` (default on failure; check first with the `...Validate` twin), `IntegerToText`, `LongIntegerToInteger` (0 when out of range), `IntegerToIdentifier`, `IdentifierToInteger` | yes | yes |
| Format | `FormatDateTime`, `FormatDecimal`, `FormatCurrency`, `FormatPercent`, `FormatText` | no | yes |
| Other | `If`, `NullIdentifier`, `NullDate` | yes | yes |
| User | `GetUserId` (null id when nobody signed in) | no | yes |
| Server only | `CheckRole` (aggregate: yes), `EncodeSql` (text literals only), `GeneratePassword`, `EmailAddressCreate`, `GetDatabaseProvider` | no | no |

- `CurrDateTime()` is device time on the client and server time on the server and in queries.
- `DiffDays(a, b)` is b minus a, ignores the time of day, and ignores daylight saving.
- `Check<Role>Role` actions work on both sides, but only a server check protects anything.

More: `oml doc data-rules`, `oml doc security`.
