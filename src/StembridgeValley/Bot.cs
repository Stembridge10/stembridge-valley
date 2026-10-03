using StardewValley.Buildings;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Pathfinding;
using StardewValley.TerrainFeatures;

namespace StembridgeValley;

/// <summary>
/// Load-test bot. Only active when the test harness sets SV_BOT=1; does nothing in normal play.
/// Plays like a busy person: walks around, changes maps, hoes/waters/plants, chats, and clicks through menus.
/// Skips drawing (no one is watching) and writes state/bot.csv every 10 seconds: ping, tick rate, where it is.
/// </summary>
internal static class Bot
{
    private static readonly Random rng = new();
    private static readonly string[] Places = { "Farm", "Farm", "Farm", "Town", "BusStop", "Forest", "Mountain", "Beach" };
    private static readonly string[] Lines = { "hi", "anyone want parsnips?", "heading to town", "nice day", "brb", "lol", "where's the mine", "gm" };
    private static int nextActionTick, actions, ticks, warps, farmed, chats, drawCounter, visits, blocked, leaks, homeTrips, homeMisses;
    private static DateTime windowStart = DateTime.UtcNow;

    public static void Apply(IModHelper helper, Harmony harmony)
    {
        if (Environment.GetEnvironmentVariable("SV_BOT") != "1")
            return;
        Log.Warn("LOAD-TEST BOT ACTIVE");
        harmony.Patch(AccessTools.Method(typeof(Game1), "Draw", new[] { typeof(GameTime) }),
            prefix: new HarmonyMethod(typeof(Bot), nameof(Draw_Prefix)));
        helper.Events.GameLoop.UpdateTicked += OnTicked;
        SV.WriteFlag("bot.csv", "time,connected,location,ping_ms,tps,actions,warps,farmed,chats,mem_mb,home,visits,blocked,leaks,home_trips,home_misses\n");
    }

    private static bool Draw_Prefix() => Game1.activeClickableMenu is SaveGameMenu || ++drawCounter % 60 == 0;

    private static void OnTicked(object? sender, UpdateTickedEventArgs e)
    {
        ticks++;
        if ((DateTime.UtcNow - windowStart).TotalSeconds >= 10)
            Report();

        if (!Context.IsWorldReady)
            return;
        ClearPopups();
        if (pendingHomeCheck != null && Context.IsPlayerFree && Game1.locationRequest == null)
        {
            homeTrips++;
            if (Game1.currentLocation?.Name != pendingHomeCheck) homeMisses++;
            pendingHomeCheck = null;
        }
        if (!Context.IsPlayerFree || Game1.player.controller != null || e.Ticks < nextActionTick)
            return;
        if (Environment.GetEnvironmentVariable("SV_BOT_DOOR_CHECK") == "1" && DoorCheck(e.Ticks))
            return;

        nextActionTick = (int)e.Ticks + rng.Next(180, 480); // every 3-8 seconds
        actions++;
        try
        {
            int roll = rng.Next(100);
            if (roll < 45) Walk();
            else if (roll < 58) Warp();
            else if (Farms.Enabled && roll < 68) Visit();
            else if (roll < 92) Farm();
            else Chat();
        }
        catch (Exception ex)
        {
            Log.Debug($"[bot] action failed: {ex.Message}");
        }
    }

    private static int doorStage, doorWait;

