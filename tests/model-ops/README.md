# Model API changes as specs

`tests/model-ops-check.ps1` tests everything here. [examples.md](examples.md) lists each example of the official
OutSystems skill (examples/*.cs, integration-examples/*.md) with the spec that does it; it is generated from
[examples.json](examples.json). [../model-api/coverage.md](../model-api/coverage.md) is the member-level map.

## Generic operations (edits)

`add`, `remove`, `move`, and `set` with `type`: see `oml spec add` (skill/reference/spec.md, "Generic model operations").
Positive case: [seed.json](seed.json) then [ops-positive.json](ops-positive.json). Refusals, each with the message it
must give: [refusals.json](refusals.json) (production rules 2 to 5, unknown type, unknown member, bad enum, bad
number, taken name, unknown sibling, missing values).

## Changes agents need

From the official skill's examples, the usage log and the coverage map. "Before" is oml before this work.

| # | Change | Source | Before | Now |
|---:|---|---|---|---|
| 1 | Add an attribute at the start of an entity | AddAttributeAtBeginning | no | `add` with `first` (recipe attribute-position-reorder) |
| 2 | Add an attribute between two others | AddAttributeBetweenExistingAttribute | no | `add` with `after` / `before` (recipe) |
| 3 | Reorder the attributes of an entity | ReorderAttributes | no | `move` |
| 4 | Reorder parameters, widgets, records | coverage map (ISequence) | no | `move` (same code; only attributes tested) |
| 5 | Local storage (client) entity and its attributes | AddClientEntity | no | `add` `IClientEntity`, `add` `IEntityAttribute` |
| 6 | Point an existing link or button at another screen or action | AddEditScreen, AddListScreen | no (`set` refused `onClick`) | `set` with `onClick` |
| 7 | Arguments of that event | same | no | `set` with `{ "destination", "args" }` (not tested) |
| 8 | Dropdown bound to a record list | AddDropdownWidgetToEditScreen | documented keys did not work | `labels`, `values` on one item (doc fixed, tested) |
| 9 | ListAppend, ListClear, ListInsert, ListRemove in a server action | usage, osui-verified-blocks.md | no: not referenced in a new module | `execute`; oml adds the (System) action from Service Studio's tutorial module |
| 10 | LogMessage, CommitTransaction, AbortTransaction | usage | no | same as 9 (LogMessage tested) |
| 11 | ListAppend in a client action, when the module lacks it | usage | no | refused with the steps: Service Studio adds (System) client actions |
| 12 | Decimal, Long Integer, Double values | coverage map | `set` took whole numbers only | `set`, `add` |
| 13 | Date-time values | coverage map | no | ISO 8601 text |
| 14 | A property that points at an unnamed element (Folder, connector, email, attribute) | coverage map (22 members) | no | `{ "element": "<path>" }` |
| 15 | A child no key makes (SOAP and REST parts, process callbacks, site property flags) | coverage map (108 members) | no | `add` with `type` and `args` (site property and client entity tested; the rest is reachable, not tested) |
| 16 | Take out something added earlier in the same spec | generic | `delete` | `remove` (refuses existing elements) |
| 17 | Check types, members and values before apply | small models | shape only | `oml spec check` resolves `type` and checks each key and value |
| 18 | An unknown member names the closest ones | small models | "has no settable" | closest names, the type's keys, `oml model-types <Type>` |
| 19 | An unknown enum value names the closest one | small models | the list | closest name and the list |
| 20 | Production rules on any member a spec reaches | brief | rules per key | the before/after check of `oml review` after generic operations |
| 21 | Rename a group-by attribute | ChangeGroupBy | yes (`rename`) | tested |
| 22 | Rename an aggregated attribute | ChangeAggregatedAttribute | yes | tested |
| 23 | Change a group filter | ChangeGroupByFilter | yes (`set`) | tested |
| 24 | Use a public element of another module | ImportPublicElement | yes (`references`) | tested, by name |
| 25 | Take a role off a screen | RemovePermission | yes (`setAccess`) | tested; rule 7: only when the user asks |
| 26 | Foreign key to the (System) User entity | int-06 | yes | tested |
| 27 | Re-lay out a flow | LayoutActionFlow | yes (`tidyFlows`) | tested (oml places the nodes; no hand coordinates) |
| 28 | Login from a client action | AddClientActionDoLogin | no | not possible offline: the (System) or Users reference comes from the platform server (Service Studio, or `references` with `module` from a module that has it) |
| 29 | Charts and OutSystems UI blocks (DonutChart, DatePicker, DropdownSearch) | AddDashboardScreenWithDonutChart, AddDatePickerWidgetToEditScreen | yes with the producer `.oml` (`references`) | not tested here: no OutSystems UI or Charts module on this machine |
| 30 | A comment node with an arrow to a node | AddListScreen | no | not covered: oml makes comment nodes without arrows |
| 31 | A built-in event's parameter type on an existing table (OnSort ClickedColumn) | AddListScreen | at creation (`onSort`) | not tested on an existing table |
| 32 | Elements with keys the agent chooses | int-01 to int-04 | no | out of scope: oml makes the keys |
| 33 | Delete or rename existing entities, attributes, actions, parameters, screens | Delete*.cs, SetName, SetAttributeName | refused | refused (rules 2 and 3), tested |
| 34 | Change type, length, mandatory or identifier of an existing attribute or entity | ChangeAttributes, ChangeIdentifierAttribute | refused | refused (rule 5), tested |
| 35 | A mandatory input on an existing public action | AddParameter | refused | refused (rule 4), tested |
| 36 | Read the module | ProbeModelWithException, ProbeModuleInventory | `oml inspect`, `describe`, `query` | not a spec: reading needs no change |

Found on the way: `addParts` adds an aggregate that widget expressions in the same spec cannot read yet; give the
aggregate in one spec and the widgets in the next, or create both with the `screens` key.
