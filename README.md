# tcg-multiplayer

Multiplayer for **The Coin Game** (Steam app 598980). Two MelonLoader mods:

| Mod | Purpose |
|-----|---------|
| **TcgFsmDump** | M0 — dumps every PlayMaker FSM graph to JSON and traces which events fire while you play. A research tool; keep it around. |
| **TcgMultiplayer** | The mod itself. M1: Steam lobby and peer transport. M2: remote player avatars, plus a Mirror mode that makes them testable with one copy of the game. |

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

### M2 — remote avatars

Other players get a real body: a clone of `PLAYER/LARRY Mesh` with every FSM, IK
controller, collider, rigidbody, light and audio source stripped out, driven from
the network with a nameplate over its head. Snapshots go out at 15 Hz on an
unreliable channel; remote bodies render ~120 ms in the past and interpolate
between snapshots, so motion stays smooth between packets.

### Mirror mode — testing avatars with one copy of the game

Steam runs one instance of a game per account, so a second player needs a second
PC *and* a second copy. **Mirror** removes that blocker for everything except
Steam's own delivery.

Tick **Mirror me** in the overlay. Your own player state is serialised through the
real wire format, held for a configurable delay, then fed back through the real
receive path — spawning a real remote avatar. You get a ghost of Larry walking
your exact path a second and a half behind you.

If the ghost walks correctly, then cloning, stripping, snapshot encoding,
interpolation, the animator binding and nameplates are all proven. The only part
Mirror does not cover is Steam moving the bytes, and M1 already exercises that API.

Slide the delay down to 0.25 s to check interpolation smoothness; push it to 5 s to
watch a long path replay.

### M3 — machine occupancy and spectating

**Ownership.** The host is authoritative. When your card goes into a machine, the
mod asks the host to grant it; everyone else gets the game's own `FREEZE MACHINE`
event on that cabinet, so it visibly refuses their card. Two players grabbing the
same machine in the same instant cannot both win. If someone disconnects mid-round
their machines are released rather than left locked.

Occupancy is read off the game's own lifecycle states — `Card Inserted`,
`Turn On MECH`, `Ready To Play` to claim; `Card Removed`, `Button Exit Machine`,
`Send Explore Mode Event`, `Off of Ride and Done` to release — so it works however
the player got there.

**Spectating.** The machine's owner mirrors every non-system FSM event fired inside
that machine's subtree, and spectators replay them on their own copy of the same
FSMs. Events rather than states, because a PlayMaker graph fed the same events walks
the same path, and because the M0 trace showed a whole round is a couple of dozen
events, not a stream. Sent reliably and ordered — a dropped machine event desyncs
the cabinet for the rest of the round, unlike a dropped position snapshot.

Known limit: a graph that rolls dice locally (`ActionRandomPrizeSelection`) can
diverge. That's where per-machine adapters eventually earn their keep. The overlay
shows sent/applied counts so drift is visible.

One adapter covers all 72 interactables — cabinets, rides, booths, vending
machines, lotto menus and the four vehicles.

### M4 — per-player wallets

Every player keeps their own money, tickets, prizes and inventory. That needed
**no synchronisation at all** — each client runs its own PlayMaker globals, so
separate wallets are the default state. What it needed was a *guard*.

M3's spectating replays the machine owner's FSM events, and the payout is one of
those events: left alone, everyone watching a Skee Ball win gets paid for it. So
the economy globals are snapshotted immediately before a mirrored event is applied
and restored immediately after. That closes every payout path at once rather than
blacklisting the handful anyone thought to look for.

The list of what's protected came from dumping all **658** PlayMaker globals — the
economy partitions cleanly by name (`COINS *`, `TICKETS *`, `CHIPS *`,
`GAMECARDS *`, `PRIZE CREDITS *`, `INVENTORYSPOT_*`, `PLAYCOUNT_*`, the per-machine
game credits, and player condition like `HEALTH Balance` / `BATTERY POWER`). It's a
prefix list in `MelonPreferences` under `ProtectedEconomyGlobals`, so a game patch
that adds a currency doesn't need a rebuild.

Balances are broadcast once a second purely so the overlay can show a scoreboard —
nothing authoritative rides on them. Coins are stored in cents (`COINS Balance` 250
is `$2.50`).

### M5 — the shared island

**Decision: full progression together.** Guests can do everything — unlock areas,
open doors, buy vehicles. So the 658 globals split in two, and the split is the
whole design:

| | |
|---|---|
| **Player-owned** — never crosses the wire, defended from spectating | money, tickets, chips, prizes, inventory, per-machine playcounts, health/energy/battery, passes |
| **World-owned** — host-arbitrated, replicated to everyone | `SURVIVOR_*` (area unlocks, bunker/underground/phonebooth doors, final scene), `LOOT BOX Unlocked 1-4`, and the vehicles: golf cart, van, lambo, superkart, car 1/2 |

