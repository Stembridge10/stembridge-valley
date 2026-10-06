# Junimo Hollow

A small test of a shared, always-on Stardew Valley world: one server running on Stembridge's PC, friends join by address.

## For players

1. Install Stardew Valley and [SMAPI](https://smapi.io) (the normal mod loader).
2. Download **Play-Junimo-Hollow.exe** from the [latest release](https://github.com/Stembridge10/stembridge-valley/releases/latest).
3. Run it and paste the invite code Stembridge sent you (looks like `sv:1.2.3.4:24642/acorn-berry-fig-42`).
4. Pick a free cabin and make your farmer. Next time, just run the launcher again.

The launcher keeps its own mod folder in `%LOCALAPPDATA%\StembridgeValley\Mods`. Your normal Stardew mods and Vortex setup are not touched.
When the server gets new mods, the launcher updates automatically and rejoins.

Windows may warn that the launcher is from an unknown publisher (it isn't code-signed). Choose **More info → Run anyway**.

## Rules in this test

- A day lasts about an hour of real time, and the clock always runs, even with nobody online.
- Your energy carries over between days; it doesn't refill at 6am.
- Get energy back by resting: sitting on chairs or benches is fast, standing still is slow, and using a bed is a nap that fully refills you without ending the day.
- If you were away for 20 minutes or more, you come back fully rested.
- 24-hour days: the clock runs all night and the new day starts at 6am, right where you stand (a short save, then keep playing).
- Skull Cavern dives are timed (8 minutes); food with a buff adds a minute, up to 5 extra.
- Crops don't die when the season changes; they keep growing until harvest.
- Friendships don't drop when you're offline.

## For the server owner

See [docs/SERVER.md](docs/SERVER.md).

## What's in here

- `src/StembridgeValley` — the SMAPI mod (rules, server password, version check)
- `src/Host` — runs a hidden copy of Stardew as the server (own saves, invisible desktop, low priority)
- `src/Launcher` — the player launcher
- `tools/` — pack builder, publisher and the automated end-to-end test
