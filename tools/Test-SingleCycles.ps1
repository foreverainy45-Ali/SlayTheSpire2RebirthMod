param([int]$Count=10)
$ErrorActionPreference='Stop'
$directory=[System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.work\probe\single'))
function Read-State {
    try { return Get-Content -LiteralPath (Join-Path $directory 'state.json') -Raw | ConvertFrom-Json } catch { return $null }
}
function Await-State([scriptblock]$Condition) {
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(40)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $state=Read-State
        if ($state -and $state.failed) { throw 'Runtime transition failed' }
        if ($state -and (& $Condition $state)) { return $state }
        Start-Sleep -Milliseconds 200
    }
    throw 'Runtime test timed out'
}
$results=@()
for ($round=1; $round -le $Count; $round++) {
    $before=Read-State
    if (!$before -or !$before.seed) { throw 'Singleplayer probe is not ready' }
    $watch=[Diagnostics.Stopwatch]::StartNew()
    & (Join-Path $PSScriptRoot 'Invoke-Probe.ps1') -Mode single -Operation request | Out-Host
    $after=Await-State {param($s) $s.seed -ne $before.seed -and !$s.committed -and !$s.request -and !$s.isChangingRooms}
    if ($after.players[0].character -ne $before.players[0].character -or $after.room -ne 'EventRoom') { throw 'Character/room mismatch' }
    $restartMs=$watch.ElapsedMilliseconds
    $connection=$after.netIdentity
    $log=Join-Path $directory 'godot.log'
    $previousCompletions=@(Select-String -LiteralPath $log -Pattern '快速 SL 完成').Count
    & (Join-Path $PSScriptRoot 'Invoke-Probe.ps1') -Mode single -Operation sl | Out-Host
    $loaded=Await-State {param($s)
        $completions=@(Select-String -LiteralPath $log -Pattern '快速 SL 完成').Count
        $s.netIdentity -ne $connection -and $completions -gt $previousCompletions -and !$s.slBusy -and !$s.isChangingRooms
    }
    if ($loaded.seed -ne $after.seed -or $loaded.players[0].character -ne $before.players[0].character) { throw 'SL changed seed/role' }
    $results+=@{round=$round; seed=$loaded.seed; restartMs=$restartMs; quickSlPassed=$true}
    Write-Output "PASS cycle $round seed=$($loaded.seed) restart=${restartMs}ms + QuickSL"
}
[System.IO.File]::WriteAllText((Join-Path $directory 'cycles-result.json'),(ConvertTo-Json -InputObject $results -Depth 5))
