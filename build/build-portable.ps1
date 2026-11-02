# Build into a unique staging folder; never replace a running or older package.
[CmdletBinding()]
param([string]$DotnetPath)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    if (-not $DotnetPath) {
        $localSdk = Join-Path $repo '.tools\dotnet\dotnet.exe'
        if (Test-Path -LiteralPath $localSdk) { $DotnetPath = $localSdk }
        else {
            $installedSdk = Get-Command dotnet -ErrorAction SilentlyContinue
            if (-not $installedSdk) { throw 'Install .NET 10 SDK or provide -DotnetPath.' }
            $DotnetPath = $installedSdk.Source
        }
    }
    if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) { throw "SDK not found: $DotnetPath" }

    $props = [xml](Get-Content -Raw 'Directory.Build.props')
    $version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
    if (-not $version) { throw 'Could not read Version from Directory.Build.props.' }
    $suffix = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $dist = Join-Path $repo 'dist'
    $stage = Join-Path $dist ".build-$suffix"
    $out = Join-Path $stage 'win-x64'
    New-Item -ItemType Directory -Path $out -Force | Out-Null
    Write-Host "Publishing version $version into $stage"

    & $DotnetPath publish 'src\OnbodyMic.Desktop\OnbodyMic.Desktop.csproj' -c Release -r win-x64 --self-contained true -p:DebugType=none -p:DebugSymbols=false -p:PublishSingleFile=false -o $out --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE). Evidence retained: $stage" }

    # Keep relative documentation links usable in the extracted package.
    Copy-Item -LiteralPath 'README.md', 'CHANGELOG.md' -Destination $out
    $docOut = Join-Path $out 'docs'
    New-Item -ItemType Directory -Path $docOut | Out-Null
    Get-ChildItem -LiteralPath 'docs' -Filter '*.md' -File | Copy-Item -Destination $docOut
    Copy-Item -LiteralPath 'build\开始使用.txt' -Destination $out

    $testRoot = Join-Path $stage 'selftest'
    $log = Join-Path $stage 'selftest.log'
    $errorLog = Join-Path $stage 'selftest-errors.log'
    $exe = Join-Path $out 'OnbodyMic.Desktop.exe'
    $testProcess = Start-Process -FilePath $exe -ArgumentList @('--selftest', '--root', ('"' + $testRoot + '"')) -PassThru -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError $errorLog
    # Accessing Handle before waiting preserves ExitCode in Windows PowerShell 5.
    $null = $testProcess.Handle
    if (-not $testProcess.WaitForExit(120000)) {
        $testProcess.Kill()
        throw "Package selftest timed out. Evidence retained: $stage"
    }
    $testProcess.WaitForExit()
    $code = $testProcess.ExitCode
    $testText = Get-Content -LiteralPath $log -Raw
    if ($null -eq $code -or $code -ne 0 -or $testText -notmatch 'selftest: OK') {
        throw "Package selftest failed (exit $code). Evidence retained: $stage"
    }
    Write-Host 'Package selftest OK'

    $name = "OnbodyMic-$version-portable-x64"
    if ((Test-Path -LiteralPath (Join-Path $dist $name)) -or (Test-Path -LiteralPath (Join-Path $dist "$name.zip"))) { $name += "-$suffix" }
    $stagedZip = Join-Path $stage 'package.zip'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($out, $stagedZip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    $zip = Join-Path $dist "$name.zip"
    $package = Join-Path $dist $name
    $distPrefix = [System.IO.Path]::GetFullPath($dist).TrimEnd('\') + '\'
    foreach ($pathToCheck in @($stage, $stagedZip, $zip, $package)) {
        if (-not [System.IO.Path]::GetFullPath($pathToCheck).StartsWith($distPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to move a path outside dist: $pathToCheck"
        }
    }
    Move-Item -LiteralPath $stagedZip -Destination $zip
    Move-Item -LiteralPath $stage -Destination $package
    $sizeMb = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)
    Write-Host "Portable package ready ($sizeMb MB):"
    Write-Host "  $(Join-Path $package 'win-x64')"
    Write-Host "  $zip"
    Write-Host "SHA256: $((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash)"
    Write-Host "Selftest logs and screenshots: $package"
}
finally { Pop-Location }
