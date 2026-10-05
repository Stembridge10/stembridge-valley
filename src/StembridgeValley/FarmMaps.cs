using System.Text.Json;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Locations;
using StardewValley.Locations;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace StembridgeValley;

/// <summary>
/// Each farm's map (Standard, Riverland, Forest, Hill-top, Wilderness, Four Corners, Beach, Meadowlands), picked once
/// by the farm's owner the first time they arrive, and kept forever.
///  - farm-maps.json (server state) is the authority: { "SV_Farm1": "Farm_Island", ... }. A farm that isn't listed is
///    still waiting for its owner to pick. The first time this version runs, every farm that already has people on it
///    is written down as "Farm" (Standard), so existing farms never change.
///  - The map isn't saved with the world (Stardew rebuilds it from Data/Locations on load), so the server's
///    Data/Locations entry for each farm uses the map from farm-maps.json.
///  - Each farm carries its map in modData (SV.FarmMap; SV.FarmMapPending = "1" while it waits), so players' games
///    know which map's fish and forage apply, and the owner's game knows to ask.
///  - The server checks the request: from the owner, farm still waiting, nothing built on it yet, a known map.
/// Farm-type extras the vanilla game only gives the main farm are switched on per farm: Forest forage, Hill-top and
/// Four Corners ore, Beach forage and ocean crab pots, Wilderness night monsters, Meadowlands' coop and two chickens.
/// </summary>
internal static class FarmMaps
{
    public const string MapKey = "SV.FarmMap", PendingKey = "SV.FarmMapPending";
    private const string MsgPick = "FarmMapPick", MsgReply = "FarmMapReply";

    public sealed record Kind(string Map, string DataKey, string Title, string DescKey);

    /// <summary>Every farm map, in the order the picker lists them.</summary>
    public static readonly Kind[] Kinds =
    {
        new("Farm", "Farm_Standard", "Standard", "Strings\\UI:Character_FarmStandard"),
        new("Farm_Fishing", "Farm_Riverland", "Riverland", "Strings\\UI:Character_FarmFishing"),
        new("Farm_Foraging", "Farm_Forest", "Forest", "Strings\\UI:Character_FarmForaging"),
        new("Farm_Mining", "Farm_Hilltop", "Hill-top", "Strings\\UI:Character_FarmMining"),
        new("Farm_Combat", "Farm_Wilderness", "Wilderness", "Strings\\UI:Character_FarmCombat"),
        new("Farm_FourCorners", "Farm_FourCorners", "Four Corners", "Strings\\UI:Character_FarmFourCorners"),
        new("Farm_Island", "Farm_Beach", "Beach", "Strings\\UI:Character_FarmBeach"),
        new("Farm_Ranching", "Farm_MeadowlandsFarm", "Meadowlands", "Strings\\1_6_Strings:Farm_Ranching_Description"),
    };

    public static Kind? KindOf(string? map) => Kinds.FirstOrDefault(k => k.Map.Equals(map, StringComparison.OrdinalIgnoreCase));

    /// <summary>Maps players can pick: the ones whose path to the quarry is built.</summary>
    public static IEnumerable<Kind> Offered => Kinds.Where(k => Quarry.Sides.Any(s => s.Map.Equals("Maps\\" + k.Map, StringComparison.OrdinalIgnoreCase)));

    private static IModHelper Helper = null!;
    private static string ModId = "";
    private static Dictionary<string, string> chosen = new();
    private static bool fileExisted;

    private static string FilePath => Path.Combine(SV.StateDir, "farm-maps.json");

    public static void Apply(IModHelper helper, Harmony harmony)
    {
        if (!Farms.Enabled)
            return;
        Helper = helper;
        ModId = helper.ModRegistry.ModID;
        helper.Events.Multiplayer.ModMessageReceived += OnMessage;
        helper.Events.Content.AssetRequested += OnAssetRequested;
        if (SV.Role == Role.Server)
        {
            Load(); // OnServerLoaded is called by Server once the farm members are loaded
            harmony.Patch(AccessTools.Method(typeof(Farm), nameof(Farm.performTenMinuteUpdate)),
                postfix: new HarmonyMethod(typeof(FarmMaps), nameof(TenMinute_Postfix)));
        }
        else
        {
            helper.Events.GameLoop.OneSecondUpdateTicked += OnClientSecond;
            helper.Events.GameLoop.SaveLoaded += (_, _) => { asked = false; dataFor.Clear(); };
        }
    }

