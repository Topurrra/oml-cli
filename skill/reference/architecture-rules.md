# Architecture rules

Layers, allowed references, module kinds and naming. Check a set of modules with `oml architecture`.

## Layers

- End-user: screens and processes for one group of users. It uses Core and Foundation and offers nothing to other modules.
- Core: business concepts: entities, business rules, business blocks. It knows nothing about screens of end users.
- Foundation: business-agnostic parts: libraries, themes, UI patterns, connectors to other systems.
- An application gets the layer of its highest module. The same rules apply between applications. An application is the unit LifeTime deploys.

## Reference rules

Fixes below are proposals for the user: moving elements between modules is manual work in Service Studio.

1. No upward references: a module never uses a module of a higher layer. Fix: move the used element down (to Core or a library).
2. No side references between End-user modules. Fix: move the shared element to a Core module (business) or a library (technical).
3. No cycles between modules. Fix: move the element to the right module, merge tightly coupled modules, or put the shared logic in a new module above both.
- Between domains or teams, offer only read-only public entities (for lists and search) and service actions (for writes and details). Wrap several writes that must commit together in one service action of the owning module.
- A public entity read by other modules should be "Expose Read Only" (entity `exposeReadOnly`; Yes on entities oml creates). All writes go through public actions of the owner module.

## Dependencies

- Strong (the consumer keeps the old code until it is published again; same request and transaction): server and client actions, blocks, themes, scripts, images, resources, roles, processes.
- Weak (changes reach consumers at once): entities, static entities, structures, service actions, screens.
- Server action: same transaction. Service action: remote call in its own transaction; a consumer rollback does not undo it. Choose service actions when modules have their own release cycles.
- After an incompatible producer change, consumers refresh the reference and publish again.

## Module kinds

| Kind | May contain | May not contain |
|---|---|---|
| Service | server and service actions, entities, REST, processes, timers | screens, blocks, client logic, session variables, emails, human activities |
| Library | blocks, themes, images, scripts, structures, static entities, server, client and data actions, consumed REST and SOAP | screens, entities, service actions, exposed REST or SOAP, processes, timers, roles |

- A Library may reference only other Libraries and Extensions. Its site properties are read-only. Its static entities cannot be used in queries or foreign keys.
- A theme module holds only look and feel (CSS, layouts, menu, login flow). Do not use it for shared logic.
- An integration module (`_IS`) wraps one external concept: no business entities, no screens. Map the external structures to your own; do not pass the generated ones up.

## Naming suffixes

OutSystems convention: `_CS` core service, `_BL` business logic and `_CW` core widgets are Core layer. Others: `_IS` integration service, `_Sync` synchronization, `_Eng` engine (timers, processes), `_Th` theme, `_Lib` library; mobile `_MCS`, `_MCW`, `_MUI`, `_MDB`.
`oml architecture` guesses layers from these suffixes only: `_CW`, `_UI`, `_Portal`, `_App`, `_CS`, `_BL`, `_Core`, `_API`, `_IS`, `_Sync`, `_Lib`, `_Th`, `_Theme`, `_Pat`, `_Ext`, `_Drv`; other modules by their content. Correct a wrong guess with `--layers <file.json>`.

More: `oml doc processes-timers-email`, `oml doc security`.
