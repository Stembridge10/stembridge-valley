using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Locations;
using xTile;
using xTile.Layers;
using xTile.Tiles;
using XRect = Microsoft.Xna.Framework.Rectangle;
using SObject = StardewValley.Object;

namespace StembridgeValley;

/// <summary>
/// The farm quarry: a small area of its own off the side of every farm, unlocked by Mining.
///  - The map is the quarry from Stardew's Mountain (east of the mines), cut out on its own: cliffs above, forest
///    below and to the right, the ravine and its wooden bridge on the left, so every edge is a natural one. A dirt
///    path is opened through its right-hand forest; that path joins the farm.
///  - It is its own place (not part of the farm map), so more unlock areas can be added beside it later, and it
///    works the same whatever farm map a farm uses: only the farm-side opening depends on the farm map.
///  - Locked until someone on the farm reaches Mining 15. Then rocks appear every morning, more and better as
///    Mining goes up (gold at 20, gems at 25, more at 30, iridium at 35, mystic stones at 40, double at 45).
///  - It belongs to the farm: same owners, visitors look but don't touch.
/// </summary>
internal static class Quarry
{
    public const string Prefix = "SV_Quarry";
    private const string MapAsset = "Maps/SV_FarmQuarry";
    /// <summary>The part of the Mountain map that becomes the quarry area.</summary>
    private const int SrcX = 96, Width = 39, Height = 41;

    /// <summary>Farm side (standard farm map): walking off the left edge on these rows goes to the quarry.</summary>
    public static readonly int[] FarmExitRows = { 44, 45, 46 };
    /// <summary>Where you arrive on the farm coming back.</summary>
    public static readonly Point FarmArrival = new(1, 45);
    /// <summary>Quarry side: walking off the right edge on these rows goes back to the farm.</summary>
    public static readonly int[] QuarryExitRows = { 21, 22, 23 };
    /// <summary>Where you arrive in the quarry area.</summary>
    public static readonly Point Arrival = new(Width - 2, 22);

    /// <summary>The quarry's dirt floor (where rocks appear).</summary>
    public static readonly XRect Area = new(106 - SrcX, 13, 22, 22);
    /// <summary>A spot in the middle of the quarry floor.</summary>
    public static readonly Point Middle = new(118 - SrcX, 24);

    public const int UnlockLevel = 15;
    private const int MaxRocks = 45;
    private const string LevelKey = "Stembridge/QuarryLevel";

    public static string QuarryOf(string farmName) => Prefix + farmName.Substring(Farms.Prefix.Length);
    public static string FarmOfQuarry(string quarryName) => Farms.Prefix + quarryName.Substring(Prefix.Length);
    public static bool IsQuarry(GameLocation? loc) => loc != null && loc.Name.StartsWith(Prefix, StringComparison.Ordinal);

    private static IModHelper Helper = null!;

    public static void Apply(IModHelper helper, Harmony harmony)
    {
        if (!Farms.Enabled)
            return;
        Helper = helper;
        helper.Events.Content.AssetRequested += OnAssetRequested;
        harmony.Patch(AccessTools.Method(typeof(GameLocation), nameof(GameLocation.updateWarps)),
            postfix: new HarmonyMethod(typeof(Quarry), nameof(UpdateWarps_Postfix)));
        harmony.Patch(AccessTools.Method(typeof(Game1), nameof(Game1.warpFarmer), new[] { typeof(LocationRequest), typeof(int), typeof(int), typeof(int) }),
            prefix: new HarmonyMethod(typeof(Quarry), nameof(WarpFarmer_Prefix)) { priority = Priority.First });
        if (SV.Role == Role.Server)
        {
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.GameLoop.SaveLoaded += (_, _) => { ClearAllWays(); PublishLevels(); };
            helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecond;
            Skills.LevelUp += OnLevelUp;
        }
    }

    // ---------- locations and maps ----------

