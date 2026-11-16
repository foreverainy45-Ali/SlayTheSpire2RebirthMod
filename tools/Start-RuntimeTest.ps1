param([ValidateSet('single','host','client','client2','client3')][string]$Mode='single', [switch]$Windowed, [ValidateRange(2,4)][int]$Players=2)
$ErrorActionPreference='Stop'
$workspace=[System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testGame=Join-Path $workspace ".work\games\$Mode"
$probe=Join-Path $workspace ".work\probe\$Mode"
New-Item -ItemType Directory -Path $probe -Force | Out-Null
$env:SEEDRESTART_PROBE_DIR=$probe
$env:SEEDRESTART_PROBE_MODE=$Mode
$env:SEEDRESTART_PROBE_AUTO_SL='1'
$env:SEEDRESTART_PROBE_PLAYERS="$Players"
$argsList=@('--force-steam=off','--audio-driver','Dummy','--log-file',(Join-Path $probe 'godot.log'))
if (!$Windowed) { $argsList+= '--headless' }
if ($Mode -eq 'host') { $argsList+=@('--fastmp','host_standard','--clientId','1') }
if ($Mode.StartsWith('client')) {
    $clientId=switch($Mode){'client'{'1000'} 'client2'{'1001'} 'client3'{'1002'}}
    $argsList+=@('--fastmp','join','--clientId',$clientId)
}
$windowStyle=if($Windowed){'Normal'}else{'Hidden'}
$process=Start-Process -FilePath (Join-Path $testGame 'SlayTheSpire2.exe') -ArgumentList $argsList -WorkingDirectory $testGame -WindowStyle $windowStyle -RedirectStandardOutput (Join-Path $probe 'stdout.log') -RedirectStandardError (Join-Path $probe 'stderr.log') -PassThru
Write-Output "Mode=$Mode PID=$($process.Id)"
