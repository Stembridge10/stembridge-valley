# Stembridge Valley: game design plan

Owner decisions (Oct 2026). Stardew farming stays the core; MMO loops sit on top.

## World
- One shared world: one town, one clock, one set of seasons. ~60 real minutes per in-game day.
- Several farms, **4 players each**. No main farmhouse (the hidden server's house is removed); each farm has only the 4 player cabins.
- Farms connect to town by paths, so anyone can walk over and visit. Only a farm's own 4 players can harvest, open chests, or build there.
- Each farm has its own invite code so friend groups land together; solo joiners go to any farm with a free spot.
- Load test (Oct 2): one server held 20 bot players at ~3 ms of a 16.7 ms tick; overnight save ~10 s at 20 players.

## Build order
1. **4-player farms** (above).
2. **Projects**
   - Farm projects: per-farm bundles that unlock expansions, buildings, greenhouse, upgrades.
   - Town projects: server-wide Community Center; every farm contributes; big unlocks for everyone; contribution board.
3. **Weekly town contracts**: server-wide orders with rewards and a farm ranking.
4. **Market**: player trading; prices fall as supply rises, so farms specialize.
5. **Co-op dungeon runs**: group Skull Cavern trips, weekly boss, shared loot, mine-depth board.

## Villagers
- Friendship is replaced by **town reputation**, earned from projects and contracts. No gift grind.
- Everything friendship used to unlock (recipes, Robin discounts, Wizard buildings, etc.) is gated on reputation instead.
- Villagers stay as shopkeepers and quest givers.

## Anti-cheat rule for every loop
Anything shared or competitive (rankings, market, contracts, town projects) is scored and stored by the server, never trusted from a player's game. Server keeps an audit log of contributions and trades.
