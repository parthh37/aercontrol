<#
.SYNOPSIS
  Compiles installer\AerControl.iss into release\AerControl-Setup-<P>.exe from the
  build-msix.ps1 output (dist\AerControl_<P>). Needs Inno Setup 6.3+
  (winget install JRSoftware.InnoSetup); CI installs it automatically.
.EXAMPLE
  .\scripts\build-msix.ps1; .\scripts\build-installer.ps1
#>
param(
  [ValidateSet("x64", "ARM64")] [string]$Platform = "x64"
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$src = Join-Path $root "dist\AerControl_$Platform"
$out = Join-Path $root "release"
if (-not (Test-Path $src)) { throw "$src not found. Run scripts\build-msix.ps1 -Platform $Platform first." }
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Find-Iscc {
  $candidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
  )
  $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
  if ($cmd) { $candidates = @($cmd.Source) + $candidates }
  $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

$iscc = Find-Iscc
if (-not $iscc -and $env:GITHUB_ACTIONS) {
  choco install innosetup -y --no-progress | Out-Host
  $iscc = Find-Iscc
}
if (-not $iscc) { throw "Inno Setup (ISCC.exe) not found. Install it: winget install JRSoftware.InnoSetup" }

$version = (Get-Content (Join-Path $root "..\VERSION") -Raw).Trim()
& $iscc /Q "/DAppVersion=$version" "/DPlatform=$Platform" "/DSourceDir=$src" "/DOutputDir=$out" `
  (Join-Path $root "installer\AerControl.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
Get-Item (Join-Path $out "AerControl-Setup-$Platform.exe") | ForEach-Object { Write-Host "  $($_.Name)  $([math]::Round($_.Length / 1MB, 1)) MB" }
