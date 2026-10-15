<#
.SYNOPSIS
  Builds and signs the AerControl MSIX (x64 or ARM64) plus a self-contained aerctl.exe.
.EXAMPLE
  .\scripts\new-dev-cert.ps1          # once
  .\scripts\build-msix.ps1            # -> windows\dist\
#>
param(
  [ValidateSet("x64", "ARM64")] [string]$Platform = "x64",
  [string]$Configuration = "Release",
  [string]$CertPath = (Join-Path $PSScriptRoot "..\certs\AerControl-Dev.pfx"),
  [string]$CertPassword = "aercontrol-dev"
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null

if (-not (Test-Path $CertPath)) { throw "Signing certificate not found at $CertPath. Run scripts\new-dev-cert.ps1 first." }

# MSBuild from Visual Studio / Build Tools (the MSIX targets aren't in the dotnet CLI).
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
if (-not $msbuild) { throw "MSBuild not found. Install Visual Studio 2022 (or Build Tools) with the Windows App SDK / .NET desktop workload." }

$proj = Join-Path $root "src\AerControl.App\AerControl.App.csproj"
& $msbuild $proj /restore /m /v:minimal `
  /p:Configuration=$Configuration /p:Platform=$Platform `
  /p:GenerateAppxPackageOnBuild=true /p:UapAppxPackageBuildMode=SideloadOnly `
  /p:AppxPackageSigningEnabled=true `
  /p:PackageCertificateKeyFile="$CertPath" /p:PackageCertificatePassword="$CertPassword"
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed" }

$pkgDir = Get-ChildItem (Join-Path $root "src\AerControl.App\AppPackages") -Directory |
          Sort-Object LastWriteTime -Descending | Select-Object -First 1
Copy-Item -Recurse -Force $pkgDir.FullName (Join-Path $dist "AerControl_$Platform")
Copy-Item -Force (Join-Path $PSScriptRoot "install.ps1") (Join-Path $dist "AerControl_$Platform")
$cer = [IO.Path]::ChangeExtension($CertPath, ".cer")
if (Test-Path $cer) { Copy-Item -Force $cer (Join-Path $dist "AerControl_$Platform") }

$rid = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
dotnet publish (Join-Path $root "src\AerControl.Cli\AerControl.Cli.csproj") -c $Configuration -r $rid `
  --self-contained -p:PublishSingleFile=true -o (Join-Path $dist "aerctl_$Platform")
Write-Host "Output in $dist"