Anyone may change world state; the change routes through the host, who applies it
and broadcasts the result, exactly like machine ownership. A joiner asks for a full
snapshot as soon as the handshake completes, so they arrive on the host's island
rather than their own. Values are typed on the wire so a bool can't arrive as an int
and silently unlock something.

Detection is a half-second poll rather than a hook: unlocks get set from PlayMaker
actions scattered right across the game, and half a second of latency on "the arcade
is now unlocked" is imperceptible.

Both lists are prefix-matched preferences (`ProtectedEconomyGlobals`,
`SharedWorldGlobals`), so the line between yours and everyone's can be moved without
a rebuild.

### Machine registry: scanning, not a one-shot

A single scan after a scene load found **14** of the 72 interactables. Most of the
island starts deactivated — `OUTSIDE Starts OFF`, `CARNIE Starts OFF`, the arcade
interior — and PlayMaker only registers an FSM once its GameObject has actually
awoken, so machines appear in `FsmList` as you walk into them. The registry now
watches the list size and rebuilds when a new district comes online.

## Solo tests — verifying the two-player features with one copy

Ownership, spectating and the wallet guard only fire when *someone else* is
playing, which with one copy of the game is never. The overlay's **Solo tests**
panel stands in for that friend.

**Rehearsal** — walk up to a machine, hit *Record a round*, play it, hit *Stop*,
then *Replay as a friend*. The machine is handed to a fake peer for the duration
and the recording is replayed through the genuine spectator path at the original
timings. That exercises, in one go:

- the registry resolved the right FSMs under that machine
- event replay actually drives the cabinet
- **the wallet guard holds your balance still while someone else's round plays**

The panel reports `wallet held` or `GUARD LEAKED Nx` after each replay. If your
coins move during a rehearsal, the guard is broken — and you find that out alone
in a minute, rather than with a friend three milestones later.

**Fake: friend takes it / leaves** — forces a remote claim on the nearest machine.
The cabinet should visibly refuse your card, via the game's own `FREEZE MACHINE`.

### Still not synced

Machine physics — the claw arm, the puck, the coins themselves (M6).

### Settings

`UserData\MelonPreferences.cfg`, section `[TcgMultiplayer]`:

| Key | Default | Notes |
|-----|---------|-------|
| `ToggleKey` | `F9` | Any `KeyCode` name. |
| `MaxPlayers` | `4` | 2–8. The bandwidth model is designed around 4. |
| `OpenOverlayOnStart` | `true` | |
| `SuppressGameInputWhileOpen` | `true` | Disables Rewired input maps so chat typing doesn't also drive the player. |
| `SnapshotHz` | `15` | How often local position is sent. Plenty for walking speed. |
| `InterpolationDelaySeconds` | `0.12` | How far in the past remote bodies render. |
| `MirrorDelaySeconds` | `1.5` | Mirror mode ghost delay. |
| `AnimatorSpeedScale` | `1.0` | Multiplies what's fed to the walk/run blend. Raise it if remote bodies glide with barely-moving legs; lower it if they sprint on the spot. |

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
`InputLock` does the same by reflection.

**There is a player movement class after all.** The M0 write-up said there wasn't;
that was wrong. `UnitySampleAssets.Characters.FirstPerson.RigidbodyFirstPersonController`
sits on `PLAYER` — it lives in `Assembly-CSharp-firstpass`, not `Assembly-CSharp`,
which is why the first sweep missed it. It exposes `Velocity`, `Grounded`,
`Jumping` and `Running`, which is exactly the animation state a remote body needs.
Read by reflection, with a transform-delta fallback for speed.

**Avatars are positioned from the mesh, not the player root.** `LARRY Mesh` sits at
a local offset inside `PLAYER`; driving the clone from the root's position plants
it at the wrong height.

**Locomotion is a 2D blend, so velocity crosses the wire in the body's frame.**
Larry's animator exposes `Walk`, `Turn`, `MoveSpeedX`, `MoveSpeedY`, `Run`,
`Crouch` plus a pile of action parameters (`Insert Card HIGH/MID/LOW`, `IsDriving`,
`IsSitting`, `IsBiking`, `RaiseBat Bool`, …). `MoveSpeedX` is **strafe** and
`MoveSpeedY` is **forward** — a name-matching heuristic picked `MoveSpeedX` first
and fed forward speed into the strafe axis, which makes the body sidle instead of
walk. The rig is now bound by an explicit table; the heuristic only survives as a
fallback for a rig we haven't seen. The snapshot carries `velX`, `velZ` (local
frame) and a smoothed `turn` rate rather than one scalar speed.

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
