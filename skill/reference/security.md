# Security rules

The Reactive security model, roles, user identity, SQL, escaping and secrets.

## How Reactive security works

- The user can read and change everything that runs in the browser: client actions, client variables, inputs, hidden widgets.
- Every server action, data action and aggregate that a screen uses becomes a generated REST endpoint. On a screen with roles, only signed-in users can call it. On an Anonymous screen, anyone can call it with any arguments.
- So every check that protects data must run on the server. Hiding or disabling a button protects nothing.
- A role check in JavaScript (`$public.Security.checkIfCurrentUserHasRole`) only shapes the UI; check again on the server.
- Screen aggregates and data actions send the fields the screen uses to the browser: never fetch password hashes, tokens or rows the user may not see.

## Screens and roles

- Give screens custom roles. Registered means any user signed in to any app on the same server.
- Anonymous only for screens that must be public (login, sign-up, password reset), and only when the user asks (rule 7).
- Make one screen per role, not one screen with parts hidden by role.
- Each custom role creates `Check<Role>Role`, `Grant<Role>Role`, `Revoke<Role>Role` and the exception `Not<Role>`. The check uses the signed-in user when `UserId` is empty and returns False for inactive users.

## User identity

- Call `GetUserId()` in the server action or in the aggregate filter. Never take a user id from a client input, a client variable or a block argument: the client can send any id.
- Never trust any client argument that decides which rows come back. Check on the server that the user may see them.
- Client variables never decide access or a step the server must trust: the user can change them.
- Start each server action that writes data with an identity and role check; raise `Not<Role>` when it fails.
- Never call entity actions (create, update, delete) from a client action. Call a server action that checks and validates first.
- Do not show the User entity or other system entities in aggregates of Anonymous or Registered screens.

## SQL and output

- Keep `expandInline` false. Bound parameters are safe. Inline text must be fixed text chosen by the app; use `EncodeSql` only on text literals, and for `IN` lists `BuildSafe_InClauseIntegerList` or `BuildSafe_InClauseTextList` from the Sanitization extension (needs a `references` entry). Never inline user text after `ORDER BY` or `GROUP BY`.
- `VerifySqlLiteral` is deprecated: use bound parameters or the `BuildSafe_InClause` functions.
- Expressions are escaped by default. User text in HTML or JavaScript needs `EncodeHtml`, `EncodeJavaScript`, or `SanitizeHtml` (Sanitization extension, needs a `references` entry). Never build JavaScript source from user input.
- Traditional Web: keep Expression Escape Content = Yes (default). No only for fixed app text or a `SanitizeHtml` result.
- Redirect URLs built from inputs need `EncodeUrl` and a known target.

## Secrets

- Store API keys, tokens, passwords and other credentials of external services in secret site properties (Platform Server 11.25 or later), set per environment. The secret flag and REST log redaction are Service Studio only: tell the user to set them. Never put secrets in flow literals, client variables, screen variables or logs.

## Exposed REST APIs

- An exposed REST API with Authentication = None is normal: a gateway such as Kong checks the caller. Do not report it and do not add authentication unless the user asks.

More: `oml doc rest`, `oml doc data-rules`.
