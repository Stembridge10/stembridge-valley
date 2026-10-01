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

    private static void NetStart_Prefix(Lidgren.Network.NetPeer __instance)
    {
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

        if (stage == 0 && Game1.activeClickableMenu is TitleMenu && Game1.gameMode == 0 && !SaveGame.IsProcessing)
        {
            if (++waitTicks < 30)
                return;
            stage = 1;
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
        Game1.startingCabins = Math.Clamp(cfg.StartingCabins, 1, 7);
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
        SetDedicatedHost(true);
        Game1.options.ipConnectionsEnabled = true;
        Game1.options.enableFarmhandCreation = true;
        Game1.options.pauseWhenOutOfFocus = false;
        Game1.player.ignoreCollisions = true;
        Game1.netWorldState.Value.IsPaused = false;
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

    /// <summary>Keep one unclaimed cabin (a blank farmhand nobody has customized yet) so a new friend can always join.</summary>
    private static void EnsureFreeCabin()
    {
        Farm farm = Game1.getFarm();
        var cabins = farm.buildings.Where(b => b.isCabin).ToList();
        bool hasFree = cabins.Any(b => b.GetIndoors() is Cabin c && (!c.HasOwner || !c.owner.isCustomized.Value));
        if (hasFree || cabins.Count >= 7)
            return;

        foreach (var tile in CabinSpots(farm))
        {
            var cabin = new Building("Cabin", tile);
            cabin.skinId.Value = "Log Cabin";
            cabin.magical.Value = true;
            cabin.daysOfConstructionLeft.Value = 0;
            cabin.load();
            if (farm.buildStructure(cabin, tile, Game1.player, skipSafetyChecks: false))
            {
                Log.Info($"Built a new cabin at {tile.X},{tile.Y} for the next player.");
                return;
            }
        }
        Log.Warn("No room found for another cabin.");
    }

    private static IEnumerable<Vector2> CabinSpots(Farm farm)
    {
        Point house = farm.GetMainFarmHouseEntry();
        for (int ring = 1; ring < 8; ring++)
            for (int dx = -ring; dx <= ring; dx++)
                for (int dy = -ring; dy <= ring; dy++)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == ring)
                        yield return new Vector2(house.X + dx * 6, house.Y + dy * 5);
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
                bool ok = lidgren.UPnP.ForwardPort(24642, "Stembridge Valley");
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