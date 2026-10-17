param([string]$Oml, [string]$Module)
$ErrorActionPreference = 'Stop'
function Assert($Condition, $Message) { if (!$Condition) { throw $Message } }
$folder = Join-Path (Split-Path $Module) ('openapi-' + [guid]::NewGuid().ToString('N'))
$hash = (Get-FileHash $Module).Hash
$report = & $Oml openapi $Module --out $folder | ConvertFrom-Json
Assert ($LASTEXITCODE -eq 0 -and $report.files.Count -eq 2) 'OpenAPI export failed'
foreach ($file in $report.files) {
    $bytes = [IO.File]::ReadAllBytes($file.file)
    $text = [IO.File]::ReadAllText($file.file)
    Assert ($bytes[0] -eq 123 -and !$text.Contains("`r") -and $text.Contains("`n  `"openapi`"")) 'Expected indented UTF-8 / LF'
    $doc = $text | ConvertFrom-Json -AsHashtable
    Assert ($doc.openapi -eq '3.0.3' -and $doc.info.title -and $doc.paths -and $doc.components.schemas) 'Invalid OpenAPI document structure'
    $actual = [Collections.Generic.List[string]]::new()
    function Walk($node, [string]$path, [bool]$example) {
        if ($node -is [Collections.IDictionary]) {
            if ($node.Contains('$ref')) {
                Assert ($node['$ref'].StartsWith('#/')) 'External ref'
                $target = $doc
                foreach ($token in $node['$ref'].Substring(2).Split('/')) { $target = $target[$token.Replace('~1','/').Replace('~0','~')] }
                Assert ($null -ne $target) "Unresolved ref $($node['$ref'])"
            }
            foreach ($key in $node.Keys) { Walk $node[$key] $(if ($path) { "$path.$key" } else { $key }) ($example -or ($key -eq 'example' -and $path.Contains('.content.'))) }
        } elseif ($node -is [array]) {
            for ($i=0; $i -lt $node.Count; $i++) { Walk $node[$i] "$path`[$i`]" $example }
        } elseif ($example -or ($node -is [string] -and $node.Contains('<FILL:'))) { $actual.Add($path) }
    }
    Walk $doc '' $false
    Assert (@(Compare-Object ($actual | Sort-Object) ($file.placeholders | Sort-Object)).Count -eq 0) 'Placeholder report differs from strings/example leaves'
    Assert ($file.placeholders -contains 'servers[0].url') 'Missing server placeholder'
    Assert ($doc.servers[0].url -match '/Reg/rest/') 'Missing module in default server base path'
    $operations = 0
    foreach ($path in $doc.paths.Keys) {
        foreach ($verb in $doc.paths[$path].Keys) {
            $operations++
            Assert ($verb -in @('get','post','put','patch','delete','head','options','trace')) 'Invalid HTTP verb'
            $op = $doc.paths[$path][$verb]
            Assert ($op.operationId -and $op.responses['200'].description -and $op.tags.Count -eq 1) 'Invalid operation'
            foreach ($p in $op.parameters) {
                Assert ($p.name -and $p.in -in @('query','path','header') -and $null -ne $p.schema) 'Invalid parameter'
                if ($p.in -eq 'path') { Assert ($p.required -and $path.Contains('{' + $p.name + '}')) 'Path input missing token/required' }
            }
            foreach ($status in @('400','403','404','500')) { Assert ($op.responses[$status]['$ref'] -eq "#/components/responses/$status") 'Missing shared error response' }
            Assert (!$op.responses.Contains('401')) 'None authentication should not add 401'
        }
    }
    Assert ($file.operationCount -eq $operations -and $file.schemaCount -eq $doc.components.schemas.Count) 'Wrong report counts'
    Assert ($doc.components.responses['400'].content['application/json'].schema['$ref'] -eq '#/components/schemas/OutSystemsError') 'Error schema ref'
    Assert ($doc.components.schemas.OutSystemsError.properties.Errors.items.type -eq 'string' -and $doc.components.schemas.OutSystemsError.properties.StatusCode.type -eq 'integer') 'Error shape'
    if ($doc.info.title -eq 'OrdersApi') {
        $op = $doc.paths['/orders/{Id}'].post
        Assert ($op.parameters[0].schema.format -eq 'int64') 'Identifier format'
        Assert (@($op.parameters | Where-Object { $_.name -eq 'Limit' -and $_.required }).Count -eq 1) 'Mandatory query'
        Assert (@($op.parameters | Where-Object { $_.name -eq 'X-Token' -and $_.in -eq 'header' }).Count -eq 1) 'Header input'
        Assert ($op.requestBody.required -and $op.requestBody.content['application/json'].schema['$ref']) 'Body input'
        Assert ($op.responses['200'].headers['X-Trace'].schema.type -eq 'string') 'Output header'
        Assert ($doc.components.schemas.OrderDto.required -contains 'Label') 'Mandatory attribute'
        Assert ($doc.components.schemas.OrderDto.properties.Label.maxLength -eq 45) 'Text length'
        Assert ($doc.components.schemas.OrderDto.properties.Items.type -eq 'array' -and $doc.components.schemas.OrderDto.properties.Items.items['$ref']) 'Recursive list schema'
    }
}
$r = & $Oml openapi $Module --out $folder | ConvertFrom-Json
Assert ($LASTEXITCODE -ne 0 -and $r.error -match 'already exists.*Use a new output path, or delete that file first') 'Overwrite was not refused'
$r = & $Oml openapi $Module --api Missing --out $folder | ConvertFrom-Json
Assert ($LASTEXITCODE -ne 0 -and $r.error -match 'OrdersApi' -and $r.error -match 'PublicApi') 'Unknown API error must list APIs'
$r = & $Oml openapi $Module --out (Join-Path $folder 'multi.json') | ConvertFrom-Json
Assert ($LASTEXITCODE -ne 0 -and $r.error -match 'exactly one API') 'Multiple APIs in one file accepted'
$single = Join-Path $folder 'single.json'
$r = & $Oml openapi $Module --api OrdersApi --out $single --server 'https://one.example/api' --server 'https://two.example/api' | ConvertFrom-Json
Assert ($LASTEXITCODE -eq 0 -and $r.files.Count -eq 1) 'Single API selection failed'
$d = Get-Content $single -Raw | ConvertFrom-Json
Assert ($d.servers.Count -eq 2 -and $d.servers[1].url -eq 'https://two.example/api') 'Repeated servers lost'
Assert ((Get-FileHash $Module).Hash -eq $hash) 'Source module changed'
Write-Host 'ok   OpenAPI export, structure, refs, schemas, placeholders, overwrite'
