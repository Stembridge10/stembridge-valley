using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using xTile;
using xTile.Layers;
using xTile.Tiles;
using XRect = Microsoft.Xna.Framework.Rectangle;
using SObject = StardewValley.Object;

namespace StembridgeValley;

/// <summary>
/// Bigger farms and the farm quarry.
///  - Every farm map gets a new southern area: the old bottom tree line opens up, and below it sits the lower half of
///    Stardew's own Hill-top farm (a river with wooden bridges, a fenced quarry plateau with stairs, and a second
///    plateau for later). The south exit to the forest moves down to the new bottom edge.
///  - Mining 15 (best level among the farm's members) opens the quarry: rocks appear there every morning,
///    more and better as Mining goes up (gold at 20, gems at 25, iridium at 35, mystic stones at 40, double at 45).
/// Both the server and every player's game edit the map the same way, so it lines up for everyone.
/// </summary>
internal static class Quarry
{
    private const int OldHeight = 65;    // vanilla standard farm
    private const int Cut = 62;          // standard rows 0-61 stay; the new area starts here
    private const int SrcY = 28;         // Hill-top rows 28-64 are copied in
    public const int NewHeight = Cut + (OldHeight - SrcY); // 99
    private const int Shift = Cut - SrcY;                  // Hill-top row + 34 = farm row

    /// <summary>The quarry's dirt floor (Hill-top's fenced plateau, x 5-26, y 37-44, moved down).</summary>
    public static readonly XRect Area = new(5, 37 + Shift, 22, 8);
    /// <summary>Just below the quarry stairs.</summary>
    public static readonly Point Steps = new(16, 50 + Shift);
    /// <summary>The first bridge over the river, where the old farm meets the new area.</summary>
    public static readonly Point Bridge = new(31, 30 + Shift);

    public const int UnlockLevel = 15;
    private const int MaxRocks = 45;

    private static IModHelper Helper = null!;

    public static void Apply(IModHelper helper)
    {
        Helper = helper;
        helper.Events.Content.AssetRequested += OnAssetRequested;
        if (SV.Role == Role.Server)
        {
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecond;
            Skills.LevelUp += OnLevelUp;
        }
    }

    // ---------- the map ----------

