param([int]$Count=10)
$ErrorActionPreference='Stop'
$probe=[System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.work\probe'))
function Read-State([string]$Mode) {
    try { return Get-Content -LiteralPath (Join-Path $probe "$Mode\state.json") -Raw | ConvertFrom-Json } catch { return $null }
}
function Await-Pair([scriptblock]$Condition,[int]$Seconds=65) {
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $h=Read-State host; $c=Read-State client
        if (($h -and $h.failed) -or ($c -and $c.failed)) { throw 'Multiplayer transition failed; inspect probe logs' }
        if ($h -and $c -and (& $Condition $h $c)) { return @($h,$c) }
        Start-Sleep -Milliseconds 200
    }
    throw 'Multiplayer test timed out'
}
function Send-Command([string]$Mode,[string]$Operation) {
    & (Join-Path $PSScriptRoot 'Invoke-Probe.ps1') -Mode $Mode -Operation $Operation | Out-Host
}
$cutoff=[DateTimeOffset]::UtcNow.AddSeconds(-15)
$pair=Await-Pair {param($h,$c) ([DateTimeOffset]$h.time) -gt $cutoff -and ([DateTimeOffset]$c.time) -gt $cutoff -and $h.seed -and $h.seed -eq $c.seed -and $h.room -eq 'EventRoom' -and $c.room -eq 'EventRoom' -and !$h.isChangingRooms -and !$c.isChangingRooms -and !$h.committed -and !$c.committed -and !$h.slBusy -and !$c.slBusy}
Start-Sleep -Seconds 2
$pair=Await-Pair {param($h,$c) $h.seed -and $h.seed -eq $c.seed -and $h.room -eq 'EventRoom' -and $c.room -eq 'EventRoom' -and !$h.isChangingRooms -and !$c.isChangingRooms -and !$h.committed -and !$c.committed -and !$h.slBusy -and !$c.slBusy}
$hostIdentity=$pair[0].netIdentity; $clientIdentity=$pair[1].netIdentity
$roles=ConvertTo-Json -InputObject $pair[0].players -Compress
$results=@()
for($round=1;$round -le $Count;$round++) {
    $oldSeed=$pair[0].seed
    $initiator=if($round % 2 -eq 0){'host'}else{'client'}
    $approver=if($initiator -eq 'host'){'client'}else{'host'}
    Send-Command $initiator request
    $null=Await-Pair {param($h,$c) $h.request -and $c.request -and $h.request.RequestId -eq $c.request.RequestId}
    $watch=[Diagnostics.Stopwatch]::StartNew()
    Send-Command $approver agree
    $pair=Await-Pair {param($h,$c) $h.seed -ne $oldSeed -and $h.seed -eq $c.seed -and !$h.request -and !$c.request -and !$h.committed -and !$c.committed -and $h.room -eq 'EventRoom' -and $c.room -eq 'EventRoom'}
    $restartMs=$watch.ElapsedMilliseconds
    if(!$pair[0].connected -or !$pair[1].connected -or $pair[0].netIdentity -ne $hostIdentity -or $pair[1].netIdentity -ne $clientIdentity){throw 'Original network service was not preserved'}
    foreach($state in $pair){if((ConvertTo-Json -InputObject $state.players -Compress) -ne $roles){throw 'Player/role mapping changed'}}
    $seed=$pair[0].seed
    $hostLog=Join-Path $probe 'host\stdout.log'
    $previousCompletions=@(Select-String -LiteralPath $hostLog -Pattern '多人快速 SL 完成|多人快速 SL：完成|多人快速 SL.*完成').Count
    Send-Command $initiator sl
    # Test-build-only confirmation provider returns true; original QuickSL request/vote/load protocol still runs.
    $pair=Await-Pair {param($h,$c)
        $completions=@(Select-String -LiteralPath $hostLog -Pattern '多人快速 SL 完成|多人快速 SL：完成|多人快速 SL.*完成').Count
        $completions -gt $previousCompletions -and !$h.slBusy -and !$c.slBusy -and !$h.isChangingRooms -and !$c.isChangingRooms
    }
    if($pair[0].seed -ne $seed -or $pair[1].seed -ne $seed){throw 'QuickSL restored the wrong seed'}
    $results+=@{round=$round; initiator=$initiator; seed=$seed; restartMs=$restartMs; sameHostService=$true; sameClientService=$true; quickSlPassed=$true}
    Write-Output "PASS multiplayer $round initiator=$initiator seed=$seed restart=${restartMs}ms + QuickSL"
}
[System.IO.File]::WriteAllText((Join-Path $probe 'multiplayer-cycles-result.json'),(ConvertTo-Json -InputObject $results -Depth 5))