    // ---------- which map ----------

    /// <summary>The map asset a farm uses ("Maps\\Farm_Island"). Server: from farm-maps.json; players: from the farm.</summary>
    public static string MapPathOf(string farm)
    {
        string? map = SV.Role == Role.Server
            ? (chosen.TryGetValue(farm, out string? m) ? m : null)
            : Game1.getLocationFromName(farm)?.modData.TryGetValue(MapKey, out string? n) == true ? n : null;
        return "Maps\\" + (KindOf(map)?.Map ?? Farms.TestFarmMapName ?? "Farm");
    }

    /// <summary>Farm reset: the farm waits for its owner to pick a map again.</summary>
    public static void Forget(string farm)
    {
        if (chosen.Remove(farm))
            Save();
    }

    public static bool IsPending(GameLocation? farm) => farm != null && farm.modData.TryGetValue(PendingKey, out string? p) && p == "1";

    private static void Load()
    {
        try
        {
            fileExisted = File.Exists(FilePath);
            if (fileExisted)
                chosen = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't read farm-maps.json: {ex.Message}");
        }
    }

    private static void Save()
    {
        try
        {
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(chosen, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't write farm-maps.json: {ex.Message}");
        }
    }

    /// <summary>
    /// Server, after the world and the farm members load: lock in existing farms the first time, then label every farm.
    /// </summary>
    public static void OnServerLoaded()
    {
        if (!fileExisted)
        {
            // First run with farm maps: farms people already live on keep the Standard map they have.
            foreach (string farm in Farms.AllNames)
                if (FarmRoster.Members(farm).Count > 0)
                    chosen[farm] = "Farm";
            Save();
            fileExisted = true;
            Log.Info($"Farm maps: kept the Standard map for {chosen.Count} farm(s) people already live on.");
        }
        if (Farms.TestFarmMapName is { } test)
        {
            // Test worlds: SV_TEST_FARM_MAP picks the map for every farm without asking.
            foreach (string farm in Farms.AllNames)
                chosen.TryAdd(farm, test);
        }
        Label();
    }

    /// <summary>Server: SV.FarmMap / SV.FarmMapPending on every farm, matching farm-maps.json.</summary>
    public static void Label()
    {
        foreach (string farm in Farms.AllNames)
        {
            if (Game1.getLocationFromName(farm) is not { } loc)
                continue;
            if (chosen.TryGetValue(farm, out string? map))
            {
                Set(loc, MapKey, map);
                loc.modData.Remove(PendingKey);
            }
            else
            {
                loc.modData.Remove(MapKey);
                Set(loc, PendingKey, "1");
            }
        }
    }

    private static void Set(GameLocation loc, string key, string value)
    {
        if (!loc.modData.TryGetValue(key, out string? old) || old != value)
            loc.modData[key] = value;
    }

    // ---------- data: fish, forage and farm-type map settings ----------

    /// <summary>Players: which map each farm's Data/Locations entry was built for (rebuilt when a farm's map arrives).</summary>
    private static readonly Dictionary<string, string> dataFor = new();

    private static void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo("Data/Locations"))
            e.Edit(asset =>
            {
                var data = asset.AsDictionary<string, LocationData>().Data;
                var clone = AccessTools.Method(typeof(object), "MemberwiseClone");
                foreach (string farm in Farms.DataNames())
                {
                    if (!data.TryGetValue(farm, out LocationData? entry))
                        continue;
                    string path = MapPathOf(farm);
                    Kind kind = KindOf(path["Maps\\".Length..]) ?? Kinds[0];
                    if (data.TryGetValue(kind.DataKey, out LocationData? typed))
                    {
                        // This map's own fish, forage and artifact spots; keep our name, arrival and creation.
                        var copy = (LocationData)clone.Invoke(typed, null)!;
                        copy.DisplayName = entry.DisplayName;
                        copy.DefaultArrivalTile = entry.DefaultArrivalTile;
                        copy.CreateOnLoad = entry.CreateOnLoad;
                        entry = copy;
                    }
                    if (SV.Role == Role.Server && entry.CreateOnLoad != null)
                        entry.CreateOnLoad.MapPath = path;
                    data[farm] = entry;
                    dataFor[farm] = path;
                }
            }, AssetEditPriority.Late);
        // Farm-type extras that vanilla ties to the main farm's type, as map settings each farm reads from its own map.
        else if (e.NameWithoutLocale.IsEquivalentTo("Maps/Farm_Foraging"))
            e.Edit(a => a.AsMap().Data.Properties["SpawnForestFarmForage"] = "T");
        else if (e.NameWithoutLocale.IsEquivalentTo("Maps/Farm_Mining"))
            e.Edit(a => a.AsMap().Data.Properties["SpawnMountainFarmOreRect"] = "5 37 22 8");
        else if (e.NameWithoutLocale.IsEquivalentTo("Maps/Farm_FourCorners"))
            e.Edit(a => a.AsMap().Data.Properties["SpawnMountainFarmOreRect"] = "51 67 11 3");
        else if (e.NameWithoutLocale.IsEquivalentTo("Maps/Farm_Island"))
            e.Edit(a =>
            {
                a.AsMap().Data.Properties["SpawnBeachFarmForage"] = "T";
                a.AsMap().Data.Properties["FarmOceanCrabPotOverride"] = "T";
            });
    }

    /// <summary>Where you arrive on a farm from the bus stop road (its map's own spot).</summary>
    public static Point RoadEntryOf(GameLocation? farm) =>
        farm != null && farm.TryGetMapPropertyAs("BusStopEntry", out Point p, required: false) ? p : Farms.RoadEntry;

    /// <summary>
    /// Arriving at a farm from the bus stop, the forest or the backwoods: the spot on that farm's own map, as vanilla
    /// does for the main farm (vanilla only adjusts these for "Farm", not for our farms).
    /// </summary>
    public static void AdjustArrival(GameLocation farm, ref int tileX, ref int tileY)
    {
        string from = Game1.currentLocation?.NameOrUniqueName ?? "";
        string map = farm.mapPath.Value?.Replace('/', '\\') ?? "";
        if (from == "BusStop" && tileX == 79 && tileY == 17 && farm.TryGetMapPropertyAs("BusStopEntry", out Point bus, required: false))
            (tileX, tileY) = (bus.X, bus.Y);
        else if (from == "Forest" && tileX == 41 && tileY == 64)
        {
            if (farm.TryGetMapPropertyAs("ForestEntry", out Point f, required: false)) (tileX, tileY) = (f.X, f.Y);
            else if (map.EndsWith("Farm_Island")) (tileX, tileY) = (82, 103);
            else if (map.EndsWith("Farm_FourCorners")) (tileX, tileY) = (40, 64);
        }
        else if (from == "FarmCave" && tileX == 34 && tileY == 6)
        {
            if (farm.TryGetMapPropertyAs("FarmCaveEntry", out Point c, required: false)) (tileX, tileY) = (c.X, c.Y);
            else if (map.EndsWith("Farm_Island")) (tileX, tileY) = (34, 16);
            else if (map.EndsWith("Farm_FourCorners")) (tileX, tileY) = (30, 36);
        }
        else if (from == "Backwoods" && tileX == 40 && tileY == 0 && farm.TryGetMapPropertyAs("BackwoodsEntry", out Point b, required: false))
            (tileX, tileY) = (b.X, b.Y);
    }

    // ---------- the owner picks (players' side) ----------

    private static bool asked;
    private static int nextAsk, nextRebuild;

    private static void OnClientSecond(object? sender, OneSecondUpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;
        // A farm's map (or its label) arrived or changed: rebuild that farm's fish and forage.
        if (Game1.ticks >= nextRebuild)
            foreach (string farm in Farms.AllNames)
                if (Game1.getLocationFromName(farm) is { } loc && loc.modData.ContainsKey(MapKey)
                    && dataFor.TryGetValue(farm, out string? built) && built != MapPathOf(farm))
                {
                    Helper.GameContent.InvalidateCache("Data/Locations");
                    nextRebuild = Game1.ticks + 600;
                    break;
                }
        if (asked || !Context.IsPlayerFree || Game1.ticks < nextAsk || Farms.HomeFarmOf(Game1.player) is not string home)
            return;
        if (Game1.getLocationFromName(home) is not { } homeFarm || !IsPending(homeFarm) || !FarmSettings.IsOwner(Game1.player, home))
            return;
        asked = true;
        if (Environment.GetEnvironmentVariable("SV_BOT_PICK_MAP") is { Length: > 0 } botPick)
        {
            Log.Info($"[maps] test player picks {botPick} for {home}");
            Request(home, botPick); // the test player picks without the menu
            return;
        }
        ShowPicker(home);
    }

    /// <summary>The picker: the farm maps, then that map's description and a confirmation (it's permanent).</summary>
    public static void ShowPicker(string home)
    {
        var responses = Offered.Select(k => new Response(k.Map, k.Title)).ToList();
        responses.Add(new Response("later", "Decide later"));
        Game1.currentLocation.createQuestionDialogue(
            "Welcome! Pick the map for your farm. You choose once, and it's yours for good.",
            responses.ToArray(), (who, answer) =>
            {
                if (KindOf(answer) is { } kind)
                    DelayedAction.functionAfterDelay(() => Confirm(home, kind), 100);
                else
                {
                    Game1.chatBox?.addInfoMessage("You can pick your farm's map any time at the notice board by the farm road at the bus stop.");
                    nextAsk = Game1.ticks + 60 * 60 * 5;
                    asked = false; // ask again in five minutes
                }
            });
    }

    private static void Confirm(string home, Kind kind)
    {
        string desc = Describe(kind);
        Game1.currentLocation.createQuestionDialogue($"{kind.Title}: {desc} Pick {kind.Title}? You can't change it later.",
            new[] { new Response("yes", $"Yes, {kind.Title}"), new Response("back", "Show me the list again") }, (who, answer) =>
            {
                if (answer == "yes")
                    Request(home, kind.Map);
                else
                    DelayedAction.functionAfterDelay(() => ShowPicker(home), 100);
            });
    }

    private static string Describe(Kind kind)
    {
        try
        {
            // Vanilla text is "Name_Description".
            string s = Game1.content.LoadString(kind.DescKey);
            string[] parts = s.Split('_', 2);
            return (parts.Length == 2 ? parts[1] : s).Trim();
        }
        catch
        {
            return "";
        }
    }

    private record Pick(string Farm, string Map);
    private record Reply(string Text, bool Ok);

    public static void Request(string farm, string map) =>
        Helper.Multiplayer.SendMessage(new Pick(farm, map), MsgPick, new[] { ModId }, new[] { Game1.MasterPlayer.UniqueMultiplayerID });

    private static void OnMessage(object? sender, ModMessageReceivedEventArgs e)
    {
        if (e.FromModID != ModId)
            return;
        if (SV.Role == Role.Server && e.Type == MsgPick && e.ReadAs<Pick>() is { } p)
        {
            Farmer? who = Game1.getOnlineFarmers().FirstOrDefault(f => f.UniqueMultiplayerID == e.FromPlayerID);
            string? why = who == null ? "Unknown player." : Choose(p.Farm, p.Map, who);
            string text = why ?? $"Your farm is now a {KindOf(p.Map)!.Title} farm. Have fun!";
            Helper.Multiplayer.SendMessage(new Reply(text, why == null), MsgReply, new[] { ModId }, new[] { e.FromPlayerID });
        }
        else if (SV.Role != Role.Server && e.Type == MsgReply && e.ReadAs<Reply>() is { } r)
        {
            Game1.addHUDMessage(new HUDMessage(r.Text, r.Ok ? HUDMessage.newQuest_type : HUDMessage.error_type));
            if (r.Ok)
                DelayedAction.functionAfterDelay(GoHome, 2000); // the farm outside just changed: step out at your door
            else
                asked = false;
        }
    }

    /// <summary>After the map changes, someone standing on the farm goes to their cabin's door on the new map.</summary>
    private static void GoHome()
    {
        if (Farms.HomeFarmOf(Game1.player) is not string home || Game1.currentLocation?.Name != home || Game1.getLocationFromName(home) is not { } farm)
            return;
        foreach (Building b in farm.buildings)
            if (b.isCabin && b.HasIndoorsName(Game1.player.homeLocation.Value))
            {
                Game1.warpFarmer(home, b.tileX.Value + b.humanDoor.X, b.tileY.Value + b.humanDoor.Y + 1, 2);
                return;
            }
        Point p = RoadEntryOf(farm);
        Game1.warpFarmer(home, p.X, p.Y, 3);
    }

    // ---------- the server applies it ----------

    /// <summary>Server: give a waiting farm its map. Returns why not, or null when done.</summary>
    public static string? Choose(string farmName, string map, Farmer who)
    {
        if (Game1.getLocationFromName(farmName) is not Farm farm || !Farms.IsFarm(farm))
            return "That farm doesn't exist.";
        if (!FarmSettings.IsOwner(who, farmName))
            return "Only the farm's owner can pick its map.";
        if (chosen.ContainsKey(farmName))
            return "This farm's map is already picked.";
        if (KindOf(map) is not { } kind || !Offered.Contains(kind))
            return "That farm map isn't available.";
        bool same = farm.mapPath.Value?.Replace('/', '\\') == "Maps\\" + kind.Map;
        if (!same && WhatsBuilt(farm) is { } built)
            return $"The map can only be picked before anything is built or planted ({built}).";

        chosen[farmName] = kind.Map;
        Save();
        Helper.GameContent.InvalidateCache("Data/Locations");
        if (!same)
            SwitchMap(farm, "Maps\\" + kind.Map, who);
        Label();
        Log.Info($"{who.Name} picked the {kind.Title} map for {Farms.DisplayName(farmName)}.");
        return null;
    }

    /// <summary>Something a player made on the farm (other than the cabins and the shipping bin), or null.</summary>
    private static string? WhatsBuilt(Farm farm)
    {
        if (farm.buildings.FirstOrDefault(b => !b.isCabin && b.buildingType.Value != "Shipping Bin") is { } b)
            return b.buildingType.Value;
        if (farm.objects.Pairs.FirstOrDefault(p => !IsWild(p.Value)) is { Value: { } o })
            return o.DisplayName;
        if (farm.terrainFeatures.Pairs.FirstOrDefault(p => p.Value is StardewValley.TerrainFeatures.HoeDirt { crop: not null }
                or StardewValley.TerrainFeatures.FruitTree or StardewValley.TerrainFeatures.Flooring) is { Value: { } tf })
            return tf.GetType().Name;
        return null;
    }

    private static bool IsWild(SObject o) => o.IsWeeds() || o.IsBreakableStone() || o.IsTwig() || o.IsSpawnedObject
        || o.QualifiedItemId is "(O)590" or "(O)SeedSpot"; // artifact spots

    /// <summary>
    /// Server: swap a fresh farm onto another map. Wild things from the old map go, the new map's own trees and debris
    /// come in, then the shipping bin and cabins move to the new map's farmhouse area.
    /// </summary>
    private static void SwitchMap(Farm farm, string mapPath, Farmer owner)
    {
        // Wild things from the old map.
        foreach (var key in farm.objects.Keys.ToList())
            if (IsWild(farm.objects[key]))
                farm.objects.Remove(key);
        foreach (var key in farm.terrainFeatures.Keys.ToList())
            if (farm.terrainFeatures[key] is StardewValley.TerrainFeatures.Grass or StardewValley.TerrainFeatures.Tree
                or StardewValley.TerrainFeatures.HoeDirt { crop: null } or StardewValley.TerrainFeatures.Bush)
                farm.terrainFeatures.Remove(key);
        farm.resourceClumps.Clear();
        farm.largeTerrainFeatures.Clear();

        farm.mapPath.Value = mapPath;
        farm.reloadMap();
        ResetMapCaches(farm);
        farm.loadObjects(); // the new map's trees, debris and warps (the quarry path's too)

        if (farm.buildings.FirstOrDefault(b => b.buildingType.Value == "Shipping Bin") is { } bin)
        {
            Vector2 at = farm.GetStarterShippingBinLocation();
            ClearUnder(farm, new Rectangle((int)at.X, (int)at.Y, 2, 1));
            bin.tileX.Value = (int)at.X;
            bin.tileY.Value = (int)at.Y;
        }
        string map = mapPath["Maps\\".Length..];
        if (map is "Farm_Ranching")
            AddStarterCoop(farm, owner); // before the cabins, so they leave its pen alone
        Server.RelineCabins(farm);
        if (map is "Farm_Mining")
            for (int i = 0; i < 28; i++) farm.doDailyMountainFarmUpdate();
        else if (map is "Farm_FourCorners")
            for (int i = 0; i < 10; i++) farm.doDailyMountainFarmUpdate();
        farm.updateWarps();
        // Its quarry's way back now lands on the new map's path.
        Game1.getLocationFromName(Quarry.QuarryOf(farm.Name))?.updateWarps();
    }

    /// <summary>Forget everything the farm worked out from its old map (positions and farm-type switches).</summary>
    private static void ResetMapCaches(Farm farm)
    {
        farm.mapShippingBinPosition = null;
        farm.mainFarmhouseEntry = null;
        farm.mapMainMailboxPosition = null;
        farm.mapGrandpaShrinePosition = null;
        farm.mapSpouseAreaCorner = null;
        foreach (string field in new[] { "_mountainForageRectangle", "_shouldSpawnForestFarmForage", "_shouldSpawnBeachFarmForage", "_oceanCrabPotOverride", "_fishLocationOverride" })
            AccessTools.Field(typeof(Farm), field)?.SetValue(farm, null);
    }

    private static void ClearUnder(GameLocation farm, Rectangle area)
    {
        for (int x = area.Left; x < area.Right; x++)
            for (int y = area.Top; y < area.Bottom; y++)
            {
                farm.objects.Remove(new Vector2(x, y));
                farm.terrainFeatures.Remove(new Vector2(x, y));
            }
    }

    /// <summary>Meadowlands' start, as vanilla gives it: a small fenced pen with a coop and two chickens.</summary>
    private static void AddStarterCoop(Farm farm, Farmer owner)
    {
        try
        {
            for (int x = 47; x < 63; x++) farm.objects.TryAdd(new Vector2(x, 20), new Fence(new Vector2(x, 20), "322", isGate: false));
            for (int y = 16; y < 20; y++) farm.objects.TryAdd(new Vector2(47, y), new Fence(new Vector2(47, y), "322", isGate: false));
            for (int y = 7; y < 20; y++) farm.objects.TryAdd(new Vector2(62, y), new Fence(new Vector2(62, y), "322", y == 13));
            ClearUnder(farm, new Rectangle(54, 9, 6, 3));
            var coop = new Building("Coop", new Vector2(54, 9));
            coop.FinishConstruction(onGameStart: true);
            coop.LoadFromBuildingData(coop.GetData(), forUpgrade: false, forConstruction: true);
            coop.load();
            if (coop.GetIndoors() is AnimalHouse house)
            {
                string[] names = Game1.content.LoadString("Strings\\1_6_Strings:StarterChicken_Names").Split('|');
                string pair = names[Game1.random.Next(names.Length)];
                var a = new FarmAnimal("White Chicken", Game1.Multiplayer.getNewID(), owner.UniqueMultiplayerID) { Name = pair.Split(',')[0].Trim() };
                var b = new FarmAnimal("Brown Chicken", Game1.Multiplayer.getNewID(), owner.UniqueMultiplayerID) { Name = pair.Split(',')[^1].Trim() };
                house.adoptAnimal(a);
                house.adoptAnimal(b);
            }
            farm.buildings.Add(coop);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't add the Meadowlands coop on {farm.Name}: {ex.Message}");
        }
    }

    /// <summary>Wilderness farms: monsters come out at night when someone's there (vanilla does this only for the main farm's type).</summary>
    private static void TenMinute_Postfix(Farm __instance, int timeOfDay)
    {
        if (!Farms.IsFarm(__instance) || Game1.spawnMonstersAtNight || timeOfDay < 1900 || !__instance.farmers.Any())
            return;
        if (!(__instance.mapPath.Value?.Replace('/', '\\').EndsWith("Farm_Combat") ?? false))
            return;
        if (Game1.random.NextDouble() < 0.25 - Game1.player.team.AverageDailyLuck() / 2.0)
            __instance.spawnGroundMonsterOffScreen();
    }
}
