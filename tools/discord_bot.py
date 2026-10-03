#!/usr/bin/env python3
"""Stembridge Valley Discord bot.

/play            -> privately sends the member their personal invite code (same code every time).
                    A new player gets a farm of their own.
/invite @friend  -> puts a friend (who hasn't made a farmer yet) on your farm, up to 4 people.
/farm            -> who lives on your farm.
/newcode         -> replaces your code (if you think someone else saw it).

Writes the game server's discord-players.json:
  {"tokens": {TOKEN: {"id": DISCORD_ID, "name": NAME, "friend": INVITER_ID|null}}, "revoked": [DISCORD_ID, ...]}
and reads the server's farm-status.json ({FARM: {"name", "members": [{"key": "discord-ID", "started"}]}}).
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
FARMS = STATE / "farm-status.json"
FARM_SIZE = 4
ADDRESS = os.environ["SV_PUBLIC_ADDRESS"]
GUILD_ID = int(os.environ.get("SV_GUILD_ID") or 0) or None
# Only servers Stembridge owns count, so nobody can add the bot to their own server and hand out codes.
OWNER_ID = int(os.environ.get("SV_OWNER_ID") or 0) or None


def allowed_guild(g):
    return g is not None and (GUILD_ID is None or g.id == GUILD_ID) and (OWNER_ID is None or g.owner_id == OWNER_ID)
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


def farms():
    try:
        return json.loads(FARMS.read_text())
    except (FileNotFoundError, json.JSONDecodeError):
        return {}


def farm_of(uid, status=None):
    """(farm id, farm entry, member entry) for a Discord ID, or (None, None, None)."""
    for fid, f in (status if status is not None else farms()).items():
        for m in f["members"]:
            if m["key"] == f"discord-{uid}":
                return fid, f, m
    return None, None, None


def name_of(data, key):
    uid = key.removeprefix("discord-")
    for p in data["tokens"].values():
        if p["id"] == uid:
            return p["name"]
    return "someone"


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


def ensure_player(member, invited_by=None, fresh=False):
    uid = str(member.id)
    data = load()
    tok = token_for(data, uid)
    if tok and fresh:
        old = data["tokens"].pop(tok)
        tok = None
    else:
        old = None
    if not tok:
        tok = secrets.token_urlsafe(18)
        data["tokens"][tok] = {"id": uid, "name": member.display_name, "friend": (old or {}).get("friend")}
    p = data["tokens"][tok]
    p["name"] = member.display_name
    if invited_by is not None:
        p["friend"] = str(invited_by.id)
    data["revoked"] = [r for r in data["revoked"] if r != uid]
    save(data)
    return tok


def allowed(inter):
    return allowed_guild(inter.guild)


@tree.command(name="play", description="Get your personal Stembridge Valley invite code (sent privately).")
async def play(inter: discord.Interaction):
    if not allowed(inter):
        await inter.response.send_message("Use this in the Stembridge Valley Discord server.", ephemeral=True)
        return
    tok = ensure_player(inter.user)
    data = load()
    fid, farm, me = farm_of(inter.user.id)
    p = data["tokens"][tok]
    if farm and me["started"]:
        where = f"You live on **{farm['name']}**."
    elif p.get("friend") and farm_of(p["friend"])[1]:
        host = farm_of(p["friend"])[1]
        where = f"You'll start on **{host['name']}** with {name_of(data, 'discord-' + p['friend'])} (if it still has room)."
    else:
        where = "You'll get a farm of your own. Bring friends onto it with **/invite**."
    await inter.response.send_message(f"Your invite code:\n```{code(str(inter.user.id), tok)}```{where}\n\n{HOW_TO}", ephemeral=True)
    log.info("code for %s (%s)", inter.user, inter.user.id)


@tree.command(name="invite", description="Invite a friend to live on your farm (up to 4 people).")
@app_commands.describe(friend="Who to invite. They must not have made a farmer yet.")
async def invite(inter: discord.Interaction, friend: discord.Member):
    if not allowed(inter):
        await inter.response.send_message("Use this in the Stembridge Valley Discord server.", ephemeral=True)
        return
    status = farms()
    fid, farm, _ = farm_of(inter.user.id, status)
    if not farm:
        await inter.response.send_message("You don't have a farm yet: type **/play**, join the game once, then invite friends.", ephemeral=True)
        return
    if friend.bot or friend.id == inter.user.id:
        await inter.response.send_message("Pick a friend to invite.", ephemeral=True)
        return
    ffid, ffarm, fme = farm_of(friend.id, status)
    if ffid == fid:
        await inter.response.send_message(f"{friend.display_name} already lives on your farm.", ephemeral=True)
        return
    if fme and fme["started"]:
        await inter.response.send_message(
            f"{friend.display_name} already has a farmer on **{ffarm['name']}**, so they can't move to yours. "
            "Moving farms isn't possible yet.", ephemeral=True)
        return
    if len(farm["members"]) >= FARM_SIZE:
        await inter.response.send_message(f"**{farm['name']}** is full ({FARM_SIZE}/{FARM_SIZE}).", ephemeral=True)
        return
    ensure_player(friend, invited_by=inter.user)
    await inter.response.send_message(
        f"{friend.mention}, {inter.user.display_name} invited you to live on **{farm['name']}**! "
        "Type **/play** to get your code. You'll start there with your own cabin.")
    log.info("%s invited %s to %s", inter.user.id, friend.id, fid)


@tree.command(name="farm", description="See who lives on your farm.")
async def farm_cmd(inter: discord.Interaction):
    if not allowed(inter):
        await inter.response.send_message("Use this in the Stembridge Valley Discord server.", ephemeral=True)
        return
    fid, farm, _ = farm_of(inter.user.id)
    if not farm:
        await inter.response.send_message("You don't have a farm yet: type **/play** and join the game.", ephemeral=True)
        return
    data = load()
    names = [name_of(data, m["key"]) + ("" if m["started"] else " (not started yet)") for m in farm["members"]]
    await inter.response.send_message(
        f"**{farm['name']}** ({len(names)}/{FARM_SIZE}): {', '.join(names)}"
        + ("" if len(names) >= FARM_SIZE else "\nInvite a friend with **/invite**."), ephemeral=True)


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
    if allowed_guild(guild):
        set_revoked(str(user.id), True)


@client.event
async def on_member_unban(guild, user):
    if allowed_guild(guild):
        set_revoked(str(user.id), False)


async def still_member(guild, uid):
    try:
        await guild.fetch_member(int(uid))
        return True
    except discord.NotFound:
        return False


async def reconcile():
    """Anyone with a code who isn't in the server any more (left, kicked or banned) is revoked; anyone back is restored."""
    guilds = [g for g in client.guilds if allowed_guild(g)]
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
    for g in list(client.guilds):
        if allowed_guild(g):
            tree.copy_global_to(guild=g)
            await tree.sync(guild=g)
        else:
            log.warning("leaving %s: not Stembridge's server", g.name)
            await g.leave()
    log.info("ready as %s in %s", client.user, [g.name for g in client.guilds])
    if not periodic.is_running():
        periodic.start()


@client.event
async def on_guild_join(guild):
    if not allowed_guild(guild):
        log.warning("leaving %s: not Stembridge's server", guild.name)
        await guild.leave()
        return
    tree.copy_global_to(guild=guild)
    await tree.sync(guild=guild)
    log.info("joined %s", guild.name)


if __name__ == "__main__":
    client.run(TOKEN, log_handler=None)
