param(
    [Parameter(Mandatory = $true)][int]$AppPid,
    [Parameter(Mandatory = $true)][string]$Zip,
    [Parameter(Mandatory = $true)][string]$AppDir,
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$ArgsFile = ''
)

$ErrorActionPreference = 'Stop'
$log = Join-Path $env:TEMP 'onbodymic-update.log'
$staging = Join-Path $env:TEMP ('onbodymic-update-' + [guid]::NewGuid().ToString('N'))

function Write-Log([string]$text) {
    try { $text | Set-Content -LiteralPath $log -Encoding UTF8 } catch { }
}

try {
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        if (-not (Get-Process -Id $AppPid -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 500
    }
    if (Get-Process -Id $AppPid -ErrorAction SilentlyContinue) { throw 'Previous process did not exit in time.' }

    Expand-Archive -LiteralPath $Zip -DestinationPath $staging -Force
    $payload = Get-ChildItem -LiteralPath $staging -Recurse -Directory -Filter 'win-x64' | Select-Object -First 1
    if (-not $payload) { throw 'Package does not contain win-x64.' }

    foreach ($name in @('onbodymic-desktop.exe', 'OnbodyMic.EngineHost.exe')) {
        $source = Join-Path $payload.FullName $name
        if (-not (Test-Path -LiteralPath $source)) { throw "Package is missing $name." }
        $target = Join-Path $AppDir $name
        $copied = $false
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            try {
                Copy-Item -LiteralPath $source -Destination $target -Force
                $sourceLength = (Get-Item -LiteralPath $source).Length
                $targetLength = (Get-Item -LiteralPath $target).Length
                if ($sourceLength -eq $targetLength) { $copied = $true; break }
            } catch { }
            Start-Sleep -Milliseconds 400
        }
        if (-not $copied) { throw "Could not replace $name." }
    }
    Write-Log "updated $(Get-Date -Format o)"
} catch {
    Write-Log ("failed: " + ($_ | Out-String))
} finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $Zip -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $Exe) {
        $launch = @{ FilePath = $Exe; WorkingDirectory = $AppDir }
        if ($ArgsFile -and (Test-Path -LiteralPath $ArgsFile)) {
            $restart = (Get-Content -Raw -LiteralPath $ArgsFile).Trim()
            if ($restart) { $launch['ArgumentList'] = $restart }
        }
        Start-Process @launch
        Remove-Item -LiteralPath $ArgsFile -Force -ErrorAction SilentlyContinue
    }
}