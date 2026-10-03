using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Locations;
using StardewValley.Locations;
using StardewValley.Network;
using StardewValley.Tools;
using xTile.Dimensions;

namespace StembridgeValley;

/// <summary>
/// Several 4-player farms in one shared world. Each farm is its own copy of the farm map (SV_Farm1, SV_Farm2, ...)
/// with four cabins and no main farmhouse. Everyone shares the town, clock and seasons.
///  - "Go to the farm" (map edge, totem, return scepter) takes you to YOUR farm.
///  - The fence by the farm road at the bus stop lets you visit other farms.
///  - On someone else's farm you can look around, but not use tools, harvest, open chests or place things.
/// The vanilla "Farm" still exists (the game needs it) but nobody lives there.
/// </summary>
internal static class Farms
{
    public const string Prefix = "SV_Farm";
    public const int PlayersPerFarm = 4;
    private static readonly string[] Names = { "Cedar", "Maple", "Willow", "Birch", "Aspen", "Pine", "Oak", "Elm", "Rowan", "Hazel",
        "Juniper", "Alder", "Cypress", "Laurel", "Poplar", "Spruce", "Linden", "Sumac", "Holly", "Larch",
        "Ash", "Beech", "Chestnut", "Dogwood", "Fir", "Hawthorn", "Hemlock", "Hickory", "Magnolia", "Mulberry",
        "Olive", "Palm", "Redwood", "Sequoia", "Walnut", "Yew", "Acacia", "Banyan", "Sycamore", "Tamarack",
        "Bramble", "Clover", "Fern", "Heather", "Ivy", "Lilac", "Meadow", "Moss", "Orchard", "Thistle",
        "Briar", "Cobble", "Dell", "Glen", "Hollow", "Knoll", "Marsh", "Ridge", "Brook", "Vale" };
    /// <summary>Most farms the world can grow to (every player can have their own).</summary>
    public static int MaxFarms => Names.Length;
    private static IModHelper Helper = null!;
    /// <summary>Server: how many farms are open. Grows as farms fill up; saved through farms.json.</summary>
    private static int serverOpen;

    /// <summary>Where you arrive on a farm from the bus stop road (same as vanilla).</summary>
    public static readonly Point RoadEntry = new(79, 17);

    public static bool Enabled => SV.Config.Server.FarmCount > 0;
    /// <summary>Open farms. The server decides; players see the farms the server has sent them.</summary>
    public static IEnumerable<string> AllNames => SV.Role == Role.Server
        ? Enumerable.Range(1, serverOpen).Select(LocationName)
        : Game1.locations.Where(l => IsFarm(l) && l.isAlwaysActive.Value).Select(l => l.Name).ToList();
    public static int Count => AllNames.Count();
    public static string LocationName(int i) => Prefix + i;
    public static string DisplayName(string locationName) =>
        int.TryParse(locationName.AsSpan(Prefix.Length), out int i) && i >= 1 && i <= Names.Length ? Names[i - 1] + " Farm" : locationName;
    public static bool IsFarm(GameLocation? loc) => loc != null && loc.Name.StartsWith(Prefix, StringComparison.Ordinal);

    private static DateTime lastWarn = DateTime.MinValue;

