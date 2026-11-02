[CmdletBinding()]
param([string]$DotnetPath)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $DotnetPath) {
    $localSdk = Join-Path $repo '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localSdk) { $DotnetPath = $localSdk }
    else {
        $sdk = Get-Command dotnet -ErrorAction SilentlyContinue
        if (-not $sdk) { throw 'Install .NET 10 SDK or provide -DotnetPath.' }
        $DotnetPath = $sdk.Source
    }
}
$env:DOTNET_ROOT = Split-Path -Parent $DotnetPath
$env:DOTNET_CLI_HOME = Join-Path $repo '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repo '.tools\nuget-packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $repo '.tools\nuget-http-cache'
$env:TEMP = Join-Path $repo '.tools\tmp\msbuild-tmp'
$env:TMP = $env:TEMP
$env:APPDATA = Join-Path $repo '.tools\tmp\appdata-roaming'
foreach ($directory in @($env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:TEMP, $env:APPDATA)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}
$stage = Join-Path $repo ('.tools\engine-publish-' + [guid]::NewGuid().ToString('N'))
$destination = Join-Path $repo 'desktop\src-tauri\binaries'
New-Item -ItemType Directory -Force -Path $stage, $destination | Out-Null
& $DotnetPath publish (Join-Path $repo 'src\OnbodyMic.EngineHost\OnbodyMic.EngineHost.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false -p:RestoreConfigFile="$repo\NuGet.config" -o $stage --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "Engine publish failed ($LASTEXITCODE); evidence retained at $stage" }
$source = Join-Path $stage 'OnbodyMic.EngineHost.exe'
if (-not (Test-Path -LiteralPath $source)) { throw "Missing engine executable: $source" }
$target = Join-Path $destination 'OnbodyMic.EngineHost-x86_64-pc-windows-msvc.exe'
Copy-Item -LiteralPath $source -Destination $target -Force
Write-Host "Published real audio backend: $target"
Remove-Item -LiteralPath $stage -Recurse -Force
