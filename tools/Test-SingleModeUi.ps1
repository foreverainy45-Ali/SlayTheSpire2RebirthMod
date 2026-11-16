param(
    [ValidateSet('Standard','Daily','Blitz')][string]$GameMode,
    [ValidateRange(1,10)][int]$Count=2,
    [switch]$SkipQuickSl
)
$ErrorActionPreference='Stop'
$directory=[System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.work\probe\single'))
function Read-State {
    try { return Get-Content -LiteralPath (Join-Path $directory 'state.json') -Raw | ConvertFrom-Json } catch { return $null }
}
function Await-State([scriptblock]$Condition, [int]$Seconds=50) {
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $state=Read-State
        if ($state -and $state.failed) { throw 'Runtime mode transition failed' }
        if ($state -and (& $Condition $state)) { return $state }
        Start-Sleep -Milliseconds 150
    }
    throw "$GameMode runtime UI test timed out"
}
function Send([string]$Operation) {
    & (Join-Path $PSScriptRoot 'Invoke-Probe.ps1') -Mode single -Operation $Operation | Out-Host
}
function Read-LatestHistory([string]$UserDir) {
    $file=Get-ChildItem -LiteralPath $UserDir -Recurse -Filter *.run | Where-Object Length -gt 0 |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (!$file) { return $null }
    return Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
}

$cutoff=[DateTimeOffset]::UtcNow.AddSeconds(-20)
$ready=Await-State { param($s) ([DateTimeOffset]$s.time -gt $cutoff) -and $s.seed -and
    $s.gameMode -eq $GameMode -and $s.room -and $s.pauseMenuButtonRegistered -and $s.popupAvailable }
Start-Sleep -Seconds 2
$results=@()
for ($round=1; $round -le $Count; $round++) {
    $before=Read-State
    $beforeSeed=$before.seed
    $beforeLosses=[long]$before.losses
    $beforeRuns=[long]$before.runs
    $beforeHistory=[int]$before.historyFiles
    $beforeActs=ConvertTo-Json -InputObject $before.acts -Compress
    $beforeModifiers=ConvertTo-Json -InputObject $before.modifiers -Compress -Depth 20
    $beforeDailyTime=$before.dailyTime

    Send request_ui
    $null=Await-State { param($s) $s.popupOpen }
    Send cancel_ui
    $canceled=Await-State { param($s) !$s.popupOpen }
    if ($canceled.seed -ne $beforeSeed -or [long]$canceled.losses -ne $beforeLosses -or
        [long]$canceled.runs -ne $beforeRuns -or [int]$canceled.historyFiles -ne $beforeHistory) {
        throw "$GameMode cancel changed seed, progress, or history"
    }

    Send request_ui
    $null=Await-State { param($s) $s.popupOpen }
    $watch=[Diagnostics.Stopwatch]::StartNew()
    Send confirm_ui
    $loading=Await-State { param($s) $s.request -and $s.nativeLoadingOpen -and $s.loadingQuote -and
        $s.renderedQuote -eq $s.loadingQuote -and $s.quoteBottomCentered }
    if ($loading.loadingStatus -match '其他玩家|所有玩家|全员|队友') {
        throw "Singleplayer loading text incorrectly mentions other players: $($loading.loadingStatus)"
    }
    $after=Await-State { param($s) [int]$s.historyFiles -eq ($beforeHistory+1) -and !$s.committed -and
        !$s.request -and !$s.isChangingRooms -and $s.gameMode -eq $GameMode }
    if ($after.players[0].character -ne $before.players[0].character) { throw "$GameMode character changed" }
    $history=Read-LatestHistory $after.userDir
    if ($null -eq $history -or $history.win -ne $false -or $history.was_abandoned -ne $false -or
        $history.game_mode -ne $GameMode.ToLowerInvariant()) { throw "$GameMode latest history is not a non-abandoned failure" }
    if ($GameMode -eq 'Daily') {
        if ($after.seed -ne $beforeSeed) { throw 'Daily seed changed' }
        if ($after.room -ne 'EventRoom' -or !$after.startedWithNeow) { throw 'Daily did not return to first-act Neow' }
        if ((ConvertTo-Json -InputObject $after.acts -Compress) -ne $beforeActs -or
            (ConvertTo-Json -InputObject $after.modifiers -Compress -Depth 20) -ne $beforeModifiers -or
            $after.dailyTime -ne $beforeDailyTime) { throw 'Daily fixed acts, modifiers, or leaderboard day changed' }
    } else {
        if ($after.seed -eq $beforeSeed) { throw 'Blitz did not switch seed' }
        if (!$after.room -or [int]$after.totalFloor -gt 1) { throw "$GameMode did not return to its opening floor" }
        if ([long]$after.losses -ne ($beforeLosses+1) -or [long]$after.runs -ne ($beforeRuns+1)) {
            throw "$GameMode did not increment the regular failure and run counters"
        }
    }

    $quickSlPassed=$false
    if (!$SkipQuickSl) {
        $log=Join-Path $directory 'godot.log'
        $connection=$after.netIdentity
        $previousCompletions=@(Select-String -LiteralPath $log -Pattern '快速 SL 完成').Count
        Send sl
        $loaded=Await-State { param($s)
            $completions=@(Select-String -LiteralPath $log -Pattern '快速 SL 完成').Count
            $s.netIdentity -ne $connection -and $completions -gt $previousCompletions -and !$s.slBusy -and !$s.isChangingRooms
        }
        if ($loaded.seed -ne $after.seed -or $loaded.gameMode -ne $GameMode) { throw "QuickSL changed the $GameMode seed or mode" }
        $quickSlPassed=$true
    }
    $results+=@{round=$round;mode=$GameMode;oldSeed=$beforeSeed;seed=$after.seed;room=$after.room;
        restartMs=$watch.ElapsedMilliseconds;lossRecorded=$true;quickSlPassed=$quickSlPassed;quote=$loading.loadingQuote}
    Write-Output "PASS $GameMode UI ${round}: native loading popup, quote='$($loading.loadingQuote)', seed $beforeSeed -> $($after.seed), room=$($after.room), failure recorded, restart=$($watch.ElapsedMilliseconds)ms, QuickSL=$quickSlPassed"
}
$name=$GameMode.ToLowerInvariant()
[System.IO.File]::WriteAllText((Join-Path $directory "single-$name-ui-result.json"),(ConvertTo-Json -InputObject $results -Depth 8))
