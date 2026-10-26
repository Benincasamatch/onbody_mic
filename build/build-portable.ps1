# Portable package build (docs/03 T33).
#
# Reproducible: running this script is the only manual step. Produces
# dist\win-x64\ and dist\OnbodyMic-<version>-portable-x64.zip with the user
# documents inside the package.
#
# Written in PowerShell rather than batch on purpose: the package ships Chinese
# file names, and cmd.exe mangles them depending on the active code page.
#
# Deliberately not part of the normal build: a self-contained publish is ~120 MB
# of output and must not slow down `dotnet build` / `dotnet test`.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    $dev = Join-Path $repo '.tools\dev.cmd'
    if (-not (Test-Path $dev)) { throw "missing $dev (local SDK wrapper)" }

    $props = [xml](Get-Content -Raw 'Directory.Build.props')
    $version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
    if (-not $version) { throw 'could not read Version from Directory.Build.props' }
    Write-Host "Building portable package for version $version"

    $out = Join-Path $repo 'dist\win-x64'
    if (Test-Path $out) {
        # a running instance from a previous selftest would lock the DLLs
        Get-Process OnbodyMic.Desktop, OnbodyMic.Cli -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -like "$out*" } |
            ForEach-Object { Write-Host "stopping leftover $($_.Id) ($($_.Path))"; Stop-Process -Id $_.Id -Force }
        Start-Sleep -Milliseconds 500
        Remove-Item $out -Recurse -Force
    }

    # 1. self-contained publish without debug symbols: PDBs roughly double the
    #    download and are useless to a user running the portable build
    & cmd.exe /c "`"$dev`" publish src\OnbodyMic.Desktop\OnbodyMic.Desktop.csproj -c Release -r win-x64 --self-contained true -p:DebugType=none -p:DebugSymbols=false -p:PublishSingleFile=false -o `"$out`" --nologo -v q"
    if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }

    # 2. ship what a user needs without the repository
    Copy-Item 'docs\05-usage-and-precautions.md' (Join-Path $out '使用说明.md') -Force
    Copy-Item 'docs\04-sources-and-decisions.md' (Join-Path $out '依赖与许可证.md') -Force
    Copy-Item 'CHANGELOG.md' (Join-Path $out '变更记录.md') -Force
    Copy-Item 'build\开始使用.txt' (Join-Path $out '开始使用.txt') -Force

    # 3. verify the package itself, not just the build output: --selftest covers
    #    shell construction plus the multi-DPI layout audit
    $testRoot = Join-Path $repo 'dist\_selftest'
    if (Test-Path $testRoot) { Remove-Item $testRoot -Recurse -Force }
    $exe = Join-Path $out 'OnbodyMic.Desktop.exe'
    $log = Join-Path $env:TEMP 'onbody-package-selftest.txt'
    & $exe --selftest --root $testRoot *> $log
    $code = $LASTEXITCODE
    $text = Get-Content -Raw $log
    if (Test-Path $testRoot) { Remove-Item $testRoot -Recurse -Force }
    if ($code -ne 0 -or $text -notmatch 'selftest: OK') {
        Write-Host $text
        throw "package selftest failed (exit $code)"
    }

    Write-Host 'Package selftest OK'

    # 4. zip containing the win-x64 folder, so extraction cannot scatter files
    $zip = Join-Path $repo "dist\OnbodyMic-$version-portable-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $out, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)

    $sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host ''
    Write-Host "Portable package ready ($sizeMb MB):"
    Write-Host "  $out"
    Write-Host "  $zip"
}
finally {
    Pop-Location
}
