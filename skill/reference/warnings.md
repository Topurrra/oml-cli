# Validation messages

Common errors and warnings. Look one up with `oml warn <id or title words>`.

## Read the report

- `newErrors`: not saved. `reopenNewErrors`: saved, do not install. Each of `newWarnings` is a defect: fix or explain it.
- `CustomObjectWarning` and `CustomObjectError` are generic plugin ids: look up the `message` (`oml warn security warning`).

## Data model

| Id | Title | Cause | Fix |
|---|---|---|---|
| `InvalidEntity_OnlyAuto` | Invalid Entity | Only an auto-number Id. | Add a real attribute. |
| `InvalidAttribute_BadExternalRule`, `InvalidAttribute_BadExternalEntIDRule` | Invalid Attribute | Reference to an extension or unreferenced entity. | Set `deleteRule` to Ignore. |
| `InvalidPropertyValue_DefaultValue` | Invalid Property Value | Default is not a literal. | Use a literal; compute the rest in logic. |

## Flows and logic

| Id | Title | Cause | Fix |
|---|---|---|---|
| `RequiredConnector_In`, `RequiredConnector_Out` | Invalid <kind> Flow | Node without an incoming or outgoing connector. | Fix the `next`/`true`/`false`/`cycle` targets; run `oml spec check`. |
| `InvalidFlow_BadForEach` | Invalid Action Flow | Loop body does not return to the For Each. | Set `next` of the last body node to the For Each. |
| `InvalidDataAction_OutputMustBeSet`, `InvalidDataAction_NoOutputs` | Invalid Data Action | Data action output missing or never set. | Declare outputs and assign each one. |
| `InvalidFunction_UnsupportedServerAction`, `InvalidFunction_UnsupportedExecuteAction` | Invalid Client Function | Client function calls the server or a non-function. | Set `function` to false, or remove the call. |

## Expressions

| Id | Title | Cause | Fix |
|---|---|---|---|
| `ExpressionUnknownObject` | Invalid Expression | Name not in scope (renamed aggregate, wrong path, loop label). | Use final names and `<List>.Current`; check with `oml tree`. |
| `ExpressionUnknownFunction` | Invalid Expression | Function does not exist. | See `oml doc logic-rules`; errors are `<Handler>.ExceptionMessage`. |
| `ExpressionInvalidVariableType`, `ExpressionInvalidExpressionType` | Invalid Data Type | Value type does not fit. | Convert it; bind an Identifier with a Dropdown, not an Input. |
| `ParserUnexpectedElement`, `ParserUnterminatedStringLiteral` | Invalid Expression | Syntax error, often text without quotes. | Write text as `"\"Text\""` in the spec. |

## UI and properties

| Id | Title | Cause | Fix |
|---|---|---|---|
| `RequiredPropertyValue_PropertyIn`, `RequiredPropertyValue_Property` | Required Property Value | Empty mandatory property (Variable, Source, On Click, Source Block) or no callee. | Set it; find a callee with `oml find`. |
| `InvalidVariable_MultiWidget`, `InvalidVariable_MultiWidgetWarning` | Duplicate Bind | One variable on two inputs. | One variable per input. |
| `InvalidDestination_Link`, `InvalidParameter_InLink` | Invalid Link | Navigate link to an action, or with a record or list argument. | Navigate to a screen; pass the identifier. |

## SQL

| Id | Title | Cause | Fix |
|---|---|---|---|
| `InvalidSQL_UnknownEntity`, `InvalidSQL_UnknownAttribute`, `InvalidSQL_UnknownParam` | Invalid SQL | Name does not exist. | Use real `{Entity}`, `[Attribute]` and `@Param` names. |
| `SQL_WildcardNotValidated` | Unexpected SQL | `SELECT *`. | Use `SELECT {Entity}.*`. |
| `SQL_ColumnsMismatch`, `SQL_WrongMapping` | Unexpected SQL | SELECT columns differ from outputs. | Align columns and `outputs`. |
| `SQLInjection`, `SQL_InputParameterPropagation` | SQL Injection | `expandInline` parameter. | Set `expandInline` false; inline only fixed text. |

## Warnings

