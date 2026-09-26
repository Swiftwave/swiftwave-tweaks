[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$release = Join-Path $root 'release'
Remove-Item $release -Recurse -Force -ErrorAction SilentlyContinue
& (Join-Path $root 'New-Icon.ps1')
dotnet restore (Join-Path $root 'GameReadyOptimizer.csproj')
dotnet build (Join-Path $root 'GameReadyOptimizer.csproj') -c Debug
dotnet publish (Join-Path $root 'GameReadyOptimizer.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $release
Copy-Item (Join-Path $root 'release-assets\README.txt') $release
Copy-Item (Join-Path $root 'release-assets\CHANGELOG.md') $release
Copy-Item (Join-Path $root 'release-assets\VERSION.txt') $release
Write-Host "Release package created: $release"
