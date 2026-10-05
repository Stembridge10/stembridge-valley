using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Network;

namespace StembridgeValley;

/// <summary>
/// Player side: when the game reaches the title screen, connect straight to the server address
/// and open the normal farmhand screen (pick your character, or make one the first time).
/// If the server goes away (restart, PC reboot), keep quietly retrying until it's back.
/// </summary>
internal static class Join
{
    private static int waitTicks;
    private static int attempts;
    private static FarmhandMenu? current;

    public static void Apply(IModHelper helper)
    {
        helper.Events.GameLoop.UpdateTicked += OnUpdate;
        helper.Events.GameLoop.SaveLoaded += (_, _) => attempts = 0;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SV_TEST_CHARACTER")))
            helper.Events.GameLoop.UpdateTicked += TestAutoPick;
    }

    private static void OnUpdate(object? sender, UpdateTickedEventArgs e)
    {
        if (string.IsNullOrEmpty(SV.Address) || Game1.gameMode != 0 || Game1.activeClickableMenu is not TitleMenu)
        {
            waitTicks = 0;
            return;
        }

        IClickableMenu? sub = TitleMenu.subMenu;
        bool idle = sub == null;
        bool disconnectedDialog = sub is ConfirmationDialog && current != null;
        bool failedMenu = sub is FarmhandMenu fm && fm == current && fm.client != null && !fm.approvingFarmhand
            && (fm.client.timedOut || fm.client.availableFarmhands is { Count: 0 }); // no free cabin yet: ask again shortly

        if (!idle && !disconnectedDialog && !failedMenu)
        {
            waitTicks = 0;
            return;
        }

        // First connection quickly; retries every ~10 seconds.
        int delay = attempts == 0 ? 60 : 600;
        if (++waitTicks < delay)
            return;
        waitTicks = 0;
        Connect();
    }

    private static void Connect()
    {
        attempts++;
        Log.Info(attempts == 1 ? $"Connecting to Junimo Hollow at {SV.Address}..." : $"Reconnecting to {SV.Address} (try {attempts})...");
        var multiplayer = (Multiplayer)AccessTools.Field(typeof(Game1), "multiplayer").GetValue(null)!;
        Client client = multiplayer.InitClient(new LidgrenClient(SV.Address));
        var title = (TitleMenu)Game1.activeClickableMenu;
        title.skipToTitleButtons();
        current = new FarmhandMenu(client);
        TitleMenu.subMenu = current;
        picked = false;
        SV.WriteFlag("joined.txt", DateTime.Now.ToString("s"));
    }

    /// <summary>Automated tests only: pick (or create) a farmhand without a person clicking.</summary>
    private static bool picked;
    private static DateTime pickedAt;
    private static void TestAutoPick(object? sender, UpdateTickedEventArgs e)
    {
        // Two new players can race for the same empty cabin; the loser is turned back. Start over after a while.
        if (picked && !Context.IsWorldReady && DateTime.UtcNow - pickedAt > TimeSpan.FromSeconds(40))
        {
            Log.Info("[test] Join didn't finish; trying again.");
            picked = false;
            current = null;
            Game1.gameMode = 0;
            if (Game1.activeClickableMenu is not TitleMenu)
                Game1.activeClickableMenu = new TitleMenu();
            TitleMenu.subMenu = null;
            return;
        }
        if (picked || TitleMenu.subMenu is not FarmhandMenu menu || menu.client?.availableFarmhands is not { Count: > 0 } list)
            return;
        string name = Environment.GetEnvironmentVariable("SV_TEST_CHARACTER")!;
        var blanks = list.Where(f => !f.isCustomized.Value).ToList();
        Farmer? farmer = list.FirstOrDefault(f => f.isCustomized.Value && f.Name == name)
            ?? (blanks.Count > 0 ? blanks[Random.Shared.Next(blanks.Count)] : null);
        if (farmer == null)
            return;
        picked = true;
        pickedAt = DateTime.UtcNow;
        Game1.game1.loadForNewGame();
        AccessTools.Property(typeof(Game1), nameof(Game1.player)).SetValue(null, farmer);
        if (!farmer.isCustomized.Value)
        {
            farmer.Name = name;
            farmer.displayName = name;
            farmer.favoriteThing.Value = "Testing";
            farmer.isCustomized.Value = true;
            farmer.ConvertClothingOverrideToClothesItems();
        }
        menu.client.availableFarmhands = null;
        menu.client.sendPlayerIntroduction();
        menu.approvingFarmhand = true;
        Game1.gameMode = 6;
        Log.Info($"[test] Joining as {name}.");
    }
}
