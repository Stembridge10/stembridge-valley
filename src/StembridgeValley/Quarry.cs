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

    /// <summary>
    /// The farm's side of the path to the quarry, per farm map: the off-map tiles that lead there, where you come
    /// back to, which way you walk to leave, and the strips kept clear of wild debris (the opening, the lane to it).
    /// </summary>
    public sealed record FarmSide(string Map, Point[] Exits, Point Arrival, int Dir, XRect Way, XRect Lane);

    public static readonly FarmSide[] Sides =
    {
        // Standard: a gap in the left bank under a rock wall.
        // (row 47, under the grass lip, is open at the edge too, so it is an exit as well: no walking off the map)
        new("Maps\\Farm", Row(-1, 44, 47), new(1, 45), 3, new(0, 43, 8, 5), new(0, 44, 16, 3)),
        // Meadowlands: a small plank bridge over the river on the left, to a little grass landing at the edge.
        new("Maps\\Farm_Ranching", Row(-1, 28, 30), new(1, 29), 3, new(0, 28, 4, 3), new(0, 27, 15, 5)),
        // Beach: the dock at the bottom runs on off the bottom edge.
        new("Maps\\Farm_Island", Column(110, 51, 53), new(52, 108), 2, new(51, 95, 3, 15), new(51, 95, 3, 15)),
        // Riverland: the small south-west bank landing joins the quarry path at the left edge.
        new("Maps\\Farm_Fishing", Row(-1, 39, 42), new(1, 40), 3, new(0, 39, 5, 4), new(0, 34, 14, 13)),
        // Forest: a short trail through the left woods.
        new("Maps\\Farm_Foraging", Row(-1, 26, 29), new(1, 27), 3, new(0, 26, 4, 4), new(0, 21, 13, 13)),
        // Hill-top: a break in the left ledge beside the southern dirt patch.
        new("Maps\\Farm_Mining", Row(-1, 36, 39), new(1, 37), 3, new(0, 36, 3, 4), new(0, 31, 12, 13)),
        // Wilderness: below the large west ledge.
        new("Maps\\Farm_Combat", Row(-1, 37, 40), new(1, 38), 3, new(0, 37, 3, 4), new(0, 32, 12, 13)),
        // Four Corners: the existing gap in the west tree belt.
        new("Maps\\Farm_FourCorners", Row(-1, 35, 38), new(1, 36), 3, new(0, 35, 5, 4), new(0, 30, 14, 13)),
    };

    private static Point[] Row(int x, int y0, int y1) => Enumerable.Range(y0, y1 - y0 + 1).Select(y => new Point(x, y)).ToArray();
    private static Point[] Column(int y, int x0, int x1) => Enumerable.Range(x0, x1 - x0 + 1).Select(x => new Point(x, y)).ToArray();

    /// <summary>This farm's side of the quarry path, or null when its farm map has none yet.</summary>
    public static FarmSide? SideOf(GameLocation? farm)
    {
        string map = (farm?.mapPath.Value ?? "").Replace('/', '\\');
        return Sides.FirstOrDefault(sd => sd.Map.Equals(map, StringComparison.OrdinalIgnoreCase));
    }
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
        else if (e.NameWithoutLocale.IsEquivalentTo("Maps/Farm_Ranching"))
            e.Edit(asset => AddMeadowBridge(asset.AsMap().Data));
        else if (e.NameWithoutLocale.IsEquivalentTo("Maps/Farm_Island"))
            e.Edit(asset => ExtendBeachDock(asset.AsMap().Data));
        else if (e.NameWithoutLocale.IsEquivalentTo("Maps/Farm_Fishing"))
            e.Edit(asset => OpenRiverlandQuarryPath(asset.AsMap().Data));
        else if (e.NameWithoutLocale.IsEquivalentTo("Maps/Farm_Foraging"))
            e.Edit(asset => OpenForestQuarryPath(asset.AsMap().Data));
        else if (e.NameWithoutLocale.IsEquivalentTo("Maps/Farm_Mining"))
            e.Edit(asset => OpenHilltopQuarryPath(asset.AsMap().Data));
        else if (e.NameWithoutLocale.IsEquivalentTo("Maps/Farm_Combat"))
            e.Edit(asset => OpenWildernessQuarryPath(asset.AsMap().Data));
        else if (e.NameWithoutLocale.IsEquivalentTo("Maps/Farm_FourCorners"))
            e.Edit(asset => OpenFourCornersQuarryPath(asset.AsMap().Data));
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
        Set(back, 0, 44, 587); Set(back, 1, 44, 587); Set(back, 2, 44, 587);
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

    /// <summary>
    /// Meadowlands: a plank bridge over the river on row 29 (Riverland's own bridge pieces), landing on a small
    /// grass spot cleared at the left edge. Invisible walls along both sides of the planks, like Riverland's
    /// bridges, so nobody steps off into the river. Matches the preview (meadow_D.json); a walk check of it finds
    /// no way off the map and no walking on water.
    /// </summary>
    private static void AddMeadowBridge(Map map)
    {
        TileSheet? s = map.TileSheets.FirstOrDefault(t => t.Id == "untitled tile sheet");
        Layer? back = map.GetLayer("Back"), buildings = map.GetLayer("Buildings");
        if (s == null || back == null || buildings == null || back.LayerWidth < 20 || back.LayerHeight < 40)
        {
            Log.Warn("Quarry: the Meadowlands map isn't the expected one; no path to the quarry.");
            return;
        }
        void Set(Layer layer, int x, int y, int? index) =>
            layer.Tiles[x, y] = index is int i ? new StaticTile(layer, s, BlendMode.Alpha, i) : null;

        Set(back, 8, 29, 1271);   // this river tile blocks walking (Passable=F); plain water under the bridge
        Set(buildings, 0, 28, null); Set(buildings, 1, 28, null); Set(buildings, 2, 28, null); Set(buildings, 3, 28, null); Set(buildings, 5, 28, 16); Set(buildings, 6, 28, 16); Set(buildings, 7, 28, 16);
        Set(buildings, 0, 29, null); Set(buildings, 1, 29, null); Set(buildings, 2, 29, null); Set(buildings, 3, 29, null); Set(buildings, 4, 29, 779); Set(buildings, 5, 29, 780); Set(buildings, 6, 29, 781); Set(buildings, 7, 29, 780); Set(buildings, 8, 29, 781); Set(buildings, 9, 29, 782);
        Set(buildings, 0, 30, null); Set(buildings, 1, 30, null); Set(buildings, 2, 30, null); Set(buildings, 3, 30, null); Set(buildings, 5, 30, 16); Set(buildings, 6, 30, 16); Set(buildings, 7, 30, 16); Set(buildings, 8, 30, 16);
        for (int x = 4; x <= 9; x++)
        {
            buildings.Tiles[x, 29].Properties["Passable"] = "T";
            buildings.Tiles[x, 29].Properties["NoSpawn"] = "All";
            buildings.Tiles[x, 29].Properties["Type"] = "Wood";
        }
    }

    /// <summary>
    /// Beach: the dock at the bottom runs on to the bottom edge (rows 102-109), same planks and railings as the
    /// dock above. The surf drawn over its end is cleared so it doesn't cover the dock or the player. Matches the
    /// preview (beach_A.json); a walk check finds no new way off the map.
    /// </summary>
    private static void ExtendBeachDock(Map map)
    {
        TileSheet? s = map.TileSheets.FirstOrDefault(t => t.Id == "untitled tile sheet2");
        Layer? back = map.GetLayer("Back"), buildings = map.GetLayer("Buildings"), over = map.GetLayer("AlwaysFront");
        if (s == null || back == null || buildings == null || back.LayerWidth < 60 || back.LayerHeight < 110)
        {
            Log.Warn("Quarry: the Beach map isn't the expected one; no path to the quarry.");
            return;
        }
        void Set(Layer layer, int x, int y, int? index) =>
            layer.Tiles[x, y] = index is int i ? new StaticTile(layer, s, BlendMode.Alpha, i) : null;

        for (int y = 102; y <= 109; y++)
        {
            bool odd = (y - 102) % 2 == 1;   // the dock alternates two plank rows and two railing pieces
            int[] planks = odd ? new[] { 552, 553, 554 } : new[] { 549, 550, 551 };
            for (int i = 0; i < 3; i++)
            {
                Set(back, 51 + i, y, planks[i]);
                back.Tiles[51 + i, y].Properties["NoSpawn"] = "All";
                Set(buildings, 51 + i, y, null);
                if (over != null && y >= 108)
                    Set(over, 51 + i, y, null);
            }
            Set(buildings, 50, y, odd ? 525 : 557);
            Set(buildings, 54, y, odd ? 556 : 524);
        }
    }

    // Each variant uses the same rock ledge tile recipe as the standard farm's north bank. The two wooded
    // variants continue the cleared strip only as far as the existing open ground.
    private static void OpenRiverlandQuarryPath(Map map) => OpenLeftQuarryGate(map, 39, 4);
    private static void OpenForestQuarryPath(Map map) => OpenLeftQuarryGate(map, 26, 3);
    private static void OpenHilltopQuarryPath(Map map) => OpenLeftQuarryGate(map, 36, 2);
    private static void OpenWildernessQuarryPath(Map map) => OpenLeftQuarryGate(map, 37, 2);
    private static void OpenFourCornersQuarryPath(Map map) => OpenLeftQuarryGate(map, 35, 4);

    private static void OpenLeftQuarryGate(Map map, int y0, int continuationX)
    {
        TileSheet? s = map.TileSheets.FirstOrDefault(t => t.Id == "untitled tile sheet");
        Layer? back = map.GetLayer("Back"), buildings = map.GetLayer("Buildings"), front = map.GetLayer("Front"), always = map.GetLayer("AlwaysFront");
        if (s == null || back == null || buildings == null || front == null || always == null || back.LayerWidth < 20 || back.LayerHeight <= y0 + 4)
        {
            Log.Warn("Quarry: the farm map isn't the expected one; no path to the quarry.");
            return;
        }
        void Set(Layer layer, int x, int y, int? index) =>
            layer.Tiles[x, y] = index is int i ? new StaticTile(layer, s, BlendMode.Alpha, i) : null;

        foreach (int x in Enumerable.Range(0, 3))
            Set(buildings, x, y0 - 5, x == 2 ? 444 : 16);
        int[][] cliff = { new[] { 468, 468, 469 }, new[] { 493, 492, 494 }, new[] { 518, 517, 519 }, new[] { 543, 542, 544 } };
        for (int row = 0; row < cliff.Length; row++)
            for (int x = 0; x < 3; x++)
                Set(buildings, x, y0 - 4 + row, cliff[row][x]);

        for (int y = y0 - 6; y < y0; y++)
            for (int x = 0; x < 3; x++)
            {
                Set(front, x, y, null);
                Set(always, x, y, null);
            }
        for (int x = 0; x <= continuationX; x++)
            for (int y = y0; y <= y0 + 3; y++)
            {
                Set(back, x, y, 587);
                Set(buildings, x, y, null);
                Set(front, x, y, null);
                Set(always, x, y, null);
            }
        int[] lip = { 413, 414, 438 };
        for (int x = 0; x < 3; x++)
        {
            Set(front, x, y0 + 3, lip[x]);
            Set(always, x, y0 + 4, null);
        }
    }

    /// <summary>Server: the farm's side of the path leads to its quarry; the quarry's right edge leads back.</summary>
    private static void UpdateWarps_Postfix(GameLocation __instance)
    {
        if (Game1.IsClient)
            return;
        if (Farms.IsFarm(__instance))
        {
            if (SideOf(__instance) is not { } side)
                return;
            string quarry = QuarryOf(__instance.Name);
            foreach (Point p in side.Exits)
                __instance.warps.Add(new Warp(p.X, p.Y, quarry, Arrival.X, Arrival.Y, false));
        }
        else if (IsQuarry(__instance))
        {
            string farm = FarmOfQuarry(__instance.Name);
            __instance.warps.Clear();
            Point back = SideOf(Game1.getLocationFromName(farm))?.Arrival ?? Farms.RoadEntry;
            foreach (int y in QuarryExitRows)
                __instance.warps.Add(new Warp(Width, y, farm, back.X, back.Y, false));
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
        if (Game1.currentLocation?.Name == farm && SideOf(Game1.currentLocation) is { } side)
        {
            // Step back from the edge, facing the farm.
            Game1.player.Halt();
            var pos = Game1.player.Position;
            Game1.player.Position = side.Dir is 1 or 3
                ? new Vector2(side.Arrival.X * 64, pos.Y)
                : new Vector2(pos.X, side.Arrival.Y * 64);
            Game1.player.faceDirection((side.Dir + 2) % 4);
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

    /// <summary>Remove natural debris only (stumps, boulders, logs, weeds, stones, twigs, grass), never anything a player placed.</summary>
    private static void ClearDebris(GameLocation loc, XRect area, XRect lane)
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
                    || (tf is StardewValley.TerrainFeatures.Tree t && lane.Contains(x, y) && IsFarmSide(loc) && !t.tapped.Value && !t.fertilized.Value)))
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
            if (Game1.getLocationFromName(farm) is { } f && SideOf(f) is { } side)
            {
                ClearDebris(f, side.Way, side.Lane);
                ClearDebris(f, side.Lane, side.Lane);
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
