<#
.SYNOPSIS
  Creates the self-signed code signing certificate (CN=Delido) once.

.DESCRIPTION
  The private key stays in your certificate store (Cert:\CurrentUser\My). The public part is written to
  certificate\Delido.cer, which the installer trusts. With -GitHubSecret the PFX is uploaded as repository
  secrets for the release workflow (SIGNING_PFX, SIGNING_PASSWORD) without being written to disk.
#>
param([switch]$GitHubSecret)

$ErrorActionPreference = 'Stop'
$subject = 'CN=Delido'
$root = Split-Path $PSScriptRoot -Parent

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature `
        -FriendlyName 'Delido PowerToys Extensions' -CertStoreLocation Cert:\CurrentUser\My `
        -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(5) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    Write-Host "Created certificate $($cert.Thumbprint)"
} else {
    Write-Host "Using existing certificate $($cert.Thumbprint)"
}

New-Item -ItemType Directory -Force (Join-Path $root 'certificate') | Out-Null
Export-Certificate -Cert $cert -FilePath (Join-Path $root 'certificate\Delido.cer') | Out-Null
Write-Host 'Public certificate: certificate\Delido.cer'

if ($GitHubSecret) {
    $password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    $bytes = $cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password)
    [Convert]::ToBase64String($bytes) | gh secret set SIGNING_PFX
    $password | gh secret set SIGNING_PASSWORD
    Write-Host 'Uploaded SIGNING_PFX and SIGNING_PASSWORD to the GitHub repository'
}
