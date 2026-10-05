using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.Menus;

namespace StembridgeValley;

/// <summary>
/// Runs inside the hidden server copy: creates or loads the farm with the game's built-in
/// dedicated-host mode (invisible host, no end-of-night screens), opens it to join-by-address,
/// keeps at least one free cabin, and never needs a person at the keyboard.
/// </summary>
internal static class Server
{
    private static IModHelper Helper = null!;
    private const string SaveKey = "server-save";
    private static int stage;
    private static int waitTicks;
    private static string status = "starting";
    private static DateTime lastStatus = DateTime.MinValue;

    public static void Apply(IModHelper helper, Harmony harmony)
    {
        Helper = helper;
        SetPlayerLimit();
        helper.Events.GameLoop.UpdateTicked += OnUpdate;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.Saved += (_, _) => Log.Info($"Saved {Game1.season} {Game1.dayOfMonth}, year {Game1.year}.");
        helper.Events.Multiplayer.PeerConnected += (_, e) => Log.Info($"Player joined (id {e.Peer.PlayerID}).");
        helper.Events.Multiplayer.PeerDisconnected += (_, e) => Log.Info($"Player left (id {e.Peer.PlayerID}).");

        // The server never draws anything useful and shouldn't burn the GPU.
        harmony.Patch(AccessTools.Method(typeof(Game1), "Draw", new[] { typeof(GameTime) }),
            prefix: new HarmonyMethod(typeof(Server), nameof(Draw_Prefix)));

        // Ask the router to open the port (UPnP) when the game's LAN server starts.
        harmony.Patch(AccessTools.Method(typeof(Lidgren.Network.NetPeer), nameof(Lidgren.Network.NetPeer.Start)),
            prefix: new HarmonyMethod(typeof(Server), nameof(NetStart_Prefix)));
    }

    /// <summary>Vanilla allows 8 players per farm. The limit is just a number; the network layer sizes itself from it.</summary>
    internal static void SetPlayerLimit()
    {
        int limit = Math.Clamp(SV.Config.Server.MaxPlayers, 2, 128);
        if (Farms.Enabled)
            limit = Math.Max(limit, 1 + Math.Min(Farms.Count + 1, Farms.MaxFarms) * Farms.PlayersPerFarm); // room for one more farm
        Game1.Multiplayer.playerLimit = limit;
        if (Game1.netWorldState?.Value != null)
            Game1.netWorldState.Value.CurrentPlayerLimit = limit;
    }

    private static void NetStart_Prefix(Lidgren.Network.NetPeer __instance)
    {
        // Tests can run a second server next to the live one.
        if (__instance is Lidgren.Network.NetServer && int.TryParse(Environment.GetEnvironmentVariable("SV_PORT"), out int port) && port > 0)
            __instance.Configuration.Port = port;
        // Connection slots are fixed once the server starts; leave room for every farm the world can grow to.
        if (__instance is Lidgren.Network.NetServer && Farms.Enabled)
            __instance.Configuration.MaximumConnections = Math.Max(__instance.Configuration.MaximumConnections, (1 + Farms.MaxFarms * Farms.PlayersPerFarm) * 2);
        if (SV.Config.Server.TryAutomaticPortForward && __instance is Lidgren.Network.NetServer && __instance.Configuration.Port == 24642)
        {
            try { __instance.Configuration.EnableUPnP = true; }
            catch (Exception ex) { Log.Debug($"Couldn't enable UPnP: {ex.Message}"); }
        }
    }

    private static readonly System.Reflection.FieldInfo DedicatedField = AccessTools.Field(typeof(FarmerTeam), "hasDedicatedHost");

    private static void SetDedicatedHost(bool value)
    {
        var net = (Netcode.NetBool)DedicatedField.GetValue(Game1.player.team)!;
        net.Value = value;
    }

    private static int drawCounter;
    private static bool Draw_Prefix()
    {
        // Draw rarely: SaveGameMenu needs at least one draw to start saving.
        return Game1.activeClickableMenu is SaveGameMenu || ++drawCounter % 30 == 0;
    }