    private static void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo("Data/Locations"))
            e.Edit(asset =>
            {
                var data = asset.AsDictionary<string, LocationData>().Data;
                bool server = SV.Role == Role.Server;
                foreach (string farm in Farms.DataNames())
                    data[QuarryOf(farm)] = new LocationData
                    {
                        DisplayName = "Quarry",
                        CanPlantHere = false,
                        DefaultArrivalTile = Arrival,
                        CreateOnLoad = server
                            ? new CreateLocationData { MapPath = MapAsset.Replace('/', '\\'), AlwaysActive = true }
                            : new CreateLocationData { MapPath = "Maps\\Cellar", AlwaysActive = false },
                    };
            }, AssetEditPriority.Late);
        else if (e.NameWithoutLocale.IsEquivalentTo(MapAsset))
            e.LoadFrom(BuildMap, AssetLoadPriority.Exclusive);
        else if (e.NameWithoutLocale.IsEquivalentTo("Maps/Farm"))
            e.Edit(asset => OpenFarmLeftEdge(asset.AsMap().Data));
    }

    /// <summary>The quarry area: a copy of the Mountain's quarry corner, with a path through its right-hand forest.</summary>
    private static Map BuildMap()
    {
        Map src = Helper.GameContent.Load<Map>("Maps/Mountain");
        var map = new Map("SV_FarmQuarry");
        var sheets = new Dictionary<TileSheet, TileSheet>();
        foreach (TileSheet ts in src.TileSheets)
        {
            var copy = new TileSheet(ts.Id, map, ts.ImageSource, ts.SheetSize, ts.TileSize);
            foreach (var p in ts.Properties)   // includes every per-tile tag (stored as @TileIndex@n@Key)
                copy.Properties[p.Key] = p.Value;
            map.AddTileSheet(copy);
            sheets[ts] = copy;
        }
        foreach (Layer from in src.Layers)
        {
            if (from.Id == "Paths")
                continue; // the Mountain's own spawn markers
            var to = new Layer(from.Id, map, new xTile.Dimensions.Size(Width, Height), from.TileSize);
            map.AddLayer(to);
            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                    if (from.Tiles[x + SrcX, y] is { } t)
                        to.Tiles[x, y] = Copy(t, to, sheets);
        }
        map.Properties["Outdoors"] = "T";
        OpenRightEdge(map);
        RemoveCave(map);
        return map;
    }

    private static Tile Copy(Tile t, Layer layer, Dictionary<TileSheet, TileSheet> sheets)
    {
        Tile n = t is AnimatedTile a
            ? new AnimatedTile(layer, a.TileFrames.Select(f => (StaticTile)Copy(f, layer, sheets)).ToArray(), a.FrameInterval)
            : new StaticTile(layer, sheets[t.TileSheet], t.BlendMode, t.TileIndex);
        foreach (var p in t.Properties)
            n.Properties[p.Key] = p.Value;
        return n;
    }

    /// <summary>
    /// A dirt path (rows 21-23) from the quarry floor east through the grass and forest to the right edge, with
    /// the grass edges the Mountain map uses: a bank edge above, a grass lip below, matching corners.
    /// </summary>
    private static void OpenRightEdge(Map map)
    {
        TileSheet? s = map.TileSheets.FirstOrDefault(t => t.Id == "outdoors");
        Layer? back = map.GetLayer("Back"), buildings = map.GetLayer("Buildings"), front = map.GetLayer("Front"), always = map.GetLayer("AlwaysFront");
        if (s == null || back == null || buildings == null || front == null || always == null)
        {
            Log.Warn("Quarry: the Mountain map isn't the expected one; the quarry has no way out.");
            return;
        }
        void Set(Layer layer, int x, int y, int? index) =>
            layer.Tiles[x - SrcX, y] = index is int i ? new StaticTile(layer, s, BlendMode.Alpha, i) : null;

        for (int x = 132; x <= 134; x++)          // trees whose canopies hang over the path
            for (int y = 16; y <= 23; y++)
                Set(front, x, y, null);
        for (int x = 130; x <= 134; x++)
            for (int y = 19; y <= 27; y++)
                Set(always, x, y, null);
        for (int y = 21; y <= 23; y++)            // the path
            for (int x = 131; x <= 134; x++)
            {
                Set(buildings, x, y, null);
                Set(back, x, y, 537);
            }
        for (int x = 132; x <= 134; x++)
        {
            Set(back, x, 20, 474); Set(buildings, x, 20, null);                                   // grass edge above
            Set(back, x, 24, 537); Set(front, x, 24, x % 2 == 0 ? 413 : 414); Set(buildings, x, 24, null); // lip below
        }
        Set(buildings, 131, 20, null); Set(back, 131, 20, 537); Set(front, 131, 20, 440);         // corners
        Set(buildings, 131, 24, null); Set(back, 131, 24, 537); Set(front, 131, 24, 439);
    }

    /// <summary>The little cave in the cliff west of the quarry floor leads nowhere here: fill it with plain cliff.</summary>
    private static void RemoveCave(Map map)
    {
        TileSheet? s = map.TileSheets.FirstOrDefault(t => t.Id == "outdoors");
        Layer? back = map.GetLayer("Back"), buildings = map.GetLayer("Buildings"), front = map.GetLayer("Front"), always = map.GetLayer("AlwaysFront");
        if (s == null || back == null || buildings == null || front == null || always == null)
            return;
        void Set(Layer layer, int x, int y, int? index) =>
            layer.Tiles[x - SrcX, y] = index is int i ? new StaticTile(layer, s, BlendMode.Alpha, i) : null;

        var cliff = new Dictionary<int, int> { [13] = 493, [14] = 518, [15] = 492, [16] = 517, [17] = 542 };
        for (int x = 102; x <= 104; x++)
        {
            foreach (var (y, i) in cliff)
                Set(buildings, x, y, i);
            Set(always, x, 14, null);   // the cave's arch
        }
        foreach (var (x, y) in new[] { (102, 12), (103, 13), (102, 15), (103, 15), (104, 14) })
            Set(front, x, y, null);      // its shading
        for (int x = 101; x <= 105; x++)
            Set(back, x, 18, 175);       // the dirt patch at its mouth becomes grass
    }

    /// <summary>
    /// A path off the standard farm's left side (rows 44-46). The left bank above it ends in a short cliff and the
    /// one below starts with a grass lip: the same pieces the farm map uses for its own banks.
    /// </summary>
    private static void OpenFarmLeftEdge(Map map)
    {
        TileSheet? s = map.TileSheets.FirstOrDefault(t => t.Id == "untitled tile sheet");
        Layer? back = map.GetLayer("Back"), buildings = map.GetLayer("Buildings"), front = map.GetLayer("Front");
        if (s == null || back == null || buildings == null || front == null || back.LayerWidth < 10 || back.LayerHeight < 50)
        {
            Log.Warn("Quarry: the farm map isn't the standard one; no path to the quarry.");
            return;
        }
        void Set(Layer layer, int x, int y, int? index) =>
            layer.Tiles[x, y] = index is int i ? new StaticTile(layer, s, BlendMode.Alpha, i) : null;

        // A gap in the left bank, edged like the farm's north wall: a tall rock face with grass on top above the
        // path (the little tree moved up onto the grass), and the bank's grass lip below. Matches the preview
        // tiles exactly (left_edits_rock2.json).
        Set(back, 0, 40, 587); Set(back, 1, 40, 587); Set(back, 2, 40, 587);
        Set(back, 0, 41, 587); Set(back, 1, 41, 587); Set(back, 2, 41, 587);
        Set(back, 0, 42, 587); Set(back, 1, 42, 587); Set(back, 2, 42, 587);
        Set(back, 0, 43, 587); Set(back, 1, 43, 587); Set(back, 2, 43, 587);
        Set(back, 0, 44, 562); Set(back, 1, 44, 512); Set(back, 2, 44, 618);
        Set(back, 0, 45, 587); Set(back, 1, 45, 587); Set(back, 2, 45, 587);
        Set(back, 0, 46, 587); Set(back, 1, 46, 587); Set(back, 2, 46, 587);
        Set(back, 0, 47, 587); Set(back, 1, 47, 587); Set(back, 2, 47, 587);
        Set(back, 2, 48, 175);
        Set(buildings, 0, 36, 16); Set(buildings, 1, 36, 16);
        Set(buildings, 0, 37, 16); Set(buildings, 1, 37, 16);
        Set(buildings, 0, 38, 16); Set(buildings, 1, 38, 16);
        Set(buildings, 0, 39, 16); Set(buildings, 1, 39, 16); Set(buildings, 2, 39, 444);
        Set(buildings, 0, 40, 468); Set(buildings, 1, 40, 468); Set(buildings, 2, 40, 469);
        Set(buildings, 0, 41, 493); Set(buildings, 1, 41, 492); Set(buildings, 2, 41, 494);
        Set(buildings, 0, 42, 518); Set(buildings, 1, 42, 517); Set(buildings, 2, 42, 519);
        Set(buildings, 0, 43, 543); Set(buildings, 1, 43, 542); Set(buildings, 2, 43, 544);
        Set(buildings, 0, 44, null); Set(buildings, 1, 44, null); Set(buildings, 2, 44, null);
        Set(buildings, 0, 45, null); Set(buildings, 1, 45, null); Set(buildings, 2, 45, null);
        Set(buildings, 0, 46, null); Set(buildings, 1, 46, null); Set(buildings, 2, 46, null);
        Set(buildings, 0, 47, null); Set(buildings, 1, 47, null); Set(buildings, 2, 47, null);
        Set(buildings, 2, 48, 394);
        Set(front, 0, 34, 4); Set(front, 1, 34, 5);
        Set(front, 0, 35, 29); Set(front, 1, 35, 30);
        Set(front, 0, 36, 54); Set(front, 1, 36, 55);
        Set(front, 0, 37, 79); Set(front, 1, 37, 80);
        Set(front, 0, 38, 104); Set(front, 1, 38, 105);
        Set(front, 0, 39, 129); Set(front, 1, 39, 130);
        Set(front, 0, 40, null); Set(front, 1, 40, null); Set(front, 2, 40, null);
        Set(front, 0, 41, null); Set(front, 1, 41, null); Set(front, 2, 41, null);
        Set(front, 0, 42, null); Set(front, 1, 42, null); Set(front, 2, 42, null);
        Set(front, 0, 43, null); Set(front, 1, 43, null); Set(front, 2, 43, null);
        Set(front, 0, 44, null); Set(front, 1, 44, null); Set(front, 2, 44, null);
        Set(front, 0, 45, null); Set(front, 1, 45, null); Set(front, 2, 45, null);
        Set(front, 0, 46, null); Set(front, 1, 46, null); Set(front, 2, 46, null);
        Set(front, 0, 47, 413); Set(front, 1, 47, 414); Set(front, 2, 47, 438);
    }

    /// <summary>Server: the farm's left edge leads to its quarry; the quarry's right edge leads back.</summary>
    private static void UpdateWarps_Postfix(GameLocation __instance)
    {
        if (Game1.IsClient)
            return;
        if (Farms.IsFarm(__instance))
        {
            string quarry = QuarryOf(__instance.Name);
            foreach (int y in FarmExitRows)
                __instance.warps.Add(new Warp(-1, y, quarry, Arrival.X, Arrival.Y, false));
        }
        else if (IsQuarry(__instance))
        {
            string farm = FarmOfQuarry(__instance.Name);
            __instance.warps.Clear();
            foreach (int y in QuarryExitRows)
                __instance.warps.Add(new Warp(Width, y, farm, FarmArrival.X, FarmArrival.Y, false));
        }
    }

    /// <summary>Server: make the quarry area for a farm (once). Returns it, not yet sent to players.</summary>
    public static GameLocation EnsureQuarry(string farmName)
    {
        string name = QuarryOf(farmName);
        if (Game1.getLocationFromName(name) is { } existing)
            return existing;
        GameLocation quarry = Game1.CreateGameLocation(name);
        Game1.locations.Add(quarry);
        return quarry;
    }

    // ---------- the lock ----------

    /// <summary>The farm's best Mining level, as the server last told everyone (kept on the farm, so players see it).</summary>
    public static int PublishedLevel(string farm) =>
        Game1.getLocationFromName(farm)?.modData.TryGetValue(LevelKey, out string? v) == true && int.TryParse(v, out int l) ? l : 0;

    private static int lastRefusal;

    /// <summary>Until someone on the farm reaches Mining 15, the path to the quarry turns you back.</summary>
    private static bool WarpFarmer_Prefix(LocationRequest locationRequest, int tileX, int tileY)
    {
        if (locationRequest?.Name is not string target || !target.StartsWith(Prefix, StringComparison.Ordinal) || Game1.player == null)
            return true;
        string farm = FarmOfQuarry(target);
        if (PublishedLevel(farm) >= UnlockLevel)
            return true;
        if (Game1.currentLocation?.Name == farm)
        {
            Game1.player.Halt();
            Game1.player.Position = new Vector2(FarmArrival.X * 64 + 32, Game1.player.Position.Y);
            Game1.player.faceDirection(1);
        }
        if (Game1.ticks - lastRefusal > 120)
        {
            lastRefusal = Game1.ticks;
            Game1.showRedMessage($"The quarry opens when someone on this farm reaches Mining {UnlockLevel}.");
        }
        return false;
    }

    // ---------- the quarry ----------

    public static int BestMiningLevel(string farm) =>
        Farms.MembersOf(farm).Select(f => Skills.Level(f, Skills.Mining)).DefaultIfEmpty(0).Max();

    private static void PublishLevels()
    {
        foreach (string farm in Farms.AllNames)
            Publish(farm);
    }

    private static void Publish(string farm)
    {
        if (Game1.getLocationFromName(farm) is not { } f)
            return;
        string level = BestMiningLevel(farm).ToString();
        if (!f.modData.TryGetValue(LevelKey, out string? old) || old != level)
            f.modData[LevelKey] = level;
    }

    private static int RocksPerDay(int level) => (level >= 30 ? 10 : 6) * (level >= 45 ? 2 : 1);

    /// <summary>The farm's side of the opening: the farm's starting debris (stumps, rocks, weeds) lands there too.</summary>
    public static readonly XRect FarmWay = new(0, FarmExitRows[0] - 1, 8, FarmExitRows.Length + 2);

    /// <summary>A walkable lane from the open farm to the opening, kept free of wild trees and boulders too.</summary>
    public static readonly XRect FarmLane = new(0, FarmExitRows[0], 16, FarmExitRows.Length);

    /// <summary>Remove natural debris only (stumps, boulders, logs, weeds, stones, twigs, grass), never anything a player placed.</summary>
    private static void ClearDebris(GameLocation loc, XRect area)
    {
        for (int i = loc.resourceClumps.Count - 1; i >= 0; i--)
        {
            var c = loc.resourceClumps[i];
            if (new XRect((int)c.Tile.X, (int)c.Tile.Y, c.width.Value, c.height.Value).Intersects(area))
                loc.resourceClumps.RemoveAt(i);
        }
        for (int x = area.Left; x < area.Right; x++)
            for (int y = area.Top; y < area.Bottom; y++)
            {
                var tile = new Vector2(x, y);
                if (loc.objects.TryGetValue(tile, out SObject? o) && (o.IsWeeds() || o.IsBreakableStone() || o.IsTwig()))
                    loc.objects.Remove(tile);
                if (loc.terrainFeatures.TryGetValue(tile, out var tf) && (tf is StardewValley.TerrainFeatures.Grass
                    // wild trees from the farm's starting debris, on the lane only (never fruit trees, tapped or fertilized ones)
                    || (tf is StardewValley.TerrainFeatures.Tree t && FarmLane.Contains(x, y) && IsFarmSide(loc) && !t.tapped.Value && !t.fertilized.Value)))
                    loc.terrainFeatures.Remove(tile);
            }
    }

    private static bool IsFarmSide(GameLocation loc) => Farms.IsFarm(loc);

    private static void OnOneSecond(object? sender, OneSecondUpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;
        if (e.IsMultipleOf(600))
            ClearAllWays();
        if (e.IsMultipleOf(10))
            PublishLevels();
    }

    private static void ClearAllWays()
    {
        foreach (string farm in Farms.AllNames)
            if (Game1.getLocationFromName(farm) is { } f)
            {
                ClearDebris(f, FarmWay);
                ClearDebris(f, FarmLane);
            }
    }

    private static void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        foreach (string farm in Farms.AllNames)
        {
            Publish(farm);
            int level = BestMiningLevel(farm);
            if (level >= UnlockLevel)
                Spawn(EnsureQuarry(farm), level, RocksPerDay(level));
        }
    }

    /// <summary>Reaching a quarry level opens/fills the quarry right away (no waiting for tomorrow).</summary>
    private static void OnLevelUp(Farmer who, int skill, int level)
    {
        if (skill != Skills.Mining || level < UnlockLevel || Skills.UnlockAt(skill, level) == null)
            return;
        if (Farms.HomeFarmOf(who) is { } farm)
        {
            Publish(farm);
            int placed = Spawn(EnsureQuarry(farm), level, RocksPerDay(level));
            Log.Info($"{who.Name} reached Mining {level}: {placed} rocks in {Farms.DisplayName(farm)}'s quarry.");
        }
    }

    public static int RocksIn(GameLocation loc) =>
        loc.objects.Pairs.Count(p => Area.Contains((int)p.Key.X, (int)p.Key.Y) && IsRock(p.Value));

    private static bool IsRock(SObject o) => o.IsBreakableStone() || o.QualifiedItemId is "(O)44" or "(O)46";

    /// <summary>Server: put up to <paramref name="count"/> rocks on the quarry floor. Returns how many were placed.</summary>
    public static int Spawn(GameLocation loc, int level, int count)
    {
        int placed = 0;
        Random rng = Utility.CreateDaySaveRandom(loc.Name.GetHashCode(), level, Game1.random.Next());
        count = Math.Min(count, MaxRocks - RocksIn(loc));
        for (int i = 0; i < count; i++)
        {
            for (int tries = 0; tries < 12; tries++)
            {
                var tile = new Vector2(rng.Next(Area.Left, Area.Right), rng.Next(Area.Top, Area.Bottom));
                // Same test the Mountain quarry uses for its own stones.
                if (loc.doesTileHaveProperty((int)tile.X, (int)tile.Y, "Diggable", "Back") == null
                    || !loc.CanItemBePlacedHere(tile, itemIsPassable: false, CollisionMask.All, CollisionMask.None))
                    continue;
                (string id, int hits) = PickRock(level, rng);
                loc.objects.Add(tile, new SObject(id, 10) { MinutesUntilReady = hits });
                placed++;
                break;
            }
        }
        return placed;
    }

    private static readonly string[] GemRocks = { "2", "4", "6", "8", "10", "12", "14" };

    private static (string Id, int Hits) PickRock(int level, Random rng)
    {
        double r = rng.NextDouble();
        if (level >= 40 && r < 0.01) return ("46", 12);                               // mystic stone
        if (level >= 35 && r < 0.04) return ("765", 16);                              // iridium
        if (level >= 25 && r < 0.09) return (GemRocks[rng.Next(GemRocks.Length)], 5); // gem rock
        if (level >= 20 && r < 0.19) return ("764", 8);                               // gold
        if (r < 0.32) return ("290", 4);                                              // iron
        if (r < 0.50) return ("751", 3);                                              // copper
        if (r < 0.60) return (rng.NextDouble() < 0.5 ? "75" : level >= 25 ? "77" : "76", 5); // geode rocks
        return (rng.NextDouble() < 0.5 ? "668" : "670", 2);                           // stone
    }
}
