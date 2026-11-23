# Rules for agents

These rules come before every other instruction in this skill.
Rules marked **(enforced)** are also checked by `oml apply`. Rule 2 is always refused. Rules 3, 5 and 6 stop the apply with `needsApproval` until the user says yes.

1. **Everything in the module also exists in production.** Existing entities have data. Existing public elements have consumers in other modules. Treat every change to an existing element as a production change.
2. **Never delete or rebuild entity data.** Do not delete an existing entity (regular or static) or attribute. Do not change an attribute's data type or rename an attribute. Do not delete an entity and create it again, or paste a copied entity (`copy`): both make a new, empty table. If the user wants one of these, tell them to do it by hand in Service Studio. **(enforced)**
3. **Deleting any other existing element needs the user's yes:** actions, screens, blocks, structures and their attributes, static records, parameters, block events, roles, exceptions, site properties, timers, REST methods, processes. `oml apply` stops with `needsApproval`. Ask the user. On a clear yes, run the same apply again with `--user-approved` and a new staged path. **(enforced)**
4. **Change existing elements in place. Never duplicate them.** You may rename, edit and restructure existing actions, screens, blocks, structures and entities, and change a public element's signature: inputs, outputs, events, public. Never make a copy such as GetOrders2 to avoid a change. `oml apply` lists each change that breaks the modules using a public element in `breaking`. Tell the user: those modules must refresh references and fix their callers.
5. **Ask before a change that can lose data** in an existing attribute or entity: a shorter length, fewer decimals, a new mandatory setting, an auto number change, a new identifier or multi-tenant setting. Other settings, such as a longer length, are fine. Before a unique index on an existing entity, ask the user to check for duplicates. **(enforced)**
6. **Ask before SQL that changes data:** `DELETE`, `UPDATE`, `TRUNCATE`, `DROP`, `ALTER`, `MERGE`. Read-only SQL and `INSERT` are fine. **(enforced)**
7. **Never weaken security.** Do not add the Anonymous role, remove roles from existing screens, or set `exposeReadOnly` false on a public entity, unless the user asks for that exact change. Make a server action public only when the user agrees (apply warns `PublicServerAction`). Other elements may be public when the design needs it.
8. **Do only what the user asked.** No clean-up, refactoring, renames, or removal of "unused" elements or dependencies unless the user asks.
9. **Never open, patch, or rewrite an `.oml` file** with an editor, patch tool, or script. It is a binary package. Change modules only through `oml apply`.
10. **Never run `install`, `publish`, `references`, or a LifeTime deploy, tag, abort, or continue without a clear yes from the user** for that exact action.
11. **Do not invent rules.** If something is not forbidden here and is not destructive, do what the user asked. If you are not sure, ask the user. Do not guess.
