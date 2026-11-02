[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$Version = $Version.TrimStart('v', 'V')
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Set-VersionInFile {
    param([string]$RelativePath, [string]$Pattern, [string]$Description)
    $path = Join-Path $repo $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing $RelativePath" }
    $text = [IO.File]::ReadAllText($path)
    $regex = [regex]::new($Pattern, [Text.RegularExpressions.RegexOptions]::Multiline)
    if (-not $regex.IsMatch($text)) { throw "Could not find $Description in $RelativePath" }
    $updated = $regex.Replace($text, ('${1}' + $Version + '${2}'), 1)
    if ($updated -ne $text) { [IO.File]::WriteAllText($path, $updated, $utf8) }
    Write-Host "  $RelativePath"
}

Write-Host "Setting OnbodyMic version to $Version"
Set-VersionInFile 'Directory.Build.props' '(<Version>)[^<]+(</Version>)' 'Version element'
Set-VersionInFile 'desktop/package.json' '^(\s*"version":\s*")[^"]+(")' 'root version'
Set-VersionInFile 'desktop/package-lock.json' '(^  "version":\s*")[^"]+(")' 'lockfile root version'
Set-VersionInFile 'desktop/package-lock.json' '(^ {6}"version":\s*")[^"]+(")' 'lockfile package version'
Set-VersionInFile 'desktop/src-tauri/Cargo.toml' '^(version\s*=\s*")[^"]+(")' 'package version'
Set-VersionInFile 'desktop/src-tauri/Cargo.lock' '(name = "onbodymic-desktop"\r?\nversion = ")[^"]+(")' 'locked package version'
Set-VersionInFile 'desktop/src-tauri/tauri.conf.json' '^(\s*"version":\s*")[^"]+(")' 'Tauri version'

Write-Host "Done. Update CHANGELOG.md with a '$Version' section before tagging."
