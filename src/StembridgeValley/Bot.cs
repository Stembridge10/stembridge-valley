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
        if (Environment.GetEnvironmentVariable("SV_BOT_GATE_CHECK") == "1" && GateCheck())
            return;
        if (Environment.GetEnvironmentVariable("SV_BOT_SETTINGS") is { Length: > 0 } role && SettingsCheck(role))
            return;
        if (Environment.GetEnvironmentVariable("SV_BOT_QUARRY") == "1" && QuarryCheck())
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
    private static int gateStage, gateWait;
    private static int setStage, setWait;

    /// <summary>What the notice board shows this player right now (opens it, reads it, closes it).</summary>
    private static string ReadBoard()
    {
        Farms.ShowVisitMenu();
        string text = Game1.activeClickableMenu is DialogueBox box
            ? box.getCurrentString() + " | " + string.Join(" | ", box.responses.Select(r => r.responseText))
            : "(no menu)";
        (Game1.activeClickableMenu as DialogueBox)?.closeDialogue();
        Game1.activeClickableMenu = null;
        return text;
    }

    /// <summary>
    /// Test of farm names and visits (SV_BOT_SETTINGS=owner|visitor; one step every ~3 seconds).
    /// owner: reads the board (naming prompt), sends a bad name, a good name, a second name (refused: once only),
    ///        then waits for SV_BOT_CLOSE_AT step and closes the farm to visitors.
    /// visitor: reads the board, visits the farm named in SV_BOT_VISIT, and reports where it is each step
    ///          (so being sent out when it closes shows up), then reads the board again and tries to visit.
    /// </summary>
    private static bool SettingsCheck(string role)
    {
        if (Game1.activeClickableMenu != null && Game1.activeClickableMenu is not DialogueBox)
            return true;
        if (++setWait < 180)
            return true;
        setWait = 0;
        setStage++;
        string? home = Farms.HomeFarmOf(Game1.player);
        string where = Game1.currentLocation?.Name ?? "-";
        Log.Info($"[settings] {role} step {setStage} at {where}; home {home} = {(home != null ? Farms.DisplayName(home) : "-")}");
        if (role == "owner" && home != null)
        {
            int closeAt = int.TryParse(Environment.GetEnvironmentVariable("SV_BOT_CLOSE_AT"), out int c) ? c : 12;
            switch (setStage)
            {
                case 1: Log.Info($"[settings] board: {ReadBoard()}"); break;
                case 2: FarmSettings.RequestNameForTest(home, "<b>!!</b>"); break;           // refused: no letters left
                case 3: FarmSettings.RequestNameForTest(home, "Sunny Acres farm"); break;    // becomes "Sunny Acres Farm"
                case 5: Log.Info($"[settings] board after naming: {ReadBoard()}"); break;
                case 6: FarmSettings.RequestNameForTest(home, "Second Try"); break;          // refused: in game it's once only
                case 8: Log.Info($"[settings] name now {Farms.DisplayName(home)}"); break;
            }
            if (setStage == closeAt)
            {
                Log.Info("[settings] closing my farm to visitors");
                FarmSettings.SetVisits(home, true);
            }
        }
        else if (role == "visitor")
        {
            string ownerName = Environment.GetEnvironmentVariable("SV_BOT_VISIT") ?? "";
            string target = Farms.AllNames.FirstOrDefault(f => FarmSettings.OwnerName(f) == ownerName) ?? "SV_Farm2";
            switch (setStage)
            {
                case 1:
                    Log.Info($"[settings] board: {ReadBoard()}");
                    Farms.NamingDeclined = true; // as if "Not now" was picked
                    Log.Info($"[settings] board after Not now: {ReadBoard()}");
                    break;
                case 9:
                    Log.Info($"[settings] board: {ReadBoard()}");
                    Game1.warpFarmer(target, Farms.RoadEntry.X, Farms.RoadEntry.Y, 3);
                    break;
                case 26:
                    Log.Info($"[settings] board after close: {ReadBoard()}");
                    break;
            }
        }
        return true;
    }

    /// <summary>Test: go stand below the visit gate at the bus stop, click it, and report what opens. Stays there for a screenshot.</summary>
    private static bool GateCheck()
    {
        switch (gateStage)
        {
            case 0:
                Game1.warpFarmer("BusStop", 15, 23, 0);
                gateStage = 1;
                return true;
            case 1 when Game1.currentLocation?.Name != "BusStop" || Game1.locationRequest != null:
                return true;
            case 1:
                if (++gateWait < 150)
                    return true; // let the screen fade in fully
                gateWait = 0;
                var tile = Game1.currentLocation.map.GetLayer("Buildings").Tiles[15, 21];
                string action = tile?.Properties.TryGetValue("Action", out var a) == true ? a.ToString() : "-";
                Log.Info($"[gatecheck] at BusStop {Game1.player.TilePoint.X},{Game1.player.TilePoint.Y}; gate tile action={action}; passable={Game1.currentLocation.isTilePassable(new xTile.Dimensions.Location(15, 21), Game1.viewport)}");
                bool ok = Game1.currentLocation.checkAction(new xTile.Dimensions.Location(15, 21), Game1.viewport, Game1.player);
                Log.Info($"[gatecheck] clicked gate: {ok}; menu={Game1.activeClickableMenu?.GetType().Name ?? "none"}; question={(Game1.activeClickableMenu as DialogueBox)?.getCurrentString() ?? "-"}");
                gateStage = 2;
                return true;
            case 2:
                if (++gateWait < 60)
                    return true;
                Log.Info($"[gatecheck] ready for screenshot (fade {Game1.fadeToBlackAlpha}, globalFade {Game1.globalFade})");
                try
                {
                    // Render one frame of the bus stop around the gate into a texture and save it (no SkiaSharp needed).
                    var gd = Game1.graphics.GraphicsDevice;
                    var rt = new Microsoft.Xna.Framework.Graphics.RenderTarget2D(gd, 1024, 640, false,
                        Microsoft.Xna.Framework.Graphics.SurfaceFormat.Color, Microsoft.Xna.Framework.Graphics.DepthFormat.None, 0,
                        Microsoft.Xna.Framework.Graphics.RenderTargetUsage.PreserveContents);
                    var old = Game1.viewport;
                    Game1.viewport = new xTile.Dimensions.Rectangle(14 * 64 - 512 + 32, 21 * 64 - 380, 1024, 640);
                    AccessTools.Method(typeof(Game1), "_draw").Invoke(Game1.game1, new object[] { Game1.currentGameTime, rt });
                    Game1.viewport = old;
                    gd.SetRenderTarget(null);
                    string path = Path.Combine(SV.StateDir, "gatecheck.png");
                    using (var fs = File.Create(path))
                        rt.SaveAsPng(fs, rt.Width, rt.Height);
                    rt.Dispose();
                    Log.Info($"[gatecheck] screenshot saved: {path}");
                    (Game1.activeClickableMenu as DialogueBox)?.closeDialogue();
                }
                catch (Exception ex)
                {
                    Log.Info($"[gatecheck] screenshot failed: {ex.GetBaseException().Message}");
                }
                gateStage = 3;
                return true;
            default:
                return true; // stand still by the gate
        }
    }

    /// <summary>Render the current location around a tile into a PNG in the state folder (test screenshots).</summary>
    internal static void Snapshot(string file, int tileX, int tileY, int width = 1280, int height = 800)
    {
        try
        {
            var gd = Game1.graphics.GraphicsDevice;
            var rt = new Microsoft.Xna.Framework.Graphics.RenderTarget2D(gd, width, height, false,
                Microsoft.Xna.Framework.Graphics.SurfaceFormat.Color, Microsoft.Xna.Framework.Graphics.DepthFormat.None, 0,
                Microsoft.Xna.Framework.Graphics.RenderTargetUsage.PreserveContents);
            var old = Game1.viewport;
            Game1.viewport = new xTile.Dimensions.Rectangle(tileX * 64 + 32 - width / 2, tileY * 64 + 32 - height / 2, width, height);
            AccessTools.Method(typeof(Game1), "_draw").Invoke(Game1.game1, new object[] { Game1.currentGameTime, rt });
            Game1.viewport = old;
            gd.SetRenderTarget(null);
            string path = Path.Combine(SV.StateDir, file);
            using (var fs = File.Create(path))
                rt.SaveAsPng(fs, rt.Width, rt.Height);
            rt.Dispose();
            Log.Info($"[snapshot] saved {path}");
        }
        catch (Exception ex)
        {
            Log.Info($"[snapshot] failed: {ex.GetBaseException().Message}");
        }
    }

    private static int quarryStage, quarryWait;

    /// <summary>
    /// Test of the bigger farm and the quarry (SV_BOT_QUARRY=1; one step every ~3 seconds):
    /// go home, check the new map size and exits, photograph the bridge and the quarry, level Mining to 15
    /// (as if earned), and report the rocks the server puts in the quarry.
    /// </summary>
    private static bool QuarryCheck()
    {
        if (Game1.activeClickableMenu != null && Game1.activeClickableMenu is not DialogueBox)
        {
            Game1.activeClickableMenu.exitThisMenu(false);
            return true;
        }
        if (Game1.locationRequest != null || ++quarryWait < 180)
            return true;
        quarryWait = 0;
        quarryStage++;
        string? home = Farms.HomeFarmOf(Game1.player);
        GameLocation? farm = home != null ? Game1.getLocationFromName(home) : null;
        string where = Game1.currentLocation?.Name ?? "-";
        switch (quarryStage)
        {
            case 1:
                Log.Info($"[quarry] at {where}, home {home}; going to the bridge");
                if (home != null)
                    Game1.warpFarmer(home, Quarry.Bridge.X, Quarry.Bridge.Y - 3, 2);
                break;
            case 3:
            {
                var map = Game1.currentLocation!.map;
                string warps = Game1.currentLocation.TryGetMapProperty("Warp", out string? w) ? w : "-";
                Log.Info($"[quarry] at {where} {Game1.player.TilePoint}; map {map.Layers[0].LayerWidth}x{map.Layers[0].LayerHeight}; warps: {warps}");
                Log.Info($"[quarry] south exits: {string.Join(" ", Game1.currentLocation.warps.Where(x => x.Y >= Quarry.NewHeight - 1).Select(x => $"{x.X},{x.Y}->{x.TargetName} {x.TargetX},{x.TargetY}"))}");
                bool Walk(int x, int y) => !Game1.currentLocation!.isCollidingPosition(new Microsoft.Xna.Framework.Rectangle(x * 64 + 16, y * 64 + 16, 32, 32), Game1.viewport, true, 0, false, Game1.player);
                bool bridge = Walk(Quarry.Bridge.X, Quarry.Bridge.Y) && Walk(Quarry.Bridge.X, Quarry.Bridge.Y + 1) && Walk(Quarry.Bridge.X, Quarry.Bridge.Y + 2) && Walk(Quarry.Bridge.X, Quarry.Bridge.Y + 3);
                bool river = Walk(Quarry.Bridge.X - 1, Quarry.Bridge.Y) || Walk(Quarry.Bridge.X - 3, Quarry.Bridge.Y) || Walk(Quarry.Bridge.X + 2, Quarry.Bridge.Y + 3);
                bool seam = Walk(40, 61);
                Log.Info($"[quarry] walkable: bridge {bridge}, river next to it {river}, old tree line at 40,61 {seam}");
                foreach (var (tx, ty) in new[] { (Quarry.Bridge.X, Quarry.Bridge.Y), (Quarry.Bridge.X, Quarry.Bridge.Y + 1), (Quarry.Bridge.X, Quarry.Bridge.Y + 3), (Quarry.Bridge.X - 3, Quarry.Bridge.Y + 1) })
                    foreach (string ln in new[] { "Back", "Buildings" })
                    {
                        var t = map.GetLayer(ln)?.Tiles[tx, ty];
                        Log.Info($"[quarry] tile {ln} {tx},{ty}: {(t == null ? "none" : $"{t.TileSheet.Id}#{t.TileIndex} idx[{string.Join(",", t.TileIndexProperties.Select(p => p.Key + "=" + p.Value))}] own[{string.Join(",", t.Properties.Select(p => p.Key + "=" + p.Value))}]")}");
                    }
                Log.Info($"[quarry] column {Quarry.Bridge.X} rows {Quarry.Bridge.Y - 3}..{Quarry.Bridge.Y + 8}: " + string.Join(" ", Enumerable.Range(Quarry.Bridge.Y - 3, 12).Select(ry =>
                {
                    var v = new Microsoft.Xna.Framework.Vector2(Quarry.Bridge.X, ry);
                    string what = Game1.currentLocation.objects.TryGetValue(v, out var o) ? "obj:" + o.Name : Game1.currentLocation.terrainFeatures.TryGetValue(v, out var tf) ? "tf:" + tf.GetType().Name : "";
                    return $"{ry}={(Walk(Quarry.Bridge.X, ry) ? "ok" : "X")}{what}";
                })));
                foreach (var c in Game1.currentLocation.resourceClumps.Where(c => c.Tile.Y >= Quarry.Bridge.Y - 2 && c.Tile.Y <= Quarry.Bridge.Y + 10 && Math.Abs(c.Tile.X - Quarry.Bridge.X) <= 4))
                    Log.Info($"[quarry] clump {c.parentSheetIndex.Value} at {c.Tile} size {c.width.Value}x{c.height.Value}");
                foreach (var lf in Game1.currentLocation.largeTerrainFeatures.Where(f => Math.Abs(f.Tile.X - Quarry.Bridge.X) <= 4 && f.Tile.Y >= Quarry.Bridge.Y && f.Tile.Y <= Quarry.Bridge.Y + 8))
                    Log.Info($"[quarry] large feature {lf.GetType().Name} at {lf.Tile}");
                foreach (string ln in new[] { "Back", "Buildings", "Front" })
                    foreach (int ry in new[] { Quarry.Bridge.Y + 4, Quarry.Bridge.Y + 5 })
                    {
                        var t = map.GetLayer(ln)?.Tiles[Quarry.Bridge.X, ry];
                        Log.Info($"[quarry] tile {ln} {Quarry.Bridge.X},{ry}: {(t == null ? "none" : $"{t.TileSheet.Id}#{t.TileIndex} idx[{string.Join(",", t.TileIndexProperties.Select(p => p.Key + "=" + p.Value))}]")}");
                    }
                // Walk across for real: path-find from the old farm to the far side of the bridge.
                var toSteps = new PathFindController(Game1.player, Game1.currentLocation, Quarry.Steps, 0);
                Log.Info($"[quarry] path from the old farm to the quarry stairs: {(toSteps.pathToEndPoint == null ? "none" : toSteps.pathToEndPoint.Count + " steps")}");
                var walk = new PathFindController(Game1.player, Game1.currentLocation, new Point(Quarry.Bridge.X, Quarry.Bridge.Y + 6), 2);
                Log.Info($"[quarry] path across the bridge: {(walk.pathToEndPoint == null ? "none" : walk.pathToEndPoint.Count + " steps")}");
                if (walk.pathToEndPoint != null)
                    Game1.player.controller = walk;
                break;
            }
            case 4:
                Log.Info($"[quarry] after the walk: now at {Game1.player.TilePoint} (crossed = row > {Quarry.Bridge.Y + 3})");
                Snapshot("quarry-bridge.png", Quarry.Bridge.X, Quarry.Bridge.Y + 1);
                Log.Info($"[quarry] Mining {Skills.Level(Game1.player, Skills.Mining)}, rocks in quarry {(farm != null ? Quarry.RocksIn(farm) : -1)}");
                Game1.warpFarmer(home!, Quarry.Steps.X, Quarry.Steps.Y, 0);
                break;
            case 6:
                Log.Info($"[quarry] at {where} {Game1.player.TilePoint}; stairs walkable {Game1.currentLocation!.isTilePassable(new xTile.Dimensions.Location(Quarry.Steps.X, Quarry.Steps.Y - 2), Game1.viewport)}");
                Snapshot("quarry-before.png", Quarry.Area.Center.X, Quarry.Area.Center.Y + 2);
                // Earn Mining 15 (test shortcut: same call the game makes when you break a rock).
                Game1.player.gainExperience(Skills.Mining, Skills.XpForLevel(15) - Skills.Xp(Game1.player, Skills.Mining));
                Log.Info($"[quarry] Mining now {Skills.Level(Game1.player, Skills.Mining)} ({Skills.Xp(Game1.player, Skills.Mining)} XP)");
                Log.Info($"[quarry] skills page: {Skills.Describe(Game1.player, Skills.Mining).Replace(Environment.NewLine, " / ")}");
                break;
            case 10:
                Log.Info($"[quarry] rocks in quarry now {(farm != null ? Quarry.RocksIn(farm) : -1)}: {string.Join(" ", farm?.objects.Pairs.Where(p => Quarry.Area.Contains((int)p.Key.X, (int)p.Key.Y)).Select(p => p.Value.ItemId) ?? Array.Empty<string>())}");
                Snapshot("quarry-after.png", Quarry.Area.Center.X, Quarry.Area.Center.Y + 2);
                break;
        }
        return true;
    }

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
