# Stembridge Valley: game design plan

Owner decisions (Oct 2026). Stardew farming stays the core; MMO loops sit on top.
Status: progression drafted; calendar, marriage and festival decisions made Oct 2 (below).

## World
- One shared world: one town, one clock, one set of seasons. **~14 real minutes per in-game day** (normal Stardew speed; decided Oct 2).
- Several farms, **4 players each**. No main farmhouse (the hidden server's house is removed); each farm has only the 4 player cabins.
- Farms connect to town by paths, so anyone can walk over and visit. Only a farm's own 4 players can harvest, open chests, or build there.
- Each farm has its own invite code so friend groups land together; solo joiners go to any farm with a free spot.
- Load test (Oct 2): one server held 20 bot players at ~3 ms of a 16.7 ms tick; overnight save ~10 s at 20 players.

## Progression direction (decided Oct 3, overrides the town/farm project sections below where they differ)
- Goal: a game that lasts forever and is grindy in a fun way, RuneScape-style. Late joiners must have as much to do as veterans.
- **Farm projects are the main progression.** Long chain per farm; never "finished".
- **No gating:** farm projects add rewards and bonuses on top of vanilla; nothing vanilla allows gets locked behind them.
- **OSRS-style skill grinding is the core long-term loop** (owner, Oct 3): long personal skill levels with unlocks along the way.
  Progression is personal, so a late joiner starts at level 1 like everyone did; a finished town doesn't matter.
- **Rejected: RS3-style timed town events** (owner: players wouldn't like them).
- **Personal/farm unlocks instead of town unlocks:** each farm can earn its own quarry attached to the farm; the town quarry stays open as a public one. The desert bus is a personal unlock (a bus pass), not a server-wide repair.
- **Skills (agreed Oct 3):** vanilla 1-10 unchanged, then on to **50** on an OSRS-style rising XP curve (50 takes months).
  Cap can be raised later. Stretch the 5 Stardew skills first; new skills (Cooking, Ranching...) later.
  Something worthwhile about every 5 levels (farm quarry, bus pass, rare seeds, farm fishing spot, forest patch), skillcape at 50,
  Discord hiscores and milestone announcements. Vanilla level 5/10 professions stay.
- First build: leveling system + Mining (farm quarry unlock).
- Idea list offered Oct 3 (owner likes WoW and OSRS), not yet approved: skilling pets, collection log, clue scrolls,
  area achievement diaries, raids with weekly lockout, villager factions, horse mounts/cosmetics, titles, ironman mode.

## Build order
1. **4-player farms** (above).
2. **Projects**: farm projects (per farm) and town projects (server-wide Community Center).
3. **Contracts**: daily and weekly server-wide orders with a farm ranking.
4. **Market**: player trading; prices fall as supply rises, so farms specialize.
5. **Co-op dungeons**: group Skull Cavern runs, weekly boss, shared loot, depth board.

## Villagers
- Friendship is replaced by **town reputation**, earned from projects and contracts. No gift grind.
- Vanilla friendship mainly gives villager recipes, mailed gifts, heart events and marriage.
  (Robin discounts and Wizard buildings are NOT friendship unlocks in vanilla; corrected from the earlier note.)
- Reputation unlocks those recipes plus market access and better contract tiers. Villagers stay as shopkeepers and quest givers.
- **Decided:** players can marry each other; villager marriage is off (one Abigail can't marry 20 players).

## The key fact behind progression: the calendar runs fast (superseded in part, see Day length below)
One in-game day is ~1 real hour, so the calendar moves ~24 days per real day: a season every ~28 real hours,
a full year in under 5 real days. Nobody plays 24 hours a day, so:
- **Progression is paced in real days/weeks, not seasons.** Unlock gates use project items, levels and reputation, never dates.
- **Sprinklers are the first big goal.** Crops only grow on days they're watered, so automation is what lets a farm grow while you sleep.
- **Decided: keep 1-hour days** (owner asked for the recommendation). Reason: a 2-3 hour session still spans 2-3 in-game
  days, so crops visibly grow while you play. Slower days (2-3 h) were rejected before for exactly that. Pausing when
  empty doesn't work once several farms in different time zones share one clock.
- **Decided: festivals happen on their normal in-game day**, not on a fixed real-time schedule. Tradeoff: they land at
  whatever real hour that day falls on, so many players will miss a given one. Two festivals per season means one
  roughly every 14 real hours.
- Technical: vanilla blocks new players from joining during a festival or wedding (GameServer.isGameAvailable).
  The server must not lock joins for that long, and the hidden host has to handle festival start/end on its own.

## Day length (decided Oct 2, replaces the 1-hour decision)
- Normal Stardew speed, ~14 real minutes per day. A season is ~6.5 real hours, a year ~26 real hours.
- Crops only grow on days they were watered (vanilla) and never die at season change, so an unwatered farm simply waits.
  Sprinklers keep growing while you're away, but ripe crops and finished machines wait for pickup, which caps offline gains.
- Cost: the overnight save freeze happens every 14 minutes (~10 s seen at 20 players). Work on shortening it.
- Festivals happen on their normal day, about every 3 real hours.

## Crop items (decided Oct 2, revised)
- Revive Tonic only: brings back a regrowing crop that ended at the season change. Earned from contracts/dungeons, tradeable.
- Dropped by owner: Grow Tonic and blight/storms.

## Ideas log (fun at 1 player or 20)
Every loop must work solo and scale with more people. Candidates, not yet approved:
- (add as found)

## Example: four friends join (Maya, Jake, Sam, Lee)
Server has run a few weeks. Three farms exist; it's Fall, Year 3. The Pantry town project is already done.

**Day 1, first 10 minutes**
- Join the Discord, get a fresh farm's invite code. Download the launcher, paste the code.
- Launcher installs the mods, opens Stardew, connects. Make a character, wake up in one of 4 cabins on "Cedar Farm".
- Mailbox: starter tools and **in-season** starter seeds (vanilla gives parsnips even in fall; fix this).
- Town board shows: the farm's first project, today's contracts, current town project.

**Day 1 evening (2-3 hours, roughly 3 in-game days)**
- Clear land, plant, fish, mines floors 1-10, ship produce: ordinary early Stardew.
- Farm project **Break Ground** (wood, stone, fiber) about half done.
- Daily contracts give first gold and reputation (rank: Newcomer).
- They walk over to an older farm and see what a finished farm looks like.

**Days 2-4**
- Farming 2 gives basic sprinklers. The group pools copper so crops grow overnight.
- Break Ground done: Robin can now build a Coop and Barn on Cedar Farm.
- Mines reach about floor 40 (iron). Roles appear: Maya farms, Jake mines, Sam fishes, Lee runs animals.
- Rank: Local. Market opens to them; Jake sells spare iron to other farms.

**Week 2**
- Level 5 professions: each player effectively picks a class.
- Farm project **Greenhouse** (available because the server finished the Pantry). They complete it.
- Weekly contract (e.g. "Harvest Drive: 5,000 pumpkins for the town"); Cedar places 3rd of 4 farms.
- They chip into the current town project, the **Vault**, and appear on the contribution board.

**Weeks 3-4**
- Jake reaches mine floor 120: Skull Key.
- Server finishes the Vault: bus to the Desert is repaired for everyone.
- Friday night: group Skull Cavern run. Lee's Deluxe Barn auto-feeds the animals.
- Rank: Valued. Villager recipes arrive in the mail.

**Month 2 and beyond**
- Level 10 professions, iridium tools, quality sprinklers everywhere, wine and ancient fruit economy.
- Town project chain continues: Willy's boat, then Ginger Island as the server's endgame zone.
- Weekly boss, mine-depth board, monthly leaderboard reset with a new town project chain.
- New farms arrive. Town unlocks already apply to them, so they catch up faster; veterans sell them gear on the market.

## Farm projects (per farm, the 4 players share them)
1. Break Ground: wood/stone/fiber. Unlocks Coop and Barn at Robin.
2. First Fields: a few different crops. Opens more farm land (if the farm map supports staged expansion).
3. Livestock: eggs, milk, wool. Unlocks bigger animal buildings.
4. Greenhouse: needs the town Pantry first. Restores this farm's greenhouse.
5. Workshop: bars and machines. Unlocks fish ponds, sheds, and the like.
6. Wizard's Favor: late-game items. Farm obelisks and Gold Clock.

## Town projects (server-wide Community Center, amounts scaled to the server)
Same vanilla rooms and rewards, sized for every farm contributing:
- Crafts Room: quarry bridge.  - Pantry: greenhouses become a farm project for every farm.
- Fish Tank: glittering boulder.  - Boiler Room: minecarts.
- Bulletin Board: reputation boost for everyone.  - Vault: bus to the Desert (Skull Cavern).
- Then Willy's boat and Ginger Island.

## Contracts
- Daily: small, soloable. Gold plus reputation.
- Weekly: big server orders; farms ranked; top farms get rare seeds or items.

## What stays vanilla
Farming, skills, professions, tools, mines, fishing, crafting, cooking, shops, animals, Skull Cavern.

## Rule changes this design needs
- Regrowing crops (blueberries, cranberries, corn, etc.): survive until first harvest, then die at the next season
  change. The current "crops survive" rule lets them produce forever.
- Starter seeds match the current season.
- No animal mood or neglect penalty while all 4 of a farm's players are offline. Crops still need sprinklers or rain.

## Anti-cheat rule for every loop
Anything shared or competitive (rankings, market, contracts, town projects) is scored and stored by the server, never trusted from a player's game. Server keeps an audit log of contributions and trades.
