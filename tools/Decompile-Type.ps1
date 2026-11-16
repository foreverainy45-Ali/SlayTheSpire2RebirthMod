param(
    [Parameter(Mandatory)][string]$Type,
    [string]$Assembly = 'I:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll',
    [string]$Output
)
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot '..\.tools\decompiler\lib\netstandard2.0\ICSharpCode.Decompiler.dll')
$settings = [ICSharpCode.Decompiler.DecompilerSettings]::new()
$settings.ThrowOnAssemblyResolveErrors = $false
$decompiler = [ICSharpCode.Decompiler.CSharp.CSharpDecompiler]::new($Assembly, $settings)
$name = [ICSharpCode.Decompiler.TypeSystem.FullTypeName]::new($Type)
$source = $decompiler.DecompileTypeAsString($name)
if ($Output) {
    $path = [System.IO.Path]::GetFullPath($Output)
    $root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.work')) + [System.IO.Path]::DirectorySeparatorChar
    if (!$path.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Analysis output must be inside project .work' }
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($path)) | Out-Null
    [System.IO.File]::WriteAllText($path, $source)
    Write-Output $path
} else { Write-Output $source }
