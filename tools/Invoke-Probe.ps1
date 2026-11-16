param([ValidateSet('single','host','client','client2','client3')][string]$Mode='single', [ValidateSet('request','request_ui','confirm_ui','cancel_ui','agree','reject','cancel','sl','combat','screenshot','exit')][string]$Operation, [switch]$Official)
$ErrorActionPreference='Stop'
$testKey=if($Official){"official-$Mode"}else{$Mode}
$directory=[System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.work\probe\$testKey"))
$path=Join-Path $directory 'command.json'
$id=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$json=@{id=$id; operation=$Operation} | ConvertTo-Json -Compress
[System.IO.File]::WriteAllText($path,$json)
Write-Output "Sent $Operation to $Mode (id=$id)"
