#!/usr/bin/env python3
"""
Builds out the Coin Game Multiplayer Discord server.

WHY IT WORKS THIS WAY
---------------------
This does NOT drive your Discord account. Automating a user account is against
Discord's Terms of Service, and a reusable script that does it is worse than a
one-off click, because it is the thing the rule exists to stop.

Instead it uses a bot and the official API, which is what the API is for. That
means one manual step you cannot skip: you create the empty server yourself,
and invite a bot to it. Bots cannot create servers on your behalf in any way
worth having. After that this script does the rest — channels, topics, the
permission change, the pinned messages, and a never-expiring invite link.

Everything here is idempotent. Run it twice and the second run changes nothing;
it will tell you what it skipped. So it is safe to re-run after editing the
text below.

SETUP (about five minutes, once)
--------------------------------
 1. Make the server yourself: Discord -> "+" -> Create My Own -> For me and my
    friends. Call it "The Coin Game Multiplayer".

 2. Make a bot:
      https://discord.com/developers/applications  ->  New Application
      Name it anything. Bot tab -> Reset Token -> copy the token.
      That token is a password for the bot. Don't paste it anywhere public,
      and don't commit it. If it leaks, hit Reset Token again.

 3. Invite the bot to your server. Replace YOUR_CLIENT_ID with the
    Application ID from the General Information tab:

      https://discord.com/oauth2/authorize?client_id=YOUR_CLIENT_ID&scope=bot&permissions=268446737

    That permission number is exactly what this script needs and nothing more:
    view channels, send messages, manage channels, manage roles (needed to set
    a permission override), manage messages (needed to pin), create invite.

 4. Turn on Developer Mode in Discord (Settings -> Advanced), right-click your
    server -> Copy Server ID.

 5. Run it:
      python setup_discord.py --token <BOT_TOKEN> --guild <SERVER_ID>

    Or set TCG_DISCORD_TOKEN and TCG_DISCORD_GUILD in your environment and just
    run `python setup_discord.py`.

    Add --dry-run first if you want to see what it would do without touching
    anything.

 6. When it's done, kick the bot. It has no job after setup, and a bot sitting
    in a server with Manage Roles is a liability you don't need.

Needs nothing but Python 3.8+. No pip install.
"""

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request

API = "https://discord.com/api/v10"

# Permission bits, from Discord's docs.
VIEW_CHANNEL     = 1 << 10   # 1024
SEND_MESSAGES    = 1 << 11   # 2048
MANAGE_MESSAGES  = 1 << 13   # 8192
MANAGE_CHANNELS  = 1 << 4    # 16
MANAGE_ROLES     = 1 << 28   # 268435456
CREATE_INVITE    = 1 << 0    # 1

BOT_PERMISSIONS = (VIEW_CHANNEL | SEND_MESSAGES | MANAGE_MESSAGES
                   | MANAGE_CHANNELS | MANAGE_ROLES | CREATE_INVITE)

TYPE_TEXT = 0
TYPE_VOICE = 2

MAX_MESSAGE = 2000
MAX_TOPIC = 1024


# --------------------------------------------------------------- the content
# Edit freely. Lengths are checked before anything is sent.

CHANNELS = [
    {
        "name": "announcements",
        "type": TYPE_TEXT,
        "topic": "Builds, updates and known issues. Only I post here.",
        # @everyone can read and react but not post. The everyone role's id is
        # the guild id — that isn't a trick, it's how Discord models it.
        "lock_to_owner": True,
    },
    {
        "name": "general",
        "type": TYPE_TEXT,
        "topic": "Chat about the mod and the game. Say hello.",
    },
    {
        "name": "bug-reports",
        "type": TYPE_TEXT,
        "topic": ("One problem per message please, with your session report and log "
                  "attached. See the pinned message."),
    },
    {
        "name": "Playing",
        "type": TYPE_VOICE,
        "topic": None,   # voice channels take no topic
    },
]

