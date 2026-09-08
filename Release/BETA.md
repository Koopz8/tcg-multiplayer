# Closed beta — the reply, and the tester guide

The dev hasn't answered in two days. That isn't a no, and it isn't unusual for
a solo developer with a publisher. It also isn't the thing standing between you
and a good release — the untested handshake is.

The people in your thread are the fix for that. Below: a reply to post, and a
short guide to hand whoever says yes.

---

## Reply to post in your thread now

> Thanks for the interest, genuinely — I wasn't sure anyone would bite.
>
> Quick status. It works, and I've tested everything I can test on my own,
> which is more than you'd think but less than it needs. What I *haven't* been
> able to do is run it with two real people, because I only own one copy of the
> game and you can't play the same one on two accounts at once. So every part
> of this has been verified except the actual bit where two computers talk to
> each other.
>
> Rather than upload it and find out in public, I'd like to run a small closed
> test first. **If three or four of you fancy being the ones who break it, say
> so here or add me on Steam.** You'd need your own copy of the game and about
> an hour.
>
> There's a build-in self-check (F10) that verifies most of it on your own
> machine in a couple of seconds, including that visiting someone's island
> gives you your own save back afterwards. I'd want everyone to run that first
> and paste the result, then we'd all get in a lobby and try to wreck it.
>
> Once it survives that, it goes up on Nexus for everyone.
>
> devotid — still very happy to hear from you either way, and it still comes
> down if you'd rather it didn't exist.

**Why this reply works:** it's honest about the gap, it turns people who want
the mod into people who are *involved* in the mod, and it gives the dev another
low-pressure chance to weigh in without you nagging.

**Don't** send anyone a download link in public before they've said yes. Handle
it in DMs so you know who has which build.

---

## Hand this to each tester

### Before we play

1. Install it (double-click the installer, click Yes if Windows asks).
2. Launch the game and **load your save**.
3. Press **F10**. That runs the self-check.
4. Press **Copy result** in the panel and paste it to me.

That takes two minutes and catches most of what could be wrong before we waste
an evening on it. It also makes a backup of your save, which brings me to:

### Your save

The mod copies your save aside automatically before any session, and keeps five
copies. You'll find them next to your save at:

```
%USERPROFILE%\AppData\LocalLow\devotid\TheCoinGame\TcgMultiplayer_SaveBackups\
```

Paste that into an Explorer address bar now, so you know where it is before you
need it. If anything ever looks wrong: close the game, copy the files from the
newest folder back over your save, done.

I don't expect to need this. I'd rather you had it.

### What we're testing, in this order

Please don't skip ahead. If step 3 is broken, nothing we learn in step 6 means
anything.

1. **Connect.** I host, you join from the Steam friends list. Say something in
   chat. Check the ping shown next to your name looks sane.
2. **Walk around each other.** Watch each other's legs — walking forward should
   look like walking forward, not sliding sideways. This is the single thing I
   most want eyes on.
3. **Machines.** I get on a cabinet. You try the same one. It should refuse your
   card — and you should be able to stand there and watch me play it.
4. **Money.** I win something. Your wallet must not move. Check it.
5. **Unlocks.** One of us buys a vehicle or opens a door. Does it appear for
   both of us?
6. **A coin pusher.** Watch the coins while the other person plays. In the
   panel, "physics count mismatches" should stay at zero.
7. **Leave and check your own game.** This is the important one. After you
   disconnect, do you still have *your* unlocks, and not mine? If you bought
   something during the session, did you keep it?

Then try to break it. Disconnect your wifi mid-session. Alt-F4 while I'm
watching you play. Both get on machines at once. That's what a beta is for.

### If you get stuck in a machine

**F11.** It releases everything.

### What to send me afterwards

Even if it all went fine:

```
<game folder>\MelonLoader\Latest.log
```

Steam → right-click The Coin Game → Manage → Browse local files.

Rename it so I know whose it is. **I need it from both sides** — half a
multiplayer bug usually isn't enough to work with.

If something broke, tell me what you were doing, whether you were hosting or
joining, and what *each* of us saw. "I saw him at the machine, he said he was
outside" is a genuinely useful sentence.

---

## What the self-check actually covers

Worth knowing so nobody overtrusts it.

**It checks:** the wire format survives a round trip for every message type and
every field; awkward Steam names with accents and emoji don't corrupt; malformed
and random packets get rejected instead of crashing; machine IDs are stable and
don't collide; the economy globals are under guard; the save backup really
writes files; and — the important one — that a full visit cycle against your
real save gives your own progression back, keeping anything you unlocked
yourself.

**It does not check:** Steam actually delivering packets between two machines,
real latency, or two people using the same machine at once. Only a real session
does that. The report says so at the bottom, on purpose.

---

## After the beta

If it survives an evening with three or four people:

- Fix whatever came up, bump the version, upload to Nexus.
- Post the release announcement (post two in `STEAM-POST.md`).
- Credit the testers by name on the Nexus page. They earned it and it costs
  nothing.

If it doesn't survive, you'll have logs from both sides of every failure, which
is exactly the position you want to be in — and none of it happened in public.
