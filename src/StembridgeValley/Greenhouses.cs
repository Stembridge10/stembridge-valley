using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Extensions;
using StardewValley.GameData.Buildings;
using StardewValley.Menus;

namespace StembridgeValley;

/// <summary>
/// First Fields unlocks the farm's own greenhouse: a real vanilla greenhouse building (the repaired one), with its
/// own inside, standing on the farm. Crops inside grow in every season, as in vanilla.
///  - The server builds it the moment the project finishes, at the spot the farm's map keeps for a greenhouse
///    (or the nearest clear ground). Wild debris there is cleared; nothing a player made is ever touched.
///  - Players can move it with Robin like any building. It can't be demolished (it's a reward, not a purchase).
///  - It's its own building type ("SV_Greenhouse": the vanilla greenhouse's data, with an inside of its own), so it
///    never takes over the world's one shared greenhouse inside (the host's), whatever order buildings load in.
/// </summary>
internal static class Greenhouses
{
    public const string Type = "SV_Greenhouse";
    private const int W = 7, H = 6;

    public static void Apply(IModHelper helper, Harmony harmony)
    {
        if (!Farms.Enabled)
            return;
        // The door opens on a player farm whatever the town's state.
        harmony.Patch(AccessTools.Method(typeof(GreenhouseBuilding), nameof(GreenhouseBuilding.OnUseHumanDoor)),
            prefix: new HarmonyMethod(typeof(Greenhouses), nameof(Door_Prefix)));
        // No vanilla "greenhouse moved" dirt patch on player farms (it's drawn at the main farm's spot).
        harmony.Patch(AccessTools.Method(typeof(Farm), nameof(Farm.ClearGreenhouseGrassTiles)),
            prefix: new HarmonyMethod(typeof(Greenhouses), nameof(Dirt_Prefix)));
        // Robin can move it, never demolish it.
        harmony.Patch(AccessTools.Method(typeof(CarpenterMenu), nameof(CarpenterMenu.CanDemolishThis), new[] { typeof(Building) }),
            postfix: new HarmonyMethod(typeof(Greenhouses), nameof(Demolish_Postfix)));
        helper.Events.Content.AssetRequested += (_, e) =>
        {
            if (e.NameWithoutLocale.IsEquivalentTo("Data/Buildings"))
                e.Edit(asset =>
                {
                    var data = asset.AsDictionary<string, BuildingData>().Data;
                    if (!data.TryGetValue("Greenhouse", out var vanilla))
                        return;
                    var copy = (BuildingData)AccessTools.Method(typeof(object), "MemberwiseClone").Invoke(vanilla, null)!;
                    copy.NonInstancedIndoorLocation = null; // an inside of its own, never the shared one
                    copy.Builder = null;                    // not for sale: First Fields builds it
                    data[Type] = copy;
                }, AssetEditPriority.Late);
        };
        helper.Events.GameLoop.SaveLoaded += (_, _) => MarkAll();
        helper.Events.GameLoop.DayStarted += (_, _) => MarkAll();
        if (SV.Role == Role.Server)
            helper.ConsoleCommands.Add("sv_greenhouse", "sv_greenhouse <farm>: build that farm's greenhouse now (testing/repair).",
                (_, args) => Log.Info(args.Length > 0 && Game1.getLocationFromName(args[0]) is { } f ? Grant(f) ?? "not built" : "usage: sv_greenhouse SV_Farm1"));
    }

    public static Building? Of(GameLocation farm) => farm.buildings.FirstOrDefault(b => b.buildingType.Value == Type);

    public static bool Has(GameLocation farm) => Of(farm) != null;

    /// <summary>
    /// Server: build the farm's greenhouse if it hasn't got one. Returns where it went (for the message), or null.
    /// </summary>
    public static string? Grant(GameLocation farm)
    {
        if (!Game1.IsMasterGame || !Farms.IsFarm(farm))
            return null;
        if (Has(farm))
            return "where it was";
        Point want = Preferred(farm);
        Point? spot = Spots(farm, want).Cast<Point?>().FirstOrDefault(p => WhyNot(farm, p!.Value) == null);
        if (spot is not { } at)
        {
            Log.Warn($"Greenhouse: no clear 7x8 ground on {farm.Name} (wanted {want.X},{want.Y}: {WhyNot(farm, want)}).");
            return null;
        }
        Clear(farm, at);
        Building gh = Building.CreateInstanceFromId(Type, new Vector2(at.X, at.Y));
        gh.load();
        if (!farm.buildStructure(gh, new Vector2(at.X, at.Y), Game1.player, skipSafetyChecks: true))
        {
            Log.Warn($"Greenhouse: couldn't place it on {farm.Name} at {at.X},{at.Y}.");
            return null;
        }
        Mark(gh);
        Log.Info($"Greenhouse: built on {farm.Name} at {at.X},{at.Y} (inside: {gh.GetIndoorsName()}).");
        return at == want ? "in its usual spot" : $"near the usual spot";
    }

    /// <summary>Where the farm's map keeps a greenhouse (vanilla's spot for that map type).</summary>
    private static Point Preferred(GameLocation farm)
    {
        if (farm.TryGetMapPropertyAs("GreenhouseLocation", out Vector2 v, required: false))
            return new((int)v.X, (int)v.Y);
        string map = farm.mapPath.Value?.Replace('/', '\\') ?? "";
        return map.EndsWith("Farm_FourCorners") ? new(36, 29)
             : map.EndsWith("Farm_Island") ? new(14, 14)
             : new(25, 10);
    }

