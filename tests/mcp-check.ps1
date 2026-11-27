# Checks `oml mcp`, the MCP server on stdio: initialize (version negotiation), tools/list (read, write, gated), read
# calls in parallel (each id answered once), a write call (apply to a staged file), refusals (a command in the wrong
# tool, an unknown command, bad args, a batch that smuggles a write or gated step through read, batch on stdin), a
# command that exits non-zero (not isError), a child-process command (browser --help), ping, notifications, an
# unknown method, a clean exit when stdin closes, and that 5 inspect calls through one server are much faster than
# 5 oml.exe runs. No gated command runs and no server is called.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Tmp = New-TestFolder $Tmp 'mcp'
$p = $null
try {
function Require($condition, $message) { if (-not $condition) { throw $message } }

$module = Join-Path $Tmp 'M.oml'
& $Oml new $module --name McpCheck --kind CrossDevice | Out-Null
Require ($LASTEXITCODE -eq 0) 'oml new failed'
Set-Content (Join-Path $Tmp 'spec.json') '{ "entities": [ { "name": "Widget", "attributes": [ { "name": "Label", "type": "Text" } ] } ] }' -Encoding utf8
Set-Content (Join-Path $Tmp 'reads.json') '{ "steps": [ ["inspect", "M.oml", "--own"], ["stats", "M.oml"] ] }' -Encoding utf8
Set-Content (Join-Path $Tmp 'smuggle-write.json') '{ "steps": [ ["inspect", "M.oml"], ["apply", "M.oml", "spec.json", "Smuggled.oml"] ] }' -Encoding utf8
Set-Content (Join-Path $Tmp 'smuggle-gated.json') '{ "steps": [ ["inspect", "M.oml"], ["install", "Staged.oml", "M.oml"] ] }' -Encoding utf8

$psi = [Diagnostics.ProcessStartInfo]::new($Oml)
$psi.ArgumentList.Add('mcp')
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$psi.StandardInputEncoding = [Text.UTF8Encoding]::new($false); $psi.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
$psi.WorkingDirectory = $Tmp   # relative paths in calls resolve against the server's folder
$p = [Diagnostics.Process]::Start($psi)
$stderr = $p.StandardError.ReadToEndAsync()
$seen = @{}
function Send($message) { $p.StandardInput.WriteLine(($message | ConvertTo-Json -Depth 10 -Compress)); $p.StandardInput.Flush() }
function Call($id, $tool, [string[]]$a) { Send @{ jsonrpc = '2.0'; id = $id; method = 'tools/call'; params = @{ name = $tool; arguments = @{ args = $a } } } }
# Every line on stdout must be one JSON-RPC message, and every id is answered once.
function Receive {
  $task = $p.StandardOutput.ReadLineAsync()
  if (-not $task.Wait(180000)) { throw 'no answer from oml mcp in 180 s' }
  $line = $task.Result
  if ($null -eq $line) { throw "oml mcp closed stdout; stderr: $($stderr.Result)" }
  try { $m = $line | ConvertFrom-Json } catch { throw "not JSON on stdout: $line" }
  Require ($m.jsonrpc -eq '2.0') "not a JSON-RPC message: $line"
  Require (-not $seen.ContainsKey("$($m.id)")) "id $($m.id) answered twice"
  $seen["$($m.id)"] = $true
  $m
}
function Body($m) { $m.result.content[0].text | ConvertFrom-Json }
function Ask($id, $tool, [string[]]$a) { Call $id $tool $a; $m = Receive; Require ($m.id -eq $id) "answer to $($m.id), expected $id"; $m }

# 1. initialize: a version the server knows is echoed; an unknown one gets the newest it supports.
Send @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'mcp-check'; version = '1' } } }
$init = Receive
Require ($init.id -eq 1 -and $init.result.protocolVersion -eq '2024-11-05' -and $init.result.serverInfo.name -eq 'oml' -and $null -ne $init.result.capabilities.tools) "initialize: $($init | ConvertTo-Json -Depth 10 -Compress)"
Send @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
Send @{ jsonrpc = '2.0'; id = 2; method = 'initialize'; params = @{ protocolVersion = '1999-01-01' } }
$init2 = Receive
Require ($init2.id -eq 2 -and $init2.result.protocolVersion -match '^20\d\d-\d\d-\d\d$' -and $init2.result.protocolVersion -ne '1999-01-01') "initialize with an unknown version: $($init2.result.protocolVersion)"
# The notification above got no answer: the next line answers this ping.
Send @{ jsonrpc = '2.0'; id = 3; method = 'ping' }
$ping = Receive
Require ($ping.id -eq 3 -and $null -ne $ping.result) "ping: $($ping | ConvertTo-Json -Depth 10 -Compress)"
Send @{ jsonrpc = '2.0'; id = 4; method = 'resources/list' }
$unknown = Receive
Require ($unknown.id -eq 4 -and $unknown.error.code -eq -32601) "unknown method: $($unknown | ConvertTo-Json -Depth 10 -Compress)"
Write-Host 'ok   mcp: initialize (version echo and fallback), notification ignored, ping, unknown method -32601'

