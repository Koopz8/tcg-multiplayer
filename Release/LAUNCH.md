# Public beta — where to put it and how to start

## The short answer

**Nexus hosts the file. Steam brings the people. Discord runs the beta.**

Three places, three different jobs, and it matters that you don't try to make
one of them do another's.

---

## Why, with the actual numbers

I looked at what's really there before recommending anything.

**Nexus does have a Coin Game section** — it exists, it accepts uploads, and it
has a handful of mods on it. But it is *tiny*. The most substantial one there,
a quality-of-life mod, has **3 endorsements**. Nobody is browsing that page
hoping to find something. So:

> Nexus is the right **home** for the file and the wrong **channel** for
> discovery.

That's not a reason to skip it. A canonical download that is versioned, virus
scanned, permanently linkable, has a changelog, and doesn't require anyone to
join anything is worth a lot — and it's what people expect a mod to have. It
just won't bring you users on its own.

**ModDB has literally zero Coin Game mods.** Skip it.

**Steam's discussion board is where your audience actually is.** You know this
already, because that's where four people volunteered. That's your discovery
channel and it will stay your discovery channel.

**One thing worth knowing:** the existing Nexus mods use **BepInEx**, not
MelonLoader. Yours is the only MelonLoader mod on the game. If a tester already
has that QoL mod installed, two mod loaders will be hooking the game at once,
and the usual result is the game launching with no mods at all and no clue why.
The installer now detects BepInEx and warns before it happens.

---

## Do you need a Discord?

For four testers coordinating live multiplayer sessions — **yes, but a small
one.** Not because it's what mods do, but for two specific reasons:

1. **You need people online at the same time.** Testing multiplayer means
   scheduling, and a Steam thread is a terrible scheduling tool.
2. **Voice while testing is worth more than any bug report.** Half of these
   bugs are "I'm standing right next to you" / "no you're not", and that
   sentence has to be said out loud in the moment to be useful.

**Do not** build a big server. Four channels, no roles, no bots:

```
#announcements     you post builds here, nobody else can post
#general           chat
#bug-reports       one report per message, logs attached
#voice             the actual testing happens here
```

A dead Discord with fourteen empty channels looks worse than no Discord. If it
grows later, add rooms then.

**If you don't want to run a Discord**, a Steam group chat covers the
scheduling and Steam voice covers the rest. It's worse, but it's not bad, and
it's better than a server you resent maintaining.

---

## Order of operations

### 1. Before you post anything

Launch, load your save, **press F10**, confirm the self-check is all-pass on
your own machine. If something fails there, no one else should see this yet.

Then have one session with one tester before you announce anything publicly.
Not the full four — one person, one hour. That single session will find the
things that would otherwise be found by everyone at once.

### 2. Put the file on Nexus

- **Version 0.9.3**, and leave it at 0.x. That's the clearest possible signal
  that this is a beta, better than any banner.
- Upload the beta zip as the main file.
- Description, permissions and credits are all in `NEXUS-PAGE.md` — use it, but
  add the "known issues" section prominently and put the save-backup note
  **above the fold**.
- Mark requirements as MelonLoader 0.6.6, and mention the BepInEx conflict.
- Screenshots matter more than any paragraph. Two players visible together,
  someone watching someone else play a machine, the panel open. Get these from
  the first test session.

### 3. Announce in the Steam thread

Post two in `STEAM-POST.md`, with one change now that time has passed: mention
that you asked first and had no reply, so you're releasing it as an unofficial
beta that comes down on request. Say it once, plainly, and move on. It reads as
respectful rather than defensive.

Put the Discord invite in that post, and in the Nexus description.

### 4. Then gather data

See below. This is the part most beta releases get wrong.

---

## Gathering data from four people playing

The mod now writes a **session report** every time a session ends — a text file
next to the game, and a "Copy report" button in the panel. One per session, last
20 kept.

Each one says: how long it ran, whether they hosted or joined, how it ended
(pressed Leave / host left / lost Steam / timed out), how many packets and
kilobytes moved, every player with their mod version, ping, handshake state and
bad-packet count, machines registered, events mirrored sent vs applied, physics
bodies and **count mismatches**, payouts blocked by the wallet guard, world
changes, whether they were still mid-visit when it ended, anything that switched
itself off, worst frame time, and the self-test result.

Nothing is transmitted anywhere. It's a file on their disk that they choose to
send. Don't add telemetry — it would cost you more trust than the data is worth.

### What to ask testers for

After every session, from **both sides**:

```
<game folder>\TcgMultiplayer_Reports\session_<date>_<host|guest>.txt
<game folder>\MelonLoader\Latest.log
```

Two reports from opposite ends of the same session is the thing that makes a
desync solvable. One is a guess.

### What to look for when they come in

| Signal | What it means |
|---|---|
| `events mirrored: 120 sent, 60 applied` | One side is dropping. Look at the other report's receive counts. |
| `count mismatches` above 0 | The two sides disagree about what objects exist in a machine. Highest-value bug in the pile. |
| `payouts blocked: 0` while they spectated | The wallet guard didn't fire. Should be non-zero. |
| `still visiting? YES` | Progression wasn't restored. Drop everything and fix this one. |
| `NEVER HANDSHAKED` on a peer | Connected to the lobby but the transport never came up. This is the M8 risk. |
| `ended because: lost the connection to the host` | The watchdog worked. Confirm they got their save back. |
| Anything under `Problems` | A subsystem retired itself. The log has the stack. |

### Keep a table

Four testers, a few sessions each, and it'll be a mess in a week. One
spreadsheet: date, who, host or guest, duration, how it ended, mismatches,
anything broken. Patterns show up fast — "only ever fails when Dave hosts" is
the kind of thing you'll only spot written down.

---

## What "done with the beta" looks like

You're ready to call it 1.0 when:

- Four people have connected in the same session at least once.
- Nobody has lost progression, and `still visiting? YES` has never appeared.
- `count mismatches` sits at zero across several coin-pusher sessions.
- A session has survived someone rage-quitting mid-spectate.
- Two people have played for an hour without anything in `Problems`.

That's a real bar and it's reachable in a couple of weekends with four people.

---

## One thing to be careful about

You still haven't heard from devotid or Kwalee. That's almost certainly
indifference rather than objection, and you asked in public and in good faith,
which is all anyone can reasonably do.

But keep the "unofficial, unaffiliated, comes down if asked" line on the Nexus
page and in the Steam post, and mean it. If they ever do ask, take it down
first and discuss second. That's the difference between a modder a developer
tolerates and one they don't.

Sources: [The Coin Game on Nexus](https://www.nexusmods.com/thecoingame/mods/toprecent) ·
[the existing QoL mod (BepInEx)](https://www.nexusmods.com/thecoingame/mods/6) ·
[ModDB — zero mods](https://www.moddb.com/games/the-coin-game/mods)
