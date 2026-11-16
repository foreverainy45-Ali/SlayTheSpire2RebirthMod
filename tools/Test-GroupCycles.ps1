param([ValidateRange(2,4)][int]$Players=4,[int]$Count=2)
$ErrorActionPreference='Stop'
$probe=[System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.work\probe'))
$modes=@('host','client','client2','client3')[0..($Players-1)]
function Read-Group {
    $states=@()
    foreach($mode in $modes){
        try {$states+=Get-Content -LiteralPath (Join-Path $probe "$mode\state.json") -Raw | ConvertFrom-Json} catch {return $null}
    }
    return $states
}
function Await-Group([scriptblock]$Condition) {
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(80)
    while([DateTimeOffset]::UtcNow -lt $deadline){
        $states=@(Read-Group)
        if($states.Count -eq $Players -and ($states|Where-Object failed)){throw 'Group transition failed'}
        if($states.Count -eq $Players -and (& $Condition $states)){return $states}
        Start-Sleep -Milliseconds 200
    }
    throw 'Group test timed out'
}
function Command([string]$Mode,[string]$Operation){ & (Join-Path $PSScriptRoot 'Invoke-Probe.ps1') -Mode $Mode -Operation $Operation | Out-Host }
$cutoff=[DateTimeOffset]::UtcNow.AddSeconds(-15)
$states=Await-Group {param($g) !($g|Where-Object {([DateTimeOffset]$_.time) -lt $cutoff -or $_.players.Count -ne $Players -or $_.room -ne 'EventRoom' -or $_.committed -or $_.request -or $_.slBusy -or $_.isChangingRooms})}
$identities=@($states|ForEach-Object netIdentity)
$roles=ConvertTo-Json -InputObject $states[0].players -Compress
$results=@()
if($Players -ge 3){
    $unchanged=$states[0].seed
    $beforeReject=@($states)
    Command client request
    $null=Await-Group {param($g) !($g|Where-Object {!$_.request})}
    Command host agree
    $partial=Await-Group {param($g) !($g|Where-Object {$_.request.Votes.Count -ne 2})}
    if($partial|Where-Object {$_.committed -or $_.seed -ne $unchanged}){throw 'Partial consent incorrectly restarted the run'}
    Command client2 reject
    $states=Await-Group {param($g) !($g|Where-Object {$_.request -or $_.seed -ne $unchanged})}
    for($i=0;$i -lt $Players;$i++){
        if([long]$states[$i].losses -ne [long]$beforeReject[$i].losses -or [long]$states[$i].runs -ne [long]$beforeReject[$i].runs -or
            [int]$states[$i].historyFiles -ne [int]$beforeReject[$i].historyFiles){throw 'Rejected vote wrote a failure record'}
    }
    Write-Output "PASS $Players-player partial consent + rejection preserves run"
    $results+=@{players=$Players; scenario='partial-consent-reject'; passed=$true}
    $beforeSl=@($states)
    Command host request
    $null=Await-Group {param($g) !($g|Where-Object {!$_.request})}
    $log=Join-Path $probe 'host\stdout.log'
    $completions=@(Select-String -LiteralPath $log -Pattern '多人快速 SL.*完成').Count
    Command client sl
    $states=Await-Group {param($g) (@(Select-String -LiteralPath $log -Pattern '多人快速 SL.*完成').Count -gt $completions) -and !($g|Where-Object {$_.request -or $_.committed -or $_.slBusy -or $_.isChangingRooms -or $_.seed -ne $unchanged})}
    for($i=0;$i -lt $Players;$i++){
        if([long]$states[$i].losses -ne [long]$beforeSl[$i].losses -or [long]$states[$i].runs -ne [long]$beforeSl[$i].runs -or
            [int]$states[$i].historyFiles -ne [int]$beforeSl[$i].historyFiles){throw 'QuickSL cancellation wrote a reroll failure record'}
    }
    Write-Output "PASS $Players-player QuickSL cancels pending reroll and completes normally"
    $results+=@{players=$Players; scenario='quicksl-during-vote'; passed=$true}
}
for($round=1;$round -le $Count;$round++){
    $seed=$states[0].seed
    $beforeRecords=@($states)
    $initiator=$modes[$round % $Players]
    Command $initiator request
    $null=Await-Group {param($g) !($g|Where-Object {!$_.request})}
    $watch=[Diagnostics.Stopwatch]::StartNew()
    foreach($mode in $modes|Where-Object {$_ -ne $initiator}) {Command $mode agree}
    $states=Await-Group {param($g) !($g|Where-Object {$_.seed -eq $seed -or $_.seed -ne $g[0].seed -or $_.request -or $_.committed -or $_.room -ne 'EventRoom'})}
    $ms=$watch.ElapsedMilliseconds
    for($i=0;$i -lt $Players;$i++){
        if(!$states[$i].connected -or $states[$i].netIdentity -ne $identities[$i]){throw 'Connection identity changed'}
        if((ConvertTo-Json -InputObject $states[$i].players -Compress) -ne $roles){throw 'Role mapping changed'}
        if([long]$states[$i].losses -ne ([long]$beforeRecords[$i].losses+1) -or [long]$states[$i].runs -ne ([long]$beforeRecords[$i].runs+1) -or
            [int]$states[$i].historyFiles -ne ([int]$beforeRecords[$i].historyFiles+1)){throw 'A group peer did not write exactly one failure record'}
    }
    $newSeed=$states[0].seed
    $log=Join-Path $probe 'host\stdout.log'
    $completions=@(Select-String -LiteralPath $log -Pattern '多人快速 SL.*完成').Count
    Command $initiator sl
    $states=Await-Group {param($g) (@(Select-String -LiteralPath $log -Pattern '多人快速 SL.*完成').Count -gt $completions) -and !($g|Where-Object {$_.slBusy -or $_.isChangingRooms -or $_.seed -ne $newSeed})}
    $results+=@{players=$Players; round=$round; initiator=$initiator; seed=$newSeed; restartMs=$ms; connectionsPreserved=$true; quickSlPassed=$true}
    Write-Output "PASS $Players-player round $round initiator=$initiator ${ms}ms + QuickSL"
}
[System.IO.File]::WriteAllText((Join-Path $probe "group-$Players-result.json"),(ConvertTo-Json -InputObject $results -Depth 5))
