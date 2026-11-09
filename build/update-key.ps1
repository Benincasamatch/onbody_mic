[CmdletBinding()]
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$directory = Join-Path $repo '.tools\updater'
$key = Join-Path $directory 'onbodymic.key'
$passwordFile = Join-Path $directory 'password.txt'
$config = Join-Path $repo 'desktop\src-tauri\tauri.conf.json'
New-Item -ItemType Directory -Force -Path $directory | Out-Null

if ((Test-Path -LiteralPath $key) -and -not $Force) {
    throw "Updater key already exists: $key. Pass -Force only if you accept that older releases can no longer be verified."
}
if (-not (Test-Path -LiteralPath $passwordFile)) {
    [System.IO.File]::WriteAllText($passwordFile, [guid]::NewGuid().ToString('N'), (New-Object System.Text.UTF8Encoding($false)))
}
$password = (Get-Content -Raw -LiteralPath $passwordFile).Trim()

$cli = Join-Path $repo 'desktop\node_modules\@tauri-apps\cli\tauri.js'
if (-not (Test-Path -LiteralPath $cli)) { throw "Install frontend dependencies first (npm --prefix desktop ci)." }
$output = & node $cli signer generate -w $key -p $password --ci 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw $output }
Write-Host $output

$publicKey = (Get-Content -Raw -LiteralPath "$key.pub").Trim()
$settings = Get-Content -Raw -LiteralPath $config -Encoding UTF8 | ConvertFrom-Json
$settings.plugins.updater.pubkey = $publicKey
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($config, (ConvertTo-Json -InputObject $settings -Depth 10), $utf8)
Write-Host "Public key written to $config"
Write-Host "Private key: $key (kept out of git; back it up, updates cannot be signed without it)"
Write-Host "Password:   $passwordFile"