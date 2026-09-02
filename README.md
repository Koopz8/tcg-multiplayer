# TcgFsmDump

M0 of the Coin Game multiplayer project. Two jobs:

1. **Dump** every PlayMaker FSM graph in the loaded scene to JSON — owner object,
   hierarchy path, candidate NetId, states, transitions, actions, events, variables.
2. **Trace** which of those events and states actually fire while you play, so you
   can tell the handful that matter from the thousands that don't.

Nothing in here changes the game. It reads and writes files.

---

## Install

1. Install **MelonLoader** (the automated installer is easiest) and point it at
   `Steam\steamapps\common\TheCoinGame\TheCoinGame.exe`. Use MelonLoader 0.6.x.
2. Run the game once so MelonLoader creates its folders, then quit.
3. Drop `TcgFsmDump.dll` into `TheCoinGame\Mods\`.
4. Launch. The MelonLoader console should print:

   ```
   [TcgFsmDump] Ready. F7 = dump scene, F8 = toggle live trace.
   [TcgFsmDump] Output folder: ...\TheCoinGame\TcgFsmDump
   ```

If MelonLoader's console window is hidden, open
`TheCoinGame\MelonLoader\Latest.log` instead.

## Use

| Key | What it does |
|-----|--------------|
| **F7** | Dump the currently loaded scene right now |
| **F8** | Start / stop the live event trace |

A dump is also written automatically ~6 seconds after every scene load, so just
launching into the island gives you one without touching anything.

Everything lands in `TheCoinGame\TcgFsmDump\`:

- `fsm_<scene>_<timestamp>.json` — the full graph dump
- `fsm_<scene>_<timestamp>.summary.txt` — read this first: counts, and the most
  common FSM names, event names, state names and variable names
- `trace_<timestamp>.log` — tab-separated: `time, frame, kind, fsmPath|fsmName, detail`

### The workflow that actually matters

1. Load into the island, let the auto-dump run.
2. Open the `.summary.txt`. That's your map.
3. Walk up to one arcade machine. Press **F8**, play one round, press **F8** again.
4. Open the trace log. What you're looking at is the complete set of FSM events
   and state changes that one machine needs in order to be replicated. That list
   is usually short — often a dozen events — and that is the real finding.

Repeat step 3 per machine. Each trace becomes one machine adapter later.

### Settings

`TheCoinGame\UserData\MelonPreferences.cfg`, section `[TcgFsmDump]`:

| Key | Default | Notes |
|-----|---------|-------|
| `DumpActions` | `true` | Lists PlayMaker action types per state. Forces PlayMaker's lazy action load — set `false` if a dump ever destabilises a scene. |
| `DumpVariableValues` | `true` | Records current values, not just names and types. |
| `IncludeInactive` | `true` | Also sweeps FSMs on disabled objects, which PlayMaker's own list skips. |
| `AutoDumpOnSceneLoad` | `true` | |
| `AutoDumpDelaySeconds` | `6` | Raise it if the island is still streaming in when the dump fires. |
| `TraceStates` | `true` | Log state entries as well as events. |
| `TraceSystemEvents` | `false` | UPDATE / FIXED_UPDATE / etc. Floods the log. |
| `MutedEvents` | *(empty)* | Comma-separated event names to drop once you know they're noise. |
| `DumpKey` / `TraceKey` | `F7` / `F8` | Any `KeyCode` name. |

---

## One thing this is meant to settle

The plan assumed `ES2UniqueID` could serve as the network object registry. Reading
its source says otherwise — the id is handed out in `Awake` order
(`uniqueIDList[last].id + 1`), not baked into the scene, so it is not guaranteed
to agree across two machines.

So the dump records **both** `es2UniqueId` and a `pathHash` (FNV-1a 32 of the full
hierarchy path). Run the dump on two different machines on the same save and diff
the two JSON files:

- if `pathHash` matches everywhere, hierarchy path is the NetId scheme
- if `es2UniqueId` also matches everywhere, it's a usable fast lookup on top
- `Path-hash collisions` in the summary must read `0`; if not, the NetId needs to
  be 64-bit

That comparison is the actual deliverable of M0.

---

## Building

Needs the .NET SDK. Copy these out of `TheCoinGame\TheCoinGame_Data\Managed\`
into `TcgFsmDump\libs\`:

```
PlayMaker.dll
UnityEngine.dll
UnityEngine.CoreModule.dll
UnityEngine.PhysicsModule.dll
UnityEngine.AnimationModule.dll
UnityEngine.InputLegacyModule.dll
```

Then:

```
dotnet build -c Release
```

Output is `bin\Release\TcgFsmDump.dll`. MelonLoader and HarmonyX come from NuGet;
nothing is bundled into the mod.

## Target build

Written against **Unity 2022.3.62f3, Mono x64**, The Coin Game build `22761496`.
No hard reference to `Assembly-CSharp` — `ES2UniqueID` is reached by reflection —
so a game patch should not break this outright. If PlayMaker itself is upgraded,
the two Harmony patches (`Fsm.ProcessEvent`, `FsmState.OnEnter`) will log a warning
and disable themselves rather than crash.
