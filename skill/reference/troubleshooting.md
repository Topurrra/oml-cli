# Troubleshooting

Symptoms of oml itself (apply, install, publish, LifeTime, fonts and images), then of the published app. Validation messages: `oml warn <id>`. Model API host failures: [model-api.md](model-api.md) section 17.

## Apply and install

| Symptom | Cause | Fix |
|---|---|---|
| Apply returns saved false | Spec problems or new validation errors. | Read problems and newErrors, including detail and owner paths. Fix the spec and use a new staged path. |
| Apply returns saved true with exit 1 | New errors appeared after reopening. | Read reopenNewErrors. Do not install. Fix the spec/model and apply to another path. |
| "Output must differ from source; install with 'oml install' after review." | Source and output are the same path. | Choose a separate staged file. |
| "Staged output already exists:" followed by a path | Output path exists. | Use a new staged path. |
| Install refuses validation errors | Installation checks all errors, including baseline errors. | Fix all staged errors before installing. |
| Self-test or version mismatch | Installed API behavior differs. | Stop, inspect the installed API, and rerun self-test after fixing the host. |
| A model command prints "running self-test..." and never ends | oml 1.2.0 and 1.3.0 start the self-test again inside itself on a Service Studio build not checked yet. | Use an oml version later than 1.3.0. |
| "The self-test ... did not finish in 600 s, so oml stopped." | A Service Studio call waited too long. | Open Service Studio once, wait until it has started, close it, and retry. If it stops again, run `oml doctor --text` and report the output. |
| FileLoadException for "System.Diagnostics.DiagnosticSource, Version=10.0.0.0" | Host loaded incompatible shared libraries. | Use the CLI launcher and installed libraries. See reference/model-api.md section 3. |
| Element resolves ambiguously or appears as X2 | Name collision, often with generated CRUD. | Inspect/find; use a qualified or free name. Never accept silent renaming. |
| Data deletion reports SampleRecord or anonymous CRUD records | Generated model artifacts need filtering. | Inspect consumers; retain real CRUD usages. See reference/model-api.md section 17 before changing the guard. |

## Model content

| Symptom | Cause | Fix |
|---|---|---|
| Two layouts or headers appear on a new themed screen | Factory starter widgets remain. | Current CLI clears starter widgets for explicit layout/widgets. Existing screens are not cleared by creation lists. |
| "unknown placeholder" or missing LayoutTopMenu | Layout contract or dependency is absent. | Inspect placeholder names and export the required OutSystemsUI dependencies. |
| Optional column crashes while hidden | CSS visibility does not prevent expression evaluation. | Guard indexed access with If. |
| Custom timer saves but does not run | Headless API does not validate schedule grammar. | Verify custom schedule in Service Studio's editor. |
| Extra roles after broad access | Save/reopen normalizes Registered and Anonymous. | Set both flags false for a custom-role-only gate. |
| Link/Button displays "linkOverview" | Seeded Text "link" remains beside explicit children. | Current CLI removes seeded children when children is supplied. Verify the staged tree. |
| "IPPErrorNotConnected" on paste | Cross-infrastructure protected content. | Connect to the source infrastructure, use authorized IPP cleanup, or rebuild owned content. Never bypass the check. |
| "System modules cannot be loaded" for Templates.dat | Protected shipped bundle. | Use a loadable exported module. |

## Publish and Service Studio

| Symptom | Cause | Fix |
|---|---|---|
| Publish fails with "Invalid compiler output. Object reference not set to an instance of an object" | On 11.55.92, generic Identifier types on Id caused this failure. | Use LongIntegerType for entity Id and IntegerType for static Id. Current CLI uses these types. |
| File-based publish refuses an open module | Open copy would be published instead of the file. | Close that copy, or explicitly select the open module by name. |
| Publish fails with "A concurrent publication was detected" (and the output has a version 0 `warning`) | The module is a copy extracted from an `.oap`, not the server version. | Open the server version in Service Studio (or `oml pull` it), apply the spec again to that file, then publish. |
| Publish status Blocked | Automatic merge and publish approval is missing. | Ask the user to enable automatic merge and publish in the MCP Server approval dialog. |
| Save passes but publish fails | Compilation and runtime validation are separate. | Fix the typed model or dependency; test in the target environment. |

