# Nexus page copy

Paste-ready. Nexus's description box takes BBCode; headings here map to
`[size=4][b]...[/b][/size]` and lists to `[list][*]`. Everything below the
form fields is written to be copied as-is.

---

## Form fields

**Mod name**
> The Coin Game Multiplayer

**Summary** (shown in search results, keep it short)
> Play The Coin Game with friends. Shared island, your own wallet, watch each
> other play the machines.

**Category**
> Miscellaneous — or whatever the closest fit is on the Coin Game page.
> If there's a Multiplayer or Gameplay category, use that.

**Version**
> 0.9.0

**Requirements — this mod requires**
> MelonLoader 0.6.6 (the installer handles it)

**Tags**
> multiplayer, co-op, online, steam, melonloader

---

## Description

**Play The Coin Game with a friend.**

You share an island. You walk around it together, you take turns on the
machines, and you watch each other play. Your money stays yours.

This is an unofficial fan mod. You each need your own copy of the game.

---

**WHAT IT DOES**

- **See each other.** Real bodies with real animation, not floating markers.
- **Take turns on the machines.** If someone's on a cabinet it won't take your
  card — but you can stand there and watch them play it, coins and all, rather
  than being shut out of the room.
- **Keep your own money.** Coins, tickets, prizes and inventory are yours
  alone. Watching a friend win a jackpot doesn't pay you, and doesn't pay them
  twice.
- **Progress together.** Unlocks, doors and vehicles are shared, so the island
  opens up for everyone as you play.
- **Join through Steam.** Host, hit Invite Friend, done. Or right-click them in
  your friends list and Join Game. No port forwarding, no IP addresses, no
  server to run — it goes over Steam's own networking with NAT traversal and a
  relay fallback.

---

**INSTALLING**

Download, extract anywhere, double-click `INSTALL - double click me.bat`.

It finds the game through Steam, installs MelonLoader 0.6.6 for you (verified
against the official release), and puts the mod in place. Click Yes if Windows
asks for permission — the game usually lives in Program Files.

Both players install it. Both players need the same version; the mod checks
and tells you if you don't match.

There's an `UNINSTALL.bat` in the same folder that puts the game back to stock.

---

**YOUR SAVE IS BACKED UP AUTOMATICALLY**

The first time you host or join, your save folder is copied aside. Five copies
are kept, in a `TcgMultiplayer_SaveBackups` folder next to your save. The panel
shows you where, and has a button to make one any time.

I'd still recommend you know where that folder is before your first session.

---

**VISITING SOMEONE ELSE'S ISLAND**

When you join someone you're a guest on their island, so their unlocks apply
while you're there. When you leave, your own progression comes back exactly as
it was — and anything you unlocked yourself while visiting stays yours.

The gap: if the game crashes mid-session, that tidy-up doesn't get to run.
That's what the backup is for.

---

**ACHIEVEMENTS AND LEADERBOARDS**

While you're in a session, the mod blocks the game from submitting anything to
Steam.

This is deliberate and it isn't negotiable. Spectating works by replaying your
friend's round on your machine — without the block, their Skee-Ball score would
post to the leaderboard under your name. That's cheating, even by accident.

**Playing alone, with nobody connected, is completely untouched.** Achievements
and leaderboards work exactly as they always have.

---

**CONTROLS**

| Key | Does |
|---|---|
| **F9** | Open the multiplayer panel |
| **F11** | Get unstuck — releases every machine you're holding |

All keys and settings are in MelonLoader's config, under `TcgMultiplayer`.

---

**KNOWN ISSUES AND LIMITATIONS**

I'd rather you read this before downloading than find out afterwards.

- **Two to four players.** Four is what the bandwidth is designed around.
- **No host migration.** If the host leaves, the session ends for everyone.
  It ends cleanly and everyone keeps their own progression, but it ends.
- **NPCs aren't synced.** You'll each see them doing their own thing.
- **Both players need the same game version.** The mod warns you if you don't
  match. A Coin Game update will probably need a mod update.
- **Not everything in the game has been tested with two people.** The arcade
  machines, the island, unlocks and vehicles have. The stranger corners of the
  game may well have rough edges — please report them.
- **Voice chat isn't in.** Use Discord.

---

**BUG REPORTS**

Please include:

1. `<game folder>\MelonLoader\Latest.log` — **from both players if you can.**
   Half a multiplayer bug is usually not enough to fix it.
2. What you were doing, and whether you were hosting or joining.
3. What each of you saw. "I saw him at the machine, he says he was outside" is
   a genuinely useful sentence.

Steam → right-click The Coin Game → Manage → Browse local files.

If part of the mod switches itself off, the panel says so at the top with the
error. That text is worth copying into the report.

---

**CREDITS**

- [MelonLoader](https://github.com/LavaGang/MelonLoader) by Lava Gang —
  Apache 2.0. Downloaded from its official release page by the installer,
  verified by hash, installed unmodified. Not redistributed here.
- [HarmonyX](https://github.com/BepInEx/HarmonyX) — MIT.
- [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET) by Riley
  Labrecque — MIT. Already shipped with the game.
- **devotid** for making The Coin Game, and Kwalee for publishing it. This mod
  is unofficial and unaffiliated. Nothing from the game is redistributed here.
  If they'd prefer it didn't exist, it comes down.

---

## Permissions (Nexus permissions form)

Set these in the form itself, not just in the description.

| Setting | Answer |
|---|---|
| Others can use assets with credit | **Yes** |
| Others can upload without permission | **No** — ask first, so there aren't three stale forks |
| Others can modify and release | **Yes, with credit** |
| Others can convert to other games | **Yes** |
| Others can use assets in mods sold for money | **No** |
| Asked permission from original asset creators | **N/A** — no third-party assets are redistributed |

Description text for the permissions box:

> The mod is MIT licensed — see LICENSE.txt in the download. Fork it, fix it,
> keep it alive if I stop. Credit and a link back is all I ask.
>
> Please don't re-upload this build as-is; open an issue or comment instead so
> there's one page people can find. If I go quiet for a long stretch, take it
> over — that's what the licence is for.
>
> No assets from The Coin Game, MelonLoader, or anything else are redistributed
> in this download.

---

## First-post comment to pin

> Heads up before you install:
>
> **Back up your save.** The mod does it automatically the first time you host
> or join, into a `TcgMultiplayer_SaveBackups` folder next to your save, but
> please know where that folder is.
>
> **Both players need the same version of the mod and the same version of the
> game.** The mod checks and will tell you if you don't match.
>
> This is a new mod and the first public build. It's been tested heavily, but
> multiplayer finds bugs that solo testing can't. If something breaks, grab
> `MelonLoader\Latest.log` from **both** machines and post it — that's what
> makes it fixable.

---

## What still needs doing before you upload

- [ ] **Play a real session with a second player.** Nothing below matters until
      this happens. See M8 in the plan.
- [ ] Take screenshots — two players visible together, the panel open, someone
      watching someone else play a machine. Nexus pages live or die on the
      first image.
- [ ] A short clip if you can manage one. Even 20 seconds of two people walking
      around the same arcade sells this better than any paragraph above.
- [ ] Decide whether to message devotid on the Steam discussion board first.
      Costs nothing, and finding out they're fine with it — or that they have
      multiplayer planned — is much better news before upload than after.
- [ ] Check the Coin Game Nexus page for an existing mod that does any of this,
      so you're not colliding with someone.