# 2. tools/list: exactly read, write and gated, each with an args list.
Send @{ jsonrpc = '2.0'; id = 5; method = 'tools/list' }
$list = Receive
$names = @($list.result.tools | ForEach-Object name)
Require (($names -join ',') -eq 'read,write,gated') "tools: $($names -join ',')"
foreach ($t in $list.result.tools) {
  Require ($t.inputSchema.properties.args.type -eq 'array' -and @($t.inputSchema.required) -contains 'args') "$($t.name): no args schema"
}
Require ($list.result.tools[0].description -match '\binspect\b' -and $list.result.tools[1].description -match '\bapply\b' -and $list.result.tools[2].description -match '\binstall\b') 'tool descriptions do not name their commands'
Write-Host 'ok   mcp: tools/list read, write, gated'

# 3. Four read calls at once: all answered, each once, with the command's JSON.
$calls = @{ 10 = @('inspect', 'M.oml', '--own'); 11 = @('stats', 'M.oml'); 12 = @('validate', 'M.oml'); 13 = @('doc') }
foreach ($id in $calls.Keys) { Call $id 'read' $calls[$id] }
$answers = @{}
foreach ($i in 1..4) { $m = Receive; $answers["$($m.id)"] = $m }
Require ((@($answers.Keys | Sort-Object) -join ',') -eq '10,11,12,13') "parallel reads answered: $(@($answers.Keys) -join ',')"
foreach ($id in $calls.Keys) {
  $b = Body $answers["$id"]
  Require ($answers["$id"].result.isError -eq $false -and $b.exit -eq 0 -and $null -ne $b.output) "read $($calls[$id] -join ' '): $($answers["$id"].result.content[0].text)"
}
Require ((Body $answers['10']).output.name -eq 'McpCheck') 'inspect output is not the parsed JSON'
Write-Host 'ok   mcp: 4 read calls at once (inspect, stats, validate, doc), each answered once'

# 4. A write: apply to a staged file. The same through read is refused with the right tool.
$r = Ask 20 'read' @('apply', 'M.oml', 'spec.json', 'Staged.oml')
Require ($r.result.isError -eq $true -and (Body $r).output.useTool -eq 'write' -and -not (Test-Path (Join-Path $Tmp 'Staged.oml'))) "apply through read: $($r.result.content[0].text)"
$r = Ask 21 'write' @('apply', 'M.oml', 'spec.json', 'Staged.oml')
$b = Body $r
Require ($r.result.isError -eq $false -and $b.exit -eq 0 -and (Test-Path (Join-Path $Tmp 'Staged.oml'))) "apply through write: $($r.result.content[0].text)"
$r = Ask 22 'read' @('describe', 'Staged.oml', 'Widget')
Require ((Body $r).exit -eq 0) "describe of the staged entity: $($r.result.content[0].text)"
Write-Host 'ok   mcp: write runs apply; apply through read is refused (useTool write)'

