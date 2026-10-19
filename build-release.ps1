# Builds a Monoview release into artifacts\: the portable zip, the per-user MSI installer, and SHA-256 checksums.
# The version comes from src\Monoview.App\Monoview.App.csproj. Run from any folder:
#   powershell -ExecutionPolicy Bypass -File build-release.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\Monoview.App\Monoview.App.csproj'
$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $project" }

$out = Join-Path $root 'artifacts'
$name = "Monoview-$version-win-x64"
$app = Join-Path $out $name
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

Write-Host "Publishing Monoview $version"
dotnet publish $project -c Release -p:Platform=x64 -r win-x64 -o $app
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

# Debug symbols hold build paths from this PC; releases leave them out.
Get-ChildItem $app -Filter *.pdb -Recurse | Remove-Item
Copy-Item (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD_PARTY_NOTICES.md') $app

Write-Host 'Zipping the portable app'
Compress-Archive -Path $app -DestinationPath (Join-Path $out "$name.zip") -CompressionLevel Optimal

Write-Host 'Building the installer'
dotnet build (Join-Path $root 'installer\Monoview.Installer.wixproj') -c Release -p:Version=$version -p:PublishDir=$app -o $out
if ($LASTEXITCODE) { throw 'Installer build failed' }
Get-ChildItem $out -Filter *.wixpdb | Remove-Item

$sums = Get-ChildItem $out -File | Where-Object { $_.Extension -in '.zip', '.msi' } |
    ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name }
$sums | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii
$sums
Write-Host "Done: $out"
