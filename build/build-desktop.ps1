[CmdletBinding()]
param([Alias('Installer')][switch]$Bundle)
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
$updaterKey = Join-Path $repo '.tools\updater\onbodymic.key'
$updaterPassword = Join-Path $repo '.tools\updater\password.txt'
if ($Bundle) {
    if (-not (Test-Path -LiteralPath $updaterKey)) {
        throw "Missing updater signing key: $updaterKey. Run build/update-key.ps1 first."
    }
    $env:TAURI_SIGNING_PRIVATE_KEY = (Get-Content -Raw -LiteralPath $updaterKey).Trim()
    if (Test-Path -LiteralPath $updaterPassword) {
        $env:TAURI_SIGNING_PRIVATE_KEY_PASSWORD = (Get-Content -Raw -LiteralPath $updaterPassword).Trim()
    }
}
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
    if ($Bundle) {
        $bundleDir = Join-Path $release 'bundle\nsis'
        $installers = @(Get-ChildItem -LiteralPath $bundleDir -Filter '*.exe' -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "*$($config.version)*setup*" })
        if ($installers.Count -eq 0) {
            $installers = @(Get-ChildItem -LiteralPath $bundleDir -Filter '*.exe' -File -ErrorAction SilentlyContinue)
        }
        if ($installers.Count -eq 0) { throw "Missing NSIS installer output under $bundleDir" }
        $installer = $installers | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        $installerBase = "OnbodyMic-$($config.version)-desktop-installer-x64"
        $installerName = "$installerBase.exe"
        $installerSuffix = 1
        while (Test-Path -LiteralPath (Join-Path $dist $installerName)) {
            $installerName = "$installerBase-$installerSuffix.exe"
            $installerSuffix++
        }
        $installerTarget = Join-Path $dist $installerName
        Copy-Item -LiteralPath $installer.FullName -Destination $installerTarget
        Write-Host "Native NSIS installer: $installerTarget"

        $signatureFile = "$($installer.FullName).sig"
        if (-not (Test-Path -LiteralPath $signatureFile)) {
            throw "Missing updater signature: $signatureFile. Build with TAURI_SIGNING_PRIVATE_KEY set."
        }
        $signature = (Get-Content -Raw -LiteralPath $signatureFile).Trim()
        $portableArchive = Join-Path $dist "$name.zip"
        $portableHash = (Get-FileHash -LiteralPath $portableArchive -Algorithm SHA256).Hash.ToLowerInvariant()
        $portableSize = (Get-Item -LiteralPath $portableArchive).Length
        $downloadBase = "https://github.com/Benincasamatch/onbody_mic/releases/download/v$($config.version)"
        $changelog = Get-Content -Raw -LiteralPath (Join-Path $repo 'CHANGELOG.md') -Encoding UTF8
        $notesMatch = [regex]::Match($changelog, "(?ms)^## \[$([regex]::Escape($config.version))[^\r\n]*\r?\n(.*?)(?=^## |\z)")
        $notes = if ($notesMatch.Success) { $notesMatch.Groups[1].Value.Trim() } else { '' }
        $manifest = [ordered]@{
            version = $config.version
            notes = $notes
            pub_date = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
            platforms = [ordered]@{
                'windows-x86_64' = [ordered]@{
                    signature = $signature
                    url = "$downloadBase/$installerName"
                }
            }
            portable = [ordered]@{
                url = "$downloadBase/$($name).zip"
                sha256 = $portableHash
                size = $portableSize
            }
        }
        $manifestPath = Join-Path $dist 'latest.json'
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject $manifest -Depth 6), $utf8)
        Write-Host "Updater manifest: $manifestPath"
        Write-Host "Signed portable archive SHA-256: $portableHash"
    }
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
