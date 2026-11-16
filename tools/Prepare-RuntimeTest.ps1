param([ValidateSet('single','host','client','client2','client3')][string]$Mode = 'single')
$ErrorActionPreference = 'Stop'
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = 'I:\SteamLibrary\steamapps\common\Slay the Spire 2'
$target = Join-Path $workspace ".work\games\$Mode"
New-Item -ItemType Directory -Path $target -Force | Out-Null
# Read-only game assets share disk blocks; all mutable test files use independent copies.
foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
    $relative = [System.IO.Path]::GetRelativePath($source, $file.FullName)
    if ($relative -match '^(mods|mods_STEAMTEST|logs)[\\/]') { continue }
    $destination = Join-Path $target $relative
    if (!(Test-Path -LiteralPath $destination)) {
        New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        New-Item -ItemType HardLink -Path $destination -Target $file.FullName | Out-Null
    }
}
Copy-Item -LiteralPath (Join-Path $workspace "tests\runtime\override-$Mode.cfg") -Destination (Join-Path $target 'override.cfg') -Force
$testUserDir = Join-Path $env:APPDATA ('SlayTheSpire2_SeedRestart_Test' + (Get-Culture).TextInfo.ToTitleCase($Mode))
if ([System.IO.Path]::GetFileName($testUserDir) -notmatch '^SlayTheSpire2_SeedRestart_Test(Single|Host|Client[23]?)$') { throw 'Unsafe test profile' }
New-Item -ItemType Directory -Path $testUserDir -Force | Out-Null
if (!(Test-Path -LiteralPath (Join-Path $testUserDir 'settings.save'))) {
    Copy-Item -LiteralPath (Join-Path $workspace 'tests\runtime\settings.save') -Destination (Join-Path $testUserDir 'settings.save')
}
$account = switch($Mode) { 'client' { '1000' } 'client2' { '1001' } 'client3' { '1002' } default { '1' } }
$scoped = Join-Path $testUserDir "default\$account"
New-Item -ItemType Directory -Path $scoped -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $workspace 'tests\runtime\settings.save') -Destination (Join-Path $scoped 'settings.save') -Force
$testMods = Join-Path $target 'mods'
New-Item -ItemType Directory -Path $testMods -Force | Out-Null
foreach ($id in @('3747526103','3747539544','3747533700')) {
    $modSource = "I:\SteamLibrary\steamapps\workshop\content\2868840\$id"
    if (Test-Path -LiteralPath $modSource) { Copy-Item -LiteralPath $modSource -Destination $testMods -Recurse -Force }
}
$testMod = Join-Path $testMods 'SeedRestart'
New-Item -ItemType Directory -Path $testMod -Force | Out-Null
$obsoleteQuoteConfig = Join-Path $testMod 'SeedRestart.quotes.json'
if (Test-Path -LiteralPath $obsoleteQuoteConfig) { Remove-Item -LiteralPath $obsoleteQuoteConfig -Force }
Copy-Item -LiteralPath (Join-Path $workspace '.work\probe-build\SeedRestart.dll') -Destination $testMod -Force
Copy-Item -LiteralPath (Join-Path $workspace 'src\SeedRestart\SeedRestart.json') -Destination $testMod -Force
Copy-Item -LiteralPath (Join-Path $workspace 'src\SeedRestart\SeedRestart.quotes.cfg') -Destination $testMod -Force
$probe=Join-Path $workspace ".work\probe\$Mode"
New-Item -ItemType Directory -Path $probe -Force | Out-Null
[System.IO.File]::WriteAllText((Join-Path $probe 'command.json'),'{"id":0,"operation":"noop"}')
Write-Output "Test game: $target; isolated profile: $testUserDir"
