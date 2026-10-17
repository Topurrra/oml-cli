# ODC with the OutSystems MCP

This file belongs to the `outsystems-oml` skill. Read it only for ODC modules.

Use this appendix only for modules in an ODC tenant.
The remote MCP endpoint is `https://<tenant>/mcp`. The harness signs in through OAuth.
Use the live `tools/list` as the source of truth for tool names and arguments. Read it before the first call.

### Read an app

- Use per-type context lookups for entities, actions, screens, structures, roles, themes, and connections.
  Read app references too. Independent lookups can run in parallel.
- Pass the app name directly to `app`. Names resolve without case sensitivity.
- With app set, `owned_only` defaults true. Use `owned_only: false` to include library elements, such as OutSystemsUI.
- References may list only kinds in `indexedKinds`. A missing kind is unknown, not absent. Report that limit.
- Use Mentor for reads only when context lookups cannot answer, such as flow internals.

### Edit and publish through Mentor

A session holds one app's OML. Each turn returns a `runId` to poll until terminal status.
Run one turn at a time per session. Preserve the session handle byte for byte and reuse it for the whole task.

1. Group the data model into one turn.
2. Publish in a separate turn.
3. Build screens after publishing the data model.
4. Use the strongest model tier for multiple screens or a whole data model.

A `succeeded` turn only means the turn ended. Read the terminal payload for applied changes, validation errors, and turn errors.
Publishing uses a Mentor turn asking to publish. Its note must be at most 500 characters.
A publish timeout may still succeed. A failed result with `indeterminate: true` does not prove failure.
Check the publication key or revision history before publishing again.

After a Mentor timeout, retry the same app in the same session with a narrower prompt.
Never create another app to avoid a timeout. Never pass a placeholder such as `"null"` as an app key.
Poll Mentor at its advertised interval, at least 30 seconds apart. Poll other operations every 5–15 seconds.
Give the user one short update per pause.

### Deploy

- A deployment without a pinned `build_key` or `revision` requires `from_env`.
- Poll the operation to terminal status. Read deployment messages on failure.
- Run deployment-impact analysis before asking to deploy.
  Trust the verdict only when `impactKnown` is true and `report` is present. An empty report does not prove no impact.

### Confirmation, identifiers, and retries

Confirm before creating an ODC app, publishing, deploying, rolling back, or uploading/deleting an external library.
Restate the operation and wait for approval. Mentor editing turns and reads need no confirmation.
For O11, follow the confirmation rules in section 2, including install, reference refresh, LifeTime mutations, and data deletion.

Never guess `app_key`, `env_key`, session handles, or file paths. Ask for missing values.
Report stable identifiers with names: asset key for ODC apps, env_key for environments, staged and backup paths for O11 files.

| Failure category | Response |
|---|---|
| `AuthError` | Sign in again once. |
| `CapacityError` | Retry with backoff. |
| `ValidationError` | Fix the call. |
| oml exit 1 | Fix the spec or model; inspect remote status first if a LifeTime operation may have run. |
| oml exit 2 | Fix usage, credentials, or environment. |

Retry by category instead of matching message text.
