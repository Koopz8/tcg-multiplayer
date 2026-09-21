# Builds the two release zips.
#
#   TheCoinGameMultiplayer-<ver>-manual.zip     -> Nexus. DLL only, no scripts.
#   TheCoinGameMultiplayer-<ver>-installer.zip  -> Discord. The .bat installer.
#
# Run it from anywhere:  powershell -ExecutionPolicy Bypass -File Release\package.ps1

param(
    [string] $Version = "",
    [string] $OutDir  = "",
    [string] $Dll     = ""      # defaults to the copy in your game's Mods folder
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $OutDir) { $OutDir = Join-Path $Root "dist" }

# Version comes from Plugin.cs unless you override it, so there is only ever
# one place to change it.
if (-not $Version) {
    $plugin = Join-Path $Root "TcgMultiplayer\Plugin.cs"
    $m = Select-String -Path $plugin -Pattern 'Version\s*=\s*"([0-9.]+)"' | Select-Object -First 1
    if (-not $m) { throw "Couldn't find the version constant in $plugin" }
    $Version = $m.Matches[0].Groups[1].Value
}

# The build drops straight into the game's Mods folder, so that copy is the
# one that actually ran - which is what we want to ship. Find it the same way
# the installer does.
function Find-GameFolder {
    $steam = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { return $null }
    $libs = @($steam)
    $vdf  = Join-Path $steam "steamapps\libraryfolders.vdf"
    if (Test-Path $vdf) {
        Select-String -Path $vdf -Pattern '"path"\s+"(.+?)"' -AllMatches | ForEach-Object {
            $_.Matches | ForEach-Object { $libs += $_.Groups[1].Value -replace '\\\\', '\' }
        }
    }
    foreach ($lib in $libs) {
        $p = Join-Path $lib "steamapps\common\TheCoinGame"
        if (Test-Path (Join-Path $p "TheCoinGame.exe")) { return $p }
    }
    return $null
}

if (-not $Dll) {
    $candidates = @()
    $game = Find-GameFolder
    if ($game) { $candidates += Join-Path $game "Mods\TcgMultiplayer.dll" }
    $candidates += Join-Path $Root "Installer\mods\TcgMultiplayer.dll"
    $candidates += Join-Path $Root "TcgMultiplayer\bin\Release\net472\TcgMultiplayer.dll"
    $Dll = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Dll -or -not (Test-Path $Dll)) {
    throw "Couldn't find TcgMultiplayer.dll. Build it, or pass -Dll <path>."
}
$dll = $Dll

Write-Host "Packaging $Version" -ForegroundColor Cyan
Write-Host "  dll  $dll"
Write-Host "       $((Get-Item $dll).Length) bytes, built $((Get-Item $dll).LastWriteTime)"

# Cheap guard against shipping yesterday's build by accident.
$stamp = (Get-Item $dll).LastWriteTime
if ((Get-Date) - $stamp -gt [TimeSpan]::FromDays(7)) {
    Write-Host "  NOTE: that DLL is $([int]((Get-Date) - $stamp).TotalDays) days old. Rebuilt lately?" -ForegroundColor Yellow
}

$stage = Join-Path ([System.IO.Path]::GetTempPath()) "tcgpkg-$([guid]::NewGuid().ToString('n').Substring(0,8))"
New-Item -ItemType Directory -Path $stage -Force | Out-Null
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

function Pack ($name, $sourceDir) {
    $zip = Join-Path $OutDir $name
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $sourceDir "*") -DestinationPath $zip
    "{0}  ({1:N0} bytes)" -f $name, (Get-Item $zip).Length
}

# ---------------------------------------------------------------- manual (Nexus)
# Archive root mirrors the game folder, so "extract here" just works and Vortex
# understands it. Nothing in here executes.
$manual = Join-Path $stage "manual"
New-Item -ItemType Directory -Path (Join-Path $manual "Mods") -Force | Out-Null
Copy-Item $dll (Join-Path $manual "Mods\TcgMultiplayer.dll")
foreach ($f in @("READ ME FIRST.txt", "CHANGELOG.txt", "LICENSE.txt")) {
    Copy-Item (Join-Path $Root "Release\$f") (Join-Path $manual $f)
}
$a = Pack "TheCoinGameMultiplayer-$Version-manual.zip" $manual

# ------------------------------------------------------------ installer (Discord)
# Not published on Nexus. A script that downloads and runs a third-party
# installer is a reasonable thing to hand a friend directly and a bad thing to
# put on a public mod page.
$inst = Join-Path $stage "installer"
New-Item -ItemType Directory -Path (Join-Path $inst "mods") -Force | Out-Null
Copy-Item $dll (Join-Path $inst "mods\TcgMultiplayer.dll")
foreach ($f in @("INSTALL - double click me.bat", "UNINSTALL.bat", "install.ps1", "uninstall.ps1")) {
    Copy-Item (Join-Path $Root "Installer\$f") (Join-Path $inst $f)
}
Copy-Item (Join-Path $Root "Installer\READ ME FIRST.txt") (Join-Path $inst "READ ME FIRST.txt")
foreach ($f in @("CHANGELOG.txt", "LICENSE.txt")) {
    Copy-Item (Join-Path $Root "Release\$f") (Join-Path $inst $f)
}
$b = Pack "TheCoinGameMultiplayer-$Version-installer.zip" $inst

Remove-Item $stage -Recurse -Force

Write-Host ""
Write-Host "Done - $OutDir" -ForegroundColor Green
Write-Host "  $a"
Write-Host "  $b"
Write-Host ""
Write-Host "  manual     -> Nexus"
Write-Host "  installer  -> Discord, testers only"
