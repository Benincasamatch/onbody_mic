[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidatePattern('^v?\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [switch]$Push,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$Version = $Version.TrimStart('v', 'V')
$tag = "v$Version"
Push-Location $repo
try {
    $dirty = @(git status --porcelain)
    if ($dirty.Count -gt 0) {
        throw "Working tree is not clean. Commit or discard these paths first:`n$($dirty -join "`n")"
    }

    $props = [xml](Get-Content -LiteralPath 'Directory.Build.props' -Raw -Encoding UTF8)
    $propVersion = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
    $package = Get-Content -LiteralPath 'desktop\package.json' -Raw -Encoding UTF8 | ConvertFrom-Json
    $tauri = Get-Content -LiteralPath 'desktop\src-tauri\tauri.conf.json' -Raw -Encoding UTF8 | ConvertFrom-Json
    $cargo = [regex]::Match((Get-Content -LiteralPath 'desktop\src-tauri\Cargo.toml' -Raw -Encoding UTF8), '(?m)^version\s*=\s*"([^"]+)"').Groups[1].Value
    foreach ($pair in @(
        @('Directory.Build.props', $propVersion),
        @('desktop/package.json', $package.version),
        @('desktop/src-tauri/Cargo.toml', $cargo),
        @('desktop/src-tauri/tauri.conf.json', $tauri.version)
    )) {
        if ($pair[1] -ne $Version) { throw "Version mismatch: $($pair[0]) is $($pair[1]), expected $Version. Run build/set-version.ps1 first." }
    }

    $changelog = Get-Content -LiteralPath 'CHANGELOG.md' -Raw -Encoding UTF8
    if ($changelog -notmatch "(?m)^## \[$([regex]::Escape($Version))\]") {
        throw "CHANGELOG.md has no '## [$Version]' section."
    }

    $existing = @(git tag --list $tag)
    if ($existing.Count -gt 0) { throw "Tag $tag already exists locally." }
    if ($Push) {
        $remote = @(git ls-remote --tags origin "refs/tags/$tag")
        if ($remote.Count -gt 0) { throw "Tag $tag already exists on origin." }
    }

    if ($DryRun) {
        Write-Host "Dry run passed: versions, CHANGELOG and tag availability are valid for $tag."
        return
    }

    git tag -a $tag -m "随声麦 $tag"
    if ($LASTEXITCODE -ne 0) { throw "git tag failed ($LASTEXITCODE)." }
    Write-Host "Created annotated tag $tag"

    if ($Push) {
        git push origin $tag
        if ($LASTEXITCODE -ne 0) { throw "git push failed ($LASTEXITCODE)." }
        Write-Host "Pushed $tag to origin"
    } else {
        Write-Host "Tag kept local. Publish with: git push origin $tag"
    }
}
finally {
    Pop-Location
}
