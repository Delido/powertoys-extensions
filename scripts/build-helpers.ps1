<#
.SYNOPSIS
  Builds the Go helpers (helpers\*) into src\PowerToysExtensions\Helpers\, if Go is installed.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'src\PowerToysExtensions\Helpers'
$helpers = Get-ChildItem (Join-Path $root 'helpers') -Directory -ErrorAction SilentlyContinue
if (-not $helpers) { return }

if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
    if (Get-ChildItem $out -Filter *.exe -ErrorAction SilentlyContinue) { Write-Host 'Go not found, using existing helpers'; return }
    throw 'Go 1.26+ is needed to build the helpers (winget install GoLang.Go)'
}

New-Item -ItemType Directory -Force $out | Out-Null
foreach ($dir in $helpers) {
    $exe = Join-Path $out "$($dir.Name).exe"
    Push-Location $dir.FullName
    try {
        $env:GOOS = 'windows'; $env:GOARCH = 'amd64'; $env:CGO_ENABLED = '0'
        go build -trimpath -ldflags '-s -w -H windowsgui' -o $exe .
        if ($LASTEXITCODE) { throw "go build failed for $($dir.Name)" }
    } finally { Pop-Location }
    Write-Host "Helper: $exe"
}