    private static void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo("Maps/Farm"))
            e.Edit(asset => Extend(asset.AsMap()), AssetEditPriority.Early);
        else if (e.NameWithoutLocale.IsEquivalentTo("Maps/Forest"))
            e.Edit(asset =>
            {
                // The forest's path north lands at the farm's (new) bottom edge.
                var map = asset.AsMap().Data;
                if (map.Properties.TryGetValue("Warp", out var warp))
                    map.Properties["Warp"] = RewriteWarps(warp.ToString(), (target, fromY, toY) =>
                        (fromY, target == "Farm" && toY >= OldHeight - 5 && toY < OldHeight ? toY + (NewHeight - OldHeight) : toY));
            });
    }

    private static void Extend(IAssetDataForMap asset)
    {
        Map map = asset.Data;
        if (map.Layers[0].LayerHeight != OldHeight)
            return;
        Map hill = Helper.GameContent.Load<Map>("Maps/Farm_Mining");

        asset.ExtendMap(minHeight: NewHeight);
        // Open the old bottom tree line (and the bush/tree bits that hung over it) so you can walk south.
        TileSheet? ground = map.TileSheets.FirstOrDefault(t => t.Id == "untitled tile sheet");
        Clear(map, ground, 3, 58, 68, 61);
        Clear(map, ground, 66, 58, 70, 59);
        Clear(map, ground, 69, 60, 76, 61);
        // The new area.
        asset.PatchMap(hill, new XRect(0, SrcY, 80, OldHeight - SrcY), new XRect(0, Cut, 80, OldHeight - SrcY), PatchMapMode.Replace);
        // Tile-sheet tags (bridge planks are walkable, water is water) differ between the two maps even though the
        // art is shared: the farm tags some river tiles as walls. Point the new area at its own copy of the Hill-top
        // sheet so every tile keeps exactly the behaviour it has on the Hill-top farm.
        RebindToHillSheet(hill, map);
        // The south exit moves to the new bottom edge.
        if (map.Properties.TryGetValue("Warp", out var warp))
            map.Properties["Warp"] = RewriteWarps(warp.ToString(), (target, fromY, toY) => (fromY == OldHeight ? NewHeight : fromY, toY));
    }

    private const string HillSheetId = "zz_sv_hilltop";

    private static void RebindToHillSheet(Map hill, Map map)
    {
        TileSheet? src = hill.TileSheets.FirstOrDefault(t => t.Id == "untitled tile sheet");
        if (src == null)
        {
            Log.Warn("Bigger farm: Hill-top tile sheet not found; the new area may not be walkable.");
            return;
        }
        var copy = new TileSheet(HillSheetId, map, src.ImageSource, src.SheetSize, src.TileSize);
        foreach (var p in src.Properties)   // includes every per-tile tag (stored as @TileIndex@n@Key)
            copy.Properties[p.Key] = p.Value;
        map.AddTileSheet(copy);

        int moved = 0;
        foreach (Layer from in hill.Layers)
        {
            Layer? to = map.GetLayer(from.Id);
            if (to == null)
                continue;
            for (int y = SrcY; y < OldHeight; y++)
                for (int x = 0; x < from.LayerWidth; x++)
                {
                    if (from.Tiles[x, y] is not { } s || s.TileSheet != src || to.Tiles[x, y + Shift] is not { } d)
                        continue;
                    to.Tiles[x, y + Shift] = Rebind(d, to, copy);
                    moved++;
                }
        }
        Log.Info($"Bigger farm: {moved} tiles use the Hill-top tile tags.");
    }

    private static Tile Rebind(Tile t, Layer layer, TileSheet sheet)
    {
        Tile n = t is AnimatedTile a
            ? new AnimatedTile(layer, a.TileFrames.Select(f => (StaticTile)Rebind(f, layer, sheet)).ToArray(), a.FrameInterval)
            : new StaticTile(layer, sheet, t.BlendMode, t.TileIndex);
        foreach (var p in t.Properties)
            n.Properties[p.Key] = p.Value;
        return n;
    }

    /// <summary>Open dirt: nothing standing on it.</summary>
    private static void Clear(Map map, TileSheet? ground, int x0, int y0, int x1, int y1)
    {
        foreach (Layer layer in map.Layers)
        {
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    if (layer.Id == "Back")
                    {
                        if (ground != null)
                            layer.Tiles[x, y] = new StaticTile(layer, ground, BlendMode.Alpha, 587);
                    }
                    else
                        layer.Tiles[x, y] = null;
                }
        }
    }

    /// <summary>Map "Warp" property: groups of "fromX fromY target toX toY". <paramref name="change"/> gets (target, fromY, toY) and returns the new pair.</summary>
    private static string RewriteWarps(string warps, Func<string, int, int, (int FromY, int ToY)> change)
    {
        string[] p = warps.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 4 < p.Length; i += 5)
        {
            if (!int.TryParse(p[i + 1], out int fromY) || !int.TryParse(p[i + 4], out int toY))
                continue;
            var (newFrom, newTo) = change(p[i + 2], fromY, toY);
            p[i + 1] = newFrom.ToString();
            p[i + 4] = newTo.ToString();
        }
        return string.Join(' ', p);
    }

    // ---------- the quarry ----------

    public static int BestMiningLevel(string farm) =>
        Farms.MembersOf(farm).Select(f => Skills.Level(f, Skills.Mining)).DefaultIfEmpty(0).Max();

    private static int RocksPerDay(int level) => (level >= 30 ? 10 : 6) * (level >= 45 ? 2 : 1);

    /// <summary>The walk from the bridge down to the quarry stairs. New farms copy in the Hill-top farm's stumps and
    /// boulders, and one lands right where you step off the bridge; a new player can't break it yet, so keep this way clear.</summary>
    public static readonly XRect Way = new(Bridge.X - 2, Bridge.Y - 2, 5, 12);

    private static void ClearWay(GameLocation loc)
    {
        for (int i = loc.resourceClumps.Count - 1; i >= 0; i--)
        {
            var c = loc.resourceClumps[i];
            if (new XRect((int)c.Tile.X, (int)c.Tile.Y, c.width.Value, c.height.Value).Intersects(Way))
                loc.resourceClumps.RemoveAt(i);
        }
    }

    private static void OnOneSecond(object? sender, OneSecondUpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;
        foreach (string farm in Farms.AllNames)
            if (Game1.getLocationFromName(farm) is { } loc && loc.Map.Layers[0].LayerHeight == NewHeight)
                ClearWay(loc);
    }

    private static void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        foreach (string farm in Farms.AllNames)
        {
            int level = BestMiningLevel(farm);
            if (level >= UnlockLevel && Game1.getLocationFromName(farm) is { } loc)
                Spawn(loc, level, RocksPerDay(level));
        }
    }

    /// <summary>Reaching a quarry level fills the quarry right away (no waiting for tomorrow).</summary>
    private static void OnLevelUp(Farmer who, int skill, int level)
    {
        if (skill != Skills.Mining || level < UnlockLevel || Skills.UnlockAt(skill, level) == null)
            return;
        if (Farms.HomeFarmOf(who) is { } farm && Game1.getLocationFromName(farm) is { } loc)
        {
            int placed = Spawn(loc, level, RocksPerDay(level));
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