# 4b. A module stays open between calls, but a file that changed on disk is read again.
Copy-Item $module (Join-Path $Tmp 'C.oml')
$r = Ask 23 'read' @('inspect', 'C.oml', '--own')
Require (-not ((Body $r).output | ConvertTo-Json -Depth 20 -Compress).Contains('"Widget"')) 'C.oml has Widget before the copy'
Copy-Item (Join-Path $Tmp 'Staged.oml') (Join-Path $Tmp 'C.oml') -Force
(Get-Item (Join-Path $Tmp 'C.oml')).LastWriteTimeUtc = [DateTime]::UtcNow
$r = Ask 24 'read' @('inspect', 'C.oml', '--own')
Require (((Body $r).output | ConvertTo-Json -Depth 20 -Compress).Contains('"Widget"')) 'a changed C.oml was not read again'
Write-Host 'ok   mcp: a module that changed on disk between calls is read again'

# 5. Refusals: a gated command through read, an unknown command, bad args, memory add through read.
$r = Ask 30 'read' @('install', 'Staged.oml', 'M.oml')
Require ($r.result.isError -eq $true -and (Body $r).output.useTool -eq 'gated' -and (Body $r).output.error -match 'gated') "install through read: $($r.result.content[0].text)"
$r = Ask 31 'read' @('selftest')
Require ($r.result.isError -eq $true -and (Body $r).output.error -match 'not a command of oml mcp') "unknown command: $($r.result.content[0].text)"
Send @{ jsonrpc = '2.0'; id = 32; method = 'tools/call'; params = @{ name = 'read'; arguments = @{ args = 'inspect M.oml' } } }
$r = Receive
Require ($r.id -eq 32 -and $r.result.isError -eq $true) "args as a string: $($r | ConvertTo-Json -Depth 10 -Compress)"
$r = Ask 33 'read' @('memory', 'add', 'a fact')
Require ($r.result.isError -eq $true -and (Body $r).output.useTool -eq 'write') "memory add through read: $($r.result.content[0].text)"
$r = Ask 34 'read' @('review', '--server', 'M', '1', '2')
Require ($r.result.isError -eq $true -and (Body $r).output.useTool -eq 'gated') "review --server through read: $($r.result.content[0].text)"
Send @{ jsonrpc = '2.0'; id = 35; method = 'tools/call'; params = @{ name = 'admin'; arguments = @{ args = @('inspect') } } }
$r = Receive
Require ($r.id -eq 35 -and $r.error.code -eq -32602) "unknown tool: $($r | ConvertTo-Json -Depth 10 -Compress)"
# apply --user-approved saves deletes and data-risk changes: only through gated, so a person says yes.
$r = Ask 36 'write' @('apply', 'M.oml', 'spec.json', 'Staged2.oml', '--user-approved')
Require ($r.result.isError -eq $true -and (Body $r).output.useTool -eq 'gated') "apply --user-approved through write: $($r.result.content[0].text)"
Write-Host 'ok   mcp: refused: install and review --server through read (gated), apply --user-approved through write (gated), memory add (write), unknown command, bad args, unknown tool'

# Merged forms run through read like the plain command (they were refused as unknown commands).
$id = 90
foreach ($form in @(@('spec', 'widget-types'), @('spec', 'model-types', 'OutSystems.Model.Logic.Nodes.IIfNode'), @('doc', 'library', 'status'))) {
  $r = Ask $id 'read' $form; $id++
  Require ($r.result.isError -eq $false) "merged form $($form -join ' ') through read: $($r.result.content[0].text)"
}
Write-Host 'ok   mcp: merged forms (spec model-types, spec widget-types, doc library status) run through read'

