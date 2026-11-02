[CmdletBinding()]
param([switch]$Bundle)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$localCargo = Join-Path $repo '.tools\cargo\bin\cargo.exe'
if (Test-Path -LiteralPath $localCargo) {
    $env:CARGO_HOME = Join-Path $repo '.tools\cargo'
    $env:RUSTUP_HOME = Join-Path $repo '.tools\rustup'
    $env:PATH = (Split-Path -Parent $localCargo) + ';' + $env:PATH
} elseif (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw 'Install the Rust MSVC toolchain before building the desktop.'
}
$env:TEMP = Join-Path $repo '.tools\tmp\native-build'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
$frontend = Join-Path $repo 'desktop'
Push-Location $frontend
try {
    & npm.cmd ci --no-fund
    if ($LASTEXITCODE -ne 0) { throw "Frontend dependency restore failed ($LASTEXITCODE)." }
    $arguments = @('run', 'tauri', '--', 'build')
    if (-not $Bundle) { $arguments += '--no-bundle' }
    & npm.cmd @arguments
    if ($LASTEXITCODE -ne 0) { throw "Native desktop build failed ($LASTEXITCODE)." }
}
finally { Pop-Location }

$config = Get-Content -LiteralPath (Join-Path $frontend 'src-tauri\tauri.conf.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$release = Join-Path $frontend 'src-tauri\target\release'
$executables = @('onbodymic-desktop.exe', 'OnbodyMic.EngineHost.exe')
foreach ($executable in $executables) {
    if (-not (Test-Path -LiteralPath (Join-Path $release $executable) -PathType Leaf)) {
        throw "Missing native release executable: $executable"
    }
}
$dist = Join-Path $repo 'dist'
$baseName = "OnbodyMic-$($config.version)-desktop-portable-x64"
$name = $baseName
$suffix = 1
while ((Test-Path -LiteralPath (Join-Path $dist $name)) -or (Test-Path -LiteralPath (Join-Path $dist "$name.zip"))) {
    $name = "$baseName-$suffix"
    $suffix++
}
$stage = Join-Path $dist ('.desktop-build-' + [guid]::NewGuid().ToString('N'))
$package = Join-Path $stage $name
$runtime = Join-Path $package 'win-x64'
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
try {
    foreach ($executable in $executables) {
        Copy-Item -LiteralPath (Join-Path $release $executable) -Destination $runtime
    }
    Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $package
    Copy-Item -LiteralPath (Join-Path $repo 'CHANGELOG.md') -Destination $package
    Copy-Item -LiteralPath (Join-Path $repo 'docs') -Destination $package -Recurse
    $archive = Join-Path $stage "$name.zip"
    Compress-Archive -LiteralPath $package -DestinationPath $archive -CompressionLevel Optimal
    Move-Item -LiteralPath $package -Destination (Join-Path $dist $name)
    Move-Item -LiteralPath $archive -Destination (Join-Path $dist "$name.zip")
    Write-Host "Native portable directory: $(Join-Path $dist $name)"
    Write-Host "Native portable archive: $(Join-Path $dist "$name.zip")"
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
