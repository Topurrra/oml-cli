# Rules for agents

These rules come before every other instruction in this skill. This is the full list: do not invent other restrictions.
Rules marked **(enforced)** are also checked by `oml apply`. It refuses the spec and tells the user what to do by hand.

1. **Everything in the module also exists in production.** Existing entities have data. Existing public elements have consumers in other modules. Treat every change to an existing element as a production change.
2. **Never delete an existing entity**, regular or static. If the user wants it removed, tell them to delete it manually in Service Studio. **(enforced)**
3. **Never rename or delete existing elements:** entities, attributes, static records, structures, actions and their parameters, screens, blocks, roles, exceptions, site properties, timers, REST methods, processes. Add new ones instead. Tell the user to rename or delete by hand. You may still change flow nodes, widgets and local variables. **(enforced)**
4. **Never change the signature of an existing public element:** inputs, outputs, their types, or mandatory settings. Other modules in production call it. Add a new action instead, and tell the user.
5. **Never change the type, length, decimals or mandatory setting of an existing attribute.** Production data can be cut off or fail to convert. **(enforced)**
6. **Never write SQL that changes data:** `DELETE`, `UPDATE`, `TRUNCATE`, `DROP`, `ALTER`, `MERGE`. Read-only SQL and `INSERT` are fine. **(enforced)**
7. **Never weaken security.** Do not add the Anonymous role, remove roles from existing screens, or make an element public, unless the user asks for that exact change.
8. **Do only what the user asked.** No clean-up, refactoring, renames, or removal of "unused" elements or dependencies unless the user asks.
9. **Never open, patch, or rewrite an `.oml` file** with an editor, patch tool, or script. It is a binary package. Change modules only through `oml apply`.
10. **Never run `install`, `publish`, `references`, or a LifeTime deploy, tag, abort, or continue without a clear yes from the user** for that exact action.
11. **Do not invent rules.** If something is not forbidden here and is not destructive, do what the user asked. If you are not sure, ask the user. Do not guess.
