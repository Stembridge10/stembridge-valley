using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;

namespace StembridgeValley;

/// <summary>
/// Skull Cavern dives are timed, now that the day no longer cuts them short at 2am.
/// Each trip down starts a timer (shown on screen). Eating food that gives a buff while down there adds time,
/// up to a limit. When it runs out, the player is pulled back up to the cavern entrance with everything they carry.
/// Going back in starts again from the top, with a fresh timer.
/// Runs on each player's own game.
/// </summary>
internal static class Dive
{
    private static int secondsLeft = -1, bonusGiven;
    private static bool warnedMinute, warnedTen, pulling;

    private static int BaseSeconds => int.TryParse(Environment.GetEnvironmentVariable("SV_DIVE_SECONDS"), out int s) && s > 0
        ? s : SV.Config.DiveMinutes * 60;

    public static void Apply(IModHelper helper, Harmony harmony)
    {
        if (SV.Config.DiveMinutes <= 0)
            return;
        helper.Events.GameLoop.OneSecondUpdateTicked += OnSecond;
        helper.Events.Display.RenderedHud += Draw;
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => secondsLeft = -1;
        harmony.Patch(AccessTools.Method(typeof(Farmer), nameof(Farmer.doneEating)),
            prefix: new HarmonyMethod(typeof(Dive), nameof(DoneEating_Prefix)));
    }

    /// <summary>A Skull Cavern floor (the Mines' floors 121 and down, not the quarry).</summary>
    public static bool InCavern(GameLocation? where) =>
        where != null && MineShaft.IsGeneratedLevel(where, out int level)
        && level > MineShaft.bottomOfMineLevel && level != MineShaft.quarryMineShaft;

    public static bool Diving => secondsLeft >= 0;

    private static void OnSecond(object? sender, OneSecondUpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.newDay)
            return;
        if (!InCavern(Game1.currentLocation))
        {
            if (Diving && Game1.locationRequest == null && !Game1.isWarping)
                End();
            return;
        }
        if (!Diving)
            Start();
        if (pulling || Game1.eventUp || Game1.player.passedOut)
            return;

        secondsLeft--;
        if (secondsLeft == 60 && !warnedMinute)
        {
            warnedMinute = true;
            Game1.addHUDMessage(new HUDMessage("One minute left in the cavern. Eat something to stay longer.", HUDMessage.error_type));
        }
        else if (secondsLeft == 10 && !warnedTen)
        {
            warnedTen = true;
            Game1.addHUDMessage(new HUDMessage("10 seconds left in the cavern!", HUDMessage.error_type));
        }
        if (secondsLeft <= 0)
            PullOut();
    }

    private static void Start()
    {
        secondsLeft = BaseSeconds;
        bonusGiven = 0;
        warnedMinute = warnedTen = pulling = false;
        Log.Info($"[dive] started: {secondsLeft}s.");
        Game1.addHUDMessage(new HUDMessage($"Cavern dive: {Format(secondsLeft)}. Food with a buff adds time.", HUDMessage.newQuest_type));
    }

    private static void End()
    {
        Log.Info($"[dive] ended with {Math.Max(0, secondsLeft)}s left.");
        secondsLeft = -1;
        pulling = false;
    }

    private static void PullOut()
    {
        Farmer p = Game1.player;
        if (Game1.activeClickableMenu is { } menu and not (ReadyCheckDialog or SaveGameMenu or LevelUpMenu))
        {
            menu.emergencyShutDown();
            Game1.exitActiveMenu();
        }
        if (Game1.activeClickableMenu != null || p.UsingTool || Game1.locationRequest != null)
            return; // try again next second
        pulling = true;
        p.mount?.dismount();
        Log.Info("[dive] time's up: back to the cavern entrance.");
        Game1.addHUDMessage(new HUDMessage("Time's up! You climb back out of the cavern with everything you found.", HUDMessage.newQuest_type));
        Game1.warpFarmer("SkullCave", 3, 4, 2);
    }

    /// <summary>Eating food (or a drink) that gives a buff while diving adds time.</summary>
    private static void DoneEating_Prefix(Farmer __instance)
    {
        if (!__instance.IsLocalPlayer || !Diving || pulling || __instance.itemToEat is not StardewValley.Object food)
            return;
        bool buff;
        try { buff = food.GetFoodOrDrinkBuffs().Any(); }
        catch { buff = false; }
        if (!buff)
            return;
        int room = SV.Config.DiveMaxBonusMinutes * 60 - bonusGiven;
        int add = Math.Min(SV.Config.DiveBuffSeconds, room);
        if (add <= 0)
        {
            Game1.addHUDMessage(new HUDMessage("You can't stretch this dive any longer.", HUDMessage.error_type));
            return;
        }
        bonusGiven += add;
        secondsLeft += add;
        if (secondsLeft > 60) warnedMinute = false;
        if (secondsLeft > 10) warnedTen = false;
        Log.Info($"[dive] {food.Name} added {add}s ({secondsLeft}s left).");
        Game1.addHUDMessage(new HUDMessage($"+{Format(add)} in the cavern ({Format(secondsLeft)} left).", HUDMessage.achievement_type));
    }

    private static string Format(int seconds) => $"{Math.Max(0, seconds) / 60}:{Math.Max(0, seconds) % 60:00}";

    private static void Draw(object? sender, RenderedHudEventArgs e)
    {
        if (!Diving || !InCavern(Game1.currentLocation) || Game1.eventUp)
            return;
        string text = $"Cavern {Format(secondsLeft)}";
        var font = Game1.dialogueFont;
        Vector2 size = font.MeasureString(text);
        var area = Game1.graphics.GraphicsDevice.Viewport.TitleSafeArea;
        var pos = new Vector2((area.Left + area.Width / 2f) / Game1.options.uiScale - size.X / 2f, area.Top / Game1.options.uiScale + 16);
        IClickableMenu.drawTextureBox(e.SpriteBatch, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
            (int)pos.X - 20, (int)pos.Y - 12, (int)size.X + 40, (int)size.Y + 24, Color.White, 1f, drawShadow: false);
        Color color = secondsLeft <= 60 ? Color.Red : Game1.textColor;
        Utility.drawTextWithShadow(e.SpriteBatch, text, font, pos, color);
    }
}
