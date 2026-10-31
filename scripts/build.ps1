<#
.SYNOPSIS
  Builds and signs the MSIX package into artifacts\.

.PARAMETER Version
  Package version (x.y.z); default from Package.appxmanifest.

.PARAMETER Pfx
  Base64 PFX and -Password for CI. Without it the CN=Delido certificate from Cert:\CurrentUser\My is used.
#>
param([string]$Version, [string]$Pfx, [string]$Password)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\PowerToysExtensions\PowerToysExtensions.csproj'
$manifest = Join-Path $root 'src\PowerToysExtensions\Package.appxmanifest'
$artifacts = Join-Path $root 'artifacts'

& (Join-Path $PSScriptRoot 'build-helpers.ps1')

$original = Get-Content $manifest -Raw
if ($Version) {
    $patched = $original -replace '(<Identity [^>]*Version=")[^"]+(")', "`${1}$Version.0`${2}"
    Set-Content $manifest $patched -NoNewline -Encoding utf8
}

try {
    Remove-Item $artifacts -Recurse -Force -ErrorAction SilentlyContinue
    dotnet build $project -c Release -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true --nologo -v:minimal
    if ($LASTEXITCODE) { throw 'dotnet build failed' }
} finally {
    if ($Version) { Set-Content $manifest $original -NoNewline -Encoding utf8 }
}

$msix = Get-ChildItem $artifacts -Recurse -Filter *.msix | Select-Object -First 1
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw 'signtool.exe not found – install the Windows SDK' }

if ($Pfx) {
    $pfxFile = Join-Path ([IO.Path]::GetTempPath()) "signing-$([guid]::NewGuid()).pfx"
    [IO.File]::WriteAllBytes($pfxFile, [Convert]::FromBase64String($Pfx))
    try { & $signtool.FullName sign /fd SHA256 /f $pfxFile /p $Password $msix.FullName }
    finally { Remove-Item $pfxFile -Force }
} else {
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=Delido' -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) { throw 'No CN=Delido certificate – run scripts\new-certificate.ps1 first' }
    & $signtool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint /s My $msix.FullName
}
if ($LASTEXITCODE) { throw 'signing failed' }

$out = Join-Path $artifacts 'PowerToysExtensions.msix'
Move-Item $msix.FullName $out -Force
Copy-Item (Join-Path $root 'certificate\Delido.cer') $artifacts
Write-Host "Package: $out"
