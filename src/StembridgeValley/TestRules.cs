using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace StembridgeValley;

/// <summary>
/// Test worlds only (server with SV_TEST_RULES=1; does nothing otherwise). Checks two rules across a real season change:
///  - crops: jumps to Summer 27 and plants a harvested blueberry, an unharvested blueberry and a parsnip on an empty
///    farm, keeps them watered, and reports each morning. On Fall 1 only the harvested blueberry should be gone.
///  - animals: once a player is online, puts a chicken on that player's farm and one on a farm nobody is on, and
///    reports their mood each morning. The away farm's chicken should keep its mood; the other loses some (unpetted).
/// </summary>
internal static class TestRules
{
    private static readonly (string Seed, bool Harvested, string Label)[] Plants =
    {
        ("481", true, "blueberry (harvested)"), ("481", false, "blueberry (not harvested)"), ("479", false, "melon"),
    };
    private static bool jumped, animalsPlaced;
    private static string cropFarm = "", homeFarm = "", awayFarm = "";
    private static readonly List<long> animalIds = new();

    public static void Apply(IModHelper helper)
    {
        if (SV.Role != Role.Server || Environment.GetEnvironmentVariable("SV_TEST_RULES") != "1")
            return;
        Log.Warn("TEST RULES PROBE ACTIVE (test world only)");
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.OneSecondUpdateTicked += (_, e) =>
        {
            if (Context.IsWorldReady && !animalsPlaced && e.IsMultipleOf(120))
                PlaceAnimals();
        };
    }

    private static Vector2 Tile(int i) => new(58 + i * 2, 28);

    private static void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (!jumped)
        {
            jumped = true;
            cropFarm = Farms.AllNames.Last();
            Game1.season = Season.Summer;
            Game1.dayOfMonth = 27;
            Game1.netWorldState.Value.UpdateFromGame1();
            GameLocation farm = Game1.getLocationFromName(cropFarm);
            for (int i = 0; i < Plants.Length; i++)
            {
                Vector2 t = Tile(i);
                farm.objects.Remove(t);
                farm.terrainFeatures.Remove(t);
                var dirt = new HoeDirt(1, farm);
                farm.terrainFeatures.Add(t, dirt);
                dirt.plant(Plants[i].Seed, Game1.MasterPlayer, false);
                if (dirt.crop is { } crop)
                {
                    crop.currentPhase.Value = Math.Max(0, crop.phaseDays.Count - 2);
                    if (Plants[i].Harvested)
                    {
                        crop.currentPhase.Value = crop.phaseDays.Count - 1;
                        crop.fullyGrown.Value = true; // what harvesting a regrowing crop does
                        crop.dayOfCurrentPhase.Value = 4;
                    }
                }
            }
            Log.Info($"[rules-test] jumped to Summer 27; planted on {cropFarm}: {Report()}");
            return;
        }
        GameLocation f = Game1.getLocationFromName(cropFarm);
        for (int i = 0; i < Plants.Length; i++)
            if (f.terrainFeatures.TryGetValue(Tile(i), out var tf) && tf is HoeDirt d)
                d.state.Value = 1;
        Log.Info($"[rules-test] morning {Game1.season} {Game1.dayOfMonth}: crops {Report()}; animals {AnimalReport()}");
    }

    private static string Report()
    {
        GameLocation farm = Game1.getLocationFromName(cropFarm);
        return string.Join("; ", Plants.Select((p, i) =>
        {
            var crop = farm.terrainFeatures.TryGetValue(Tile(i), out var tf) && tf is HoeDirt d ? d.crop : null;
            return $"{p.Label}: {(crop == null ? "gone" : crop.dead.Value ? "dead" : $"alive (phase {crop.currentPhase.Value})")}";
        }));
    }

    private static void PlaceAnimals()
    {
        Farmer? player = Game1.getOnlineFarmers().FirstOrDefault(f => !f.IsMainPlayer && Farms.HomeFarmOf(f) != null);
        if (player == null)
            return;
        homeFarm = Farms.HomeFarmOf(player)!;
        awayFarm = Farms.AllNames.First(n => n != homeFarm && n != cropFarm && Farms.MembersOf(n).All(m => !Game1.getOnlineFarmers().Contains(m)));
        foreach (string name in new[] { homeFarm, awayFarm })
        {
            GameLocation farm = Game1.getLocationFromName(name);
            long id = Game1.Multiplayer.getNewID();
            var a = new FarmAnimal("White Chicken", id, player.UniqueMultiplayerID) { Position = new Vector2(60 * 64, 34 * 64) };
            a.happiness.Value = 200;
            a.friendshipTowardFarmer.Value = 300;
            farm.animals.Add(id, a);
            animalIds.Add(id);
        }
        animalsPlaced = true;
        Log.Info($"[rules-test] chickens placed: one on {homeFarm} (player {player.Name} online), one on {awayFarm} (nobody online): {AnimalReport()}");
    }

    private static string AnimalReport()
    {
        if (!animalsPlaced)
            return "(not placed yet)";
        return string.Join("; ", new[] { (homeFarm, "home farm"), (awayFarm, "away farm") }.Select(x =>
        {
            GameLocation farm = Game1.getLocationFromName(x.Item1);
            FarmAnimal? a = farm.animals.Values.FirstOrDefault(v => animalIds.Contains(v.myID.Value));
            return a == null ? $"{x.Item2}: missing" : $"{x.Item2} ({x.Item1}): happiness {a.happiness.Value}, friendship {a.friendshipTowardFarmer.Value}";
        }));
    }
}
