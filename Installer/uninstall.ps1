<#
    Puts The Coin Game back the way it was.

    Removes MelonLoader and the mod. Your save game is stored by the game
    itself, not by the mod, so it is not touched.
#>

param([string]$GamePath = "", [switch]$NoPause)

$ErrorActionPreference = "Stop"
$AppId = "598980"

$ScriptPath = $MyInvocation.MyCommand.Path

function Say  ($m) { Write-Host $m }
function Good ($m) { Write-Host "  [ok]   $m" -ForegroundColor Green }
function Warn ($m) { Write-Host "  [note] $m" -ForegroundColor Yellow }

function Fail ($m) {
    Write-Host "  [STOP] $m" -ForegroundColor Red
    if (-not $NoPause) { Say ""; Read-Host "Press Enter to close" }
    exit 1
}

Say ""
Say "  The Coin Game - Multiplayer : uninstall"
Say "  ---------------------------------------------"
Say ""

if (Get-Process -Name "TheCoinGame" -ErrorAction SilentlyContinue) {
    Fail "The Coin Game is running. Close it completely, then run this again."
}

function Find-Game {
    $roots = @()
    foreach ($k in @("HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam")) {
        try {
            $p = Get-ItemProperty -Path $k -ErrorAction Stop
            foreach ($v in @($p.SteamPath, $p.InstallPath)) { if ($v -and (Test-Path $v)) { $roots += $v } }
        } catch { }
    }
    $roots += "C:\Program Files (x86)\Steam"

    $libs = @()
    foreach ($r in ($roots | Where-Object { Test-Path $_ } | Select-Object -Unique)) {
        $libs += $r
        $vdf = Join-Path $r "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $libs += ($m.Groups[1].Value -replace '\\\\', '\')
            }
        }
    }
    foreach ($lib in ($libs | Where-Object { Test-Path $_ } | Select-Object -Unique)) {
        $acf = Join-Path $lib "steamapps\appmanifest_$AppId.acf"
        if (Test-Path $acf) {
            $m = [regex]::Match((Get-Content $acf -Raw), '"installdir"\s+"([^"]+)"')
            if ($m.Success) {
                $g = Join-Path $lib ("steamapps\common\" + $m.Groups[1].Value)
                if (Test-Path (Join-Path $g "TheCoinGame.exe")) { return $g }
            }
        }
        $common = Join-Path $lib "steamapps\common"
        if (Test-Path $common) {
            $hit = Get-ChildItem $common -Directory -ErrorAction SilentlyContinue |
                   Where-Object { Test-Path (Join-Path $_.FullName "TheCoinGame.exe") } | Select-Object -First 1
            if ($hit) { return $hit.FullName }
        }
    }
    return $null
}

$game = if ($GamePath) { $GamePath } else { Find-Game }
if (-not $game -or -not (Test-Path (Join-Path $game "TheCoinGame.exe"))) {
    $game = (Read-Host "  Couldn't find the game. Paste its folder path").Trim('"').Trim()
    if (-not (Test-Path (Join-Path $game "TheCoinGame.exe"))) { Fail "No TheCoinGame.exe in that folder." }
}
$game = (Resolve-Path $game).Path
Say "  Game folder: $game"
Say ""

# elevate if we need to
$me = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$probe = Join-Path $game ".tcgmp_write_test"
try { New-Item -Path $probe -ItemType File -Force | Out-Null; Remove-Item $probe -Force }
catch {
    if ($me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Fail "Can't write to the game folder." }
    Warn "Needs administrator. Windows will ask you to confirm."
    Start-Sleep -Seconds 2
    Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$ScriptPath`" -GamePath `"$game`""
    exit 0
}

$removed = 0
foreach ($t in @("version.dll", "MelonLoader", "Mods\TcgMultiplayer.dll", "Mods\TcgFsmDump.dll", "TcgFsmDump")) {
    $p = Join-Path $game $t
    if (Test-Path $p) {
        Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path $p)) { Good "removed $t"; $removed++ } else { Warn "could not remove $t" }
    }
}

Say ""
if ($removed -eq 0) {
    Say "  Nothing to remove - the game was already clean."
} else {
    Say "  Done. The game is back to normal."
    Say "  If anything still misbehaves, Steam > right-click the game >"
    Say "  Properties > Installed Files > Verify integrity."
}
Say ""
Say "  Your save is untouched. The Mods and UserData folders are left in place"
Say "  in case you have other mods there."
Say ""

if (-not $NoPause) { Read-Host "  Press Enter to close" }
