using Microsoft.Xna.Framework;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.LocationContexts;
using StardewValley.Menus;
using StardewValley.TerrainFeatures;
using StardewValley.Network.Dedicated;

namespace StembridgeValley;

/// <summary>The shared-world rules: long days, rest-based energy, beds as naps, crops that survive the season, no penalties.</summary>
internal static class Rules
{
    private static IModHelper Helper = null!;
    private static float energyCarry;
    private static DateTime lastSeenWrite = DateTime.MinValue;
    private const string RestedKey = "rested";

    public static void Apply(IModHelper helper, Harmony harmony)
    {
        Helper = helper;
        var cfg = SV.Config;

        // 1. Day length. 6am to 2am is 120 ten-minute ticks.
        int msPerTen = Math.Max(1000, cfg.RealMinutesPerDay * 60_000 / 120);
        Game1.realMilliSecondsPerGameTenMinutes = msPerTen;
        Game1.realMilliSecondsPerGameMinute = msPerTen / 10;
        Log.Info($"Day length: {cfg.RealMinutesPerDay} real minutes ({msPerTen / 1000.0:0.#}s per 10 game minutes).");

        // 2. Crops keep growing across seasons.
        harmony.Patch(AccessTools.Method(typeof(Crop), nameof(Crop.IsInSeason), new[] { typeof(GameLocation) }),
            postfix: new HarmonyMethod(typeof(Rules), nameof(CropInSeason_Postfix)));

        // 3. Friendship doesn't decay.
        harmony.Patch(AccessTools.Method(typeof(Farmer), nameof(Farmer.resetFriendshipsForNewDay)),
            prefix: new HarmonyMethod(typeof(Rules), nameof(Friendship_Prefix)),
            postfix: new HarmonyMethod(typeof(Rules), nameof(Friendship_Postfix)));

        // 4. Energy carries over the night.
        harmony.Patch(AccessTools.Method(typeof(Farmer), nameof(Farmer.dayupdate)),
            prefix: new HarmonyMethod(typeof(Rules), nameof(DayUpdate_Prefix)),
            postfix: new HarmonyMethod(typeof(Rules), nameof(DayUpdate_Postfix)));

        // 4b. The server refills every cabin owner's energy overnight (Cabin.DayUpdate); undo that there, at the source.
        harmony.Patch(AccessTools.Method(typeof(StardewValley.Locations.Cabin), nameof(StardewValley.Locations.Cabin.DayUpdate)),
            prefix: new HarmonyMethod(typeof(Rules), nameof(CabinDayUpdate_Prefix)),
            postfix: new HarmonyMethod(typeof(Rules), nameof(CabinDayUpdate_Postfix)));

        // 4c. Weeds/stones scattered on the farm never land on a living crop (crops now outlive the season, so the 1st-of-month scatter would hit them).
        harmony.Patch(AccessTools.Method(typeof(GameLocation), nameof(GameLocation.spawnWeedsAndStones)),
            prefix: new HarmonyMethod(typeof(Rules), nameof(Weeds_Prefix)),
            postfix: new HarmonyMethod(typeof(Rules), nameof(Weeds_Postfix)));

        // 5. Beds are naps (players only; the server's invisible host still sleeps for real).
        harmony.Patch(AccessTools.Method(typeof(GameLocation), nameof(GameLocation.answerDialogueAction)),
            prefix: new HarmonyMethod(typeof(Rules), nameof(Answer_Prefix)));

        // 6. Clock keeps running when nobody is online.
        harmony.Patch(AccessTools.Method(typeof(DedicatedServer), nameof(DedicatedServer.Tick)),
            postfix: new HarmonyMethod(typeof(Rules), nameof(DedicatedTick_Postfix)));

        helper.Events.Content.AssetRequested += OnAssetRequested;
        if (SV.Role == Role.Client)
        {
            helper.Events.GameLoop.OneSecondUpdateTicked += OnSecond;
            helper.Events.GameLoop.SaveLoaded += OnJoined;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
        }
    }

    // ---------- patches ----------

