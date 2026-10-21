# REST rules

Exposed and consumed REST APIs: URLs, errors, status codes, authentication, types. Spec shape: `oml spec restServices`, `oml spec restClients`; OpenAPI export: `oml openapi`.

## Choose

- Other modules in the same environment: use a service action, not REST.
- Other systems: an exposed REST API. Name it by version (`v1`). Never rename, delete or break the signature of a published method (rules 3 and 4); put a breaking change in a new API (`v2`).

## Exposed APIs

- Request order: HTTPS and internal-network checks, OnRequest, OnAuthentication, parameter parsing and checks, the method flow, OnResponse (always runs).
- URL: `https://<server>/<module>/rest/<api>/<method path>`. Use plural nouns, no trailing slash, no `.` in the last segment, no `;`, `?` or `=` in the path.
- A `{Name}` in the path needs a mandatory input of that name. Inputs not in the path come from the query string.
- One body parameter per method. Use a structure for several fields. A structure body needs `Content-Type: application/json` from the caller.
- GET methods cannot have a body input.
- A bad request (wrong type, missing mandatory input, unknown path or verb) returns 400, 404, 405, 406 or 415 with an `Errors` list. The flow does not run.
- An unhandled exception in the flow rolls back the database and returns 500 with the exception message.
- Other status codes (201 after a create, 404 when not found): call `SetStatusCode` just before End or before raising. It comes from the HTTPRequestHandler extension: add a `references` entry.
- Exposed APIs allow calls from any web origin (CORS). CORS protects nothing.
- Descriptions of the API, methods and parameters become its documentation.
- By default a parameter that holds its default value is left out of the JSON.
- PATCH cannot tell "not sent" from a default. Use unlikely defaults per field and update only fields that differ.

## Authentication of exposed APIs

- Default is None. That is normal: a gateway such as Kong checks callers. The Authentication property and all REST callbacks (OnRequest, OnAuthentication, OnResponse) are Service Studio only: if the user wants them, tell the user to set them.
- Basic: the OnAuthentication callback gets `Username` and `Password` and signs the user in; then `GetUserId()` and role checks work. It applies to every method and is only safe over HTTPS.
- Custom: OnAuthentication runs before every method. Read headers with `GetRequestHeader` (HTTPRequestHandler extension, needs a `references` entry); raise an exception to refuse (401).
- 401 means "who are you?"; 403 means "known caller, no permission". Use `SetStatusCode` for 403.

## Consumed APIs

- A consumed method is called like a server action: `execute` with `Client.Method`.
- Every response with status 400 or higher raises an exception. Put an AllExceptions handler around the call and read the message.
- OnAfterResponse can read the status and body before the exception, and can change them.
- Built-in authentication: Basic and OAuth 2.0 client credentials. For other schemes, add the header in OnBeforeRequest. A hand-made Authorization header is replaced when built-in auth is set. Authentication, OAuth, OnBeforeRequest and OnAfterResponse are Service Studio only: tell the user to set them.
- Keep tokens in one module: get, cache with expiry, refresh; retry once at most after 401.
- Read secrets from secret site properties. Never write them in the spec.
- Query parameters only as `key={value}`.
- Without a charset in Content-Type, the response is read as ISO-8859-1. The Service Studio test button uses UTF-8, so a test can pass and runtime fail.
- Define your own structures for the data you pass on. Do not expose the API's generated structures to other modules.

## JSON types

Integer numbers map to Long Integer, numbers with a fraction to Decimal, ISO date-times to Date Time (converted to server time), `YYYY-MM-DD` to Date, true and false to Boolean, base64 data URIs to Binary Data, everything else to Text. Structure attributes can have their own JSON name.

More: `oml doc security`.
