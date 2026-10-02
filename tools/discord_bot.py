#!/usr/bin/env python3
"""Stembridge Valley Discord bot.

/play                 -> privately sends the member their personal invite code (same code every time).
/play friend:@someone -> same, and a brand-new player is placed on that friend's farm if it has room.
/newcode              -> replaces your code (if you think someone else saw it).

Writes the game server's discord-players.json:
  {"tokens": {TOKEN: {"id": DISCORD_ID, "name": NAME, "friend": FRIEND_ID|null}}, "revoked": [DISCORD_ID, ...]}
Banned members and members who left are listed as revoked; the game turns them away and kicks them if online.

Config (environment):
  SV_BOT_TOKEN_FILE  file with the bot token
  SV_STATE_DIR       the game server's state folder
  SV_PUBLIC_ADDRESS  host:port players connect to
  SV_GUILD_ID        optional: only answer in this Discord server
"""
import json, logging, os, secrets, tempfile
from pathlib import Path

import discord
from discord import app_commands
from discord.ext import tasks

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("svbot")

STATE = Path(os.environ["SV_STATE_DIR"])
ROSTER = STATE / "discord-players.json"
ADDRESS = os.environ["SV_PUBLIC_ADDRESS"]
GUILD_ID = int(os.environ.get("SV_GUILD_ID") or 0) or None
TOKEN = Path(os.environ["SV_BOT_TOKEN_FILE"]).read_text().strip()


def load():
    try:
        data = json.loads(ROSTER.read_text())
    except (FileNotFoundError, json.JSONDecodeError):
        data = {}
    data.setdefault("tokens", {})
    data["revoked"] = list(dict.fromkeys(data.get("revoked", [])))
    return data


def save(data):
    STATE.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=STATE, prefix=".discord-players.")
    with os.fdopen(fd, "w") as f:
        json.dump(data, f, indent=2)
    os.chmod(tmp, 0o640)
    os.replace(tmp, ROSTER)  # atomic: the game never reads a half-written file


def token_for(data, uid):
    for tok, p in data["tokens"].items():
        if p["id"] == uid:
            return tok
    return None


def code(uid, tok):
    # The Discord ID tells the player's game which character is theirs; the token proves it.
    return f"sv:{ADDRESS}/d-{uid}-{tok}"


def set_revoked(uid, revoked):
    data = load()
    r = set(data["revoked"])
    if revoked == (uid in r):
        return
    (r.add if revoked else r.discard)(uid)
    data["revoked"] = sorted(r)
    save(data)
    log.info("%s %s", "revoked" if revoked else "restored", uid)


intents = discord.Intents.default()  # no privileged intents: bans arrive as events; departures are polled below
client = discord.Client(intents=intents)
tree = app_commands.CommandTree(client)

HOW_TO = (
    "**How to play**\n"
    "1. Download **Play-Stembridge-Valley.exe**: https://github.com/Stembridge10/stembridge-valley/releases/latest\n"
    "2. You need Stardew Valley (PC) and SMAPI (https://smapi.io).\n"
    "3. Run the launcher and paste your code when it asks.\n"
    "Keep your code to yourself: it's your character's key."
)


def ensure_player(member, friend=None, fresh=False):
    uid = str(member.id)
    data = load()
    tok = token_for(data, uid)
    if tok and fresh:
        del data["tokens"][tok]
        tok = None
    if not tok:
        tok = secrets.token_urlsafe(18)
        data["tokens"][tok] = {"id": uid, "name": member.display_name, "friend": None}
    p = data["tokens"][tok]
    p["name"] = member.display_name
    if friend is not None:
        p["friend"] = str(friend.id) if friend.id != member.id else None
    data["revoked"] = [r for r in data["revoked"] if r != uid]
    save(data)
    return tok


def allowed(inter):
    return inter.guild is not None and (GUILD_ID is None or inter.guild.id == GUILD_ID)


@tree.command(name="play", description="Get your personal Stembridge Valley invite code (sent privately).")
@app_commands.describe(friend="Optional: start on this friend's farm (if it has room).")
async def play(inter: discord.Interaction, friend: discord.Member | None = None):
    if not allowed(inter):
        await inter.response.send_message("Use this in the Stembridge Valley Discord server.", ephemeral=True)
        return
    tok = ensure_player(inter.user, friend)
    extra = f"\nNew players are placed on **{friend.display_name}**'s farm if it has room." if friend else ""
    await inter.response.send_message(f"Your invite code:\n```{code(str(inter.user.id), tok)}```{extra}\n\n{HOW_TO}", ephemeral=True)
    log.info("code for %s (%s)", inter.user, inter.user.id)


@tree.command(name="newcode", description="Replace your invite code (if someone else saw it).")
async def newcode(inter: discord.Interaction):
    if not allowed(inter):
        await inter.response.send_message("Use this in the Stembridge Valley Discord server.", ephemeral=True)
        return
    tok = ensure_player(inter.user, fresh=True)
    await inter.response.send_message(
        f"Your new code (the old one no longer works):\n```{code(str(inter.user.id), tok)}```\nPaste it into the launcher when it asks.",
        ephemeral=True)


@client.event
async def on_member_ban(guild, user):
    if GUILD_ID is None or guild.id == GUILD_ID:
        set_revoked(str(user.id), True)


@client.event
async def on_member_unban(guild, user):
    if GUILD_ID is None or guild.id == GUILD_ID:
        set_revoked(str(user.id), False)


async def still_member(guild, uid):
    try:
        await guild.fetch_member(int(uid))
        return True
    except discord.NotFound:
        return False


async def reconcile():
    """Anyone with a code who isn't in the server any more (left, kicked or banned) is revoked; anyone back is restored."""
    guilds = [g for g in client.guilds if GUILD_ID is None or g.id == GUILD_ID]
    if not guilds:
        return
    data = load()
    known = {p["id"] for p in data["tokens"].values()}
    gone = set()
    for uid in known:
        try:
            here = any([await still_member(g, uid) for g in guilds])
        except discord.HTTPException as e:
            log.warning("member check failed for %s: %s", uid, e)
            here = uid not in data["revoked"]
        if not here:
            gone.add(uid)
    new = sorted((set(data["revoked"]) - known) | gone)
    if new != sorted(data["revoked"]):
        data = load()
        data["revoked"] = new
        save(data)
    log.info("reconciled: %d players, %d not allowed", len(known), len(gone))


@tasks.loop(minutes=3)
async def periodic():
    try:
        await reconcile()
    except Exception:
        log.exception("reconcile failed")


@client.event
async def on_ready():
    for g in client.guilds:
        if GUILD_ID is None or g.id == GUILD_ID:
            tree.copy_global_to(guild=g)
            await tree.sync(guild=g)
    log.info("ready as %s in %s", client.user, [g.name for g in client.guilds])
    if not periodic.is_running():
        periodic.start()


@client.event
async def on_guild_join(guild):
    tree.copy_global_to(guild=guild)
    await tree.sync(guild=guild)
    log.info("joined %s", guild.name)


if __name__ == "__main__":
    client.run(TOKEN, log_handler=None)
