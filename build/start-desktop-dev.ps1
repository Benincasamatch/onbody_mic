$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build-engine.ps1')
$frontend = Join-Path (Split-Path -Parent $PSScriptRoot) 'desktop'
Push-Location $frontend
try {
    & npm.cmd run dev
    if ($LASTEXITCODE -ne 0) { throw "Frontend dev server failed ($LASTEXITCODE)." }
}
finally { Pop-Location }