    /// <summary>The preferred spot, then every other spot on the map by distance from it.</summary>
    private static IEnumerable<Point> Spots(GameLocation farm, Point want)
    {
        yield return want;
        int w = farm.map.Layers[0].LayerWidth, h = farm.map.Layers[0].LayerHeight;
        foreach (var p in Enumerable.Range(1, w - W - 2).SelectMany(x => Enumerable.Range(1, h - H - 3).Select(y => new Point(x, y)))
                     .OrderBy(p => Math.Abs(p.X - want.X) + Math.Abs(p.Y - want.Y)))
            yield return p;
    }

    /// <summary>
    /// What stops a greenhouse here: its 7x6 footprint plus the 3x2 entrance below the door and a one-tile margin
    /// must be open farm ground with nothing anyone made on it. Wild debris is fine (it's cleared).
    /// </summary>
    private static string? WhyNot(GameLocation farm, Point at)
    {
        for (int x = at.X - 1; x <= at.X + W; x++)
            for (int y = at.Y - 1; y <= at.Y + H + 2; y++)
            {
                var v = new Vector2(x, y);
                if (!farm.isTileOnMap(v))
                    return $"{x},{y} off the map";
                if (farm.getBuildingAt(v) is { } b)
                    return $"{x},{y} building {b.buildingType.Value}";
                if (farm.buildings.Any(bb => bb.GetAdditionalPlacementTiles().Any(t => t.TileArea.GetPoints().Any(pt => pt.X + bb.tileX.Value == x && pt.Y + bb.tileY.Value == y))))
                    return $"{x},{y} in front of a building's door";
                if (farm.doesTileHaveProperty(x, y, "Diggable", "Back") == null
                    && !farm.doesTileHavePropertyNoNull(x, y, "Buildable", "Back").Equals("t", StringComparison.OrdinalIgnoreCase)
                    && !farm.doesTileHavePropertyNoNull(x, y, "Buildable", "Back").Equals("true", StringComparison.OrdinalIgnoreCase))
                    return $"{x},{y} not buildable";
                if (farm.doesTileHavePropertyNoNull(x, y, "Buildable", "Back").Equals("f", StringComparison.OrdinalIgnoreCase))
                    return $"{x},{y} not buildable";
                if (farm.isWaterTile(x, y) || farm.map.GetLayer("Buildings")?.Tiles[x, y] != null)
                    return $"{x},{y} water or map wall";
                if (farm.terrainFeatures.TryGetValue(v, out var tf) && !IsWild(tf))
                    return $"{x},{y} {tf.GetType().Name}";
                if (farm.objects.TryGetValue(v, out var o) && !(o.IsWeeds() || o.IsBreakableStone() || o.IsTwig()))
                    return $"{x},{y} {o.Name}";
            }
        var pixels = new Rectangle((at.X - 1) * 64, (at.Y - 1) * 64, (W + 2) * 64, (H + 4) * 64);
        if (farm.farmers.Any(f => f.GetBoundingBox().Intersects(pixels)))
            return "someone standing there";
        return null;
    }

    private static bool IsWild(StardewValley.TerrainFeatures.TerrainFeature tf) => tf switch
    {
        StardewValley.TerrainFeatures.Grass => true,
        StardewValley.TerrainFeatures.HoeDirt d => d.crop == null,
        StardewValley.TerrainFeatures.Tree t => !t.tapped.Value,
        _ => false,
    };

    private static void Clear(GameLocation farm, Point at)
    {
        var area = new Rectangle(at.X - 1, at.Y - 1, W + 2, H + 4);
        for (int x = area.Left; x < area.Right; x++)
            for (int y = area.Top; y < area.Bottom; y++)
            {
                farm.objects.Remove(new Vector2(x, y));
                farm.terrainFeatures.Remove(new Vector2(x, y));
            }
        var pixels = new Rectangle(area.X * 64, area.Y * 64, area.Width * 64, area.Height * 64);
        farm.resourceClumps.RemoveWhere(r => r.getBoundingBox().Intersects(pixels));
        farm.largeTerrainFeatures.RemoveWhere(l => l.getBoundingBox().Intersects(pixels));
    }

    /// <summary>The greenhouse's inside counts as a greenhouse (crops ignore seasons). Re-marked on load, as the flag isn't saved.</summary>
    private static void Mark(Building gh)
    {
        if (gh.GetIndoors() is { } inside && !inside.IsGreenhouse)
            inside.IsGreenhouse = true;
    }

    private static void MarkAll()
    {
        if (!Game1.IsMasterGame)
            return;
        var marked = new List<string>();
        foreach (string name in Farms.AllNames)
            if (Game1.getLocationFromName(name) is { } farm && Of(farm) is { } gh)
            {
                Mark(gh);
                marked.Add($"{name} ({gh.GetIndoors()?.terrainFeatures.Count() ?? 0} planted/tilled inside)");
            }
        if (marked.Count > 0)
            Log.Info("Greenhouse: ready on " + string.Join(", ", marked));
    }

    // ---------- patches ----------

    private static bool Door_Prefix(GreenhouseBuilding __instance, ref bool __result)
    {
        if (!Farms.IsFarm(__instance.GetParentLocation()))
            return true;
        __result = true;
        return false;
    }

    private static bool Dirt_Prefix(Farm __instance) => !Farms.IsFarm(__instance);


    private static void Demolish_Postfix(Building building, ref bool __result)
    {
        if (building?.buildingType.Value == Type && Farms.IsFarm(building.GetParentLocation()))
            __result = false;
    }
}
