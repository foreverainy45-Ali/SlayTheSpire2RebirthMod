param([string]$Pattern = 'RunManager|StartRunLobby|RunLobbyPlayer|INetGameService|INetMessage|NGame|NRun|NPauseMenu', [string]$MemberPattern = '.', [string]$Assembly)
$ErrorActionPreference = 'Stop'
$gameDir = 'I:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64'
$libDir = 'I:\SteamLibrary\steamapps\workshop\content\2868840\3747526103'
[System.Runtime.Loader.AssemblyLoadContext]::Default.add_Resolving({
    param($context, $name)
    if ($name.Name.EndsWith('.resources')) { return $null }
    foreach ($base in @($gameDir, $libDir)) {
        $candidate = [System.IO.Path]::Combine($base, $name.Name + '.dll')
        if ([System.IO.File]::Exists($candidate)) { return $context.LoadFromAssemblyPath($candidate) }
    }
    return $null
})
if (!$Assembly) { $Assembly = Join-Path $gameDir 'sts2.dll' }
$asm = [System.Reflection.Assembly]::LoadFrom($Assembly)
try { $types = $asm.GetTypes() } catch [System.Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $_ } }
foreach ($type in $types | Where-Object FullName -Match $Pattern) {
    if ($type.FullName -match '\+') { continue }
    Write-Output "TYPE $($type.FullName)"
    $flags = [System.Reflection.BindingFlags]'Public,NonPublic,Instance,Static,DeclaredOnly'
    foreach ($member in $type.GetMembers($flags)) {
        try {
            if ($member.MemberType -in @('Method','Constructor','Property','Field') -and $member.ToString() -match $MemberPattern) { Write-Output ('  ' + $member.ToString()) }
        } catch { Write-Output ('  [unresolved] ' + $member.Name) }
    }
}
