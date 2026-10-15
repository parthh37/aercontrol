<#
.SYNOPSIS
  Installs AerControl from an extracted release folder: trusts the dev certificate
  (one UAC prompt), installs the Windows App Runtime dependency, then the app.
#>
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot

$cer = Get-ChildItem -Path $here -Filter *.cer -Recurse | Select-Object -First 1
if ($cer) {
  $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Subject -eq "CN=AerControl Dev" }
  if (-not $trusted) {
    Write-Host "Trusting the AerControl signing certificate (administrator approval required)..."
    Start-Process powershell -Verb RunAs -Wait -ArgumentList @(
      "-NoProfile", "-Command",
      "Import-Certificate -FilePath '$($cer.FullName)' -CertStoreLocation Cert:\LocalMachine\TrustedPeople")
  }
}

# Preferred: install through the .appinstaller, so Windows keeps AerControl updated from
# GitHub Releases on its own. Falls back to the bundled packages (offline install).
$ai = Get-ChildItem -Path $here -Filter *.appinstaller -Recurse | Select-Object -First 1
if ($ai) {
  try {
    Write-Host "Installing via App Installer (enables automatic updates)..."
    Add-AppxPackage -AppInstallerFile $ai.FullName -ForceApplicationShutdown -ErrorAction Stop
    Write-Host "Installed. Start AerControl from the Start menu; add the widget from the Widgets board (Win+W > +)."
    return
  } catch {
    Write-Host "App Installer route failed ($($_.Exception.Message)); installing the bundled packages instead."
  }
}

$arch = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "arm64" } else { "x64" }
$deps = Get-ChildItem -Path $here -Recurse -Filter *.msix |
        Where-Object { $_.FullName -match "\\Dependencies\\$arch\\" }
# Release zips carry one architecture's runtime (e.g. the x64 zip on an ARM64 PC): use what's there.
if (-not $deps) {
  $deps = Get-ChildItem -Path $here -Recurse -Filter *.msix | Where-Object { $_.FullName -match "\\Dependencies\\" }
}
foreach ($d in $deps) {
  Write-Host "Installing dependency $($d.Name)"
  try { Add-AppxPackage -Path $d.FullName -ErrorAction Stop } catch { Write-Host "  (already installed or newer present)" }
}

$pkg = Get-ChildItem -Path $here -Recurse -Filter "AerControl*.msix" |
       Where-Object { $_.FullName -notmatch "\\Dependencies\\" } | Select-Object -First 1
if (-not $pkg) { throw "AerControl .msix not found under $here" }
Write-Host "Installing $($pkg.Name)"
Add-AppxPackage -Path $pkg.FullName -ForceApplicationShutdown
Write-Host "Installed. Start AerControl from the Start menu; add the widget from the Widgets board (Win+W > +)."