    public static void Apply(IModHelper helper, Harmony harmony)
    {
        if (!Enabled)
            return;
        Helper = helper;
        if (SV.Role == Role.Server)
            serverOpen = Math.Clamp(Math.Max(SV.Config.Server.FarmCount, FarmRoster.SavedFarmCount()), 1, MaxFarms);
        helper.Events.Content.AssetRequested += OnAssetRequested;
        GameLocation.RegisterTileAction("SV_Visit", (loc, args, who, tile) => { ShowVisitMenu(); return true; });

        harmony.Patch(AccessTools.Method(typeof(Farm), nameof(Farm.AddDefaultBuildings)),
            prefix: new HarmonyMethod(typeof(Farms), nameof(AddDefaultBuildings_Prefix)));
        harmony.Patch(AccessTools.Method(typeof(Game1), nameof(Game1.warpFarmer), new[] { typeof(LocationRequest), typeof(int), typeof(int), typeof(int) }),
            prefix: new HarmonyMethod(typeof(Farms), nameof(WarpFarmer_Prefix)));
        harmony.Patch(AccessTools.Method(typeof(FarmHouse), nameof(FarmHouse.getFrontDoorSpot)),
            postfix: new HarmonyMethod(typeof(Farms), nameof(FrontDoor_Postfix)));
        harmony.Patch(AccessTools.Method(typeof(Farm), nameof(Farm.IsBuildableLocation)),
            postfix: new HarmonyMethod(typeof(Farms), nameof(IsBuildable_Postfix)));

        if (SV.Role == Role.Client)
        {
            // Visitors can look but not touch.
            harmony.Patch(AccessTools.Method(typeof(Game1), nameof(Game1.pressUseToolButton)),
                prefix: new HarmonyMethod(typeof(Farms), nameof(UseTool_Prefix)));
            harmony.Patch(AccessTools.Method(typeof(Game1), nameof(Game1.tryToCheckAt)),
                prefix: new HarmonyMethod(typeof(Farms), nameof(CheckAt_Prefix)));
            harmony.Patch(AccessTools.Method(typeof(Utility), nameof(Utility.tryToPlaceItem)),
                prefix: new HarmonyMethod(typeof(Farms), nameof(PlaceItem_Prefix)));
            helper.Events.GameLoop.SaveLoaded += (_, _) => greeted = false;
            helper.Events.GameLoop.OneSecondUpdateTicked += Greet;
        }
    }

    // ---------- content ----------

