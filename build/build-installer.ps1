<#
.SYNOPSIS
  Builds artifacts\AIMeter-<version>-x64.msi (per-user, self-contained; teammates need no .NET install).

.PARAMETER Version
  Product version (major.minor.patch). Defaults to <Version> in AIMeter.csproj.
  Bump it for every release you hand out, or installed copies won't upgrade.

.PARAMETER CertThumbprint
  Optional code-signing certificate (CurrentUser\My or LocalMachine\My). Signs the exe and the MSI so
  Windows SmartScreen shows your company instead of "Unknown publisher".

.EXAMPLE
  .\build\build-installer.ps1
  .\build\build-installer.ps1 -Version 1.1.0 -CertThumbprint 0123ABCD...
#>
param(
    [string]$Version,
    [string]$CertThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$appDir = Join-Path $artifacts 'app'

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'AIMeter.csproj'))).Project.PropertyGroup |
        ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')." }

function Find-SignTool {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

function Invoke-Sign([string]$file) {
    if (-not $CertThumbprint) { return }
    $signtool = Find-SignTool
    if (-not $signtool) { throw 'signtool.exe not found; install the Windows SDK or omit -CertThumbprint.' }
    & $signtool sign /sha1 $CertThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /d 'AI Meter' $file
    if ($LASTEXITCODE) { throw "Signing failed for $file" }
}

if (-not (Test-Path (Join-Path $root 'assets\AIMeter.ico'))) { & (Join-Path $PSScriptRoot 'make-icon.ps1') }

Write-Host "==> Publishing AI Meter $Version (self-contained, win-x64)" -ForegroundColor Cyan
if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
dotnet publish (Join-Path $root 'AIMeter.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:Version=$Version -o $appDir
if ($LASTEXITCODE) { throw 'dotnet publish failed.' }
Invoke-Sign (Join-Path $appDir 'AIMeter.exe')

Write-Host "==> Building MSI" -ForegroundColor Cyan
dotnet build (Join-Path $root 'installer\AIMeter.Installer.wixproj') -c Release `
    -p:Version=$Version -p:PublishDir="$appDir\" -o $artifacts
if ($LASTEXITCODE) { throw 'MSI build failed.' }

$msi = Join-Path $artifacts "AIMeter-$Version-x64.msi"
Invoke-Sign $msi
Get-ChildItem $artifacts -Filter '*.wixpdb' | Remove-Item -Force
Write-Host "==> Done: $msi ($([math]::Round((Get-Item $msi).Length / 1MB, 1)) MB)" -ForegroundColor Green
