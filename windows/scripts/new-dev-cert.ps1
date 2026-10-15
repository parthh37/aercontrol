<#
.SYNOPSIS
  Creates the self-signed code-signing certificate used to sign the AerControl MSIX
  for development, exports it to windows\certs\, and (with -Trust) trusts it on this PC.
.NOTES
  The subject MUST match Publisher in src\AerControl.App\Package.appxmanifest.
  -Trust needs an elevated PowerShell (writes to LocalMachine\TrustedPeople).
#>
param(
  [string]$Password = "aercontrol-dev",
  [switch]$Trust
)
$ErrorActionPreference = "Stop"
$subject = "CN=AerControl Dev"
$outDir = Join-Path $PSScriptRoot "..\certs"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } |
        Select-Object -First 1
if (-not $cert) {
  $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature `
    -FriendlyName "AerControl Dev MSIX signing" -CertStoreLocation "Cert:\CurrentUser\My" `
    -NotAfter (Get-Date).AddYears(3) `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
  Write-Host "Created certificate $($cert.Thumbprint)"
}

$pfx = Join-Path $outDir "AerControl-Dev.pfx"
$cer = Join-Path $outDir "AerControl-Dev.cer"
$secure = ConvertTo-SecureString -String $Password -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure | Out-Null
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Write-Host "Exported $pfx and $cer"

if ($Trust) {
  Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
  Write-Host "Trusted $subject in LocalMachine\TrustedPeople"
}
