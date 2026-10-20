Expect: entities:Supplier:1, serverActions:Supplier_*:2, screens:SupplierList:1

## Task (identical for every agent)
In the OutSystems 11 module, build a small CRUD feature for suppliers.

- Entity `Supplier` with these attributes:
  - `Name`: Text, length 50, mandatory.
  - `Rating`: Decimal.
  - `IsActive`: Boolean, default True.
- Two server actions: `Supplier_CreateOrUpdate` and `Supplier_Delete`. `Supplier_CreateOrUpdate` takes the Supplier fields as input parameters and creates or updates the record. `Supplier_Delete` deletes the record by its identifier. Each one calls the matching entity action of `Supplier`.
- A Reactive list screen `SupplierList` in the module's main flow (`MainFlow`), using the module's existing layout. It shows a table of suppliers with the columns `Name`, `Rating` and `IsActive`.

Build only what this task names. Do not add other entities, actions or screens. Do not change the default screen.

Done when: the module has zero validation errors, the entity, both server actions and the screen exist, and you report every tool call that failed and why, and anything you could not build.