# 6. Batch: read-only steps run through read; a write or gated step cannot ride along; no steps on stdin.
$r = Ask 40 'read' @('batch', 'reads.json')
$b = Body $r
Require ($r.result.isError -eq $false -and $b.exit -eq 0 -and @($b.output.results).Count -eq 2) "read batch: $($r.result.content[0].text)"
$r = Ask 41 'read' @('batch', 'smuggle-write.json')
Require ($r.result.isError -eq $true -and (Body $r).output.useTool -eq 'write' -and -not (Test-Path (Join-Path $Tmp 'Smuggled.oml'))) "batch with apply through read: $($r.result.content[0].text)"
$r = Ask 42 'read' @('batch', 'smuggle-gated.json')
Require ($r.result.isError -eq $true -and (Body $r).output.useTool -eq 'gated') "batch with install through read: $($r.result.content[0].text)"
$r = Ask 43 'write' @('batch', 'smuggle-gated.json')
Require ($r.result.isError -eq $true -and (Body $r).output.error -match 'Step 2') "batch with install through write: $($r.result.content[0].text)"
$r = Ask 44 'read' @('batch', '-')
Require ($r.result.isError -eq $true -and (Body $r).output.error -match 'stdin') "batch -: $($r.result.content[0].text)"
$r = Ask 45 'write' @('batch', 'smuggle-write.json')
Require ($r.result.isError -eq $false -and (Body $r).exit -eq 0 -and (Test-Path (Join-Path $Tmp 'Smuggled.oml'))) "batch with apply through write: $($r.result.content[0].text)"
Write-Host 'ok   mcp: batch: reads through read; apply only through write; install never; no stdin'

# 7. A command that exits non-zero is a result, not a refusal.
$r = Ask 50 'read' @('describe', 'M.oml', 'NoSuchElement')
$b = Body $r
Require ($r.result.isError -eq $false -and $b.exit -ne 0 -and $b.output.error) "non-zero exit: $($r.result.content[0].text)"
Write-Host "ok   mcp: describe of a missing element: exit $($b.exit), isError false"

# 8. A child-process command (browser) while an in-process read runs: both answered.
Call 60 'write' @('browser', '--help')
Call 61 'read' @('inspect', 'M.oml', '--own')
$a = @{}; foreach ($i in 1..2) { $m = Receive; $a["$($m.id)"] = $m }
Require ((Body $a['60']).exit -eq 0 -and "$((Body $a['60']).output)" -match 'oml browser') "browser --help in a child: $($a['60'].result.content[0].text)"
Require ((Body $a['61']).exit -eq 0) "inspect next to a child: $($a['61'].result.content[0].text)"
Write-Host 'ok   mcp: browser --help in a child process next to an in-process inspect'

# 9. Speed: 5 inspect calls through the server against 5 oml.exe runs.
$watch = [Diagnostics.Stopwatch]::StartNew()
foreach ($i in 1..5) { $r = Ask (100 + $i) 'read' @('inspect', 'M.oml', '--own'); Require ((Body $r).exit -eq 0) 'inspect through mcp failed' }
$server = $watch.ElapsedMilliseconds
$watch.Restart()
foreach ($i in 1..4) { Call (200 + $i) 'read' @('inspect', 'M.oml', '--own') }
foreach ($i in 1..4) { $r = Receive; Require ((Body $r).exit -eq 0) 'parallel inspect failed' }
$parallel = $watch.ElapsedMilliseconds
$watch.Restart()
foreach ($i in 1..5) { $text = & $Oml inspect $module --own 2>&1; Require ($LASTEXITCODE -eq 0) "oml inspect failed: exit $LASTEXITCODE, $(($text | Out-String).Trim())" }
$process = $watch.ElapsedMilliseconds
Write-Host "     mcp timings: 5 inspect calls through oml mcp $server ms, 4 at once $parallel ms; 5 oml.exe runs $process ms"
Require ($server * 3 -lt $process) "5 calls through oml mcp ($server ms) are not much faster than 5 oml.exe runs ($process ms)"
Write-Host 'ok   mcp: one server is at least 3 times faster than one process per call'

# 10. stdin closed: the server ends with exit 0.
$p.StandardInput.Close()
Require ($p.WaitForExit(60000)) 'oml mcp did not exit after stdin closed'
Require ($p.ExitCode -eq 0) "oml mcp exit code $($p.ExitCode)"
$rest = $p.StandardOutput.ReadToEnd()
Require ($rest.Trim().Length -eq 0) "unexpected output after the last answer: $rest"
Write-Host 'ok   mcp: exits 0 when stdin closes; nothing but JSON-RPC on stdout'
}
finally {
  if ($p -and -not $p.HasExited) { $p.Kill($true) }
  Remove-TestWorkFolder $Tmp
}
