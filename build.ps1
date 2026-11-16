param(
    [string]$Sts2Dir = 'I:\SteamLibrary\steamapps\common\Slay the Spire 2',
    [string]$JmcModLibDir = 'I:\SteamLibrary\steamapps\workshop\content\2868840\3747526103'
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools\cli-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.tools\packages'
$sdk = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $sdk)) { $sdk = 'dotnet' }
& $sdk build (Join-Path $PSScriptRoot 'src\SeedRestart\SeedRestart.csproj') -c Release "-p:Sts2Dir=$Sts2Dir" "-p:JmcModLibDir=$JmcModLibDir" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Mod build failed' }
& $sdk run --project (Join-Path $PSScriptRoot 'tests\SeedRestart.Tests') -c Release --no-launch-profile
if ($LASTEXITCODE -ne 0) { throw 'Protocol tests failed' }
$package = Join-Path $PSScriptRoot 'dist\SeedRestart'
New-Item -ItemType Directory -Path $package -Force | Out-Null
$obsoleteQuoteConfig = Join-Path $package 'SeedRestart.quotes.json'
if (Test-Path -LiteralPath $obsoleteQuoteConfig) { Remove-Item -LiteralPath $obsoleteQuoteConfig -Force }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src\SeedRestart\bin\Release\net9.0\SeedRestart.dll') -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src\SeedRestart\SeedRestart.json') -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src\SeedRestart\SeedRestart.quotes.cfg') -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $package -Force
$packageDocs=Join-Path $package 'docs'
New-Item -ItemType Directory -Path $packageDocs -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\验证记录.md') -Destination $packageDocs -Force
Compress-Archive -LiteralPath $package -DestinationPath (Join-Path $PSScriptRoot 'dist\SeedRestart-0.4.1.zip') -Force
Write-Output "Package: $package"
