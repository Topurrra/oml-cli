namespace OmlCli;

// Everyday developer words for OutSystems concepts, for search (Search.cs, DocSearch.cs).
// How it was built: a walk over the concepts that the knowledge files and spec.md name (data elements and types,
// logic and flow nodes, widgets, UI patterns and lifecycle events, security, integrations, processes, architecture
// layers, tools), not over failed queries. For each concept we wrote the words a developer from React, .NET, SQL or
// general web work would use for it. A phrase counts when all of its words are in a query; it then adds the concept's
// own words (Adds) to the query at SearchOptions.ConceptWeight. Each concept is also a search entry of its own: its
// everyday words are its title, and it opens the concept's main section (Target, an id of `oml doc --search-list`).
// Avoid an everyday word that is also an OutSystems word with another meaning (class, callback, select, build, render):
// write it as a phrase instead (select query). `oml doc --search-verify` checks that every Target exists.
static class SearchGlossary
{
    internal static readonly (string Concept, string Target, string Adds, string Everyday)[] Entries =
    {
        // Data
        ("Entity", "data-rules#entities-and-attributes", "entity entities", "database table; db table; sql table; data model; db model; record type; schema; orm"),
        ("Attribute", "data-rules#entities-and-attributes", "attribute attributes", "column; db column; database field; table field; db field"),
        ("Static entity", "data-rules#static-entities", "static entity records", "enum; enumeration; lookup table; constant list; fixed values; list of values"),
        ("Structure", "spec#data-model", "structure structures", "dto; struct; custom type; type definition; view model"),
        ("Relationship", "data-rules#relationships-delete-rules-indexes", "Identifier relationship reference attribute", "foreign key; fk; primary key; relation between tables; one to many; many to many"),
        ("Delete rule", "data-rules#relationships-delete-rules-indexes", "delete rule Protect Ignore", "cascade; cascading delete; on delete"),
        ("Index", "data-rules#relationships-delete-rules-indexes", "index indexes unique", "db index; database index; unique constraint; composite key"),
        ("Data types", "data-rules#types-and-null", "type Text Integer Decimal Boolean Date null", "string; varchar; int; float; double; bool; datetime; timestamp; nullable; undefined"),
        ("Aggregate", "data-rules#aggregates", "aggregate fetch", "select query; select statement; query builder; linq; orm query; fetch data; read data; get data; load data"),
        ("SQL node", "data-rules#sql", "SQL", "raw query; raw sql; by hand; handwritten query; stored procedure; native query"),
        ("Transaction", "data-rules#transactions", "transaction CommitTransaction AbortTransaction", "rollback; atomic; unit of work"),
        ("Paging", "ui-reactive#tables-lists-sorting-paging", "Pagination StartIndex MaxRecords paging", "pager; next page; page size; infinite scroll; limit offset"),
        ("Sorting", "ui-reactive#tables-lists-sorting-paging", "sort sorting dynamic", "order by; sortable column; sort column"),
        ("Search and filter", "recipe:list-search-paging", "search filter", "where clause; search box; filter list"),
        ("Group by", "spec#queries/groupby", "groupBy Count Sum", "group by; totals; sum up; count per"),
        // Logic
        ("Server action", "logic-rules#action-types", "server action", "backend function; backend method; service method; api handler; controller; business logic"),
        ("Client action", "logic-rules#action-types", "client action", "frontend function; frontend logic; ui logic; browser code; click handler"),
        ("Data action", "logic-rules#action-types", "data action", "custom fetch; load from server"),
        ("Service action", "architecture-rules#dependencies", "service action", "microservice; remote call; internal api; rpc"),
        ("Function", "logic-rules#action-types", "function", "helper function; utility function; pure function; computed value"),
        ("If and Switch", "spec#actions-and-flow-nodes/if", "If Switch condition", "if else; conditional; case statement"),
        ("For Each", "spec#actions-and-flow-nodes/foreach", "For Each Current", "loop; iterate; for loop; foreach; each item"),
        ("Assign", "spec#actions-and-flow-nodes/assign", "Assign", "set variable; assignment"),
        ("Default value", "spec#data-model/variable", "default Variable literal", "default value; initial value; preset value"),
        ("Exception handler", "logic-rules#exceptions", "exception handler onException AllExceptions", "try catch; catch error; error handling; crash; error page; friendly message; global error; fails; failure"),
        ("Raise exception", "spec#actions-and-flow-nodes/raiseexception", "Raise Exception user exception", "throw; throw error; custom error"),
        ("Expressions", "logic-rules#expressions", "expression", "formula; inline code; string concatenation; concatenate"),
        ("Date functions", "logic-rules#built-in-functions/date", "CurrDate AddDays DiffDays", "now; today; current date; date difference; add days"),
        ("Format functions", "logic-rules#built-in-functions/format", "FormatCurrency FormatDecimal", "currency; number format; format number; money"),
        ("JavaScript node", "spec#actions-and-flow-nodes/javascript", "JavaScript", "custom js; run javascript; js code"),
        ("Script element", "best-practices#javascript-and-css", "Script requiredScripts", "javascript library; js library; npm package; third party script; cdn"),
        ("Message node", "spec#additional-flow-nodes/message", "Message", "toast; snackbar; feedback message; flash message"),
        ("Download", "spec#additional-flow-nodes/download", "Download", "file download; export file"),
        ("Excel", "spec#additional-flow-nodes/recordlisttoexcel", "Excel", "csv; spreadsheet; xlsx"),
        ("Logging", "troubleshooting#symptoms-in-the-running-app/youneedatrace", "LogMessage", "logging; console log; debug print; trace"),
        // UI
        ("Screen", "ui-reactive#screens-and-blocks", "screen", "page; view; route; web page"),
        ("Block", "ui-reactive#screens-and-blocks", "block", "component; partial view; web component; user control"),
        ("Screen inputs", "spec#reactive-ui-and-mobile-ui/screenblock", "input parameters", "query string; url parameter; route parameter; props"),
        ("Block events", "ui-reactive#block-communication", "event triggerEvent", "emit; notify parent; child to parent; output event"),
        ("Lifecycle events", "ui-reactive#lifecycle", "OnInitialize OnReady OnRender OnAfterFetch OnDestroy", "page load; mount; useeffect; componentdidmount; unmount"),
        ("Popup", "ui-reactive#popups", "Popup", "modal; dialog; small window; overlay window; lightbox"),
        ("Form validation", "ui-reactive#forms-and-validation", "Form Valid ValidationMessage mandatory", "required fields; required field; form errors; input validation; field validation"),
        ("Input widget", "spec#reactive-ui-and-mobile-ui/widget", "Input widget", "text box; textbox; input field; text field"),
        ("Dropdown", "recipe:dropdown-from-static", "Dropdown", "select box; combo box; picklist; select list"),
        ("Table and List widgets", "ui-reactive#tables-lists-sorting-paging", "Table List TableRecords", "grid; data grid; datatable; html table; repeater"),
        ("Button and Link", "spec#reactive-ui-and-mobile-ui/widget", "Button Link onClick", "hyperlink; anchor tag; submit button"),
        ("Destination", "spec#actions-and-flow-nodes/destination", "Destination screen input", "navigate; redirect; go to page; go to screen; open screen; routing"),
        ("Layout", "ui-reactive#layouts-themes-and-css", "layout LayoutTopMenu", "master page; page template; header footer; page shell"),
        ("Theme and CSS", "ui-reactive#layouts-themes-and-css", "theme CSS Style Classes", "stylesheet; css class; styling; look and feel; css variables; theme colors; design tokens"),
        ("Responsive", "ui-reactive#responsive", "responsive phone tablet", "mobile view; media query; breakpoint; small screen"),
        ("Accessibility", "ui-reactive#accessibility-musts", "accessibility aria", "screen reader; a11y; wcag; keyboard navigation; alt text"),
        ("Wizard", "osui-patterns#patterns/wizardwizarditem", "Wizard", "stepper; multi step; several steps; step by step"),
        ("Tabs", "osui-patterns#patterns/tabstabsheaderitemtabscontentitem", "Tabs", "tab bar; tab control"),
        ("Empty state", "osui-patterns#patterns/blankslate", "BlankSlate empty", "no results; nothing found; empty list; zero state"),
        ("Loading state", "recipe:loading-empty-error-states", "IsDataFetched loading", "spinner; loader; skeleton; loading indicator"),
        ("Charts", "osui-patterns#patterns/columnchartbarchartlinechartareachartpie", "chart", "graph; plot; bar chart; pie chart; diagram"),
        ("Date picker", "osui-patterns#patterns/datepicker", "DatePicker", "calendar; date input; date field"),
        ("Upload", "spec#reactive-ui-and-mobile-ui", "Upload Binary", "file upload; file input; attach file"),
        ("Images", "spec#copy-translations-resources-and-folders/images", "image images", "picture; photo; logo"),
        ("Notification pattern", "osui-patterns#patterns/notification", "Notification", "toast; snackbar; banner"),
        ("Translations", "spec#copy-translations-resources-and-folders/translations", "translations locales translate", "language; i18n; localization; localisation; multilingual; another language"),
        ("Client variables", "spec#module-variables-and-system-events", "clientVariables client variable", "local storage; localstorage; remember; across screens; global state; app state"),
        ("Session variables", "spec#module-variables-and-system-events", "sessionVariables session variable", "session; session state"),
        ("Site property", "spec#platform-elements/siteproperties", "site property", "config setting; configuration; env variable; environment variable; app setting; feature flag"),
        // Security
        ("Roles", "security#screens-and-roles", "role CheckRole", "permission; user group; authorization; access control; rbac; admin only"),
        ("Screen access", "spec#platform-elements/screenaccess", "Anonymous Registered access", "public page; login required; without login; guest"),
        ("User identity", "security#user-identity", "GetUserId user", "logged in user; current user; signed in user; who is logged in; user id"),
        ("Secrets", "security#secrets", "secret site properties", "api key; password; credentials; connection string"),
        ("SQL injection", "security#sql-and-output", "SQL injection EncodeSql expandInline", "sanitize input; escape sql; injection"),
        ("Escaping", "security#sql-and-output", "EncodeHtml SanitizeHtml escape", "xss; cross site scripting; html injection; sanitize html"),
        // Integrations
        ("Consumed REST", "rest#consumed-apis", "consumed REST restClients", "call external api; external api; web service; http client; axios; third party api; consume api"),
        ("Exposed REST", "rest#exposed-apis", "exposed REST restServices", "endpoint; our api; expose api; webhook; public api"),
        ("JSON", "rest#json-types", "JSON", "serialize; deserialize; parse json"),
        ("Email", "processes-timers-email#email-reactive-and-mobile", "email sendEmail", "send mail; smtp; mail"),
        ("References", "key:references", "references producer", "import module; dependency; shared module"),
        // Processes
        ("Timer", "processes-timers-email#timers", "timer schedule Wake", "cron; scheduled job; background job; batch job; nightly; recurring task; background; async; later"),
        ("Process", "processes-timers-email#processes-bpt", "process BPT activity", "workflow; bpm; approval flow; state machine; long running"),
        ("Human activity", "spec#platform-elements/humanactivity", "human activity Taskbox", "task inbox; approval task; manual step; user task"),
        // Architecture
        ("Layers", "architecture-rules#layers", "layer Core Foundation", "split into modules; module structure; architecture; tiers; monolith"),
        ("Core widgets", "architecture-rules#naming-suffixes", "_CW Core Widgets", "reusable ui; shared blocks; shared components; ui library; design system"),
        ("Library module", "architecture-rules#module-kinds/library", "Library", "shared library; utility module; common module"),
        ("Reference rules", "architecture-rules#reference-rules", "cycle upward reference", "circular dependency; circular reference; dependency cycle"),
        // Tools
        ("Publish", "troubleshooting#publish-and-service-studio", "publish", "deploy; compile; release"),
        ("LifeTime", "troubleshooting#lifetime", "LifeTime deployment", "ci cd; pipeline; promote; production deploy; environments"),
        ("Testing", "testing", "BDDFramework test", "unit test; automated test; test case; tdd; integration test"),
        ("Preview", "ui-checklist#the-loop", "preview screenshot", "see the ui; visual check; screenshot"),
        ("Mobile and offline", "best-practices#mobile", "Mobile offline sync", "offline mode; native app; pwa; sync data"),
        ("Traditional to Reactive", "traditional-to-reactive", "Traditional Web Reactive", "migrate; legacy; convert screen"),
        ("Performance", "troubleshooting#symptoms-in-the-running-app/ascreenisslow", "slow server calls", "slow page; slow screen; lag"),
    };
}