## LifeTime

| Symptom | Cause | Fix |
|---|---|---|
| LifeTime credentials missing or HTTP 401/403 | Environment values or service-account permissions are wrong. | Set credentials securely and check permissions. Server error bodies are not echoed because they may reflect the token. |
| LifeTime HTTPS or redirect failure | Certificate, connection, or redirect refusal. | Fix the endpoint or certificate. Never use test HTTP settings for real LifeTime. |
| Start/request failure after plan creation | Operation may already have happened. | Use the reported key with status before creating another plan. |
| needs_user_intervention, including deployment_prepared | Human decision required. | Read messages and confirm before continue or abort. |
| latest excludes untagged changes | BaseApplicationVersionKey points to a tag. | Review isModified and create a confirmed tag first. |
| Deployment creation returns a bare text key | Pipeline v2 accepts non-JSON POST responses. | Preserve that key before starting; report it even if start fails. |
| LifeTime polling timeout | Plan still runs. | Read status before any new mutation. |

## Fonts and images

| Symptom | Cause | Fix |
|---|---|---|
| Image reports it cannot load libSkiaSharp.dll | Native library is missing, has the wrong architecture, or a native dependency cannot load. | Check OML_SERVICE_STUDIO; use the installed library in the root or runtimes/win-x64/native. Do not download or redistribute libraries. |
| PNG palette optimization is absent | SkiaSharp 2.88 does not expose an indexed-colour encoder. | Use normal lossless PNG or select WebP. |
| Font tables differ in head or DSIG | WOFF2 requires head.flags bit 11, checksum recalculation, and removal of invalidated signatures. | Compare other bytes exactly; validate rebuilt sfnt checksums. |
| Chrome reports Failed to convert WOFF 2.0 font to SFNT | Chromium expects the final compressed block to end at a four-byte boundary. | Add up to three zero bytes after the compressed stream; include padding in file length but not totalCompressedSize. Do not pad individual tables in the Brotli stream. |
| SVG path changes after rounding | Implicit numeric separators and compact arc flags have special grammar. | Preserve separators when signs disappear; keep arc path data unchanged. |

## Symptoms in the running app

Failures of the published app. First find where the code runs: browser or server.

| Symptom | Where to look | Cause and fix |
|---|---|---|
| A client action fails | Browser console; Service Studio debugger, client side | Fix the client flow. A server error behind it is in the error log. |
| A server action, aggregate or data action fails | Service Center error log | Read the message and module. Set Log Error = Yes on the AllExceptions handler. Show users no internal details. |
| You need a trace | `LogMessage` (client and server versions) | Service Center, Monitoring, General, filtered by module. Logging is asynchronous: lines can come out of order. Log no secrets or personal data; remove debug logs after use. |
| A screen is slow | Browser Network tab: many or slow requests | Server calls in loops or lifecycle events: run `oml check`. |
| A query or integration is slow | LifeTime Analytics, Slow Calls (a call over 200 ms by default; repeats in one action add up, so a query in a For Each is one slow call); `SLOWSQL` and `SLOWEXTENSION` lines in Monitoring, General | Fetch once with a join; suggest an index to the user. |
| Screen data stays empty | Aggregate or data action | On-demand fetch never refreshed, or read too early in OnInitialize or OnReady: `oml doc ui-reactive#lifecycle`. |
| The screen flickers or freezes | OnRender, OnAfterFetch | OnRender changes screen data, or OnAfterFetch refreshes its own source (`oml check` rules `render-assign`, `self-refreshing-aggregate`). |
## Platform problems

Servers, Service Center, LifeTime, network: run `oml library fetch` once, then `oml doc --search "<words>" --library` (OutSystems 11 docs).

| Symptom | Likely cause | Check |
|---|---|---|
| Server action works; as a service action: "Unable to connect to the remote server" | A service action is an HTTP call to the host name, which the server cannot reach: NAT loopback, SSL offload without a 443 binding, or a proxy. | The URL in the Service Center error. On the server: `Test-NetConnection <host> -Port 443`, `netsh winhttp show proxy`. |
