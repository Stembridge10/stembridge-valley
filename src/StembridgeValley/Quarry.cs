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
/// The Hills and the quarry.
///  - Every farm has its own "Hills": the whole of Stardew's Hill-top farm map, reached by walking off the left side
///    of the farm (a path opens through the farm's left bank). The Hills keep their own rivers, cliffs and bridges,
///    so nothing is cut off. They belong to the farm: same owners, visitors look but don't touch, crops grow there.
///  - Mining 15 (best level among the farm's members) opens the Hills quarry: rocks appear there every morning,
///    more and better as Mining goes up (gold at 20, gems at 25, more at 30, iridium at 35, mystic stones at 40, double at 45).
/// </summary>
internal static class Quarry
{
    public const string Prefix = "SV_Hills";
    private const string HillsMap = "Maps\\Farm_Mining";

    /// <summary>Farm side: walking off the left edge on these rows goes to the Hills.</summary>
    public static readonly int[] FarmExitRows = { 44, 45, 46 };
    /// <summary>Where you arrive on the farm coming back.</summary>
    public static readonly Point FarmArrival = new(1, 45);
    /// <summary>Where you arrive in the Hills (just inside the Hill-top farm's road entrance on its right edge).</summary>
    public static readonly Point HillsArrival = new(78, 17);

    /// <summary>The quarry's dirt floor (the Hill-top farm's fenced plateau).</summary>
    public static readonly XRect Area = new(5, 37, 22, 8);
    /// <summary>Just below the quarry stairs.</summary>
    public static readonly Point Steps = new(16, 50);
    /// <summary>The bridge on the way from the entrance to the quarry.</summary>
    public static readonly Point Bridge = new(31, 30);

    public const int UnlockLevel = 15;
    private const int MaxRocks = 45;

    public static string HillsOf(string farmName) => Prefix + farmName.Substring(Farms.Prefix.Length);
    public static string FarmOfHills(string hillsName) => Farms.Prefix + hillsName.Substring(Prefix.Length);
    public static bool IsHills(GameLocation? loc) => loc != null && loc.Name.StartsWith(Prefix, StringComparison.Ordinal);

