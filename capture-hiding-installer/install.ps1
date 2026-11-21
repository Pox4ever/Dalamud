# Installs the patched Dalamud.dll ("Hide Dalamud from OBS Game Capture") into XIVLauncher's Dalamud folder,
# so launching the game from XIVLauncher loads it. Run through install.cmd.
$ErrorActionPreference = 'Stop'

$DalamudVersion = '15.0.4.1'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$xl = Join-Path $env:APPDATA 'XIVLauncher'
$hooks = Join-Path $xl "addon\Hooks\$DalamudVersion"
$backup = Join-Path $hooks 'capture-hiding-backup'
$config = Join-Path $xl 'dalamudConfig.json'

function Stop-WithMessage($msg) {
    Write-Host ""
    Write-Host $msg -ForegroundColor Red
    exit 1
}

Write-Host "Installing Dalamud $DalamudVersion with 'Hide Dalamud from OBS Game Capture'" -ForegroundColor Cyan

foreach ($f in 'Dalamud.dll', 'Dalamud.pdb') {
    if (-not (Test-Path (Join-Path $here $f))) { Stop-WithMessage "$f is missing next to this script. Extract the whole zip first." }
}

if (Get-Process -Name 'ffxiv_dx11' -ErrorAction SilentlyContinue) {
    Stop-WithMessage "FFXIV is running. Close the game (and XIVLauncher) and run this again."
}

if (-not (Test-Path (Join-Path $hooks 'Dalamud.dll'))) {
    Stop-WithMessage "Dalamud $DalamudVersion is not installed at:`n  $hooks`nStart the game once through XIVLauncher so it downloads Dalamud. If Dalamud has updated past $DalamudVersion, this build no longer matches; ask for a new one."
}

$versionJson = Join-Path $hooks 'version.json'
if (Test-Path $versionJson) {
    $installed = (Get-Content $versionJson -Raw | ConvertFrom-Json).AssemblyVersion
    if ($installed -ne $DalamudVersion) { Stop-WithMessage "Installed Dalamud is $installed, but this build is for $DalamudVersion." }
}

# Back up the current files once, so uninstall.cmd can put them back.
if (-not (Test-Path $backup)) {
    New-Item -ItemType Directory -Path $backup | Out-Null
    Copy-Item (Join-Path $hooks 'Dalamud.dll'), (Join-Path $hooks 'Dalamud.pdb'), (Join-Path $hooks 'hashes.json') $backup
    if (Test-Path $config) { Copy-Item $config $backup }
    Write-Host "Backed up the current files to $backup"
} else {
    Write-Host "Backup already exists at $backup (keeping the original one)"
}

# Copy the patched files.
Copy-Item (Join-Path $here 'Dalamud.dll'), (Join-Path $here 'Dalamud.pdb') $hooks -Force

# XIVLauncher checks every file against hashes.json and re-downloads mismatches, so record the new hash.
$hashesPath = Join-Path $hooks 'hashes.json'
$md5 = (Get-FileHash (Join-Path $hooks 'Dalamud.dll') -Algorithm MD5).Hash
$hashes = [IO.File]::ReadAllText($hashesPath)
$newHashes = [regex]::Replace($hashes, '"Dalamud\.dll"\s*:\s*"[0-9A-Fa-f]+"', ('"Dalamud.dll": "' + $md5 + '"'))
if ($newHashes -eq $hashes -and $hashes -notmatch [regex]::Escape($md5)) {
    Stop-WithMessage "Could not find Dalamud.dll in hashes.json; XIVLauncher would replace the patched file. Run uninstall.cmd."
}
[IO.File]::WriteAllText($hashesPath, $newHashes)
Write-Host "Installed patched Dalamud.dll (MD5 $md5)"

# Turn the option on in Dalamud's settings.
if (Test-Path $config) {
    $text = [IO.File]::ReadAllText($config)
    if ($text -match '"HideDalamudFromGameCapture"\s*:\s*(true|false)') {
        $text = [regex]::Replace($text, '"HideDalamudFromGameCapture"\s*:\s*(true|false)', '"HideDalamudFromGameCapture": true')
    } elseif ($text -match '"SwapChainHookMode"\s*:\s*\d+\s*,') {
        $text = [regex]::Replace($text, '("SwapChainHookMode"\s*:\s*\d+\s*,)', "`$1`r`n  `"HideDalamudFromGameCapture`": true,")
    } else {
        $text = $null
    }

    if ($text) {
        [IO.File]::WriteAllText($config, $text)
        Write-Host "Turned on 'Hide Dalamud from OBS Game Capture'"
    } else {
        Write-Host "Could not turn the option on automatically. In game: Dalamud Settings > Experimental > tick 'Hide Dalamud from OBS Game Capture' > Save." -ForegroundColor Yellow
    }
} else {
    Write-Host "No dalamudConfig.json yet. In game: Dalamud Settings > Experimental > tick 'Hide Dalamud from OBS Game Capture' > Save." -ForegroundColor Yellow
}

$launcherConfig = Join-Path $xl 'launcherConfigV3.json'
if (Test-Path $launcherConfig) {
    $m = Select-String -Path $launcherConfig -Pattern '"InGameAddonLoadMethod"\s*:\s*"(\w+)"'
    if ($m -and $m.Matches[0].Groups[1].Value -ne 'EntryPoint') {
        Write-Host "XIVLauncher's Dalamud load method is '$($m.Matches[0].Groups[1].Value)'. 'EntryPoint' is the most reliable for this." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Done. Start the game from XIVLauncher as usual." -ForegroundColor Green
Write-Host "In OBS use Game Capture with 'Capture third-party overlays' OFF to hide Dalamud, ON to show it."
