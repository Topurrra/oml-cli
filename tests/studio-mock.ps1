# A local stand-in for Service Studio's MCP server, for tests/studio-check.ps1. Two servers on two ports:
#   $Port      an early contract 1 build (11.55.97): listApps, no searchApps or openModule, no contract line;
#   $Port + 1  a newer build (11.55.99.65452+): listOpenModules, searchApps, openModule, closeModule, "MCP contract 2".
# Shapes follow the official skill (OutSystems/outsystems11-mcp, servicestudio-mcp-oml/reference/module-discovery.md
# and contracts.md, commit 8cdd94f); no real server was recorded. Every tool call is a line "<port offset> <tool>" in
# $CallLog. A call of the wrong shape is written to $ProblemFile and answers an error, so the check fails with the reason.
param([int]$Port, [string]$ReadyFile, [string]$ProblemFile, [string]$CallLog)
$ErrorActionPreference = 'Stop'
$listener = [System.Net.HttpListener]::new()
foreach ($p in 0..1) { $listener.Prefixes.Add("http://127.0.0.1:$($Port + $p)/") }
$listener.Start()
$token = 'mock-token-1'
$opened = @{}      # module key -> times openModule was called (the first answers "opening")
function Row($key, $tab, $name, [bool]$open, [bool]$ref = $false) {
    $r = [ordered]@{ key = $key; name = $name; kind = 'eSpace'; version = '1'; isOpen = $open; isReference = $ref; moduleType = 'Reactive' }
    if ($tab) { $r.tabId = $tab; $r.isSelected = $false }
    $r
}
function OpenRows([int]$mode) {
    $rows = @((Row 'k-orders' 't-1' 'Orders' $true), (Row 'k-osui' $null 'OutSystemsUI' $false $true))
    if ($mode -eq 1 -and $opened['k-shop'] -ge 2) { $rows += (Row 'k-shop' 't-2' 'Shop' $true) }
    if ($mode -eq 0) { foreach ($r in $rows) { $r.Remove('tabId'); $r.Remove('isSelected') } }
    , $rows
}
$catalog = @(
    @{ key = 'app-shop'; name = 'Shop App'; modules = @(@{ key = 'k-shop'; name = 'Shop'; moduleType = 'Reactive'; lastChangedBy = 'ana' }) }
    @{ key = 'app-orders'; name = 'Order Management'; modules = @(@{ key = 'k-orders'; name = 'Orders'; moduleType = 'Reactive'; lastChangedBy = 'rui' }) }
    @{ key = 'app-twins'; name = 'Twins'; modules = @(@{ key = 'k-twin-1'; name = 'Twin'; moduleType = 'Reactive' }, @{ key = 'k-twin-2'; name = 'twin'; moduleType = 'Service' }) }
)
function Problem([string]$text) { Add-Content -LiteralPath $ProblemFile -Value $text; throw $text }
function ToolResult($value) { @{ content = @(@{ type = 'text'; text = ($value | ConvertTo-Json -Depth 10 -Compress) }); isError = $false } }
function ToolError([string]$text) { @{ content = @(@{ type = 'text'; text = $text }); isError = $true } }

