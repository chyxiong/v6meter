# Builds the release: dist/v6meter.exe (single self-contained file, no .NET install needed) and its SHA-256.
# Usage: ./build/publish.ps1
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$dist = Join-Path $root 'dist'
$staging = Join-Path $root 'artifacts/publish'

dotnet test (Join-Path $root 'tests/V6Meter.Core.Tests') --nologo
if ($LASTEXITCODE -ne 0) { throw 'tests failed' }

dotnet publish (Join-Path $root 'src/V6Meter.App') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:DebugType=none -o $staging --nologo
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item (Join-Path $staging 'v6meter.exe') $dist -Force
$exe = Join-Path $dist 'v6meter.exe'
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  v6meter.exe" | Set-Content (Join-Path $dist 'v6meter.exe.sha256') -Encoding ascii
$ver = (Get-Item $exe).VersionInfo.ProductVersion
"v6meter $ver -> $exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB)"
"SHA256 $hash"
