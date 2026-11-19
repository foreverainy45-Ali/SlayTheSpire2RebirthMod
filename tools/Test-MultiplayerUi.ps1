param(
    [ValidateRange(1,10)][int]$Count=2,
    [ValidateSet('Standard','Daily','Blitz')][string]$GameMode='Standard'
)
$ErrorActionPreference='Stop'
$probe=[System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.work\probe'))
function Read-State([string]$Mode) {
    try { return Get-Content -LiteralPath (Join-Path $probe "$Mode\state.json") -Raw | ConvertFrom-Json } catch { return $null }
}
function Await-Pair([scriptblock]$Condition,[int]$Seconds=70) {
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    while([DateTimeOffset]::UtcNow -lt $deadline) {
        $h=Read-State host; $c=Read-State client
        if(($h -and $h.failed) -or ($c -and $c.failed)){throw 'Multiplayer UI transition failed'}
        if($h -and $c -and (& $Condition $h $c)){return @($h,$c)}
        Start-Sleep -Milliseconds 150
    }
    throw 'Multiplayer UI test timed out'
}
function Command([string]$Mode,[string]$Operation) {
    & (Join-Path $PSScriptRoot 'Invoke-Probe.ps1') -Mode $Mode -Operation $Operation | Out-Host
}
function Read-LatestHistory([string]$UserDir) {
    $file=Get-ChildItem -LiteralPath $UserDir -Recurse -Filter *.run | Where-Object Length -gt 0 |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (!$file) { return $null }
    return Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
}

$cutoff=[DateTimeOffset]::UtcNow.AddSeconds(-20)
$pair=Await-Pair {param($h,$c) ([DateTimeOffset]$h.time -gt $cutoff) -and ([DateTimeOffset]$c.time -gt $cutoff) -and
    $h.seed -and $h.seed -eq $c.seed -and $h.room -eq 'EventRoom' -and $c.room -eq 'EventRoom' -and
    $h.gameMode -eq $GameMode -and $c.gameMode -eq $GameMode -and
    $h.pauseMenuButtonRegistered -and $c.pauseMenuButtonRegistered -and !$h.request -and !$c.request}
Start-Sleep -Seconds 2
$pair=Await-Pair {param($h,$c) $h.seed -and $h.seed -eq $c.seed -and $h.room -eq 'EventRoom' -and $c.room -eq 'EventRoom' -and
    !$h.isChangingRooms -and !$c.isChangingRooms -and !$h.request -and !$c.request}
$identities=@($pair[0].netIdentity,$pair[1].netIdentity)
$roles=ConvertTo-Json -InputObject $pair[0].players -Compress
$results=@()
for($round=1;$round -le $Count;$round++) {
    $initiator=if($round % 2){'client'}else{'host'}
    $voter=if($initiator -eq 'host'){'client'}else{'host'}
    $before=@((Read-State host),(Read-State client))
    $oldSeed=$before[0].seed
    $beforeActs=@((ConvertTo-Json -InputObject $before[0].acts -Compress),(ConvertTo-Json -InputObject $before[1].acts -Compress))
    $beforeModifiers=@((ConvertTo-Json -InputObject $before[0].modifiers -Compress -Depth 20),(ConvertTo-Json -InputObject $before[1].modifiers -Compress -Depth 20))

    # First prove that the other player's native Reject button cancels without recording a loss.
    Command $initiator request_ui
    $null=Await-Pair {param($h,$c) if($initiator -eq 'host'){$h.popupOpen}else{$c.popupOpen}}
    Command $initiator confirm_ui
    $null=Await-Pair {param($h,$c) $h.request -and $c.request -and
        (($initiator -eq 'host' -and $h.nativeVoteOpen) -or ($initiator -eq 'client' -and $c.nativeVoteOpen)) -and
        (($voter -eq 'host' -and $h.popupOpen) -or ($voter -eq 'client' -and $c.popupOpen))}
    Command $voter cancel_ui
    $canceled=Await-Pair {param($h,$c) !$h.request -and !$c.request -and !$h.popupOpen -and !$c.popupOpen}
    for($i=0;$i -lt 2;$i++) {
        if($canceled[$i].seed -ne $oldSeed -or [long]$canceled[$i].losses -ne [long]$before[$i].losses -or
            [long]$canceled[$i].runs -ne [long]$before[$i].runs -or [int]$canceled[$i].historyFiles -ne [int]$before[$i].historyFiles) {
            throw 'Multiplayer rejection changed seed or failure records'
        }
    }

    # Then approve the same flow and require a local loss/history record on both peers.
    Command $initiator request_ui
    $null=Await-Pair {param($h,$c) if($initiator -eq 'host'){$h.popupOpen}else{$c.popupOpen}}
    Command $initiator confirm_ui
    $null=Await-Pair {param($h,$c) $h.request -and $c.request -and
        (($initiator -eq 'host' -and $h.nativeVoteOpen) -or ($initiator -eq 'client' -and $c.nativeVoteOpen)) -and
        (($voter -eq 'host' -and $h.popupOpen) -or ($voter -eq 'client' -and $c.popupOpen))}
    $watch=[Diagnostics.Stopwatch]::StartNew()
    Command $voter confirm_ui
    $loading=Await-Pair {param($h,$c) $h.request -and $c.request -and
        $h.nativeLoadingOpen -and $c.nativeLoadingOpen -and $h.loadingQuote -and $c.loadingQuote -and
        $h.renderedQuote -eq $h.loadingQuote -and $c.renderedQuote -eq $c.loadingQuote -and
        $h.quoteBottomCentered -and $c.quoteBottomCentered}
    $pair=Await-Pair {param($h,$c) $h.seed -eq $c.seed -and !$h.request -and !$c.request -and
        [int]$h.historyFiles -eq ([int]$before[0].historyFiles+1) -and [int]$c.historyFiles -eq ([int]$before[1].historyFiles+1) -and
        !$h.committed -and !$c.committed -and $h.room -eq 'EventRoom' -and $c.room -eq 'EventRoom'}
    for($i=0;$i -lt 2;$i++) {
        if($pair[$i].netIdentity -ne $identities[$i] -or !$pair[$i].connected){throw 'Original multiplayer connection changed'}
        if($pair[$i].gameMode -ne $GameMode){throw 'A peer changed game mode'}
        $history=Read-LatestHistory $pair[$i].userDir
        if($null -eq $history -or $history.win -ne $false -or $history.was_abandoned -ne $false -or
            $history.game_mode -ne $GameMode.ToLowerInvariant()){throw 'A peer did not write a non-abandoned failure history'}
        if($GameMode -ne 'Daily' -and ([long]$pair[$i].losses -ne ([long]$before[$i].losses+1) -or
            [long]$pair[$i].runs -ne ([long]$before[$i].runs+1))){throw 'A peer did not increment failure counters'}
        if($GameMode -eq 'Daily' -and ((ConvertTo-Json -InputObject $pair[$i].acts -Compress) -ne $beforeActs[$i] -or
            (ConvertTo-Json -InputObject $pair[$i].modifiers -Compress -Depth 20) -ne $beforeModifiers[$i] -or
            $pair[$i].dailyTime -ne $before[$i].dailyTime)){throw 'Daily fixed configuration changed'}
    }
    if($GameMode -eq 'Daily' -and $pair[0].seed -ne $oldSeed){throw 'Daily seed changed'}
    if($GameMode -ne 'Daily' -and $pair[0].seed -eq $oldSeed){throw "$GameMode seed did not change"}
    if((ConvertTo-Json -InputObject $pair[0].players -Compress) -ne $roles){throw 'Player role mapping changed'}
    $results+=@{round=$round;initiator=$initiator;seed=$pair[0].seed;restartMs=$watch.ElapsedMilliseconds;
        rejectionSafe=$true;bothLossesRecorded=$true;hostQuote=$loading[0].loadingQuote;clientQuote=$loading[1].loadingQuote}
    Write-Output "PASS $GameMode multiplayer UI ${round}: native loading popups, initiator=$initiator, rejection safe, both loss histories, seed=$($pair[0].seed), restart=$($watch.ElapsedMilliseconds)ms"
}
[System.IO.File]::WriteAllText((Join-Path $probe 'multiplayer-ui-result.json'),(ConvertTo-Json -InputObject $results -Depth 5))
