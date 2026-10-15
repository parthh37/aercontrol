<#
.SYNOPSIS
  Turns build-msix.ps1 output into GitHub release assets:
    AerControl-<P>.msix            the app package
    <P>-<dependency>.msix          Windows App Runtime framework package(s)
    AerControl-<P>.appinstaller    App Installer file: install + automatic updates
    AerControl-Dev.cer             signing certificate to trust
    AerControl-windows-<P>.zip     app msix, .appinstaller, .cer, install.ps1, Dependencies\<arch>\
    aerctl-windows-<P>.zip         CLI
.NOTES
  The .appinstaller points at releases/latest/download/AerControl-<P>.appinstaller, so
  Windows checks the newest release on launch (and in the background) and updates in place.
#>
param(
  [ValidateSet("x64", "ARM64")] [string]$Platform = "x64",
  [Parameter(Mandatory)] [string]$Tag,
  [string]$Repository = $(if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { "parthh37/aercontrol" })
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$dist = Join-Path $root "dist\AerControl_$Platform"
$out = Join-Path $root "release"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$arch = $Platform.ToLowerInvariant()

function Get-Identity([string]$msix) {
  $zip = [IO.Compression.ZipFile]::OpenRead($msix)
  try {
    $reader = New-Object IO.StreamReader($zip.GetEntry("AppxManifest.xml").Open())
    [xml]$xml = $reader.ReadToEnd()
    $reader.Dispose()
    return $xml.Package.Identity
  } finally { $zip.Dispose() }
}

$main = Get-ChildItem $dist -Recurse -Filter *.msix | Where-Object { $_.FullName -notmatch "\\Dependencies\\" } | Select-Object -First 1
if (-not $main) { throw "No app .msix under $dist" }
$mainName = "AerControl-$Platform.msix"
Copy-Item $main.FullName (Join-Path $out $mainName) -Force
$id = Get-Identity $main.FullName

$downloadBase = "https://github.com/$Repository/releases/download/$Tag"
$depXml = ""
$deps = Get-ChildItem $dist -Recurse -Filter *.msix | Where-Object { $_.FullName -match "\\Dependencies\\$arch\\" }
foreach ($d in $deps) {
  $depName = "$Platform-$($d.Name)"
  Copy-Item $d.FullName (Join-Path $out $depName) -Force
  $did = Get-Identity $d.FullName
  $depXml += "    <Package Name=`"$($did.Name)`" Publisher=`"$($did.Publisher)`" Version=`"$($did.Version)`" ProcessorArchitecture=`"$($did.ProcessorArchitecture)`" Uri=`"$downloadBase/$depName`" />`n"
}

$appInstaller = @"
<?xml version="1.0" encoding="utf-8"?>
<AppInstaller xmlns="http://schemas.microsoft.com/appx/appinstaller/2018"
              Version="$($id.Version)"
              Uri="https://github.com/$Repository/releases/latest/download/AerControl-$Platform.appinstaller">
  <MainPackage Name="$($id.Name)" Publisher="$($id.Publisher)" Version="$($id.Version)"
               ProcessorArchitecture="$arch" Uri="$downloadBase/$mainName" />
  <Dependencies>
$depXml  </Dependencies>
  <UpdateSettings>
    <OnLaunch HoursBetweenUpdateChecks="12" ShowPrompt="true" UpdateBlocksActivation="false" />
    <AutomaticBackgroundTask />
  </UpdateSettings>
</AppInstaller>
"@
$aiPath = Join-Path $out "AerControl-$Platform.appinstaller"
Set-Content -Path $aiPath -Value $appInstaller -Encoding UTF8

$cer = Get-ChildItem $dist -Filter *.cer | Select-Object -First 1
if ($cer) { Copy-Item $cer.FullName (Join-Path $out "AerControl-Dev.cer") -Force }

# Offline bundle: only what install.ps1 needs for this platform. AppPackages ships the runtime for
# every architecture plus symbols; keep the app msix at the top and this arch's deps under
# Dependencies\<arch>\, the layout install.ps1 searches.
$bundle = Join-Path $root "dist\bundle_$Platform"
if (Test-Path $bundle) { Remove-Item -Recurse -Force $bundle }
$bundleDeps = Join-Path $bundle "Dependencies\$arch"
New-Item -ItemType Directory -Force -Path $bundleDeps | Out-Null
Copy-Item $main.FullName (Join-Path $bundle $mainName)
foreach ($d in $deps) { Copy-Item $d.FullName $bundleDeps }
Copy-Item $aiPath $bundle
if ($cer) { Copy-Item $cer.FullName (Join-Path $bundle "AerControl-Dev.cer") }
Copy-Item (Join-Path $PSScriptRoot "install.ps1") $bundle
$zipPath = Join-Path $out "AerControl-windows-$Platform.zip"
Compress-Archive -Path (Join-Path $bundle "*") -DestinationPath $zipPath -Force
$zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
try { $zip.Entries | ForEach-Object { Write-Host "  zip: $($_.FullName)  $([math]::Round($_.Length / 1MB, 1)) MB" } } finally { $zip.Dispose() }
Compress-Archive -Path (Join-Path $root "dist\aerctl_$Platform\aerctl.exe") -DestinationPath (Join-Path $out "aerctl-windows-$Platform.zip") -Force
Get-ChildItem $out | ForEach-Object { Write-Host "  $($_.Name)  $([math]::Round($_.Length / 1MB, 1)) MB" }
