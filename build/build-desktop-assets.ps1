$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build-engine.ps1')
if (-not $?) { throw 'Audio backend build failed.' }
$frontend = Join-Path (Split-Path -Parent $PSScriptRoot) 'desktop'
Push-Location $frontend
try {
    & npm.cmd run build
    if ($LASTEXITCODE -ne 0) { throw "Frontend build failed ($LASTEXITCODE)." }
}
finally { Pop-Location }
