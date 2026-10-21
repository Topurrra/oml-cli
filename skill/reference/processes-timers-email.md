# Processes, timers and email

BPT processes, timers and sending email. Spec shape: `oml spec processes`, `oml spec timers`, `oml spec emails`, `oml spec sendEmail`.

## Processes (BPT)

- Use a process for work that waits: human tasks, long approvals, the life cycle of one record. Libraries cannot hold processes, timers or roles; Service modules can.
- `launchOn` an entity starts the process when a record of it is created and adds a mandatory `<Entity>Id` input. Entity events reach processes only when the entity exposes process events.
- `Launch<Process>` starts it from any server action. It returns at once with only the process Id. Its Text inputs are limited to 2000 characters.
- The process runs in its own transaction and cannot see uncommitted rows. On Oracle, PostgreSQL and DB2, commit before you launch.
- An automatic activity runs its flow in one transaction. On an error it is retried later, so side effects outside the database (emails, REST calls, explicit commits) must be safe to repeat.
- A human activity waits for a user in the Taskbox. `Close<Activity>` is server only: wrap it in a server action. In Reactive, the activity's destination screen needs an Activity Identifier input fed with `ActivityId`.
- A Wait activity ends on a timeout, on a database event, or when closed. Its OnClose callback can raise an exception to keep waiting.
- A Decision takes exactly one outgoing path. Several connectors leaving an activity run in parallel; join parallel paths only through a sub-process (Execute Process).
- An activity output used after a Decision must be set on every path that reaches the use.
- Callbacks: On Process Start; On Ready on human activities, Wait and Conditional Start; On Open on human activities; On Start on Conditional Start; On Close and On Skip on human activities and Wait. A callback that raises an exception blocks that step. Callbacks are Service Studio only: tell the user to add them.
- Each pass through an activity makes an instance. A process with more than 10,000 activity instances is suspended: avoid loops without an exit.

## Process design and change

- Keep flows small; split large ones into sub-processes.
- End idle processes instead of waiting for months; resume them with a timer.
- Do not update the same record from screen logic and from the process in one request: they lock each other. Move process-only fields to a 1:1 entity.
- Few Conditional Starts: each keeps a listening instance and adds work to every deployment. A dependency connector makes it listen only from the step that needs it. One with no Start On should become its own process.
- Reports on process steps: keep your own `<Process>Process` entity, keyed by the business record Id and filled by callbacks. Queries on the process entities get slow with many instances.
- Publishing a changed process upgrades running instances. Deleting or moving an activity, removing a connector, or changing activity outputs or mandatory inputs can suspend live instances. Adding an activity is safe. To retire an activity, route new instances past it with a Decision and delete it later.

## Timers

- The action should have no inputs; nobody can read its outputs.
- Default timeout is 20 minutes. A run that fails or times out is retried (3 times by default) and its next run time does not move.
- At most 3 timers run at once per front-end server by default.
- Timers and processes run in their own session: session variables start with their default values.
- Long jobs: work in chunks, store progress, check the time and stop before the timeout, then call `Wake<Timer>` to continue. The same timer never runs twice at once.
- "When published" runs once after each publish: use it for setup data.
- Prefer explicit minutes in schedules over step syntax, and check custom schedules in Service Studio.

## Email (Reactive and Mobile)

- An email is a UI element in its own UI flow with a light theme. Mail clients clip large HTML (Gmail at about 102 KB), so do not use the OutSystems UI theme.
- Allowed widgets: Container, Expression, If, Image, Link, List, Table, Text. No JavaScript, no actions, no pagination.
- Email expressions see only the email's inputs and locals. Load data in the server action and pass it as inputs (structures and lists are allowed).
- Send Email is server only. Make one server action with the email inputs and the address; it runs `sendEmail` with an email from `emails`.
- Send Email only queues the mail. It is sent later, without the user's session; failed mails are retried for 2 days.
- Use a From address of the company domain, or spam filters reject the mail.
- Attachments: a list of `Attachment` (FileName, FileContent, MimeType). Limit the aggregate that feeds it with Max Records.

## Email (Traditional Web)

An email is an Email screen built from the theme's email layout, with a Preparation. Send Email sets From, To, CC and BCC.

More: `oml doc logic-rules`, `oml doc architecture-rules`.