    private static void OnUpdate(object? sender, UpdateTickedEventArgs e)
    {
        if (DateTime.UtcNow - lastStatus > TimeSpan.FromSeconds(5))
        {
            lastStatus = DateTime.UtcNow;
            string online = Context.IsWorldReady ? (Game1.getOnlineFarmers().Count - 1).ToString() : "0";
            string clock = Context.IsWorldReady ? $"{Game1.season} {Game1.dayOfMonth} Y{Game1.year} {Game1.timeOfDay}" : "-";
            SV.WriteFlag("server-status.txt", $"{status}\nplayers={online}\nclock={clock}\nupdated={DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        }

        if (status == "running" && e.IsMultipleOf(300) && Context.IsWorldReady)
            EnsureFreeCabin();

        if (stage == 0 && Game1.activeClickableMenu is TitleMenu && Game1.gameMode == 0 && !SaveGame.IsProcessing)
        {
            if (++waitTicks < 30)
                return;
            stage = 1;
            SetPlayerLimit();
            var data = Helper.Data.ReadGlobalData<ServerSaveData>(SaveKey);
            if (data?.SaveName is { Length: > 0 } name && Directory.Exists(Path.Combine(Constants.SavesPath, name)))
                LoadFarm(name);
            else
                CreateFarm();
        }
    }

    private static void CreateFarm()
    {
        var cfg = SV.Config.Server;
        status = "creating farm";
        Log.Info($"Creating a new farm: {cfg.FarmName}.");

        Game1.multiplayerMode = 2;
        Game1.player.team.useSeparateWallets.Value = true;
        Game1.cabinsSeparate = false;
        // With 4-player farms nobody lives on the normal farm, so it gets no cabins.
        Game1.startingCabins = Farms.Enabled ? 0 : Math.Clamp(cfg.StartingCabins, 1, 7);
        Game1.whichFarm = cfg.FarmType;
        Game1.whichModFarm = null;
        Game1.spawnMonstersAtNight = cfg.FarmType == 4;
        Game1.player.Name = "Valley";
        Game1.player.displayName = "Valley";
        Game1.player.farmName.Value = cfg.FarmName;
        Game1.player.favoriteThing.Value = "Friends";
        Game1.player.isCustomized.Value = true;
        SetDedicatedHost(true);
        Game1.uniqueIDForThisGame = (ulong)Utility.RandomLong(new Random());

        Game1.game1.loadForNewGame();
        SetDedicatedHost(true);
        Game1.saveOnNewDay = true;
        Game1.player.eventsSeen.Add("60367");
        Game1.player.currentLocation = Utility.getHomeOfFarmer(Game1.player);
        Game1.player.Position = new Vector2(9f, 9f) * 64f;
        Game1.player.isInBed.Value = true;
        Game1.NewDay(0f);
        Game1.exitActiveMenu();
        Game1.setGameMode(3);
    }

    private static void LoadFarm(string saveName)
    {
        status = "loading farm";
        Log.Info($"Loading farm save {saveName}.");
        Game1.multiplayerMode = 2;
        SaveGame.Load(saveName);
        if (Game1.activeClickableMenu != null)
            Game1.activeClickableMenu.exitThisMenu(false);
    }

    private static void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        if (!Game1.IsServer)
            return;
        MapDump.Run();
        SetDedicatedHost(true);
        Game1.options.ipConnectionsEnabled = true;
        Game1.options.enableFarmhandCreation = true;
        Game1.options.pauseWhenOutOfFocus = false;
        Game1.player.ignoreCollisions = true;
        Game1.netWorldState.Value.IsPaused = false;
        SetPlayerLimit();
        if (Farms.Enabled)
        {
            FarmRoster.Load();
            FarmMaps.OnServerLoaded(); // needs the members: farms people live on keep their map
            FarmRoster.TrimSpareCabins();
            FarmRoster.EnsureCabins();
            AlignCabins();
            Log.Info($"{Farms.Count} farms: " + string.Join(", ", Farms.AllNames.Select(f => $"{Farms.DisplayName(f)} ({FarmRoster.Members(f).Count}/4)")));
        }
        Helper.Data.WriteGlobalData(SaveKey, new ServerSaveData { SaveName = Constants.SaveFolderName });
        status = "running";
        Log.Info($"Farm is up: {Game1.player.farmName.Value}. Players join at this PC's address, port 24642.");
        TryPortForward();
    }

    private static void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (!Game1.IsServer)
            return;
        EnsureFreeCabin();
        Log.Info($"New day: {Game1.season} {Game1.dayOfMonth}, year {Game1.year}.");
    }