    private static void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo("Data/Locations"))
        {
            e.Edit(asset =>
            {
                var data = asset.AsDictionary<string, LocationData>().Data;
                data.TryGetValue("Farm_Standard", out LocationData? standard);
                var clone = AccessTools.Method(typeof(object), "MemberwiseClone");
                // The server creates the farms that are open. Players get a cheap stand-in for every possible farm,
                // which the real farm from the server replaces when it arrives (also for farms opened later).
                bool server = SV.Role == Role.Server;
                int n = server ? serverOpen : MaxFarms;
                for (int i = 1; i <= n; i++)
                {
                    string name = LocationName(i);
                    // Same fish, forage and artifact spots as the normal farm.
                    var entry = standard != null ? (LocationData)clone.Invoke(standard, null)! : new LocationData();
                    entry.DisplayName = DisplayName(name);
                    entry.DefaultArrivalTile = RoadEntry;
                    entry.CreateOnLoad = server
                        ? new CreateLocationData { MapPath = "Maps\\Farm", Type = "StardewValley.Farm", AlwaysActive = true }
                        : new CreateLocationData { MapPath = "Maps\\Cellar", AlwaysActive = false };
                    data[name] = entry;
                }
            });
        }
        else if (e.NameWithoutLocale.IsEquivalentTo("Maps/BusStop"))
        {
            // The fence along the north side of the farm road becomes "visit a farm".
            e.Edit(asset =>
            {
                var map = asset.AsMap().Data;
                var layer = map.GetLayer("Buildings");
                for (int x = 9; x <= 13; x++)
                    if (layer.Tiles[x, 21] is { } t)
                        t.Properties["Action"] = "SV_Visit";
            }, AssetEditPriority.Late);
        }
    }

    /// <summary>No main farmhouse, greenhouse or pet bowl on player farms: just a shipping bin. Cabins are added by the server.</summary>
    private static bool AddDefaultBuildings_Prefix(Farm __instance, bool load)
    {
        if (!IsFarm(__instance))
            return true;
        __instance.AddDefaultBuilding("Shipping Bin", __instance.GetStarterShippingBinLocation(), load);
        return false;
    }

    // ---------- growing ----------

    /// <summary>Server: open the next farm (with its four cabins) and send it to everyone online. Returns its name.</summary>
    public static string? OpenFarm()
    {
        if (SV.Role != Role.Server || serverOpen >= MaxFarms)
            return null;
        serverOpen++;
        string name = LocationName(serverOpen);
        Helper.GameContent.InvalidateCache("Data/Locations"); // SMAPI reloads Game1.locationData with the new farm
        GameLocation farm = Game1.CreateGameLocation(name);
        Game1.locations.Add(farm);
        farm.AddDefaultBuildings();
        Server.BuildCabin(farm, 0); // one cabin; another is added beside it for each invited friend
        farm.modData["SV.CabinsInRow"] = "1";
        FarmRoster.Load(); // gives it an invite code
        Server.SetPlayerLimit();
        var send = AccessTools.Method(typeof(GameServer), "sendLocation");
        if (Game1.server is GameServer gs)
            foreach (long peer in Game1.otherFarmers.Keys.ToList())
                send.Invoke(gs, new object[] { peer, farm, false });
        Log.Info($"Opened {DisplayName(name)}: {serverOpen} farms now.");
        return name;
    }

    // ---------- who lives where ----------

    /// <summary>The farm a location belongs to (a farm, or a building on it), or null for town etc.</summary>
    public static GameLocation? FarmOf(GameLocation? loc)
    {
        for (int depth = 0; loc != null && depth < 4; depth++)
        {
            if (IsFarm(loc) || loc is Farm)
                return loc;
            loc = loc.GetParentLocation();
        }
        return null;
    }

    /// <summary>Location name of the farm whose cabin is named <paramref name="homeName"/>.</summary>
    public static string? FarmOfHome(string? homeName)
    {
        if (string.IsNullOrEmpty(homeName))
            return null;
        if (Game1.getLocationFromName(homeName) is Cabin { ParentBuilding: { } b } && b.GetParentLocation() is { } parent)
            return parent.Name;
        foreach (string name in AllNames)
            if (Game1.getLocationFromName(name) is { } farm && farm.buildings.Any(x => x.GetIndoorsName() == homeName))
                return name;
        return null;
    }

    public static string? HomeFarmOf(Farmer who) => FarmOfHome(who.homeLocation.Value);

    /// <summary>Customized (real) players living on each farm.</summary>
    public static List<Farmer> MembersOf(string farmName) =>
        Game1.getAllFarmers().Where(f => !f.IsMainPlayer && f.isCustomized.Value && HomeFarmOf(f) == farmName).ToList();

    public static bool CanTouch(GameLocation? loc, Farmer who)
    {
        if (who.IsMainPlayer)
            return true;
        GameLocation? farm = FarmOf(loc);
        if (farm == null || !IsFarm(farm))
            return true;
        return HomeFarmOf(who) == farm.Name;
    }

    // ---------- warps ----------

    /// <summary>Anything that sends a player to "the farm" sends them to their own farm instead.</summary>
    private static void WarpFarmer_Prefix(ref LocationRequest locationRequest)
    {
        if (Game1.player == null || Game1.player.IsMainPlayer || locationRequest?.Name != "Farm")
            return;
        string? home = HomeFarmOf(Game1.player);
        if (home != null)
            locationRequest = Game1.getLocationRequest(home);
    }

    /// <summary>Leaving a cabin puts you at its door, wherever the cabin is.</summary>
    private static void FrontDoor_Postfix(FarmHouse __instance, ref Point __result)
    {
        if (__instance is Cabin { ParentBuilding: { } b })
            __result = new Point(b.tileX.Value + b.humanDoor.X, b.tileY.Value + b.humanDoor.Y + 1);
    }

    /// <summary>Robin offers to build only on your own farm.</summary>
    private static void IsBuildable_Postfix(Farm __instance, ref bool __result)
    {
        if (Game1.IsServer)
            return;
        __result = IsFarm(__instance) && HomeFarmOf(Game1.player) == __instance.Name;
    }

    // ---------- visiting ----------

    public static void ShowVisitMenu()
    {
        string? home = HomeFarmOf(Game1.player);
        var responses = new List<Response>();
        if (home != null)
            responses.Add(new Response(home, $"Home ({DisplayName(home)})"));
        // With a farm per player the full list gets long: show farms with someone online now (busiest first).
        var online = Game1.getOnlineFarmers().Where(f => !f.IsMainPlayer).ToList();
        foreach (var group in online.GroupBy(f => HomeFarmOf(f)).Where(g => g.Key != null && g.Key != home)
                     .OrderByDescending(g => g.Count()).Take(6))
            responses.Add(new Response(group.Key!, $"{DisplayName(group.Key!)}: {string.Join(", ", group.Select(m => m.Name))}"));
        responses.Add(new Response("cancel", "Never mind"));
        Game1.currentLocation.createQuestionDialogue("Which farm do you want to go to?", responses.ToArray(), (who, answer) =>
        {
            if (answer != "cancel" && Game1.getLocationFromName(answer) != null)
                Game1.warpFarmer(answer, RoadEntry.X, RoadEntry.Y, 3);
        });
    }

    private static bool greeted;
    private static void Greet(object? sender, OneSecondUpdateTickedEventArgs e)
    {
        if (greeted || !Context.IsWorldReady || Game1.chatBox == null || !e.IsMultipleOf(180))
            return;
        greeted = true;
        string? home = HomeFarmOf(Game1.player);
        if (home == null)
            return;
        var mates = MembersOf(home).Where(f => f.UniqueMultiplayerID != Game1.player.UniqueMultiplayerID).Select(f => f.Name).ToList();
        Game1.chatBox.addInfoMessage($"You live on {DisplayName(home)}" + (mates.Count > 0 ? $" with {string.Join(", ", mates)}." : "."));
        Game1.chatBox.addInfoMessage("To visit other farms, check the fence by the farm road at the bus stop.");
    }

    // ---------- look, don't touch ----------

    private static bool Blocked(GameLocation? loc)
    {
        if (loc == null || CanTouch(loc, Game1.player))
            return false;
        if (DateTime.UtcNow - lastWarn > TimeSpan.FromSeconds(3))
        {
            lastWarn = DateTime.UtcNow;
            string farm = DisplayName(FarmOf(loc)!.Name);
            Game1.addHUDMessage(new HUDMessage($"You're visiting {farm}. Only its farmers can change things here.", HUDMessage.error_type));
        }
        return true;
    }

    private static bool UseTool_Prefix(ref bool __result)
    {
        if (!Blocked(Game1.currentLocation))
            return true;
        __result = true; // handled: do nothing
        return false;
    }

    private static bool CheckAt_Prefix(Vector2 grabTile, Farmer who, ref bool __result)
    {
        GameLocation loc = Game1.currentLocation;
        if (loc == null || CanTouch(loc, who))
            return true;
        // Doors still work so visitors can look inside barns and coops.
        if (loc.getBuildingAt(grabTile) is { } b && b.humanDoor.Value != new Point(-1, -1)
            && (int)grabTile.X == b.tileX.Value + b.humanDoor.X && (int)grabTile.Y == b.tileY.Value + b.humanDoor.Y)
            return true;
        bool somethingThere = loc.objects.ContainsKey(grabTile) || loc.terrainFeatures.ContainsKey(grabTile)
            || loc.getBuildingAt(grabTile) != null
            || loc.Animals.Values.Any(a => a.Tile == grabTile) || loc.furniture.Any(f => f.TileLocation == grabTile)
            || loc.doesTileHaveProperty((int)grabTile.X, (int)grabTile.Y, "Action", "Buildings") != null;
        if (!somethingThere)
            return true;
        Blocked(loc);
        __result = false;
        return false;
    }

    private static bool PlaceItem_Prefix(GameLocation location, ref bool __result)
    {
        if (!Blocked(location))
            return true;
        __result = false;
        return false;
    }
}
