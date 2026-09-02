# tcg-multiplayer

Multiplayer for **The Coin Game** (Steam app 598980). Two MelonLoader mods:

| Mod | Purpose |
|-----|---------|
| **TcgFsmDump** | M0 — dumps every PlayMaker FSM graph to JSON and traces which events fire while you play. A research tool; keep it around. |
| **TcgMultiplayer** | The mod itself. M1: Steam lobby, peer transport, handshake, ping and chat. |

Target: Unity **2022.3.62f3**, Mono x64, game build `22761496`.
Loader: **MelonLoader 0.6.6** (HarmonyX 2.10.2).

---

## Install

Drop the built `.dll`s into `TheCoinGame\Mods\`. Both can run at once.

## TcgMultiplayer (M1)

Press **F9** for the overlay.

- **Host** — creates a friends-only Steam lobby
- **Invite friend** — opens Steam's invite dialog
- **Join by lobby ID** — paste a lobby ID if you'd rather not use the overlay
- Accepting a Steam invite works whether the game is running (`GameLobbyJoinRequested`)
  or closed (`+connect_lobby` on the command line)

The panel shows each peer's name, handshake state, **round-trip time in ms**, and
the raw Steam connection state. The chat box is there to prove bytes move both
ways — type, hit Enter, watch it land on the other machine.

### What it does not do yet

Nothing about the game is synced. No avatars, no machines, no economy. M1 exists
to prove the transport in isolation, because that's the piece that's hardest to
debug once gameplay is layered on top.

### Testing it

You need **two machines and two Steam accounts** — Steam only runs one instance of
a game per account, so this can't be tested solo on one PC. Solo you can still
confirm: the overlay opens, Steam initialises, Host succeeds, a lobby ID appears,
and the invite dialog opens.

### Settings

`UserData\MelonPreferences.cfg`, section `[TcgMultiplayer]`:

| Key | Default | Notes |
|-----|---------|-------|
| `ToggleKey` | `F9` | Any `KeyCode` name. |
| `MaxPlayers` | `4` | 2–8. The bandwidth model is designed around 4. |
| `OpenOverlayOnStart` | `true` | |
| `SuppressGameInputWhileOpen` | `true` | Disables Rewired input maps so chat typing doesn't also drive the player. |

### Design notes

**Steam is the game's, not ours.** `HLG.Runtime.dll` ships Steamworks.NET's
`SteamManager`, which already calls `SteamAPI.Init()` and pumps
`SteamAPI.RunCallbacks()` every frame. The mod must not do either — it would
double-dispatch every callback in the game. `SteamBridge` just waits on
`SteamManager.Initialized`, read by reflection so there's no hard reference to
HLG.Runtime.

**Transport is `SteamNetworkingMessages`**, which hands us NAT traversal, relay
fallback and authenticated identity for nothing. Channel 0 is reliable control,
channel 1 is unreliable for ping. Sessions are only accepted from peers who are
actually in our lobby.

**The cursor has to be taken by force.** The M0 trace caught an FSM re-issuing
`Cursor LOCKED` roughly 40 times a second, so a one-shot unlock loses. The overlay
reasserts `CursorLockMode.None` in `OnGUI`, which runs after every `Update`.

**Input suppression borrows the game's own move.** `ControllerDisconnectView` calls
`SetAllMapsEnabled(false)` on the Rewired player when it needs to steal input;
`InputLock` does the same by reflection. That is also the groundwork for M2, where
remote avatars have to be cut off from local input entirely.

---

## TcgFsmDump (M0)

**F7** dumps the loaded scene, **F8** toggles the live trace. A dump also runs
automatically ~6 s after each scene load. Output goes to `TheCoinGame\TcgFsmDump\`:

- `fsm_<scene>_<ts>.json` — full graph dump
- `fsm_<scene>_<ts>.summary.txt` — read this first
- `trace_<ts>.log` — `time, frame, kind, fsmPath|fsmName, detail`

The workflow that matters: load in, walk to a machine, **F8**, play one round,
**F8**. The trace is the event shortlist for that machine.

Settings live under `[TcgFsmDump]`; `MutedEvents` is worth filling in as you learn
which events are just per-frame noise.

### What M0 established

- **NetId = FNV-1a 32 of the hierarchy path.** 0 collisions across 4,533 FSMs.
  `ES2UniqueID` is on 0 of them and is not usable.
- **`Coin Machine Canvas CNTLR`** — one FSM type, 72 instances, on every machine,
  ride, booth, vending unit and vehicle. Its `Card Inserted` / `Card Removed` /
  `FREEZE MACHINE` states are the game's own occupancy model.
- **`WorldUI_Prize FSM`** — 102 instances, the single chokepoint for every purchase.
- Worst rigidbody count is 100 (Claw Machine Balls); coin pushers are 63–66 coins each.
- 2,356 graphs are named just `FSM`, so adapters must key on **path pattern**, not name.

---

## Building

Needs the .NET SDK. Copy the game assemblies each project's `libs\README.txt`
lists out of `TheCoinGame\TheCoinGame_Data\Managed\`, then:

```
dotnet build -c Release TcgFsmDump\TcgFsmDump.csproj
dotnet build -c Release TcgMultiplayer\TcgMultiplayer.csproj
```

MelonLoader and HarmonyX come from NuGet. Nothing is bundled into either mod.
