param([string]$Oml, [string]$Module)
$ErrorActionPreference = 'Stop'
function Descendants($node) {
  $node
  foreach ($child in $node.children) { Descendants $child }
}
$inspect = & $Oml inspect $Module | ConvertFrom-Json
$client = $inspect.restClients | Where-Object name -eq Weather
$service = $inspect.restServices | Where-Object name -eq OrdersApi
if ($client.baseUrl -ne 'https://api.example.com' -or $client.methods.Count -ne 2) { throw 'Consumed API missing' }
$get = $client.methods | Where-Object name -eq GetForecast
if ($get.method -ne 'GET' -or $get.url -ne '/forecast/{City}?Days={Days}') { throw 'Consumed GET URL/verb lost' }
$post = $client.methods | Where-Object name -eq PostForecast
if ($post.method -ne 'POST' -or $post.url -ne '/forecast') { throw 'Consumed POST URL/verb lost' }
if ($service.methods.Count -ne 1 -or $service.methods[0].url -ne '/orders/{Id}') { throw 'Exposed API URL lost' }
$call = & $Oml tree $Module FetchForecast | ConvertFrom-Json
$nodes = @(Descendants $call)
if (-not ($nodes | Where-Object { $_.ref -eq 'GetForecast' })) { throw 'Consumed call reference lost' }
foreach ($parameter in @('City', 'Days', 'ApiKey')) {
  if (-not ($nodes | Where-Object { $_.ref -eq $parameter })) { throw "Argument reference lost: $parameter" }
}
$exposed = & $Oml tree $Module OrdersApi/GetOrder | ConvertFrom-Json
$nodes = @(Descendants $exposed)
if (@($nodes | Where-Object type -eq Start).Count -ne 1) { throw 'Exposed flow Start missing/duplicated' }
if (-not ($nodes | Where-Object name -eq FindOrder)) { throw 'Exposed aggregate missing' }
if (-not ($nodes | Where-Object text -eq 'FindOrder.List.Current.Order.Total')) { throw 'Exposed assignment lost' }
if (-not ($nodes | Where-Object { $_.source -eq 'Start' -and $_.target -eq 'FindOrder' })) { throw 'Exposed Start connector lost' }
