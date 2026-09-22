# The Coin Game - Multiplayer

Play [The Coin Game](https://store.steampowered.com/app/598980/) with friends. 2-4 players on one island over Steam. You can see each other walk around, watch each other play the machines, ride in each others cars, and everyone keeps their own money. Guests get the host's unlocks while they're visiting and their own back when they leave.

Public beta. Works fine for me and the handful of people testing it, but it's still a beta so back up your save (the mod does this for you the first time you host or join, but still).

Download: [Nexus](https://www.nexusmods.com/thecoingame/mods/9) · bugs / chat: Koop's Mods discord (link on the Nexus page)

Not affiliated with devotid, this is a fan thing.

## Install

1. Install [MelonLoader](https://melonloader.net) 0.6.6 (x64) onto TheCoinGame.exe. Run the game once so it builds its cache, first launch is slow.
2. Drop `TcgMultiplayer.dll` into `TheCoinGame\Mods\`.
3. Launch. Press **F9** for the panel.

If you have BepInEx in the folder from another mod, take it out. Two loaders and the game starts with no mods and no error.

## Playing

- **Host** makes a friends-only Steam lobby. **Invite** opens the normal Steam invite thing. Your friend can also paste a lobby ID.
- Steam invites work whether the game is open or not.
- Walk up to a machine and put your card in like normal. Whoever gets there first gets it, everyone else's card gets refused until they're done. You can stand there and watch.
- **F7** gets in/out of a vehicle a friend is driving.
- **F8** moves the game to your next monitor (works even if you can't see the game - the base game has no setting for this and it kept opening on the wrong screen for me).
- **F10** runs a self check. **F11** un-sticks you from everything if something goes wrong.

## Settings

`UserData\MelonPreferences.cfg`, section `[TcgMultiplayer]`. The ones you'd actually touch:

| Key | Default | |
|---|---|---|
| ToggleKey | F9 | panel |
| MaxPlayers | 4 | 2-8, but it's really built around 4 |
| RideAlongKey | F7 | |
| NextMonitorKey | F8 | |
| ReleaseEverythingKey | F11 | |
| ConfineCursorToWindow | true | keeps the mouse in the game while the panel is open |
| BackupSaveBeforeSession | true | copies your save aside before hosting/joining. leave it on |
| GuestKeepsOwnProgression | true | guests get their own unlocks back after a visit. also leave it on |

## Known stuff

- Machines that roll dice locally (random prize stuff) can look different for the watcher vs the player. The panel shows a mismatch count so you can tell.
- Steam achievements/leaderboards are blocked while you're in a session, on purpose. Otherwise your friend's skee ball score could post under your name. Single player is untouched.
- MaxPlayers goes up to 8 but everything is tuned for 4. If you try a bigger group tell me how it went.

## Bugs

There's a report file written every time a session ends, in `TcgMultiplayer_Reports` next to the game. Send me that plus the MelonLoader log from both sides and I can usually figure it out. Nothing is uploaded automatically.

## Building

Needs the .NET SDK. Copy the game DLLs listed in each project's `libs\README.txt` out of `TheCoinGame_Data\Managed\`, then

```
dotnet build -c Release TcgMultiplayer\TcgMultiplayer.csproj
```

Release builds copy the dll straight into the game's Mods folder (`-p:TcgGameDir=...` if your Steam library isn't on C:). Close the game first or the copy gets skipped.

`TcgRig` is a test harness that runs two sessions in one process against a fake Steam - `dotnet run --project TcgRig -c Release`. `TcgFsmDump` is the research tool I used to figure out the game's PlayMaker graphs, you don't need it to play.

More detail on how it all works in [docs/DESIGN.md](docs/DESIGN.md).

MIT. No game files are in this repo.
