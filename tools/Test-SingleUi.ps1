param([ValidateRange(1,20)][int]$Count=5)
$ErrorActionPreference='Stop'
$directory=[System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.work\probe\single'))
function Read-State {
    try { return Get-Content -LiteralPath (Join-Path $directory 'state.json') -Raw | ConvertFrom-Json } catch { return $null }
}
function Await-State([scriptblock]$Condition, [int]$Seconds=40) {
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $state=Read-State
        if ($state -and $state.failed) { throw 'Runtime transition failed' }
        if ($state -and (& $Condition $state)) { return $state }
        Start-Sleep -Milliseconds 150
    }
    throw 'Runtime UI test timed out'
}

$cutoff=[DateTimeOffset]::UtcNow.AddSeconds(-20)
$ready=Await-State { param($s) ([DateTimeOffset]$s.time -gt $cutoff) -and $s.seed -and $s.pauseMenuButtonRegistered -and $s.popupAvailable }
$results=@()
for ($round=1; $round -le $Count; $round++) {
    $before=Read-State
    $beforeSeed=$before.seed
    $beforeLosses=[long]$before.losses
    $beforeRuns=[long]$before.runs
    $beforeHistory=[int]$before.historyFiles

    & (Join-Path $PSScriptRoot 'Invoke-Probe.ps1') -Mode single -Operation request_ui | Out-Host
    $popup=Await-State { param($s) $s.popupOpen }
    & (Join-Path $PSScriptRoot 'Invoke-Probe.ps1') -Mode single -Operation cancel_ui | Out-Host
    $canceled=Await-State { param($s) !$s.popupOpen }
    if ($canceled.seed -ne $beforeSeed -or [long]$canceled.losses -ne $beforeLosses -or
        [long]$canceled.runs -ne $beforeRuns -or [int]$canceled.historyFiles -ne $beforeHistory) {
        throw 'Cancel changed seed, progress, or run history'
    }

    & (Join-Path $PSScriptRoot 'Invoke-Probe.ps1') -Mode single -Operation request_ui | Out-Host
    $popup=Await-State { param($s) $s.popupOpen }
    $watch=[Diagnostics.Stopwatch]::StartNew()
    & (Join-Path $PSScriptRoot 'Invoke-Probe.ps1') -Mode single -Operation confirm_ui | Out-Host
    $after=Await-State { param($s) $s.seed -ne $beforeSeed -and !$s.committed -and !$s.request -and !$s.isChangingRooms }
    if ($after.room -ne 'EventRoom' -or $after.players[0].character -ne $before.players[0].character) {
        throw 'Character or Neow room was not preserved'
    }
    if ([long]$after.losses -ne ($beforeLosses + 1) -or [long]$after.runs -ne ($beforeRuns + 1) -or
        [int]$after.historyFiles -ne ($beforeHistory + 1)) {
        throw "Loss record mismatch: losses $beforeLosses->$($after.losses), runs $beforeRuns->$($after.runs), history $beforeHistory->$($after.historyFiles)"
    }
    $results+=@{round=$round; seed=$after.seed; restartMs=$watch.ElapsedMilliseconds; canceledSafely=$true; lossRecorded=$true}
    Write-Output "PASS UI round ${round}: canceled safely, confirmed loss record, seed=$($after.seed), restart=$($watch.ElapsedMilliseconds)ms"
}
[System.IO.File]::WriteAllText((Join-Path $directory 'single-ui-result.json'),(ConvertTo-Json -InputObject $results -Depth 5))
