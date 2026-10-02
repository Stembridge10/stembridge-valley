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
    private static int nextActionTick, actions, ticks, warps, farmed, chats, drawCounter;
    private static DateTime windowStart = DateTime.UtcNow;

    public static void Apply(IModHelper helper, Harmony harmony)
    {
        if (Environment.GetEnvironmentVariable("SV_BOT") != "1")
            return;
        Log.Warn("LOAD-TEST BOT ACTIVE");
        harmony.Patch(AccessTools.Method(typeof(Game1), "Draw", new[] { typeof(GameTime) }),
            prefix: new HarmonyMethod(typeof(Bot), nameof(Draw_Prefix)));
        helper.Events.GameLoop.UpdateTicked += OnTicked;
        SV.WriteFlag("bot.csv", "time,connected,location,ping_ms,tps,actions,warps,farmed,chats,mem_mb\n");
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
        if (!Context.IsPlayerFree || Game1.player.controller != null || e.Ticks < nextActionTick)
            return;

        nextActionTick = (int)e.Ticks + rng.Next(180, 480); // every 3-8 seconds
        actions++;
        try
        {
            int roll = rng.Next(100);
            if (roll < 50) Walk();
            else if (roll < 68) Warp();
            else if (roll < 92) Farm();
            else Chat();
        }
        catch (Exception ex)
        {
            Log.Debug($"[bot] action failed: {ex.Message}");
        }
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
        if (Game1.currentLocation is not StardewValley.Farm farm)
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
        string line = $"{DateTime.Now:HH:mm:ss},{(connected && Context.IsWorldReady ? 1 : 0)},{where},{ping:0},{ticks / secs:0.0},{actions},{warps},{farmed},{chats},{mem}\n";
        ticks = 0;
        try { File.AppendAllText(Path.Combine(SV.StateDir, "bot.csv"), line); } catch { }
    }
}