PINS = {
    "bug-reports": """**How to report something**

One problem per message, and please attach these two files:

`TcgMultiplayer_Reports\\session_<date>_host.txt`  (or `_guest`)
`MelonLoader\\Latest.log`

Both live in the game folder — Steam, right-click The Coin Game, Manage, Browse local files. The session report is written automatically every time you finish playing, and there's a **Copy report** button in the panel (F9).

Then tell me:
• what you were doing
• whether you were hosting or joining
• what **each** of you saw

That last one matters more than it sounds. "I saw him at the machine, he says he was outside" is genuinely useful and I can't get it from one side.

If you can, get the files from **both** players. Half a multiplayer bug usually isn't enough to fix it.

**Stuck in a machine?** Press F11. That's not a bug report, that's the fix.""",

    "announcements": """**Welcome — this is a beta.**

The mod lets you play The Coin Game with two to four people. You share an island, take turns on the machines, watch each other play, and everyone keeps their own money.

**Before you play:** load your save and press **F10**. That runs a built-in self-check — seven tests, takes a couple of seconds — and among other things it proves, against your own save, that visiting someone else's island gives your progression back afterwards. Press **Copy result** and paste it in #general so I know your setup is healthy.

**Your save is backed up automatically** the first time you host or join, into a `TcgMultiplayer_SaveBackups` folder next to your save. Five copies are kept. Please go and look at that folder now, so you know where it is before you need it.

**Controls:** F9 panel · F10 self-check · F11 unstuck

**Everyone needs the same version of the mod and the same version of the game.** The mod checks both and will tell you if you don't match.

Honest status: everything that can be tested on one machine has been, and passes. What hasn't been properly exercised is two real computers talking to each other over Steam. That's what you're all here for. Expect to find things.

This is an unofficial fan mod, not affiliated with devotid or Kwalee.""",
}


# ------------------------------------------------------------------- plumbing

class Api:
    def __init__(self, token, dry_run=False):
        self.token = token
        self.dry_run = dry_run

    def __call__(self, method, path, body=None, allow=(200, 201, 204)):
        if self.dry_run and method != "GET":
            print("      (dry run — would {} {})".format(method, path))
            return {}

        url = API + path
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(url, data=data, method=method)
        req.add_header("Authorization", "Bot " + self.token)
        req.add_header("Content-Type", "application/json")
        req.add_header("User-Agent", "TcgMultiplayerSetup (https://nexusmods.com, 1.0)")

        for attempt in range(6):
            try:
                with urllib.request.urlopen(req, timeout=30) as r:
                    raw = r.read()
                    return json.loads(raw) if raw else {}
            except urllib.error.HTTPError as e:
                raw = e.read()
                # Discord asks you to back off rather than hammering it.
                if e.code == 429:
                    try:
                        wait = float(json.loads(raw).get("retry_after", 1.0))
                    except Exception:
                        wait = 1.0
                    print("      rate limited, waiting {:.1f}s".format(wait))
                    time.sleep(wait + 0.2)
                    continue
                if e.code in allow:
                    return {}
                raise ApiError(e.code, raw.decode("utf-8", "replace"), method, path)
            except urllib.error.URLError as e:
                raise SystemExit("Network error talking to Discord: {}".format(e.reason))
        raise SystemExit("Gave up after repeated rate limits.")


class ApiError(Exception):
    def __init__(self, code, body, method, path):
        self.code = code
        self.body = body
        super().__init__("{} {} -> HTTP {}: {}".format(method, path, code, body))


def explain(err):
    """Turn Discord's error codes into something actionable."""
    if err.code == 401:
        return ("The bot token was rejected. Copy it again from the Bot tab "
                "(Reset Token gives you a fresh one).")
    if err.code == 403:
        return ("The bot is missing a permission, or its role sits too low in the "
                "server's role list. Check it was invited with the link in this "
                "file's header, then drag its role near the top in "
                "Server Settings -> Roles.")
    if err.code == 404:
        return ("Server not found. Either the ID is wrong, or the bot was never "
                "invited to that server.")
    return None


# ---------------------------------------------------------------------- steps

def check_content():
    """Fail before touching Discord, not halfway through."""
    problems = []
    for ch in CHANNELS:
        t = ch.get("topic")
        if t and len(t) > MAX_TOPIC:
            problems.append("topic for #{} is {} chars (max {})".format(ch["name"], len(t), MAX_TOPIC))
    for name, text in PINS.items():
        if len(text) > MAX_MESSAGE:
            problems.append("pinned message for #{} is {} chars (max {})".format(name, len(text), MAX_MESSAGE))
    if problems:
        print("Content won't fit Discord's limits:")
        for p in problems:
            print("  - " + p)
        raise SystemExit(1)


def ensure_channels(api, guild_id, existing):
    """Create anything missing. Never touches a channel that already exists."""
    by_name = {c["name"].lower(): c for c in existing}
    result = {}

    for spec in CHANNELS:
        name = spec["name"]
        found = by_name.get(name.lower())
        if found:
            print("  = #{} already exists, left alone".format(name))
            result[name] = found
            continue

        body = {"name": name, "type": spec["type"]}
        if spec.get("topic") and spec["type"] == TYPE_TEXT:
            body["topic"] = spec["topic"]
        if spec.get("lock_to_owner"):
            # The @everyone role always has the same id as the guild.
            body["permission_overwrites"] = [{
                "id": str(guild_id),
                "type": 0,                       # 0 = role
                "deny": str(SEND_MESSAGES),
            }]

        made = api("POST", "/guilds/{}/channels".format(guild_id), body)
        print("  + created #{}{}".format(name, "  (read-only for everyone else)"
                                          if spec.get("lock_to_owner") else ""))
        result[name] = made or {"id": None, "name": name}

    return result


