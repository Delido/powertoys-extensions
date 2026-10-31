<#
.SYNOPSIS
  Installs or updates Delido PowerToys Extensions.

.DESCRIPTION
  Run in PowerShell:
    irm https://raw.githubusercontent.com/Delido/powertoys-extensions/main/install.ps1 | iex

  Downloads the latest release, trusts its signing certificate (one UAC prompt, only the first time)
  and installs the package. Command Palette picks the extensions up after a reload.

  Local package instead of a download:  .\install.ps1 -Package artifacts\PowerToysExtensions.msix
#>
param([string]$Package)

$ErrorActionPreference = 'Stop'
$repo = 'Delido/powertoys-extensions'
$work = Join-Path ([IO.Path]::GetTempPath()) 'delido-powertoys-extensions'
New-Item -ItemType Directory -Force $work | Out-Null

if ($Package) {
    $msix = (Resolve-Path $Package).Path
    $cer = Join-Path (Split-Path $msix -Parent) 'Delido.cer'
} else {
    Write-Host 'Looking for the latest release ...'
    $release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'installer' }
    $msix = Join-Path $work 'PowerToysExtensions.msix'
    $cer = Join-Path $work 'Delido.cer'
    foreach ($name in 'PowerToysExtensions.msix', 'Delido.cer') {
        $asset = $release.assets | Where-Object name -eq $name
        if (-not $asset) { throw "Release $($release.tag_name) has no $name" }
        Invoke-WebRequest $asset.browser_download_url -OutFile (Join-Path $work $name) -UseBasicParsing
    }
    Write-Host "Downloaded $($release.tag_name)"
}

# Trust the publisher once. Windows only installs MSIX packages from trusted publishers.
$certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2 $cer
$trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $certificate.Thumbprint
if (-not $trusted) {
    Write-Host 'Trusting the publisher certificate (Windows asks for administrator rights once) ...'
    $cmd = "Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
    Start-Process powershell.exe -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList '-NoProfile', '-Command', $cmd
    if (-not (Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $certificate.Thumbprint)) {
        throw 'The certificate was not trusted (UAC prompt declined?)'
    }
}

Write-Host 'Installing the package ...'
Add-AppxPackage -Path $msix -ForceApplicationShutdown -ForceUpdateFromAnyVersion

# Ask Command Palette to load the new extensions (it asks for consent the first time)
Start-Process 'x-cmdpal://reload' -ErrorAction SilentlyContinue
Write-Host 'Done. Open Command Palette – the extensions appear under Settings > Extensions.'
