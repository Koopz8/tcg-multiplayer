# Discord server — setup pack

Everything to paste. The clicking is about two minutes and you should do that
part yourself; see the note at the bottom on why.

Keep it small. Four channels. No roles, no bots, no categories. A server with
fourteen empty channels and two people in it looks worse than no server, and
you can always add rooms once there's traffic to justify them.

---

## Creating it

Discord → the **+** button in the server list → **Create My Own** → **For me
and my friends**.

**Server name:** `The Coin Game Multiplayer`

Not "TCG Multiplayer" — the abbreviation collides with trading card games and
you want this findable by the game's actual name.

Server icon: reuse whatever you put on the Nexus page, so the two look like the
same project.

---

## Channels

Delete the defaults Discord gives you, then make these four. The topic is the
grey text next to the channel name — worth filling in, it does a lot of the
explaining for you.

**#announcements** — text
> Builds, updates and known issues. Only I post here.

Settings → Permissions → @everyone → **turn off Send Messages**. Leave View
Channel and Add Reactions on. This is the one channel that needs a permission
change and it's worth doing: it keeps the release history readable.

**#general** — text
> Chat about the mod and the game. Say hello.

**#bug-reports** — text
> One problem per message please, with your session report and log attached. See the pinned message.

**Voice** — voice channel, name it `Playing`
> Where the actual testing happens.

That's it. Resist adding more.

---

## Pin this in #bug-reports

> **How to report something**
>
> One problem per message, and please attach these two files:
>
> `TcgMultiplayer_Reports\session_<date>_host.txt`  (or `_guest`)
> `MelonLoader\Latest.log`
>
> Both live in the game folder — Steam, right-click The Coin Game, Manage,
> Browse local files. The session report is written automatically every time
> you finish playing, and there's a **Copy report** button in the panel (F9).
>
> Then tell me:
>
> • what you were doing
> • whether you were hosting or joining
> • what **each** of you saw
>
> That last one matters more than it sounds. "I saw him at the machine, he says
> he was outside" is genuinely useful and I can't get it from one side.
>
> If you can, get the files from **both** players. Half a multiplayer bug
> usually isn't enough to fix it.
>
> **Stuck in a machine?** Press F11. That's not a bug report, that's the fix.

---

## Post this in #announcements as the first message

> **Welcome — this is a beta.**
>
> The mod lets you play The Coin Game with two to four people. You share an
> island, take turns on the machines, watch each other play, and everyone keeps
> their own money.
>
> **Before you play:** load your save and press **F10**. That runs a built-in
> self-check — seven tests, takes a couple of seconds — and among other things
> it proves, against your own save, that visiting someone else's island gives
> your progression back afterwards. Press **Copy result** and paste it in
> #general so I know your setup is healthy.
>
> **Your save is backed up automatically** the first time you host or join,
> into a `TcgMultiplayer_SaveBackups` folder next to your save. Five copies are
> kept. Please go and look at that folder now, so you know where it is before
> you need it.
>
> **Controls:** F9 panel · F10 self-check · F11 unstuck
>
> **Everyone needs the same version of the mod and the same version of the
> game.** The mod checks both and will tell you if you don't match.
>
> Honest status: everything that can be tested on one machine has been, and
> passes. What hasn't been properly exercised is two real computers talking to
> each other over Steam. That's what you're all here for. Expect to find things.
>
> This is an unofficial fan mod, not affiliated with devotid or Kwalee.

---

## Server settings worth changing

**Safety Setup** → verification level **Low** (verified email). Enough to keep
drive-by spam out, not enough to annoy anyone.

**Invite link:** make one that never expires with unlimited uses — that's the
one going on the Nexus page and in the Steam thread. Right-click the server →
Invite People → Edit invite link → **Expire after: Never**, **Max uses: No
limit**.

Discord defaults invites to 7 days, which would quietly break your Nexus link
next week. Worth getting right the first time.

**Don't** enable Community Server. It adds rules screening, announcement
channels and a discovery listing you don't need at this size, and it makes the
server harder to change later.

---

## Roles

None for now. `@everyone` is fine for four people plus whoever wanders in from
Nexus.

If it grows past ~20 people, add one **Tester** role and give it access to a
private `#testing` channel — that's the point where you'll want somewhere to
talk to people who actually run new builds without cluttering #general.

---

## Post this in #announcements once the mod is on Nexus

The Nexus download is manual only — the DLL and nothing else. That's the right
call for a public page, but your four testers shouldn't have to do it the hard
way. Attach `TheCoinGameMultiplayer-<ver>-installer.zip` to this message.

> **The easy installer, for you four**
>
> The version on Nexus is a manual install on purpose. A download that runs a
> script on a stranger's machine is a lot to ask of strangers, so the public
> one is just the mod file and a set of instructions.
>
> You're not strangers, so here's the easy one. It's attached to this message.
>
> Unzip it anywhere and double-click **INSTALL - double click me.bat**. It
> finds the game through Steam, installs the right MelonLoader, checks the
> download against its official hash, and puts the mod in place. Click Yes if
> Windows asks for permission — the game lives in Program Files. There's an
> UNINSTALL.bat next to it that puts everything back.
>
> Windows may warn you about a downloaded script. That's normal for any .bat
> and not a sign anything is wrong, but you're welcome to open it in Notepad
> first — it's about twenty lines and it just calls install.ps1, which is right
> there too and also readable.
>
> If you'd rather do it the Nexus way, that works identically and the mod can't
> tell the difference.
>
> Either way: load your save, press **F10**, and paste the result in #general.

Re-attach a fresh zip whenever you cut a new build — a Discord attachment is a
snapshot, not a link, so the old one sits there forever unless you replace it.

---

## Where the invite goes

- Nexus description, near the top under the beta warning
- Your Steam thread, in the release post
- The `READ ME FIRST.txt` in the download, if you want — though that means
  re-packaging when the invite ever changes, so probably not

---

## On me clicking through it for you

I can drive your browser and do this, but I'd rather flag something first:
Discord's own policy on
[automated user accounts](https://support.discord.com/hc/en-us/articles/115002192352-Automated-User-Accounts-Self-Bots)
says automating a user account is against their Terms of Service. That rule
mostly exists for scripted messaging and mass actions, and a handful of clicks
to create a server is very unlikely to trip anything — but it's your account,
the policy is real, and the manual version genuinely takes about two minutes.

So: do the clicking yourself, paste the text above, and it's done. If you'd
rather I drove it anyway now that you know, say so and I will.