def pin(api, channel_id, message_id):
    """
    Discord has moved this route once. Try the current one, fall back to the
    classic one — cheaper than being wrong on someone else's machine.
    """
    for path in ("/channels/{}/messages/pins/{}".format(channel_id, message_id),
                 "/channels/{}/pins/{}".format(channel_id, message_id)):
        try:
            api("PUT", path)
            return True
        except ApiError as e:
            if e.code in (404, 405):
                continue
            raise
    return False


def post_and_pin(api, channels):
    for name, text in PINS.items():
        ch = channels.get(name)
        if not ch or not ch.get("id"):
            print("  ! no #{} to post in, skipping".format(name))
            continue

        cid = ch["id"]
        existing = api("GET", "/channels/{}/messages?limit=50".format(cid)) or []
        if any(m.get("pinned") for m in existing if isinstance(m, dict)):
            print("  = #{} already has a pinned message, left alone".format(name))
            continue

        msg = api("POST", "/channels/{}/messages".format(cid), {"content": text})
        mid = msg.get("id") if msg else None
        if mid and pin(api, cid, mid):
            print("  + posted and pinned in #{}".format(name))
        elif mid:
            print("  + posted in #{} (couldn't pin it — do that by hand)".format(name))
        else:
            print("  + posted in #{}".format(name))


def make_invite(api, channels):
    """A never-expiring invite. Discord's default is 7 days, which would
    quietly kill the link on the Nexus page a week after release."""
    target = channels.get("general") or next(iter(channels.values()), None)
    if not target or not target.get("id"):
        return None
    inv = api("POST", "/channels/{}/invites".format(target["id"]),
              {"max_age": 0, "max_uses": 0, "unique": True})
    code = inv.get("code") if inv else None
    return "https://discord.gg/" + code if code else None


# ----------------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description="Set up the Coin Game Multiplayer Discord server.")
    ap.add_argument("--token", default=os.environ.get("TCG_DISCORD_TOKEN"),
                    help="Bot token (or set TCG_DISCORD_TOKEN)")
    ap.add_argument("--guild", default=os.environ.get("TCG_DISCORD_GUILD"),
                    help="Server ID (or set TCG_DISCORD_GUILD)")
    ap.add_argument("--dry-run", action="store_true",
                    help="Say what would happen, change nothing")
    args = ap.parse_args()

    if not args.token or not args.guild:
        ap.print_usage()
        print("\nNeed a bot token and a server ID. Read the top of this file — "
              "it walks through both, and it's a five-minute job.")
        raise SystemExit(2)

    check_content()

    api = Api(args.token.strip(), dry_run=args.dry_run)
    if args.dry_run:
        print("DRY RUN — nothing will be changed.\n")

    try:
        me = api("GET", "/users/@me")
        print("Bot: {}#{}".format(me.get("username", "?"), me.get("discriminator", "0")))

        guild = api("GET", "/guilds/{}".format(args.guild))
        print("Server: {}\n".format(guild.get("name", args.guild)))

        print("Channels")
        existing = api("GET", "/guilds/{}/channels".format(args.guild)) or []
        channels = ensure_channels(api, args.guild, existing)

        print("\nPinned messages")
        if args.dry_run:
            for name in PINS:
                print("      (dry run — would post and pin in #{})".format(name))
        else:
            post_and_pin(api, channels)

        print("\nInvite link")
        if args.dry_run:
            print("      (dry run — would create a never-expiring invite)")
        else:
            link = make_invite(api, channels)
            print("  " + (link or "  couldn't create one — make it by hand, and set"
                                  " Expire After: Never"))
            if link:
                print("\n  That one never expires and has unlimited uses, so it's safe")
                print("  to put on the Nexus page and in the Steam thread.")

    except ApiError as e:
        print("\nDiscord said no.\n  {}".format(e))
        hint = explain(e)
        if hint:
            print("\n  " + hint)
        raise SystemExit(1)

    print("\nDone.")
    if not args.dry_run:
        print("Now kick the bot — it has no job after this, and one sitting in your")
        print("server holding Manage Roles is a liability you don't need.")


if __name__ == "__main__":
    main()
