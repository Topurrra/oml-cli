# terminal-golden

Golden JSON outputs of redirected commands, captured from the v1.8.0 build (commit a54aaa3, release 1.8.0)
with the same Service Studio 11 install. `tests/terminal-check.ps1` runs the same commands on the current build
with stdout redirected and compares byte for byte: the machine output contract must not change.

Each file is the compact JSON a command prints when stdout is redirected. Paths inside a temp folder are stored as
`<TMP>` (the check rewrites its own temp folder the same way). `init-rules.json` holds the `written` path of the
rules file; the check compares it by file name.

Commands: `inspect --only entities`, `expr "1 + 2"`, `expr "1 +"`, `validate`, `describe` (missing element),
`find Id`, `tree` (missing element), `check --init-rules`, `spec check` (bad spec), `version`, `spec schema`.