    /// <summary>Test: walk out of our own cabin through its real door warp, report where we land, then photograph the farm.</summary>
    private static bool DoorCheck(uint tick)
    {
        switch (doorStage)
        {
            case 0 when Game1.currentLocation is StardewValley.Locations.Cabin cabin:
            {
                Warp w = cabin.warps.First();
                Building? b = cabin.ParentBuilding;
                Point door = b == null ? Point.Zero : new Point(b.tileX.Value + b.humanDoor.X, b.tileY.Value + b.humanDoor.Y + 1);
                Log.Info($"[doorcheck] cabin exit warp -> {w.TargetName} {w.TargetX},{w.TargetY}; door front {door.X},{door.Y}");
                Game1.player.warpFarmer(w);
                doorStage = 1;
                return true;
            }
            case 0:
                return false;
            case 1 when Game1.currentLocation is StardewValley.Locations.Cabin || Game1.locationRequest != null:
                return true; // still walking out
            case 1:
                Log.Info($"[doorcheck] after leaving: {Game1.currentLocation?.Name} {Game1.player.TilePoint.X},{Game1.player.TilePoint.Y}");
                var b2 = Game1.currentLocation?.buildings.Where(x => x.isCabin).OrderBy(x => x.tileX.Value)
                    .Select(x => $"{x.tileX.Value},{x.tileY.Value}");
                Log.Info($"[doorcheck] cabins here: {string.Join(" ", b2 ?? Array.Empty<string>())}");
                Log.Info($"[doorcheck] mailbox bubble at {Game1.player.getMailboxPosition().X},{Game1.player.getMailboxPosition().Y}; own cabin mailbox {string.Join(" ", Game1.currentLocation!.buildings.Where(b => b.isCabin && b.HasIndoorsName(Game1.player.homeLocation.Value)).Select(b => $"{b.getMailboxPosition().X},{b.getMailboxPosition().Y}"))}");
                Game1.game1.takeMapScreenshot(0.25f, "cabin-row", () => Log.Info("[doorcheck] screenshot saved"));
                doorStage = 2;
                return true;
            case 2:
                // and back in through the door
                var mine = Game1.currentLocation?.buildings.FirstOrDefault(x => x.isCabin && x.GetIndoors() is StardewValley.Locations.Cabin c && c.owner == Game1.player);
                if (mine != null)
                {
                    Point d = new(mine.tileX.Value + mine.humanDoor.X, mine.tileY.Value + mine.humanDoor.Y);
                    bool ok = mine.doAction(new Vector2(d.X, d.Y), Game1.player);
                    Log.Info($"[doorcheck] knocked on own door at {d.X},{d.Y}: {ok}");
                }
                doorStage = 3;
                return true;
            case 3 when Game1.currentLocation is not StardewValley.Locations.Cabin && ++doorWait < 600:
                return true; // still walking in
            case 3:
                Log.Info($"[doorcheck] after entering: {Game1.currentLocation?.Name}");
                doorStage = 4;
                return true;
        }
        return doorStage < 4;
    }

    private static void ClearPopups()
    {
        switch (Game1.activeClickableMenu)
        {
            case LevelUpMenu lvl:
                lvl.okButtonClicked();
                break;
            case DialogueBox box:
                box.closeDialogue();
                break;
            case LetterViewerMenu or ShippingMenu or QuestLog or ItemGrabMenu:
                Game1.exitActiveMenu();
                break;
        }
        if (Game1.CurrentEvent is { skippable: true, skipped: false } ev && !Game1.isFestival())
            ev.skipEvent();
    }

