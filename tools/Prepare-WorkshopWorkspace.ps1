$ErrorActionPreference='Stop'
$workspace=[System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root=(Resolve-Path -LiteralPath 'I:\ModUploader-win-x64').Path
if($root -ne 'I:\ModUploader-win-x64'){throw 'Unexpected uploader root'}
$source=[System.IO.Path]::GetFullPath((Join-Path $root 'NewModWorkspace'))
$destination=[System.IO.Path]::GetFullPath((Join-Path $root 'SeedRestartWorkshop'))
if($source -ne 'I:\ModUploader-win-x64\NewModWorkspace' -or $destination -ne 'I:\ModUploader-win-x64\SeedRestartWorkshop'){throw 'Unexpected rename targets'}
foreach($path in @($root,$source)){
    if((Get-Item -LiteralPath $path).Attributes -band [System.IO.FileAttributes]::ReparsePoint){throw 'Do not rename a redirected workspace'}
}
if(Test-Path -LiteralPath $destination){throw 'Destination exists; refusing to merge workspaces'}
if(Test-Path -LiteralPath (Join-Path $source 'mod_id.txt')){throw 'Existing Workshop item; inspect before renaming'}
if(!(Test-Path -LiteralPath (Join-Path $workspace 'assets\workshop\rebirth-cover.png'))){throw 'Cover is not ready'}
$publish=Get-Content -LiteralPath (Join-Path $workspace 'publishing\workshop.json') -Raw | ConvertFrom-Json
if($publish.visibility -ne 'private'){throw 'Preparation must keep Workshop visibility private'}
Rename-Item -LiteralPath $source -NewName 'SeedRestartWorkshop'
$backup=Join-Path $destination '_template-backup'
New-Item -ItemType Directory -Path (Join-Path $backup 'content') -Force | Out-Null
foreach($relative in @('image.png','workshop.json','README.md','content\README.md')){
    $file=Join-Path $destination $relative
    if(Test-Path -LiteralPath $file){Copy-Item -LiteralPath $file -Destination (Join-Path $backup $relative)}
}
Copy-Item -LiteralPath (Join-Path $workspace 'publishing\workshop.json') -Destination (Join-Path $destination 'workshop.json') -Force
Copy-Item -LiteralPath (Join-Path $workspace 'publishing\README.md') -Destination (Join-Path $destination 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $workspace 'assets\workshop\rebirth-cover.png') -Destination (Join-Path $destination 'image.png') -Force
foreach($file in Get-ChildItem -LiteralPath (Join-Path $workspace 'dist\SeedRestart') -Force){
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination 'content') -Recurse -Force
}
$actual=(Get-FileHash -LiteralPath (Join-Path $destination 'content\SeedRestart.dll') -Algorithm SHA256).Hash
$expected=(Get-FileHash -LiteralPath (Join-Path $workspace 'dist\SeedRestart\SeedRestart.dll') -Algorithm SHA256).Hash
if($actual -ne $expected){throw 'Packaged DLL verification failed'}
Write-Output "Prepared: $destination; visibility=private; DLL hash verified. No upload performed."
Get-ChildItem -LiteralPath $destination -Recurse -File | Select-Object FullName,Length
