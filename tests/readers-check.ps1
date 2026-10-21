# Readers (inspect, describe, tree, find, census) on parts whose model getters throw: each one used to end the command.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
function Require($ok, $message) { if (-not $ok) { throw $message } }
function Oml([string[]]$cliArgs) {
  $text = & $Oml @cliArgs 2>&1
  $json = try { ($text -join "`n") | ConvertFrom-Json } catch { $null }
  [pscustomobject]@{ Code = $LASTEXITCODE; Json = $json; Text = ($text -join "`n") }
}
function Clean($r, $what) {
  Require ($r.Code -eq 0 -and $r.Json) "$what failed (exit $($r.Code)): $($r.Text.Substring(0, [Math]::Min(400, $r.Text.Length)))"
  Require (-not ($r.Text -match '"unreadable"')) "$what listed unreadable parts: $($r.Text.Substring(0, [Math]::Min(400, $r.Text.Length)))"
}
$reactive = Join-Path $Tmp 'r0.oml'; $service = Join-Path $Tmp 's0.oml'

# A Service module has no JavaScript: its getter throws (NullReferenceException).
Clean (Oml @('inspect', $service)) 'inspect of a Service module'
# (System) structures and roles keep no history: their history getters throw InvalidOperationException
# ("Operation is not valid due to the current state of the object") and NotImplementedException.
foreach ($name in 'Attachment', 'Registered', 'Anonymous') {
  $d = Oml @('describe', $reactive, $name, '--history'); Clean $d "describe $name --history"
  Require ($null -eq $d.Json.history) "$name got a history"
}
Clean (Oml @('inspect', $reactive, '--history')) 'inspect --history'
Clean (Oml @('census', $service)) 'census of a Service module'

# A referenced block is a proxy: its Widgets getter throws NotSupportedException.
$consumer = Join-Path $Tmp 'readers-consumer.oml'
$spec = Join-Path $Tmp 'readers-spec.json'
@{ references = @(@{ producer = (Join-Path $Tmp 'references-producer.oml'); elements = @('CustomerCard', 'Customer', 'GetCustomerById') })
   screens = @(@{ name = 'Cards'; widgets = @(@{ type = 'BlockInstance'; sourceBlock = 'CustomerCard' }) }) } | ConvertTo-Json -Depth 8 | Set-Content $spec
$r = & $Oml apply $reactive $spec $consumer 2>$null | ConvertFrom-Json
Require $r.saved 'consumer with a referenced block'
foreach ($name in 'ReferenceProducer.CustomerCard', 'CustomerCard', 'Cards') {
  Clean (Oml @('describe', $consumer, $name, '--depth', '2')) "describe $name"
  Clean (Oml @('tree', $consumer, $name)) "tree $name"
}
Clean (Oml @('find', 'Customer*', $consumer)) 'find'

# A file that is not a module: a JSON error and exit 2, not a crash with a stack trace.
$bad = Join-Path $Tmp 'readers-not-a-module.oml'
Set-Content $bad 'not a module'
foreach ($cliArgs in @(@('inspect', $bad), @('describe', $bad, 'X'), @('tree', $bad, 'X'))) {
  $x = Oml $cliArgs
  Require ($x.Code -eq 2 -and $x.Json.error -match 'not a valid \.oml module') "$($cliArgs[0]) of a file that is not a module: exit $($x.Code), $($x.Text.Substring(0, [Math]::Min(300, $x.Text.Length)))"
}
# install of a file that is not a module, and the same file inside oml run (shared load): the same error with its path.
$target = Join-Path $Tmp 'readers-install-target.oml'; Copy-Item $reactive $target
$before = (Get-FileHash $target).Hash
$x = Oml @('install', $bad, $target)
Require ($x.Code -eq 2 -and $x.Json.error -match 'not a valid \.oml module' -and $x.Json.error.Contains($bad)) "install of a file that is not a module: exit $($x.Code), $($x.Text.Substring(0, [Math]::Min(300, $x.Text.Length)))"
Require ((Get-FileHash $target).Hash -eq $before) 'install of a file that is not a module changed the target'
$steps = Join-Path $Tmp 'readers-junk-steps.json'
@{ steps = @(, @('inspect', $bad)) } | ConvertTo-Json -Depth 4 | Set-Content $steps
$x = Oml @('run', $steps)
$step = @($x.Json.results)[0]
Require ($step.exitCode -eq 2 -and $step.output.error -match 'not a valid \.oml module' -and $step.output.error.Contains($bad)) "oml run inspect of a file that is not a module: $($x.Text.Substring(0, [Math]::Min(400, $x.Text.Length)))"
Write-Host 'ok   readers: Service module, system history, referenced blocks, a file that is not a module (also install and oml run)'