    /// <summary>
    /// Keep one unclaimed cabin (a blank farmhand nobody has customized yet) so a new player can always join,
    /// up to the player limit. Runs every few seconds, so the next cabin appears as soon as the last free one is taken.
    /// </summary>
    internal static void EnsureFreeCabin()
    {
        if (Farms.Enabled)
        {
            FarmRoster.KeepOneEmptyFarm();
            FarmRoster.EnsureCabins();
            return;
        }
        // Keep two free so two people joining at the same moment don't both grab the last one.
        for (int built = 0; built < 2; built++)
            if (!BuildCabinIfNeeded(wantFree: 2))
                return;
    }

    private static bool BuildCabinIfNeeded(int wantFree)
    {
        Farm farm = Game1.getFarm();
        var cabins = farm.buildings.Where(b => b.isCabin).ToList();
        int free = cabins.Count(b => b.GetIndoors() is Cabin c && (!c.HasOwner || !c.owner.isCustomized.Value));
        if (free >= wantFree || cabins.Count >= Game1.Multiplayer.playerLimit - 1)
            return false;

        return BuildCabin(farm, cabins.Count);
    }

    private static readonly string[] CabinSkins = { "Log Cabin", "Stone Cabin", "Plank Cabin", "Rustic Cabin" };

    /// <summary>Build one ready-made cabin (with its blank farmhand) on a farm, near where the farmhouse would be.</summary>
    internal static bool BuildCabin(GameLocation farm, int index)
    {
        // Player farms: cabins stand side by side in one row where the farmhouse would be (first free slot in the row).
        IEnumerable<Vector2> spots = CabinSpots(farm);
        if (Farms.IsFarm(farm))
            spots = Enumerable.Range(0, Farms.PlayersPerFarm).Select(i => RowSpot(farm, i)).Concat(spots);
        foreach (var tile in spots)
        {
            if (!SpotIsClear(farm, tile))
                continue;
            ClearSpot(farm, tile);
            var cabin = new Building("Cabin", tile);
            cabin.skinId.Value = CabinSkins[index % CabinSkins.Length];
            cabin.magical.Value = true;
            cabin.daysOfConstructionLeft.Value = 0;
            cabin.load();
            // Our own placement check above; the game's check reads the host's current map, which on a server isn't the farm.
            if (farm.buildStructure(cabin, tile, Game1.player, skipSafetyChecks: true))
            {
                Log.Info($"Built cabin #{index + 1} on {farm.Name} at {tile.X},{tile.Y}.");
                return true;
            }
        }
        Log.Warn($"No room found for another cabin on {farm.Name}.");
        return false;
    }

    private const int CabinW = 5, CabinH = 3;

    /// <summary>
    /// Footprint plus a one-tile margin (two in front of the door) must be farm ground with nothing anyone made on it.
    /// Wild debris (weeds, stones, twigs, wild trees, stumps, boulders, bushes) is fine: it gets cleared.
    /// Crops, fruit trees, chests, machines, paths and other buildings are never touched.
    /// </summary>
    private static bool SpotIsClear(GameLocation farm, Vector2 tile, bool ignoreCabins = false)
        => WhyNotClear(farm, tile, ignoreCabins) == null;

    /// <summary>What blocks a cabin here (null if nothing).</summary>
    private static string? WhyNotClear(GameLocation farm, Vector2 tile, bool ignoreCabins = false)
    {
        for (int x = (int)tile.X - 1; x <= tile.X + CabinW; x++)
            for (int y = (int)tile.Y - 1; y <= tile.Y + CabinH + 1; y++)
            {
                var v = new Vector2(x, y);
                if (!farm.isTileOnMap(v))
                    return $"{x},{y} off the map";
                Building? here = farm.getBuildingAt(v);
                if (here != null && !(ignoreCabins && here.isCabin))
                    return $"{x},{y} building {here.buildingType.Value}";
                // Under a cabin that's about to move, the game reports the cabin's own tiles; look at the map ground instead.
                bool underCabin = here != null;
                if ((underCabin ? MapProp(farm, x, y, "Diggable") : farm.doesTileHaveProperty(x, y, "Diggable", "Back")) == null)
                    return $"{x},{y} not diggable";
                if ((underCabin ? MapProp(farm, x, y, "Buildable") ?? "" : farm.doesTileHavePropertyNoNull(x, y, "Buildable", "Back")).Equals("f", StringComparison.OrdinalIgnoreCase))
                    return $"{x},{y} not buildable";
                if (farm.isWaterTile(x, y) || farm.map.GetLayer("Buildings")?.Tiles[x, y] != null)
                    return $"{x},{y} water or map wall"; // cliffs, fences on the map itself
                if (farm.terrainFeatures.TryGetValue(v, out var tf) && !IsWild(tf))
                    return $"{x},{y} {tf.GetType().Name}";
                if (farm.objects.TryGetValue(v, out var obj) && !(obj.IsWeeds() || obj.IsBreakableStone() || obj.IsTwig()))
                    return $"{x},{y} {obj.Name}";
            }
        return null;
    }