| Id | Title | Cause | Fix |
|---|---|---|---|
| `NoErrorHandlingInFlow` | No Exception Handling | UI flow without OnException, no global handler. | Add `onException` for the flow or set `globalExceptionHandler`. |
| `NoErrorHandlingForAllExceptions` | Missing 'AllExceptions' | OnException lacks an AllExceptions handler. | Add an `exceptionHandler` for AllExceptions. |
| `InvalidErrorHandler_AbortTrans`, `InvalidErrorHandler_Log` | Unexpected Exception Handler, Audit Suggestion | UI flow handler with Abort Transaction or Log Error off. | Set `abortTransaction` and `logError` true. |
| `UnusedUserAction`, `UnusedAction`, `UnusedScreenData` | Unused User Action, Unused Action, Unused Aggregate | New element that nothing uses. | Use it or leave it out; never delete existing ones. |
| `UnusedElement` | Unused Element | A part nothing reads, such as a JavaScript node input the script never reads (`$parameters.<Name>`) or an output never assigned. | Read it in the script or remove it from the node's inputs (flow nodes may change). Keep the parameters of existing actions (rule 3). |
| `UnusedEventHandler`, `UnusedArgument`, `UnusedPlaceholderArgument` | Unused Event Handler, Unused Argument, Unused Placeholder Content | Value for an event, input or placeholder that does not exist. | Fix the name or remove the value. |
| `WebBlock_CustomEventNotTriggered` | Unused Block Event | Block event that nothing fires. | Fire it with `triggerEvent`, or drop it from `events`. |
| `ConsecutiveServerInteractions`, `IteratedServerInteraction` | Performance Warning | Client action calls the server often or in a loop. | Do the work in one server action. |
| `ClientActionWithServerRequestsOnLifecycleEvent` | Performance Warning | Server call in OnInitialize. | Fetch with an aggregate or data action. |
| `ClientActionWithModelAssignmentOnLifecycleEvent`, `SelfRefreshingAggregate` | Potential Loop at runtime | OnRender changes data, or OnAfterFetch refreshes its source. | Remove that assignment or refresh. |
| `ClientActionWithScreenDataAccessesOnLifecycleEvent` | Coherence Suggestion | OnInitialize or OnReady reads fetched data. | Read it in `onAfterFetch`. |
| `ClientActionWithVariablesUsedInScreenDataOnLifecycleEvent` | Coherence Suggestion | Filter variable set after the fetch. | Assign it in OnInitialize. |
| `DataSetMustBeRefreshed`, `DataActionMustBeRefreshed` | Expected Aggregate Refresh, Expected Data Action Refresh | Block source uses an input. | Set the block's `onParametersChanged` to an action with `refreshData`. |
| `OnDemandDataSetMustBeRefreshed` | Expected Fetch for OnDemand data | On-demand source never refreshed. | Add `refreshData`, or drop `fetch`. |
| `EntityActionClientSide` | Security Warning | Client action calls an entity action. | Call a server action that checks and saves. |
| `ServerActionInAnonymousScreen` | Security Warning | Anonymous screen uses a server action. | Make a new screen non-anonymous; else tell the user. |
| `ExpressionComparingDifferentIdentifiers`, `ExpressionUnexpectedExpresssionType` | Unexpected Data Type | A number compared with an identifier. | Convert (`LongIntegerToIdentifier`); static `Id` is Integer. |
| `InputMandatoryMismatch`, `InputMaxLengthMismatch_Input` | Coherence Suggestion | Input Mandatory or Max Length differs from the attribute. | Match the attribute. |
| `CrossJoinQuery`, `CrossJoinDataSet` | Scalability Suggestion | Two sources without a join. | Add the join in `joins`. |
| `InvalidAttribute_EntRefWithoutIdx` | Scalability Suggestion | Reference attribute without index. | Add it to the entity's `indexes`. |
| `DisconnectedDestination` | Unexpected Link | Traditional link to a screen that is not a Frequent Destination. | Harmless; tell the user. |
| `PublicServerAction` | (oml) Public server action | Server action made public (rule 7). | Ask the user. If they say no, remove `public` from the spec and apply again from the same source. |
| `UniqueIndexOnExistingEntity` | (oml) Unique index | Unique index on an existing entity. | Tell the user: publish fails if production has duplicate values. Ask them to check before publishing. |
| `ARR_MissingRef`, `ARR_MissingElem`, `ARR_IncompatibleRef`, `ARR_NoSigRef` | (no title) | Producer changed, removed or not published. | Tell the user; after a yes, `oml references`. |

## Environment: stop and tell the user

| Id | Title | Cause | Fix |
|---|---|---|---|
| `ModelFeature_NotEnabledInServer`, `ModelFeature_NotKnownInServer` | Unavailable Feature | Environment lacks a feature the module uses. | Stop: the environment needs an upgrade or setting. |
| `IncompatibleVersions` | Incompatible Versions | Service Studio and server versions differ. | Stop: update Service Studio or the server. |
| `Load_HigherLUVRequired` | Incompatible Service Studio | Module saved by a newer Service Studio. | Stop: the user must update Service Studio. |
| `UnknownMessage` | Unidentified Message | Module checked by a newer Service Studio. | Stop: the user must update Service Studio. |
| `LicenseViolated` | Licensing Error | License limit or expiry. | Stop: tell the administrator. |
| `IPPErrorNotConnected` | Intellectual Property Error | Protected module outside its infrastructure. | Stop: never bypass; ask for an authorized export. |
| - | Concurrent Publishing, Locked Module, Communication error, Required Permission, Publishing Error, Internal Error | Server busy, unreachable, or no permission. | Stop: retry once, run `oml doctor`, report it. |

oml's own messages: `oml doc troubleshooting`.