Set-Content -LiteralPath $ReadyFile -Value 'ready'
try {
    while ($listener.IsListening) {
        $pending = $listener.GetContextAsync()
        while (!$pending.IsCompleted) { Start-Sleep -Milliseconds 20 }
        $context = $pending.GetAwaiter().GetResult(); $request = $context.Request; $response = $context.Response
        $mode = $request.Url.Port - $Port
        $reader = [IO.StreamReader]::new($request.InputStream); $text = $reader.ReadToEnd(); $reader.Dispose()
        $rpc = $null; $result = $null; $rpcError = $null
        try {
            if ($request.HttpMethod -ne 'POST' -or $request.Url.AbsolutePath -ne '/mcp') { Problem "$($request.HttpMethod) $($request.Url.AbsolutePath)" }
            $rpc = $text | ConvertFrom-Json -AsHashtable
            switch ($rpc.method) {
                'initialize' {
                    $result = @{ protocolVersion = '2025-06-18'; capabilities = @{ tools = @{} }; serverInfo = @{ name = 'outsystems-studio'; version = '0.0.1' } }
                    if ($mode -eq 1) { $result.instructions = "Service Studio 11.55.99.65460, MCP contract 2.`nGet the latest skill at https://github.com/OutSystems/outsystems11-mcp." }
                }
                'notifications/initialized' { }
                'tools/list' {
                    $names = if ($mode -eq 0) { @('createSessionToken', 'listApps', 'runQuery', 'omlPublish') } else { @('createSessionToken', 'listOpenModules', 'searchApps', 'openModule', 'closeModule', 'runQuery', 'omlPublish') }
                    $result = @{ tools = @($names | ForEach-Object { @{ name = $_; inputSchema = @{ type = 'object' } } }) }
                }
                'tools/call' {
                    $tool = $rpc.params.name; $arguments = $rpc.params.arguments
                    Add-Content -LiteralPath $CallLog -Value "$mode $tool"
                    if ($tool -eq 'createSessionToken') { $result = @{ content = @(@{ type = 'text'; text = $token }); isError = $false }; break }
                    if ($arguments.sessionToken -cne $token) { Problem "$tool without the session token" }
                    $input = $arguments.input
                    $known = if ($mode -eq 0) { @('listApps', 'runQuery', 'omlPublish') } else { @('listOpenModules', 'searchApps', 'openModule', 'closeModule', 'runQuery', 'omlPublish') }
                    if ($tool -notin $known) { $rpcError = @{ code = -32602; message = "Unknown tool: $tool" }; break }
                    switch ($tool) {
                        'listApps' { if ($null -ne $input) { Problem 'listApps takes no input' }; $result = ToolResult @{ apps = (OpenRows 0) } }
                        'listOpenModules' { if ($null -ne $input) { Problem 'listOpenModules takes no input' }; $result = ToolResult @{ modules = (OpenRows 1) } }
                        'searchApps' {
                            if ($input.environment -and $input.environment -ne 'dev.example.com') { $result = ToolError "NOT_LOGGED_IN: No Service Studio window is logged in to '$($input.environment)'. Logged in: dev.example.com."; break }
                            $words = "$($input.search)".ToLowerInvariant().Split(' ', [StringSplitOptions]::RemoveEmptyEntries)
                            $apps = @(foreach ($a in $catalog) {
                                $mods = @(foreach ($m in $a.modules) {
                                    $hay = "$($m.name) $($a.name)".ToLowerInvariant()
                                    if (@($words | Where-Object { $hay -notlike "*$_*" }).Count -eq 0) {
                                        $row = $m.Clone()
                                        $isOpen = $m.key -eq 'k-orders' -or ($m.key -eq 'k-shop' -and $opened['k-shop'] -ge 2)
                                        $row.isOpen = $isOpen
                                        if ($isOpen) { $row.tabIds = @($(if ($m.key -eq 'k-orders') { 't-1' } else { 't-2' })) }
                                        $row
                                    }
                                })
                                if ($mods.Count -gt 0) { @{ key = $a.key; name = $a.name; modules = $mods } }
                            })
                            $message = if ($apps.Count -eq 0) { "No module matches '$($input.search)'. Close names: Shop, Orders." } else { '' }
                            $result = ToolResult @{ environment = 'dev.example.com'; apps = $apps; hasMore = $false; message = $message }
                        }
                        'openModule' {
                            if ($input.moduleKey -ne 'k-shop') { $result = ToolError "INVALID_ARGUMENT: '$($input.moduleKey)' is not a module key searchApps lists."; break }
                            $opened['k-shop'] = 1 + [int]$opened['k-shop']
                            $result = if ($opened['k-shop'] -lt 2) { ToolResult @{ status = 'opening'; message = 'Service Studio is downloading Shop.' } }
                                      else { ToolResult @{ status = 'opened'; message = 'Opened Shop.'; module = (Row 'k-shop' 't-2' 'Shop' $true) } }
                        }
                        'runQuery' {
                            $open = @((OpenRows $mode) | Where-Object { $_.isOpen } | ForEach-Object { $_.name; $_.tabId; $_.key })
                            if ($input.eSpaceName -notin $open) { $result = ToolError "MODULE_NOT_LOADED: No open module matches '$($input.eSpaceName)'."; break }
                            $result = ToolResult @{ data = @{ Root = @{ Name = $input.eSpaceName } } }
                        }
                        default { Problem "unexpected tool $tool" }
                    }
                }
                default { Problem "unexpected method $($rpc.method)" }
            }
        }
        catch { if (-not $rpcError) { $rpcError = @{ code = -32000; message = "mock: $_" } } }
        if ($null -eq $rpc -or $null -eq $rpc.id) { $response.StatusCode = 202; $response.Close(); continue }
        $reply = @{ jsonrpc = '2.0'; id = $rpc.id }
        if ($rpcError) { $reply.error = $rpcError } else { $reply.result = $result }
        $bytes = [Text.Encoding]::UTF8.GetBytes(($reply | ConvertTo-Json -Depth 12 -Compress))
        $response.ContentType = 'application/json'
        $response.OutputStream.Write($bytes, 0, $bytes.Length)
        $response.Close()
    }
}
finally { $listener.Stop() }