    private static void Walk()
    {
        GameLocation loc = Game1.currentLocation;
        Point here = Game1.player.TilePoint;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            var target = new Point(here.X + rng.Next(-15, 16), here.Y + rng.Next(-15, 16));
            if (!loc.isTileOnMap(target.X, target.Y) || !loc.isTilePassable(new Vector2(target.X, target.Y)) || !loc.CanItemBePlacedHere(new Vector2(target.X, target.Y)))
                continue;
            var path = new PathFindController(Game1.player, loc, target, rng.Next(4));
            if (path.pathToEndPoint is { Count: > 0 })
            {
                Game1.player.controller = path;
                return;
            }
        }
    }

    private static void Warp()
    {
        string name = Places[rng.Next(Places.Length)];
        if (name == Game1.currentLocation.NameOrUniqueName)
            name = "Town";
        int x = 0, y = 0;
        Utility.getDefaultWarpLocation(name, ref x, ref y);
        if (x == 0 && y == 0)
            return;
        Game1.warpFarmer(name, x, y, 2);
        warps++;
    }

    /// <summary>On the farm: till a nearby open tile, water it and plant a parsnip; elsewhere just walk.</summary>
    private static void Farm()
    {
        if (Game1.currentLocation is not StardewValley.Farm farm || !Farms.CanTouch(farm, Game1.player))
        {
            Walk();
            return;
        }
        Point here = Game1.player.TilePoint;
        for (int attempt = 0; attempt < 15; attempt++)
        {
            var tile = new Vector2(here.X + rng.Next(-4, 5), here.Y + rng.Next(-4, 5));
            if (farm.terrainFeatures.TryGetValue(tile, out var tf))
            {
                if (tf is HoeDirt existing)
                {
                    existing.state.Value = 1; // water someone's crop
                    farmed++;
                    return;
                }
                continue;
            }
            if (farm.objects.ContainsKey(tile) || !farm.makeHoeDirt(tile))
                continue;
            if (farm.terrainFeatures.TryGetValue(tile, out var made) && made is HoeDirt dirt)
            {
                dirt.state.Value = 1;
                var seeds = ItemRegistry.Create("(O)472", 1);
                Game1.player.addItemToInventory(seeds);
                if (dirt.plant("472", Game1.player, false))
                    Game1.player.Items.ReduceId("(O)472", 1);
                Game1.player.Stamina = Math.Max(Game1.player.Stamina - 4, 10);
                farmed++;
                return;
            }
        }
        Walk();
    }

    /// <summary>
    /// 4-player farms: go to someone else's farm and try the things a visitor must not be able to do,
    /// through the same game calls a real click uses. Then "go to the farm" must land on our own farm.
    /// </summary>
    private static void Visit()
    {
        string? home = Farms.HomeFarmOf(Game1.player);
        if (Farms.IsFarm(Game1.currentLocation) && Game1.currentLocation.Name != home)
        {
            TryToMeddle(Game1.currentLocation);
            // Head home through the vanilla "Farm" name: must be redirected.
            pendingHomeCheck = home;
            Game1.warpFarmer("Farm", 64, 15, 2);
            return;
        }
        var others = Farms.AllNames.Where(n => n != home && Farms.MembersOf(n).Count > 0).ToList();
        if (others.Count == 0)
            return;
        Game1.warpFarmer(others[rng.Next(others.Count)], Farms.RoadEntry.X, Farms.RoadEntry.Y, 3);
        visits++;
    }

    private static string? pendingHomeCheck;

    private static void TryToMeddle(GameLocation loc)
    {
        // 1. Place a torch on an open tile.
        Point here = Game1.player.TilePoint;
        for (int i = 0; i < 10; i++)
        {
            var t = new Vector2(here.X + rng.Next(-3, 4), here.Y + rng.Next(-3, 4));
            if (!loc.CanItemBePlacedHere(t))
                continue;
            var torch = (StardewValley.Object)ItemRegistry.Create("(O)93");
            bool ok = Utility.tryToPlaceItem(loc, torch, (int)t.X * 64 + 32, (int)t.Y * 64 + 32);
            if (ok || loc.objects.ContainsKey(t)) leaks++; else blocked++;
            break;
        }
        // 2. Interact with something they own (crop, chest, machine, bin).
        var target = loc.terrainFeatures.Pairs.Where(p => p.Value is HoeDirt).Select(p => p.Key)
            .Concat(loc.objects.Keys).FirstOrDefault(new Vector2(-1, -1));
        if (target.X >= 0)
        {
            int before = loc.objects.Count() + loc.terrainFeatures.Count();
            bool ok = Game1.tryToCheckAt(target, Game1.player);
            if (ok || loc.objects.Count() + loc.terrainFeatures.Count() != before) leaks++; else blocked++;
        }
        // 3. Swing a tool.
        Game1.player.CurrentToolIndex = Math.Max(0, Game1.player.Items.IndexOf(Game1.player.Items.FirstOrDefault(x => x is StardewValley.Tools.Hoe)));
        float stamina = Game1.player.Stamina;
        Game1.pressUseToolButton();
        if (Game1.player.UsingTool || Game1.player.Stamina < stamina) leaks++; else blocked++;
    }

    private static void Chat()
    {
        Game1.Multiplayer.sendChatMessage(LocalizedContentManager.CurrentLanguageCode, Lines[rng.Next(Lines.Length)], Multiplayer.AllPlayers);
        chats++;
    }

    private static void Report()
    {
        double secs = (DateTime.UtcNow - windowStart).TotalSeconds;
        windowStart = DateTime.UtcNow;
        double ping = -1;
        bool connected = false;
        try
        {
            if (Game1.client is StardewValley.Network.LidgrenClient lc && lc.client?.ServerConnection is { } conn)
            {
                ping = conn.AverageRoundtripTime * 1000;
                connected = true;
            }
        }
        catch { }
        string where = Context.IsWorldReady ? Game1.currentLocation?.NameOrUniqueName ?? "-" : (Game1.activeClickableMenu?.GetType().Name ?? "-");
        long mem = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
        string line = $"{DateTime.Now:HH:mm:ss},{(connected && Context.IsWorldReady ? 1 : 0)},{where},{ping:0},{ticks / secs:0.0},{actions},{warps},{farmed},{chats},{mem},{(Context.IsWorldReady ? Farms.HomeFarmOf(Game1.player) ?? "-" : "-")},{visits},{blocked},{leaks},{homeTrips},{homeMisses}\n";
        ticks = 0;
        try { File.AppendAllText(Path.Combine(SV.StateDir, "bot.csv"), line); } catch { }
    }
}