    /// <summary>A Back-layer property straight from the map, ignoring buildings on top.</summary>
    private static string? MapProp(GameLocation farm, int x, int y, string key)
    {
        var tile = farm.map.GetLayer("Back")?.Tiles[x, y];
        if (tile == null)
            return null;
        if (tile.Properties.TryGetValue(key, out var v) || tile.TileIndexProperties.TryGetValue(key, out v))
            return v?.ToString();
        return null;
    }

    private static bool IsWild(StardewValley.TerrainFeatures.TerrainFeature tf) => tf switch
    {
        StardewValley.TerrainFeatures.Grass => true,
        StardewValley.TerrainFeatures.HoeDirt d => d.crop == null,
        StardewValley.TerrainFeatures.Tree => true, // wild trees; planted fruit trees are FruitTree
        _ => false,
    };

    private static void ClearSpot(GameLocation farm, Vector2 tile)
    {
        var area = new Rectangle((int)tile.X - 1, (int)tile.Y - 1, CabinW + 2, CabinH + 3);
        for (int x = area.Left; x < area.Right; x++)
            for (int y = area.Top; y < area.Bottom; y++)
            {
                var v = new Vector2(x, y);
                farm.objects.Remove(v);
                farm.terrainFeatures.Remove(v);
            }
        var pixels = new Rectangle(area.X * 64, area.Y * 64, area.Width * 64, area.Height * 64);
        farm.resourceClumps.RemoveWhere(r => r.getBoundingBox().Intersects(pixels));
        farm.largeTerrainFeatures.RemoveWhere(l => l.getBoundingBox().Intersects(pixels));
        // A grown tree's leaves reach three tiles up, so wild trees just below would hide the door.
        for (int x = area.Left; x < area.Right; x++)
            for (int y = area.Bottom; y < area.Bottom + 3; y++)
                if (farm.terrainFeatures.TryGetValue(new Vector2(x, y), out var tf)
                    && tf is StardewValley.TerrainFeatures.Tree { tapped.Value: false, fertilized.Value: false })
                    farm.terrainFeatures.Remove(new Vector2(x, y));
    }

    /// <summary>
    /// Cabin slot in the row: four cabins, two tiles apart, on the open ground below where the farmhouse would be
    /// (49,19 on the Standard farm; other farm maps put their farmhouse elsewhere).
    /// </summary>
    private static Vector2 RowSpot(GameLocation farm, int index)
    {
        Point house = farm is Farm f ? f.GetMainFarmHouseEntry() : new Point(64, 15);
        return new(house.X - 15 + index * 7, house.Y + 4);
    }

    /// <summary>
    /// After a farm changes map: move its cabins (and everything inside) to the new map's farmhouse area, the row
    /// first, else the nearest clear ground. Cabins are moved, not rebuilt.
    /// </summary>
    internal static void RelineCabins(GameLocation farm)
    {
        var cabins = farm.buildings.Where(b => b.isCabin).OrderBy(b => b.tileX.Value).ThenBy(b => b.tileY.Value).ToList();
        var taken = new List<Rectangle>();
        foreach (var cabin in cabins)
        {
            var spots = Enumerable.Range(0, Farms.PlayersPerFarm).Select(i => RowSpot(farm, i)).Concat(CabinSpots(farm));
            bool moved = false;
            foreach (Vector2 t in spots)
            {
                var room = new Rectangle((int)t.X - 1, (int)t.Y - 1, CabinW + 2, CabinH + 3);
                if (taken.Any(r => r.Intersects(room)) || WhyNotClear(farm, t, ignoreCabins: true) != null)
                    continue;
                ClearSpot(farm, t);
                cabin.tileX.Value = (int)t.X;
                cabin.tileY.Value = (int)t.Y;
                cabin.updateInteriorWarps();
                taken.Add(room);
                moved = true;
                break;
            }
            if (!moved)
                Log.Warn($"No room for a cabin on {farm.Name} after its map changed.");
        }
        farm.modData["SV.CabinsInRow"] = "1";
        Log.Info($"Moved {cabins.Count} cabin(s) on {farm.Name} to its new map.");
    }