    private static void CropInSeason_Postfix(ref bool __result)
    {
        if (SV.Config.CropsSurviveSeasonChange)
            __result = true;
    }

    private static void Friendship_Prefix(Farmer __instance, out Dictionary<string, int>? __state)
    {
        __state = null;
        if (!SV.Config.NoFriendshipDecay)
            return;
        __state = __instance.friendshipData.Pairs.ToDictionary(p => p.Key, p => p.Value.Points);
    }

    private static void Friendship_Postfix(Farmer __instance, Dictionary<string, int>? __state)
    {
        if (__state == null)
            return;
        foreach (var (name, before) in __state)
        {
            if (__instance.friendshipData.TryGetValue(name, out var f) && f.Points < before)
                f.Points = before;
        }
    }

    /// <summary>Energy at the moment the night started; restored once the new day has fully begun (the cabin and other code refill it in between).</summary>
    private static float? energyAtNight;

    private static void DayUpdate_Prefix(Farmer __instance)
    {
        if (SV.Config.KeepEnergyOvernight && SV.Role == Role.Client && __instance.IsLocalPlayer)
            energyAtNight = __instance.Stamina;
    }

    private static void DayUpdate_Postfix(Farmer __instance)
    {
        if (energyAtNight != null && __instance.IsLocalPlayer)
            __instance.exhausted.Value = false;
    }

    private static void CabinDayUpdate_Prefix(StardewValley.Locations.Cabin __instance, out float? __state)
    {
        __state = SV.Config.KeepEnergyOvernight && __instance.HasOwner ? __instance.owner.Stamina : null;
    }

    private static void CabinDayUpdate_Postfix(StardewValley.Locations.Cabin __instance, float? __state)
    {
        if (__state is float before && __instance.HasOwner)
            __instance.owner.stamina = Math.Clamp(before, 10f, __instance.owner.MaxStamina);
    }

    private static void Weeds_Prefix(GameLocation __instance, out Dictionary<Vector2, (TerrainFeature dirt, StardewValley.Object? obj)>? __state)
    {
        __state = null;
        if (!SV.Config.CropsSurviveSeasonChange || __instance is not Farm)
            return;
        __state = new();
        foreach (var (tile, feature) in __instance.terrainFeatures.Pairs)
            if (feature is HoeDirt { crop: { } crop } && !crop.dead.Value)
                __state[tile] = (feature, __instance.objects.TryGetValue(tile, out var o) ? o : null);
    }

    private static void Weeds_Postfix(GameLocation __instance, Dictionary<Vector2, (TerrainFeature dirt, StardewValley.Object? obj)>? __state)
    {
        if (__state == null)
            return;
        int saved = 0;
        foreach (var (tile, (dirt, obj)) in __state)
        {
            bool changed = false;
            if (!__instance.terrainFeatures.TryGetValue(tile, out var now) || now != dirt)
            {
                __instance.terrainFeatures.Remove(tile);
                __instance.terrainFeatures.Add(tile, dirt);
                changed = true;
            }
            __instance.objects.TryGetValue(tile, out var objNow);
            if (objNow != obj)
            {
                __instance.objects.Remove(tile);
                if (obj != null)
                    __instance.objects.Add(tile, obj);
                changed = true;
            }
            if (changed) saved++;
        }
        if (saved > 0)
            Log.Info($"Kept weeds/stones off {saved} crop(s).");
    }

    /// <summary>Guard window after the new day starts: undo any late refill that arrives from the network.</summary>
    private static float? keepUntilSettled;
    private static int settleSeconds;

