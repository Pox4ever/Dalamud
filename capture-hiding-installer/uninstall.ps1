# Restores the files backed up by install.ps1. Run through uninstall.cmd.
$ErrorActionPreference = 'Stop'

$DalamudVersion = '15.0.4.1'
$xl = Join-Path $env:APPDATA 'XIVLauncher'
$hooks = Join-Path $xl "addon\Hooks\$DalamudVersion"
$backup = Join-Path $hooks 'capture-hiding-backup'

if (Get-Process -Name 'ffxiv_dx11' -ErrorAction SilentlyContinue) {
    Write-Host "FFXIV is running. Close the game and run this again." -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $backup)) {
    Write-Host "No backup found at $backup. Nothing to restore." -ForegroundColor Red
    exit 1
}

foreach ($f in 'Dalamud.dll', 'Dalamud.pdb', 'hashes.json') {
    Copy-Item (Join-Path $backup $f) $hooks -Force
}

Remove-Item $backup -Recurse -Force
Write-Host "Restored the original Dalamud files." -ForegroundColor Green
Write-Host "Your Dalamud settings were left as they are; the extra option is simply ignored by the official build."
