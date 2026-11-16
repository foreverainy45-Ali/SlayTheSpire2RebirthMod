param(
    [ValidateSet('single','host','client','client2','client3')][string]$Mode='single',
    [ValidateSet('Standard','Daily','Blitz')][string]$GameMode='Standard',
    [switch]$Windowed,
    [switch]$Official,
    [ValidateRange(2,4)][int]$Players=2
)
$ErrorActionPreference='Stop'
$workspace=[System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testKey=if($Official){"official-$Mode"}else{$Mode}
$testGame=Join-Path $workspace ".work\games\$testKey"
$probe=Join-Path $workspace ".work\probe\$testKey"
New-Item -ItemType Directory -Path $probe -Force | Out-Null
[System.IO.File]::WriteAllText((Join-Path $probe 'command.json'),'{"id":0,"operation":"noop"}')
$staleError=Join-Path $probe 'error.txt'
if(Test-Path -LiteralPath $staleError){Remove-Item -LiteralPath $staleError -Force}
$env:SEEDRESTART_PROBE_DIR=$probe
$env:SEEDRESTART_PROBE_MODE=$Mode
$env:SEEDRESTART_PROBE_AUTO_SL='1'
$env:SEEDRESTART_PROBE_PLAYERS="$Players"
$env:SEEDRESTART_PROBE_GAME_MODE=$GameMode
$argsList=@('--force-steam=off','--audio-driver','Dummy','--log-file',(Join-Path $probe 'godot.log'))
if (!$Windowed) { $argsList+= '--headless' }
if ($Mode -eq 'host') {
    $hostMode=switch($GameMode){'Daily'{'host_daily'} 'Blitz'{'host_blitz'} default{'host_standard'}}
    $argsList+=@('--fastmp',$hostMode,'--clientId','1')
}
if ($Mode.StartsWith('client')) {
    $clientId=switch($Mode){'client'{'1000'} 'client2'{'1001'} 'client3'{'1002'}}
    $argsList+=@('--fastmp','join','--clientId',$clientId)
}
$windowStyle=if($Windowed){'Normal'}else{'Hidden'}
$process=Start-Process -FilePath (Join-Path $testGame 'SlayTheSpire2.exe') -ArgumentList $argsList -WorkingDirectory $testGame -WindowStyle $windowStyle -RedirectStandardOutput (Join-Path $probe 'stdout.log') -RedirectStandardError (Join-Path $probe 'stderr.log') -PassThru
Write-Output "Mode=$Mode PID=$($process.Id)"
