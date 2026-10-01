using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace StembridgeValley;

/// <summary>
/// Automated-test probe. Only active when the test harness sets SV_TEST_PROBE; does nothing in normal play.
/// Server: jumps to Spring 27 and plants a watered parsnip, then reports whether it survives into summer.
/// Client: drains energy, naps, and reports energy/money each morning so the rules can be checked from logs.
/// </summary>
internal static class TestProbe
{
    private static readonly List<Dictionary<string, object?>> Events = new();
    private static bool setupDone, napTested;
    private static readonly HashSet<int> tiredToday = new();
    private static readonly Point[] CropTiles = { new(50, 26), new(52, 26), new(54, 26), new(56, 26) };

    public static void Apply(IModHelper helper)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SV_TEST_PROBE")))
            return;
        Log.Warn("TEST PROBE ACTIVE");
        try
        {
            string f = Path.Combine(SV.StateDir, "probe.json");
            if (File.Exists(f))
            {
                Events.AddRange(JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(File.ReadAllText(f)) ?? new());
                setupDone = Events.Any(e => e.TryGetValue("what", out var w) && w?.ToString() == "server-planted");
            }
        }
        catch { }
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.DayEnding += (_, _) =>
        {
            if (SV.Role == Role.Server) Record("server-night", CropReport());
            else Record("client-night", PlayerState());
        };
        helper.Events.GameLoop.OneSecondUpdateTicked += OnSecond;
    }

    private static void Record(string what, Dictionary<string, object?>? extra = null)
    {
        var e = new Dictionary<string, object?>
        {
            ["what"] = what,
            ["real"] = DateTime.Now.ToString("HH:mm:ss"),
            ["date"] = $"{Game1.season} {Game1.dayOfMonth} Y{Game1.year}",
            ["time"] = Game1.timeOfDay,
        };
        if (extra != null)
            foreach (var (k, v) in extra) e[k] = v;
        Events.Add(e);
        SV.WriteFlag("probe.json", JsonSerializer.Serialize(Events, new JsonSerializerOptions { WriteIndented = true }));
        Log.Info("[probe] " + JsonSerializer.Serialize(e));
    }

    private static Dictionary<string, object?> CropReport()
    {
        Farm farm = Game1.getFarm();
        int alive = 0, dead = 0, missing = 0;
        var phases = new List<int>();
        foreach (Point t in CropTiles)
        {
            var crop = farm.terrainFeatures.TryGetValue(t.ToVector2(), out var tf) && tf is HoeDirt d ? d.crop : null;
            if (crop == null) missing++;
            else if (crop.dead.Value) dead++;
            else { alive++; phases.Add(crop.currentPhase.Value); }
        }
        return new() { ["cropsAlive"] = alive, ["cropsDead"] = dead, ["cropsMissing"] = missing, ["phases"] = string.Join(",", phases) };
    }

    private static Dictionary<string, object?> PlayerState() => new()
    {
        ["name"] = Game1.player.Name,
        ["stamina"] = Math.Round(Game1.player.Stamina, 1),
        ["maxStamina"] = Game1.player.MaxStamina,
        ["money"] = Game1.player.Money,
        ["location"] = Game1.currentLocation?.NameOrUniqueName,
    };

    private static void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (SV.Role == Role.Server)
        {
            Farm farm = Game1.getFarm();
            if (!setupDone)
            {
                setupDone = true;
                Game1.dayOfMonth = 27;
                Game1.stats.DaysPlayed = (uint)Math.Max(Game1.stats.DaysPlayed, 27);
                int planted = 0;
                foreach (Point t in CropTiles)
                {
                    Vector2 tile = t.ToVector2();
                    farm.objects.Remove(tile);
                    farm.terrainFeatures.Remove(tile);
                    var dirt = new HoeDirt(1, farm);
                    farm.terrainFeatures.Add(tile, dirt);
                    dirt.plant("472", Game1.player, false); // parsnip seeds (spring only)
                    if (dirt.crop != null) planted++;
                }
                Record("server-planted", new() { ["planted"] = planted, ["jumpedTo"] = "Spring 27" });
                return;
            }
            foreach (Point t in CropTiles)
                if (farm.terrainFeatures.TryGetValue(t.ToVector2(), out var tf) && tf is HoeDirt d)
                    d.state.Value = 1; // keep watered
            var report = CropReport();
            report["online"] = Game1.getOnlineFarmers().Count - 1;
            Record("server-morning", report);
        }
        else
        {
            Record("client-morning", PlayerState());
        }
    }

    private static void OnSecond(object? sender, OneSecondUpdateTickedEventArgs e)
    {
        if (SV.Role != Role.Client || !Context.IsWorldReady || !Context.IsPlayerFree)
            return;

        // Each in-game morning around 9am: tire the farmer out so we can see energy carry over the night.
        if (Game1.timeOfDay is >= 900 and < 1000 && !tiredToday.Contains(Game1.Date.TotalDays))
        {
            // Step off the bed first: in multiplayer, lying in bed slowly restores energy (vanilla), which would hide the overnight check.
            Point entry = Utility.getHomeOfFarmer(Game1.player).getEntryLocation();
            Game1.player.setTileLocation(new Vector2(entry.X, entry.Y - 2));
            tiredToday.Add(Game1.Date.TotalDays);
            Game1.player.Stamina = 50;
            Record("client-tired", PlayerState());
        }

        // Once, around noon: take a nap and confirm the day keeps going.
        if (!napTested && Game1.timeOfDay >= 1200)
        {
            napTested = true;
            Game1.player.Stamina = 20;
            int before = Game1.timeOfDay;
            var day = Game1.dayOfMonth;
            Game1.currentLocation.answerDialogueAction("Sleep_Yes", null);
            DelayedAction.functionAfterDelay(() =>
                Record("client-after-nap", new()
                {
                    ["stamina"] = Math.Round(Game1.player.Stamina, 1),
                    ["timeBefore"] = before,
                    ["sameDay"] = Game1.dayOfMonth == day,
                    ["readySleep"] = Game1.netReady.IsReady("sleep"),
                }), 4000);
        }
    }
}
