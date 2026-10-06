using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Tools;

namespace StembridgeValley;

/// <summary>
/// The 24-hour day: the clock runs from 6am all the way round to the next 6am, and that's when the new day starts.
/// Nobody is sent home: each player's game shows a short "saving" screen and they wake up where they were standing.
///
/// Stardew's own clock stops at 2am (2600) and makes everyone pass out there. The game spells that out in a few
/// places as the number 2600; those are moved to 6am (3000). The late-night "it's getting late" messages and the
/// 4am forced faint go too. If a game update moves these places, the game simply keeps its normal 2am day and
/// the log says so.
/// </summary>
internal static class Clock
{
    /// <summary>6am the next morning, in Stardew's clock numbers (24 + 6 = 30 hours).</summary>
    public const int DayEnd = 3000;
    private const int VanillaEnd = 2600;
    /// <summary>Where a player wakes up after a day that ended with them out and about (cleared after waking).</summary>
    private const string WakeKey = "stembridge.wakeHere";

    public static bool Active { get; private set; }
    /// <summary>Ten-minute steps in one day: 144 for the full 24 hours, 120 for the game's 6am-2am.</summary>
    public static int TicksPerDay => Active ? 144 : 120;
    /// <summary>When the day ends: 6am with the full day, 2am otherwise.</summary>
    public static int End => Active ? DayEnd : VanillaEnd;

    public static void Apply(IModHelper helper, Harmony harmony)
    {
        if (!SV.Config.FullDayClock)
            return;
        try
        {
            Active = PatchClock(harmony);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't set up the 24-hour day ({ex.Message}); days end at 2am as usual.");
            Active = false;
        }
        if (!Active)
            return;

        // Waking up where you were: the game only lets you wake somewhere with a bed; we allow the spot you fell asleep.
        harmony.Patch(AccessTools.Method(typeof(GameLocation), nameof(GameLocation.CanWakeUpHere)),
            postfix: new HarmonyMethod(typeof(Clock), nameof(CanWakeUpHere_Postfix)));
        if (SV.Role == Role.Client)
        {
            harmony.Patch(AccessTools.Method(typeof(Game1), nameof(Game1.UpdateOther)),
                prefix: new HarmonyMethod(typeof(Clock), nameof(UpdateOther_Prefix)));
            helper.Events.GameLoop.OneSecondUpdateTicked += (_, _) => ForgetWakeSpot();
        }
        Log.Info("24-hour day: the new day starts at 6am, and nobody is sent home.");
    }

    // ---------- the clock itself ----------

