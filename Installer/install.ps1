<#
    The Coin Game - Multiplayer  :  installer

    Finds the game through Steam, installs MelonLoader 0.6.6 (the exact build
    the mod was written against), drops the mod in, and prints a build
    fingerprint that both players should compare before they connect.

    Safe to run more than once. Nothing outside the game folder is touched.
#>

param(
    [string]$GamePath = "",       # skip detection, use this folder
    [switch]$NoPause              # for scripted runs
)

$ErrorActionPreference = "Stop"
$ProgressPreference    = "SilentlyContinue"   # makes the download ~10x faster

$AppId          = "598980"
$MlVersion      = "0.6.6"
$MlUrl          = "https://github.com/LavaGang/MelonLoader/releases/download/v0.6.6/MelonLoader.x64.zip"
$MlZipSha256    = "687B82605606E941CEFDC007B880B720922CC319BB70270064590D4038C3C0DB"
$MlVersionDll   = "595DA98AE1C59B2D5DA8820A5398E0CB10940C21EAB01C89C07989FB063F1BFC"
$ModVersion     = "0.9.5"

$ScriptPath = $MyInvocation.MyCommand.Path
$Here       = Split-Path -Parent $ScriptPath

function Say  ($m) { Write-Host $m }
function Good ($m) { Write-Host "  [ok]   $m"   -ForegroundColor Green }
function Warn ($m) { Write-Host "  [note] $m"   -ForegroundColor Yellow }
function Bad  ($m) { Write-Host "  [STOP] $m"   -ForegroundColor Red }

function Fail ($m) {
    Bad $m
    Say ""
    Say "Nothing was changed. If you're stuck, send Mason this whole window."
    if (-not $NoPause) { Say ""; Read-Host "Press Enter to close" }
    exit 1
}

Say ""
Say "  The Coin Game - Multiplayer"
Say "  mod v$ModVersion  +  MelonLoader $MlVersion"
Say "  ---------------------------------------------"
Say ""

# ---------------------------------------------------------------- admin check
$me = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin = $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

# ---------------------------------------------------------------- is it running
if (Get-Process -Name "TheCoinGame" -ErrorAction SilentlyContinue) {
    Fail "The Coin Game is running. Close it completely, then run this again."
}

# ---------------------------------------------------------------- find the game
function Get-SteamRoots {
    $roots = @()
    foreach ($k in @("HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam")) {
        try {
            $p = (Get-ItemProperty -Path $k -ErrorAction Stop)
            foreach ($v in @($p.SteamPath, $p.InstallPath)) {
                if ($v -and (Test-Path $v)) { $roots += (Resolve-Path $v).Path }
            }
        } catch { }
    }
    $roots += "C:\Program Files (x86)\Steam"
    $roots += "C:\Steam"
    $roots | Where-Object { Test-Path $_ } | Select-Object -Unique
}

function Get-Libraries {
    $libs = @()
    foreach ($root in (Get-SteamRoots)) {
        $libs += $root
        $vdf = Join-Path $root "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            $text = Get-Content $vdf -Raw
            foreach ($m in [regex]::Matches($text, '"path"\s+"([^"]+)"')) {
                $libs += ($m.Groups[1].Value -replace '\\\\', '\')
            }
        }
    }
    $libs | Where-Object { Test-Path $_ } | Select-Object -Unique
}

