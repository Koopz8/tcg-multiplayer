# Steam discussion posts

Two posts. **Post one now.** Post two only after you've played a real session
and the Nexus page is live.

## Why this framing

Worth knowing before you post, because it's the whole reason the first post is
shaped the way it is:

- devotid told the forums in **January 2020**: *"this game will eventually get
  some kind of Multiplayer. I just have a plan and I am going to try to stick to
  it... Not yet but someday..... I promise. (And I dont promise lightly)"*
- In the same and later threads he listed the exact problems he was chewing on:
  player housing, bus and vehicle passenger capacity, **machine accessibility
  conflicts**, voice vs text chat, matchmaking and regions, and player limits.
  He also said he had a prototype running with the twins plus custom clothing,
  and that Deb's Boutique existed partly to support it.
- **1.0 shipped 19 March 2026, single-player.**

So: he wants this, he's thought hard about it, and he named several of the
problems you've now actually solved. That means the post is not "look at my
mod". It's "I went deep on your game, here's what I found about the things you
said were hard, and I've built something — is that alright with you?"

That's a post a solo dev enjoys receiving. The other kind isn't.

**Do not** post it as a demand, a comparison, or a suggestion that you beat him
to it. And say plainly that it steps aside for the official version, because
that's true and because it's the thing he'll want to know.

---

# POST ONE — post this now

**Suggested title:**
> Built a working co-op mod as a side project — devotid, would you be OK with me
> releasing it?

**Body:**

Hi all, and hi devotid if you see this.

I've spent the last while pulling The Coin Game apart to see whether co-op was
possible as a mod, and it turned out to be more possible than I expected. I have
a working build: two players on the same island, walking around, taking turns on
the machines, watching each other play.

Before I put it anywhere public I wanted to ask here first, because this is your
game and I'd rather have the conversation up front than after.

**What it does**

- Two to four players over Steam's own networking. No servers, no port
  forwarding — you host, hit invite, they join from the friends list.
- You see each other move around properly, not as dots on a map.
- Machines have an owner. If someone's on a cabinet it won't take your card,
  but you can stand there and watch them play it, coins and all, instead of
  being locked out of the room.
- Everyone keeps their own money, tickets, prizes and inventory. Watching
  someone win doesn't pay you, and doesn't pay them twice.
- Unlocks, doors and vehicles are shared while you're playing together, and a
  visitor's own progression is put back when they leave so nobody goes home
  owning someone else's island.

**The bit I'd want you to know about first**

While a co-op session is running, the mod blocks the game from submitting
anything to Steam — achievements, stats, leaderboards, all of it.

That's deliberate. Spectating works by replaying the other player's round on
your machine, so without the block their Skee-Ball score would post to the
leaderboard under my name. I wasn't willing to ship something that quietly
pollutes your leaderboards. Playing on your own with nobody connected is
completely untouched.

**On the official multiplayer**

I know you've said since 2020 that multiplayer is coming eventually, and that
you were working through things like machine conflicts, passenger capacity and
player limits before committing to anything. Nothing I've made is meant to get
in front of that. It's a mod, it'll break the first time you patch the game, and
the day an official version lands this one should just stop existing.

If it's useful, I'm happy to write up what I found — how the machines model
occupancy already, which parts of the economy are per-player and which are
world state, where the seams are for mirroring gameplay between clients rather
than replicating it. It's a fair amount of detail about your own game that you'd
have no reason to have written down, and it's yours if you want it. No strings,
and I don't need anything back.

**What I'm asking**

Just whether you're OK with me releasing it. If you'd rather I didn't, or you'd
rather it stayed a private thing between me and a friend, that's completely fine
and I'll leave it there — no hard feelings and no argument from me.

Thanks for the game. I've put an embarrassing number of hours into it, which is
presumably how I ended up here.

---

# POST TWO — only after a real session and the Nexus page is up

**Suggested title:**
> [Mod] Co-op multiplayer for The Coin Game — out now, free

**Body:**

Following up on my earlier thread: the co-op mod is out.

**What you get:** two to four players on the same island over Steam networking.
You see each other, you take turns on the machines and watch each other play,
and everyone keeps their own money and prizes. Unlocks and vehicles are shared
while you're together. Host, invite, done — no servers or port forwarding.