    private static bool PatchClock(Harmony harmony)
    {
        var tenMinute = typeof(Game1).GetNestedTypes(BindingFlags.NonPublic)
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .FirstOrDefault(m => m.Name.Contains("<performTenMinuteClockUpdate>b__") && m.ReturnType == typeof(void));
        var passOutWarp = typeof(Farmer).GetNestedTypes(BindingFlags.NonPublic)
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Concat(typeof(Farmer).GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .FirstOrDefault(m => m.Name.Contains("ContinuePassOut"));
        if (tenMinute == null || passOutWarp == null)
        {
            Log.Warn("The game's clock code isn't where expected; days end at 2am as usual.");
            return false;
        }

        // Check everything first, then patch: never leave half a clock change in place.
        var plan = new List<(MethodBase method, string what)>
        {
            (tenMinute, "clock"),
            (AccessTools.Method(typeof(Game1), nameof(Game1.UpdateOther)), "faint"),
            (AccessTools.Method(typeof(Farmer), nameof(Farmer.Update), new[] { typeof(GameTime), typeof(GameLocation) }), "simple"),
            (passOutWarp, "simple"),
            (AccessTools.Method(typeof(FishingRod), nameof(FishingRod.DoFunction)), "simple"),
        };
        foreach (var draw in typeof(FarmerRenderer).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                     .Where(m => m.Name == "draw" && CountConst(m, VanillaEnd) > 0))
            plan.Add((draw, "simple"));

        foreach (var (method, _) in plan)
            if (CountConst(method, VanillaEnd) == 0)
            {
                Log.Warn($"{method.DeclaringType?.Name}.{method.Name} has no 2am check any more; days end at 2am as usual.");
                return false;
            }

        foreach (var (method, what) in plan)
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(Clock), what switch
            {
                "clock" => nameof(ClockTranspiler),
                "faint" => nameof(NeverTranspiler),
                _ => nameof(SimpleTranspiler),
            }));
        return true;
    }

    private static int CountConst(MethodBase method, int value)
    {
        try
        {
            return PatchProcessor.GetOriginalInstructions(method).Count(i => IsConst(i, value));
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsConst(CodeInstruction i, int value) =>
        i.opcode == OpCodes.Ldc_I4 && i.operand is int v && v == value;

    /// <summary>Every "2am" in these methods becomes "6am".</summary>
    private static IEnumerable<CodeInstruction> SimpleTranspiler(IEnumerable<CodeInstruction> code)
    {
        foreach (var i in code)
        {
            if (IsConst(i, VanillaEnd))
                i.operand = DayEnd;
            yield return i;
        }
    }

    /// <summary>
    /// The game's own "it's 2am, faint now" check never fires: at 6am our code below puts each player to sleep
    /// where they stand (the game's version would faint them mid-warp and send them home). Fainting from
    /// exhaustion still works as usual.
    /// </summary>
    private static IEnumerable<CodeInstruction> NeverTranspiler(IEnumerable<CodeInstruction> code)
    {
        foreach (var i in code)
        {
            if (IsConst(i, VanillaEnd))
                i.operand = 9999;
            yield return i;
        }
    }

    /// <summary>
    /// The ten-minute clock: the cap moves to 6am, and the late-night cases (midnight message, 1am shake,
    /// 2am stop-what-you're-doing, 4am faint) are pointed at times that never come.
    /// </summary>
    private static IEnumerable<CodeInstruction> ClockTranspiler(IEnumerable<CodeInstruction> code)
    {
        var list = code.ToList();
        for (int n = 0; n < list.Count; n++)
        {
            var i = list[n];
            var next = n + 1 < list.Count ? list[n + 1] : null;
            bool isCase = next != null && (next.opcode == OpCodes.Beq || next.opcode == OpCodes.Beq_S);
            if (IsConst(i, VanillaEnd) && next?.opcode == OpCodes.Call && next.operand is MethodInfo { Name: "Min" })
                i.operand = DayEnd; // timeOfDay = Math.Min(timeOfDay, 6am)
            else if (isCase && (IsConst(i, 2400) || IsConst(i, 2500) || IsConst(i, VanillaEnd) || IsConst(i, 2800)))
                i.operand = 9900 + n % 90; // a time the clock never reaches
        }
        return list;
    }

    // ---------- waking up where you were ----------

    /// <summary>
    /// At 6am each player falls asleep on the spot (no pass-out walk home). The game then waits for everyone,
    /// saves, and starts the new day. The spot is remembered so they wake up there.
    /// </summary>
    private static string? lastWait;
    private static DateTime? sixAmSince;

    private static void UpdateOther_Prefix()
    {
        if (!Context.IsWorldReady || Game1.timeOfDay < DayEnd || Game1.newDay)
        {
            sixAmSince = null;
            return;
        }
        Farmer p = Game1.player;
        sixAmSince ??= DateTime.UtcNow;
        // Safety net: if something keeps us from sleeping on the spot for 20 seconds, faint the normal way
        // (wake up at home) so the new day can never get stuck.
        if (DateTime.UtcNow - sixAmSince > TimeSpan.FromSeconds(20) && !p.passedOut && !p.isInBed.Value
            && p.canMove && p.freezePause <= 0 && !p.UsingTool && !Game1.eventUp && Game1.locationRequest == null
            && Game1.activeClickableMenu == null && Game1.currentMinigame == null)
        {
            Log.Warn("6am: couldn't sleep on the spot; fainting the normal way (waking at home).");
            p.startToPassOut();
            p.freezePause = 7000;
            sixAmSince = DateTime.UtcNow.AddMinutes(10);
            return;
        }
        string? wait = p.passedOut ? "passedOut" : p.isInBed.Value ? "inBed" : Game1.eventUp ? "eventUp"
            : Game1.currentMinigame != null ? "minigame" : Game1.locationRequest != null ? "locationRequest"
            : Game1.isWarping ? "warping" : Game1.fadeToBlack ? "fadeToBlack" : p.freezePause > 0 ? "freezePause" : null;
        if (wait != null)
        {
            if (wait != lastWait || Game1.ticks % 600 == 0)
                Log.Debug($"6am: waiting before sleeping ({wait}).");
            lastWait = wait;
            return;
        }
        switch (Game1.activeClickableMenu)
        {
            case ReadyCheckDialog or SaveGameMenu or ShippingMenu or LevelUpMenu:
                return;
            case { } menu:
                menu.emergencyShutDown();
                Game1.exitActiveMenu();
                break;
        }
        if (p.UsingTool)
        {
            if (p.CurrentTool is FishingRod rod && (rod.isReeling || rod.pullingOutOfWater || rod.fishCaught))
                return; // let the catch finish
            p.completelyStopAnimatingOrDoingAction();
        }
        if (p.IsSitting())
            p.StopSitting(animate: false);
        p.mount?.dismount();

        (string where, Point tile) = WakeSpot(Game1.currentLocation, p.TilePoint);
        if (where != Game1.currentLocation.NameOrUniqueName)
        {
            // Mine and cavern floors are deleted overnight: step out to the entrance first, then sleep there.
            Log.Info($"6am: leaving {Game1.currentLocation.NameOrUniqueName} for {where} before the new day.");
            Game1.warpFarmer(where, tile.X, tile.Y, 2);
            return;
        }
        p.modData[WakeKey] = where;
        Game1.PassOutNewDay();
        p.lastSleepLocation.Value = where;
        p.lastSleepPoint.Value = tile;
        Log.Info($"6am: new day. Waking up at {where} {tile.X},{tile.Y}.");
    }

    /// <summary>
    /// Mine and cavern floors are rebuilt every morning, so whoever is deep inside wakes at the entrance.
    /// Everywhere else: right where they stood.
    /// </summary>
    private static (string where, Point tile) WakeSpot(GameLocation here, Point tile)
    {
        if (MineShaft.IsGeneratedLevel(here, out int level))
            return level > MineShaft.bottomOfMineLevel && level != MineShaft.quarryMineShaft
                ? ("SkullCave", new Point(3, 4))
                : ("Mine", new Point(17, 4));
        if (VolcanoDungeon.IsGeneratedLevel(here.NameOrUniqueName))
        {
            int x = 0, y = 0;
            Utility.getDefaultWarpLocation("IslandNorth", ref x, ref y);
            return ("IslandNorth", new Point(x, y));
        }
        return (here.NameOrUniqueName, tile);
    }

    private static void CanWakeUpHere_Postfix(GameLocation __instance, Farmer who, ref bool __result)
    {
        if (!__result && who.modData.TryGetValue(WakeKey, out string? where) && where == __instance.NameOrUniqueName)
            __result = true;
    }

    private static void ForgetWakeSpot()
    {
        // Once the new morning is under way, the next pass-out (if any) is an ordinary one again.
        if (Context.IsWorldReady && !Game1.newDay && Game1.timeOfDay is >= 610 and < DayEnd
            && Game1.player.modData.ContainsKey(WakeKey))
            Game1.player.modData.Remove(WakeKey);
    }
}