    /// <summary>
    /// Line up the cabins on each 4-player farm (once per farm; afterwards players may move them with Robin).
    /// Cabins are moved, not rebuilt, so everything inside stays. Skips a farm if anyone is on it,
    /// or if the row has something a player made in the way.
    /// </summary>
    internal static void AlignCabins()
    {
        foreach (string name in Farms.AllNames)
        {
            if (Game1.getLocationFromName(name) is not GameLocation farm || farm.modData.ContainsKey("SV.CabinsInRow"))
                continue;
            var cabins = farm.buildings.Where(b => b.isCabin).OrderBy(b => b.tileX.Value).ThenBy(b => b.tileY.Value).ToList();
            if (cabins.Count == 0 || cabins.Count > Farms.PlayersPerFarm || farm.farmers.Any())
                continue;
            bool inRow = cabins.Select((b, i) => b.tileX.Value == (int)RowSpot(farm, i).X && b.tileY.Value == (int)RowSpot(farm, i).Y).All(ok => ok);
            if (!inRow)
            {
                string? why = cabins.Select((_, i) => WhyNotClear(farm, RowSpot(farm, i), ignoreCabins: true)).FirstOrDefault(w => w != null);
                if (why != null)
                {
                    Log.Warn($"Couldn't line up the cabins on {name}: {why}.");
                    continue;
                }
                for (int i = 0; i < cabins.Count; i++)
                {
                    Vector2 t = RowSpot(farm, i);
                    ClearSpot(farm, t);
                    cabins[i].tileX.Value = (int)t.X;
                    cabins[i].tileY.Value = (int)t.Y;
                    cabins[i].updateInteriorWarps();
                }
                Log.Info($"Lined up {cabins.Count} cabins on {name}.");
            }
            farm.modData["SV.CabinsInRow"] = "1";
        }
    }

    /// <summary>Every farm tile, nearest to the farmhouse first, so cabins pack into whatever room is left.</summary>
    private static IEnumerable<Vector2> CabinSpots(GameLocation location)
    {
        Point house = location is Farm farm ? farm.GetMainFarmHouseEntry() : new Point(64, 15);
        int w = location.map.Layers[0].LayerWidth, h = location.map.Layers[0].LayerHeight;
        return Enumerable.Range(1, w - CabinW - 2).SelectMany(x => Enumerable.Range(1, h - CabinH - 3).Select(y => new Vector2(x, y)))
            .OrderBy(v => Math.Abs(v.X - house.X) + Math.Abs(v.Y - house.Y));
    }

    private static void TryPortForward()
    {
        if (!SV.Config.Server.TryAutomaticPortForward)
            return;
        Task.Run(() =>
        {
            try
            {
                Thread.Sleep(8000); // router discovery takes a few seconds
                var lidgren = (Game1.server as StardewValley.Network.GameServer)?.GetServer<StardewValley.Network.LidgrenServer>()?.server;
                if (lidgren?.UPnP == null)
                {
                    Log.Info("Automatic port opening isn't available (router UPnP off). Use the manual router steps.");
                    return;
                }
                bool ok = lidgren.UPnP.ForwardPort(24642, "Junimo Hollow");
                var external = ok ? lidgren.UPnP.GetExternalIP() : null;
                Log.Info(ok
                    ? $"Router opened port 24642 automatically. Public address: {external}"
                    : "Router didn't allow automatic port opening. Use the manual router steps.");
                SV.WriteFlag("port-forward.txt", ok ? $"ok\n{external}\n" : "failed\n");
            }
            catch (Exception ex)
            {
                Log.Info($"Automatic port opening failed: {ex.Message}");
                SV.WriteFlag("port-forward.txt", "failed\n");
            }
        });
    }

    private sealed class ServerSaveData
    {
        public string? SaveName { get; set; }
    }
}