    public static void Apply(IModHelper helper, Harmony harmony)
    {
        if (!Farms.Enabled)
            return;
        helper.Events.Content.AssetRequested += OnAssetRequested;
        harmony.Patch(AccessTools.Method(typeof(GameLocation), nameof(GameLocation.updateWarps)),
            postfix: new HarmonyMethod(typeof(Quarry), nameof(UpdateWarps_Postfix)));
        if (SV.Role == Role.Server)
        {
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.GameLoop.SaveLoaded += (_, _) => ClearAllWays();
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
                data.TryGetValue("Farm_Hilltop", out var hilltop);
                var clone = AccessTools.Method(typeof(object), "MemberwiseClone");
                bool server = SV.Role == Role.Server;
                foreach (string farm in Farms.DataNames())
                {
                    // Hill-top farm's fish, forage and artifact spots; crops can be planted like on a farm.
                    var entry = hilltop != null ? (LocationData)clone.Invoke(hilltop, null)! : new LocationData();
                    entry.DisplayName = "Hills";
                    entry.CanPlantHere = true;
                    entry.DefaultArrivalTile = HillsArrival;
                    entry.CreateOnLoad = server
                        ? new CreateLocationData { MapPath = HillsMap, AlwaysActive = true }
                        : new CreateLocationData { MapPath = "Maps\\Cellar", AlwaysActive = false };
                    data[HillsOf(farm)] = entry;
                }
            }, AssetEditPriority.Late);
        else if (e.NameWithoutLocale.IsEquivalentTo("Maps/Farm"))
            e.Edit(asset => OpenLeftEdge(asset.AsMap().Data));
    }

    /// <summary>
    /// A path off the farm's left side (rows 44-46). The left bank above it ends in a short cliff and the one below
    /// starts with a grass lip: the same pieces the farm map uses for its own banks.
    /// </summary>
    private static void OpenLeftEdge(Map map)
    {
        TileSheet? s = map.TileSheets.FirstOrDefault(t => t.Id == "untitled tile sheet");
        Layer? back = map.GetLayer("Back"), buildings = map.GetLayer("Buildings"), front = map.GetLayer("Front");
        if (s == null || back == null || buildings == null || front == null || back.LayerWidth < 10 || back.LayerHeight < 50)
        {
            Log.Warn("Hills: the farm map isn't the standard one; no path to the Hills.");
            return;
        }
        void Set(Layer layer, int x, int y, int? index) =>
            layer.Tiles[x, y] = index is int i ? new StaticTile(layer, s, BlendMode.Alpha, i) : null;

        for (int y = 42; y <= 47; y++)
            for (int x = 0; x <= 2; x++)
            {
                Set(back, x, y, 587);
                Set(buildings, x, y, null);
                Set(front, x, y, null);
            }
        // Bottom of the upper bank: grass, then a short cliff face.
        Set(back, 0, 41, 351); Set(back, 1, 41, 352); Set(back, 2, 41, 176);
        Set(buildings, 0, 41, 16); Set(buildings, 1, 41, 16); Set(buildings, 2, 41, 444);
        Set(buildings, 0, 42, 468); Set(buildings, 1, 42, 468); Set(buildings, 2, 42, 469);
        Set(buildings, 0, 43, 493); Set(buildings, 1, 43, 493); Set(buildings, 2, 43, 494);
        // Top of the lower bank: a grass lip, and the bank's edge carries on below it.
        Set(front, 0, 47, 413); Set(front, 1, 47, 414); Set(front, 2, 47, 438);
        Set(back, 2, 48, 175); Set(buildings, 2, 48, 394);
    }

    /// <summary>Server: the farm's left edge leads to its Hills; the Hills' road (to the bus stop in vanilla) leads back to the farm.</summary>
    private static void UpdateWarps_Postfix(GameLocation __instance)
    {
        if (Game1.IsClient)
            return;
        if (Farms.IsFarm(__instance))
        {
            string hills = HillsOf(__instance.Name);
            foreach (int y in FarmExitRows)
                __instance.warps.Add(new Warp(-1, y, hills, HillsArrival.X, HillsArrival.Y, false));
        }
        else if (IsHills(__instance))
        {
            string farm = FarmOfHills(__instance.Name);
            for (int i = __instance.warps.Count - 1; i >= 0; i--)
            {
                Warp w = __instance.warps[i];
                if (w.TargetName == "BusStop")
                    __instance.warps[i] = new Warp(w.X, w.Y, farm, FarmArrival.X, FarmArrival.Y, false);
                else if (w.TargetName is "FarmCave" or "Greenhouse" or "FarmHouse")
                    __instance.warps.RemoveAt(i);
            }
        }
    }

    /// <summary>Server: make the Hills for a farm (once). Returns it, not yet sent to players.</summary>
    public static GameLocation EnsureHills(string farmName)
    {
        string name = HillsOf(farmName);
        if (Game1.getLocationFromName(name) is { } existing)
            return existing;
        GameLocation hills = Game1.CreateGameLocation(name);
        Game1.locations.Add(hills);
        ClearWay(hills);
        return hills;
    }

    // ---------- the quarry ----------

    public static int BestMiningLevel(string farm) =>
        Farms.MembersOf(farm).Select(f => Skills.Level(f, Skills.Mining)).DefaultIfEmpty(0).Max();

    private static int RocksPerDay(int level) => (level >= 30 ? 10 : 6) * (level >= 45 ? 2 : 1);

    /// <summary>The way across the first bridge. The Hill-top map puts a big stump right where you step off it, and a
    /// new player can't break that yet, so keep it clear.</summary>
    public static readonly XRect Way = new(Bridge.X - 2, Bridge.Y - 2, 5, 12);
    /// <summary>The farm's side of the left-edge opening: the farm's starting debris (stumps, rocks, weeds) lands
    /// there too.</summary>
    public static readonly XRect FarmWay = new(0, FarmExitRows[0] - 1, 8, FarmExitRows.Length + 2);

    private static void ClearWay(GameLocation loc) => ClearDebris(loc, Way);

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
                if (loc.terrainFeatures.TryGetValue(tile, out var tf) && tf is StardewValley.TerrainFeatures.Grass)
                    loc.terrainFeatures.Remove(tile);
            }
    }

    private static void OnOneSecond(object? sender, OneSecondUpdateTickedEventArgs e)
    {
        if (Context.IsWorldReady && e.IsMultipleOf(600))
            ClearAllWays();
    }

    private static void ClearAllWays()
    {
        foreach (string farm in Farms.AllNames)
        {
            if (Game1.getLocationFromName(HillsOf(farm)) is { } hills)
                ClearWay(hills);
            if (Game1.getLocationFromName(farm) is { } f)
                ClearDebris(f, FarmWay);
        }
    }

    private static void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        foreach (string farm in Farms.AllNames)
        {
            if (Game1.getLocationFromName(HillsOf(farm)) is not { } hills)
                continue;
            ClearWay(hills);
            int level = BestMiningLevel(farm);
            if (level >= UnlockLevel)
                Spawn(hills, level, RocksPerDay(level));
        }
    }

    /// <summary>Reaching a quarry level fills the quarry right away (no waiting for tomorrow).</summary>
    private static void OnLevelUp(Farmer who, int skill, int level)
    {
        if (skill != Skills.Mining || level < UnlockLevel || Skills.UnlockAt(skill, level) == null)
            return;
        if (Farms.HomeFarmOf(who) is { } farm && Game1.getLocationFromName(HillsOf(farm)) is { } hills)
        {
            int placed = Spawn(hills, level, RocksPerDay(level));
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
                if (loc.doesTileHavePropertyNoNull((int)tile.X, (int)tile.Y, "Type", "Back") != "Dirt"
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