**Install:** download, extract, double-click one file. It finds the game through
Steam and sets up MelonLoader for you. Everyone playing needs their own copy of
the game and the same version of the mod.

**Download:** <NEXUS LINK>

**Two things to know:**

Achievements, stats and leaderboards are blocked while you're in a session, on
purpose — spectating replays the other player's round on your machine, so
without it their score would post under your name. Solo play is unaffected.

Your save is copied aside automatically before each session, five backups kept.
The mod also puts your own progression back when you leave someone else's
island. I'd still say know where that backup folder is.

**It's a mod, not a patch.** It'll break when the game updates and I'll fix it
when it does. Bug reports are very welcome — include your MelonLoader log, and
if you can, one from both players. Half a multiplayer bug usually isn't enough
to work with.

Not affiliated with devotid or Kwalee. If they ever want it gone, it's gone.

---

## BBCode version of Post One

Steam's editor takes BBCode. Paste this if the formatting doesn't survive.

```
Hi all, and hi devotid if you see this.

I've spent the last while pulling The Coin Game apart to see whether co-op was possible as a mod, and it turned out to be more possible than I expected. I have a working build: two players on the same island, walking around, taking turns on the machines, watching each other play.

Before I put it anywhere public I wanted to ask here first, because this is your game and I'd rather have the conversation up front than after.

[b]What it does[/b]
[list]
[*]Two to four players over Steam's own networking. No servers, no port forwarding — you host, hit invite, they join from the friends list.
[*]You see each other move around properly, not as dots on a map.
[*]Machines have an owner. If someone's on a cabinet it won't take your card, but you can stand there and watch them play it, coins and all, instead of being locked out of the room.
[*]Everyone keeps their own money, tickets, prizes and inventory. Watching someone win doesn't pay you, and doesn't pay them twice.
[*]Unlocks, doors and vehicles are shared while you're playing together, and a visitor's own progression is put back when they leave so nobody goes home owning someone else's island.
[/list]

[b]The bit I'd want you to know about first[/b]

While a co-op session is running, the mod blocks the game from submitting anything to Steam — achievements, stats, leaderboards, all of it.

That's deliberate. Spectating works by replaying the other player's round on your machine, so without the block their Skee-Ball score would post to the leaderboard under my name. I wasn't willing to ship something that quietly pollutes your leaderboards. Playing on your own with nobody connected is completely untouched.

[b]On the official multiplayer[/b]

I know you've said since 2020 that multiplayer is coming eventually, and that you were working through things like machine conflicts, passenger capacity and player limits before committing to anything. Nothing I've made is meant to get in front of that. It's a mod, it'll break the first time you patch the game, and the day an official version lands this one should just stop existing.

If it's useful, I'm happy to write up what I found — how the machines model occupancy already, which parts of the economy are per-player and which are world state, where the seams are for mirroring gameplay between clients rather than replicating it. It's a fair amount of detail about your own game that you'd have no reason to have written down, and it's yours if you want it. No strings, and I don't need anything back.

[b]What I'm asking[/b]

Just whether you're OK with me releasing it. If you'd rather I didn't, or you'd rather it stayed a private thing between me and a friend, that's completely fine and I'll leave it there — no hard feelings and no argument from me.

Thanks for the game. I've put an embarrassing number of hours into it, which is presumably how I ended up here.
```

---

## If you get a reply

**If he says yes** — thank him, and actually send the writeup if he wants it.
Link the Nexus page when it's up.

**If he says no** — accept it in one line, publicly and gracefully. Don't
negotiate. It costs you a hobby project and gains you a reputation as someone
worth saying yes to next time.

**If he doesn't reply** — that's the common outcome for a solo dev with a
publisher, and it isn't a no. Give it a couple of weeks, then release with the
"not affiliated, it comes down if they ask" line clearly on the page. You asked
in public and in good faith, which is all anyone can reasonably do.

**If Kwalee replies instead** — publishers care about brand and support load.
Emphasise that it's free, unaffiliated, doesn't touch leaderboards, and that
you'll take it down on request.

**Don't** get drawn into arguing with other users in the thread about whether
the dev "should" have added multiplayer already. That thread turns bad fast and
you'd be the one holding it.