    private static void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (energyAtNight is not float kept)
            return;
        energyAtNight = null;
        Farmer p = Game1.player;
        keepUntilSettled = Math.Clamp(kept, 10f, p.MaxStamina);
        settleSeconds = 5;
        p.Stamina = keepUntilSettled.Value;
    }

    private static bool Answer_Prefix(GameLocation __instance, string questionAndAnswer, ref bool __result)
    {
        if (SV.Role != Role.Client || !SV.Config.BedIsNap || questionAndAnswer != "Sleep_Yes")
            return true;

        __result = true;
        Farmer who = Game1.player;
        who.CanMove = false;
        Game1.globalFadeToBlack(() =>
        {
            who.Stamina = who.MaxStamina;
            who.health = who.maxHealth;
            who.CanMove = true;
            Game1.globalFadeToClear();
            Game1.addHUDMessage(new HUDMessage("You feel rested.", HUDMessage.health_type));
        }, 0.02f);
        return false;
    }

    private static void DedicatedTick_Postfix()
    {
        if (SV.Config.ClockRunsWhenEmpty && Game1.IsDedicatedHost && Game1.netWorldState.Value.IsPaused && Game1.CurrentEvent == null)
            Game1.netWorldState.Value.IsPaused = false;
    }

    // ---------- content ----------

    private static void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (SV.Config.NoPassOutPenalty && e.NameWithoutLocale.IsEquivalentTo("Data/LocationContexts"))
        {
            e.Edit(asset =>
            {
                foreach (var ctx in asset.AsDictionary<string, LocationContextData>().Data.Values)
                {
                    ctx.MaxPassOutCost = 0;
                    ctx.PassOutMail = new List<PassOutMailData>();
                }
            });
        }

        if (SV.Role == Role.Client && SV.Config.BedIsNap && e.NameWithoutLocale.IsEquivalentTo("Strings/Locations"))
        {
            e.Edit(asset =>
            {
                asset.AsDictionary<string, string>().Data["FarmHouse_Bed_GoToSleep"] =
                    "Take a nap? Your energy refills. The day keeps going.";
            });
        }
    }

    // ---------- per-player loop ----------

    private static void OnJoined(object? sender, SaveLoadedEventArgs e)
    {
        var data = Helper.Data.ReadGlobalData<RestedData>(RestedKey) ?? new RestedData();
        if (data.LastSeenUtc is DateTime last && DateTime.UtcNow - last >= TimeSpan.FromMinutes(SV.Config.RestedAfterMinutesAway))
        {
            Game1.player.Stamina = Game1.player.MaxStamina;
            Game1.addHUDMessage(new HUDMessage("Welcome back. You're well rested!", HUDMessage.newQuest_type));
        }
        energyCarry = 0;
    }

    private static void OnSecond(object? sender, OneSecondUpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;
        Farmer p = Game1.player;

        if (keepUntilSettled is float keep)
        {
            if (p.Stamina > keep + 1f)
                p.Stamina = keep;
            if (--settleSeconds <= 0)
                keepUntilSettled = null;
            return;
        }

        // Energy regeneration from rest.
        if (p.Stamina < p.MaxStamina && !p.UsingTool && !Game1.eventUp && !p.passedOut)
        {
            float perMinute = p.IsSitting() ? SV.Config.SittingEnergyPerMinute : SV.Config.IdleEnergyPerMinute;
            energyCarry += perMinute / 60f;
            if (energyCarry >= 1f)
            {
                float whole = MathF.Floor(energyCarry);
                energyCarry -= whole;
                p.Stamina = Math.Min(p.MaxStamina, p.Stamina + whole);
            }
        }

        // 2am: shut menus so the pass-out (and the server's day change) is never held up by someone in a shop.
        if (Game1.timeOfDay >= 2600 && Game1.activeClickableMenu != null
            && Game1.activeClickableMenu is not ReadyCheckDialog
            && Game1.activeClickableMenu is not SaveGameMenu
            && Game1.activeClickableMenu is not ShippingMenu
            && Game1.activeClickableMenu is not LevelUpMenu
            && !p.passedOut)
        {
            Game1.activeClickableMenu.emergencyShutDown();
            Game1.exitActiveMenu();
        }

        // Remember when we were last here (for the rested bonus).
        if (DateTime.UtcNow - lastSeenWrite > TimeSpan.FromSeconds(30))
        {
            lastSeenWrite = DateTime.UtcNow;
            Helper.Data.WriteGlobalData(RestedKey, new RestedData { LastSeenUtc = DateTime.UtcNow });
        }
    }

    private sealed class RestedData
    {
        public DateTime? LastSeenUtc { get; set; }
    }
}