function Find-Game {
    foreach ($lib in (Get-Libraries)) {
        # the reliable route: Steam's own manifest tells us the folder name
        $acf = Join-Path $lib "steamapps\appmanifest_$AppId.acf"
        if (Test-Path $acf) {
            $m = [regex]::Match((Get-Content $acf -Raw), '"installdir"\s+"([^"]+)"')
            if ($m.Success) {
                $g = Join-Path $lib ("steamapps\common\" + $m.Groups[1].Value)
                if (Test-Path (Join-Path $g "TheCoinGame.exe")) { return $g }
            }
        }
        # fallback: the manifest is missing or odd, so look for the exe
        $common = Join-Path $lib "steamapps\common"
        if (Test-Path $common) {
            $hit = Get-ChildItem $common -Directory -ErrorAction SilentlyContinue |
                   Where-Object { Test-Path (Join-Path $_.FullName "TheCoinGame.exe") } |
                   Select-Object -First 1
            if ($hit) { return $hit.FullName }
        }
    }
    return $null
}

if ($GamePath) {
    $game = $GamePath
} else {
    Say "  Looking for The Coin Game..."
    $game = Find-Game
}

if (-not $game -or -not (Test-Path (Join-Path $game "TheCoinGame.exe"))) {
    Warn "Couldn't find it automatically."
    Say ""
    Say "  Open Steam, right-click The Coin Game, Manage > Browse local files,"
    Say "  then copy the folder path from the address bar and paste it here."
    Say ""
    $game = (Read-Host "  Game folder").Trim('"').Trim()
    if (-not (Test-Path (Join-Path $game "TheCoinGame.exe"))) {
        Fail "No TheCoinGame.exe in that folder."
    }
}

$game = (Resolve-Path $game).Path
Good "Game folder: $game"

$asm = Join-Path $game "TheCoinGame_Data\Managed\Assembly-CSharp.dll"
if (-not (Test-Path $asm)) {
    Fail "That folder is missing TheCoinGame_Data\Managed\Assembly-CSharp.dll, so it isn't a normal install."
}

# ---------------------------------------------------------------- can we write
$probe = Join-Path $game ".tcgmp_write_test"
$canWrite = $true
try { New-Item -Path $probe -ItemType File -Force | Out-Null; Remove-Item $probe -Force }
catch { $canWrite = $false }

if (-not $canWrite) {
    if ($isAdmin) { Fail "Can't write to the game folder even as administrator. Is it read-only, or is antivirus blocking it?" }
    Warn "The game is in a protected folder, so this needs to run as administrator."
    Say  "  Windows will ask you to confirm - click Yes."
    Say ""
    Start-Sleep -Seconds 2
    $argLine = "-NoProfile -ExecutionPolicy Bypass -File `"$ScriptPath`" -GamePath `"$game`""
    Start-Process powershell -Verb RunAs -ArgumentList $argLine
    exit 0
}

# ---------------------------------------------------------------- other loaders
# The other Coin Game mods on Nexus use BepInEx, which hooks the game the same
# way MelonLoader does. Two loaders both proxying the game's startup is a known
# way to get a game that launches with no mods, or doesn't launch at all - and
# the symptom gives no hint about the cause. Better to say so up front.
$bepinex = @("winhttp.dll", "BepInEx") | Where-Object { Test-Path (Join-Path $game $_) }
if ($bepinex) {
    Warn "BepInEx is also installed here ($($bepinex -join ', '))."
    Say  "  This mod uses MelonLoader. Two mod loaders in one game folder often"
    Say  "  fight, and when they do the game usually just starts with no mods at all."
    Say  ""
    Say  "  If the game misbehaves after this, move the BepInEx folder and winhttp.dll"
    Say  "  out of the game folder, then launch again. Installing anyway."
    Say  ""
}

# ---------------------------------------------------------------- MelonLoader
function Sha ($path) { (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToUpper() }

$versionDll = Join-Path $game "version.dll"
$needsMl    = $true

if (Test-Path $versionDll) {
    if ((Sha $versionDll) -eq $MlVersionDll) {
        Good "MelonLoader $MlVersion is already installed."
        $needsMl = $false
    } else {
        Warn "A different MelonLoader is installed - replacing it with $MlVersion."
        Say  "  (Mods for other games in this folder aren't affected; this is per-game.)"
    }
}

if ($needsMl) {
    $zip = Join-Path $Here "MelonLoader.x64.zip"

    if (Test-Path $zip) {
        Say "  Using the bundled MelonLoader.x64.zip."
    } else {
        Say "  Downloading MelonLoader $MlVersion (about 21 MB)..."
        $zip = Join-Path $env:TEMP "MelonLoader.x64.zip"
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -Uri $MlUrl -OutFile $zip -UseBasicParsing
        } catch {
            Fail "Download failed: $($_.Exception.Message)`n         Ask Mason for MelonLoader.x64.zip and put it next to this installer."
        }
    }

    $got = Sha $zip
    if ($got -ne $MlZipSha256) {
        Fail "MelonLoader.x64.zip doesn't match the expected file.`n         expected $MlZipSha256`n         got      $got"
    }
    Good "MelonLoader download verified."

    $tmp = Join-Path $env:TEMP "tcgmp_ml"
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $tmp -Force

    Copy-Item (Join-Path $tmp "version.dll") $versionDll -Force
    $rc = Start-Process robocopy -ArgumentList "`"$tmp\MelonLoader`" `"$game\MelonLoader`" /E /NFL /NDL /NJH /NJS /NP /NC /NS" -Wait -PassThru -WindowStyle Hidden
    if ($rc.ExitCode -ge 8) { Fail "Copying MelonLoader into the game folder failed (robocopy $($rc.ExitCode))." }
    Remove-Item $tmp -Recurse -Force

    Good "MelonLoader $MlVersion installed."
}

# ---------------------------------------------------------------- the mod
$modsSrc = Join-Path $Here "mods"
if (-not (Test-Path $modsSrc)) { Fail "The 'mods' folder is missing next to this installer. Re-extract the zip and keep the files together." }

$modsDst = Join-Path $game "Mods"
New-Item -Path $modsDst -ItemType Directory -Force | Out-Null
New-Item -Path (Join-Path $game "UserData") -ItemType Directory -Force | Out-Null

$installed = @()
$hasDumper = $false
foreach ($dll in (Get-ChildItem $modsSrc -Filter *.dll)) {
    Copy-Item $dll.FullName (Join-Path $modsDst $dll.Name) -Force
    if ($dll.Name -eq "TcgFsmDump.dll") { $hasDumper = $true }
    $installed += "{0}  ({1})" -f $dll.Name, (Sha (Join-Path $modsDst $dll.Name)).Substring(0,12).ToLower()
}
if ($installed.Count -eq 0) { Fail "No .dll files in the 'mods' folder." }
Good "Mod installed:"
foreach ($i in $installed) { Say "           $i" }

# ---------------------------------------------------------------- fingerprint
# Same FNV-1a the mod computes, so the number here is the number the game shows.
$fingerprint = "unavailable"
try {
    Add-Type -TypeDefinition @"
public static class TcgFnv {
    public static string Of(string path) {
        byte[] b = System.IO.File.ReadAllBytes(path);
        ulong h = 14695981039346656037UL;
        unchecked { for (int i = 0; i < b.Length; i++) { h ^= b[i]; h *= 1099511628211UL; } }
        return h.ToString("x16");
    }
}
"@ -ErrorAction Stop
    $fingerprint = [TcgFnv]::Of($asm)
} catch { }

$buildId = "unknown"
try {
    $acf = Join-Path (Split-Path (Split-Path $game -Parent) -Parent) "appmanifest_$AppId.acf"
    if (Test-Path $acf) {
        $m = [regex]::Match((Get-Content $acf -Raw), '"buildid"\s+"([^"]+)"')
        if ($m.Success) { $buildId = $m.Groups[1].Value }
    }
} catch { }

Say ""
Say "  ---------------------------------------------"
Say "  Done. Launch the game normally from Steam."
Say ""
Say "  Game build fingerprint : $fingerprint"
Say "  Steam build id         : $buildId"
Say ""
Say "  Send those two lines to the person you're playing with."
Say "  If they don't match, one of you has a different version of the"
Say "  game and the mod will warn you when you connect."
Say ""
Say "  In game:"
Say "    F9   open the multiplayer panel (host, join, chat, health)"
Say "    F11  get unstuck - releases every machine you're holding"
Say "    F10  run the self-check and show the result"
if ($hasDumper) {
Say "    F7   dump scene info, only if a bug report asks for it"
}
Say ""
Say "  Your save is copied aside the first time you host or join,"
Say "  into a TcgMultiplayer_SaveBackups folder next to the save itself."
Say ""
Say "  First launch takes longer than usual while MelonLoader warms up."
Say "  A black console window alongside the game is normal."
Say ""

if (-not $NoPause) { Read-Host "  Press Enter to close" }
