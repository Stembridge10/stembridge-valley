using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace StembridgeValley;

/// <summary>
/// What skill levels 11-50 give (Mining's are the farm quarry, in Quarry.cs). Each perk is small and adds to vanilla;
/// nothing vanilla allows is locked behind one. Personal perks run in the player's own game from their own levels;
/// farm perks (forage and fish on your farm) run on the server from the best level among the farm's members.
///   Farming   12/22/32/42  extra crop on harvest (5/10/15/20%)        20  more XP from crops (+10%)
///   Fishing   11-50        a slightly bigger fishing bar every level  20/30  fish bubbles on your farm's water
///             25/40        double catch (5/10%)
///   Foraging  15/25/45     wild forage on your farm each day (2/4/6)  20/40  double forage (10/20%)
///             35           rare mushrooms among it
///   Combat    15           bus pass: free rides to the desert        20/30/40/50  +1 defense each
///             25/35/45     +1 attack each
/// </summary>
internal static class SkillPerks
{
    public static readonly Dictionary<int, SortedDictionary<int, string>> Unlocks = new()
    {
        [Skills.Farming] = new()
        {
            [12] = "5% chance of an extra crop when you harvest",
            [20] = "10% more Farming XP from crops",
            [22] = "Extra crop chance: 10%",
            [32] = "Extra crop chance: 15%",
            [42] = "Extra crop chance: 20%",
            [50] = "Farming mastered",
        },
        [Skills.Fishing] = new()
        {
            [11] = "Your fishing bar grows a little with every level from here",
            [20] = "Fish bubbles appear on your farm's water",
            [25] = "5% chance to catch two fish",
            [30] = "Fish bubbles on your farm more often",
            [40] = "Double catch chance: 10%",
            [50] = "Fishing mastered",
        },
        [Skills.Foraging] = new()
        {
            [15] = "Wild forage grows on your farm each day",
            [20] = "10% chance of double forage",
            [25] = "More forage on your farm",
            [35] = "Rare mushrooms among your farm's forage",
            [40] = "Double forage chance: 20%",
            [45] = "Even more forage on your farm",
            [50] = "Foraging mastered",
        },
        [Skills.Combat] = new()
        {
            [15] = "Bus pass: free rides to the desert (runs even if the bus isn't repaired)",
            [20] = "+1 defense",
            [25] = "+1 attack",
            [30] = "+1 defense",
            [35] = "+1 attack",
            [40] = "+1 defense",
            [45] = "+1 attack",
            [50] = "+1 defense. Combat mastered",
        },
    };

    public const int BusPassLevel = 15;
    private const string ForageKey = "SV.FarmForage";
    private const string BuffId = "Stembridge.CombatLevels";
    private static int buffLevel = -1;

    public static void Apply(IModHelper helper, Harmony harmony)
    {
        if (SV.Role == Role.Server)
        {
            if (!Farms.Enabled)
                return;
            helper.Events.GameLoop.DayStarted += (_, _) => SpawnFarmForage();
            harmony.Patch(AccessTools.Method(typeof(Farm), nameof(Farm.performTenMinuteUpdate)),
                postfix: new HarmonyMethod(typeof(SkillPerks), nameof(FarmTenMinute_Postfix)));
            return;
        }
        harmony.Patch(AccessTools.Method(typeof(Crop), nameof(Crop.harvest)),
            prefix: new HarmonyMethod(typeof(SkillPerks), nameof(Harvest_Prefix)),
            postfix: new HarmonyMethod(typeof(SkillPerks), nameof(Harvest_Postfix)));
        harmony.Patch(AccessTools.Method(typeof(FishingRod), nameof(FishingRod.pullFishFromWater)),
            prefix: new HarmonyMethod(typeof(SkillPerks), nameof(PullFish_Prefix)));
        harmony.Patch(AccessTools.Constructor(typeof(BobberBar), new[] { typeof(string), typeof(float), typeof(bool), typeof(List<string>), typeof(string), typeof(bool), typeof(string), typeof(bool) }),
            postfix: new HarmonyMethod(typeof(SkillPerks), nameof(BobberBar_Postfix)));
        harmony.Patch(AccessTools.Method(typeof(GameLocation), nameof(GameLocation.OnHarvestedForage)),
            postfix: new HarmonyMethod(typeof(SkillPerks), nameof(Forage_Postfix)));
        harmony.Patch(AccessTools.Method(typeof(BusStop), nameof(BusStop.checkAction)),
            prefix: new HarmonyMethod(typeof(SkillPerks), nameof(BusStop_Prefix)));
        helper.Events.GameLoop.DayStarted += (_, _) => ApplyCombatBuff();
        helper.Events.GameLoop.SaveLoaded += (_, _) => ApplyCombatBuff();
        helper.Events.GameLoop.OneSecondUpdateTicked += (_, e) =>
        {
            if (e.IsMultipleOf(300) && Context.IsWorldReady && Lvl(Skills.Combat) != buffLevel)
                ApplyCombatBuff();
        };
    }

    private static int Lvl(int skill) => Skills.Level(Game1.player, skill);

    // ---------- Farming ----------

    private static void Harvest_Prefix(out int __state) => __state = Game1.player?.experiencePoints[Skills.Farming] ?? 0;

    /// <summary>A crop was picked (Farming XP went up): maybe one more, and a little extra XP.</summary>
    private static void Harvest_Postfix(Crop __instance, bool __result, int xTile, int yTile, int __state, StardewValley.Characters.JunimoHarvester? junimoHarvester)
    {
        if (junimoHarvester != null || Game1.player == null)
            return;
        int gained = Game1.player.experiencePoints[Skills.Farming] - __state;
        if (gained <= 0)
            return;
        int level = Lvl(Skills.Farming);
        if (level >= 20)
            Game1.player.gainExperience(Skills.Farming, Math.Max(1, gained / 10));
        double chance = level >= 42 ? 0.20 : level >= 32 ? 0.15 : level >= 22 ? 0.10 : level >= 12 ? 0.05 : 0;
        if (chance > 0 && Game1.random.NextDouble() < chance && __instance.indexOfHarvest.Value is { Length: > 0 } id)
        {
            var extra = ItemRegistry.Create("(O)" + id);
            Game1.createItemDebris(extra, new Vector2(xTile * 64 + 32, yTile * 64 + 32), -1);
            Game1.addHUDMessage(HUDMessage.ForCornerTextbox($"Bonus {extra.DisplayName}!"));
        }
    }

    // ---------- Fishing ----------

    private static void PullFish_Prefix(ref int numCaught, bool fromFishPond, bool isBossFish, string fishId)
    {
        if (fromFishPond || isBossFish || numCaught < 1 || Game1.player == null)
            return;
        int level = Lvl(Skills.Fishing);
        double chance = level >= 40 ? 0.10 : level >= 25 ? 0.05 : 0;
        if (chance > 0 && ItemRegistry.GetData(fishId)?.Category == SObject.FishCategory && Game1.random.NextDouble() < chance)
            numCaught++;
    }

    /// <summary>Every Fishing level past 10 makes the bar 2 pixels taller (up to +80 at 50).</summary>
    private static void BobberBar_Postfix(BobberBar __instance)
    {
        int extra = Math.Clamp(Lvl(Skills.Fishing) - 10, 0, 40) * 2;
        if (extra == 0)
            return;
        __instance.bobberBarHeight = Math.Min(__instance.bobberBarHeight + extra, 540);
        __instance.bobberBarPos = 568 - __instance.bobberBarHeight;
    }

    // ---------- Foraging ----------

    private static void Forage_Postfix(Farmer who, SObject forage)
    {
        if (who == null || !who.IsLocalPlayer)
            return;
        int level = Lvl(Skills.Foraging);
        double chance = level >= 40 ? 0.20 : level >= 20 ? 0.10 : 0;
        if (chance > 0 && Game1.random.NextDouble() < chance && who.addItemToInventoryBool(forage.getOne()))
            Game1.addHUDMessage(HUDMessage.ForCornerTextbox($"Double {forage.DisplayName}!"));
    }

    // ---------- Combat ----------

    /// <summary>
    /// Combat 15+: a bus pass. Rides to the desert are free, and the bus runs for you even if the town's bus
    /// isn't repaired yet (on worlds where the town isn't finished).
    /// </summary>
    private static bool BusStop_Prefix(BusStop __instance, xTile.Dimensions.Location tileLocation)
    {
        if (__instance.getTileIndexAt(tileLocation, "Buildings", "outdoors") != 1057 || Lvl(Skills.Combat) < BusPassLevel)
            return true; // vanilla: paid ticket, or "out of service"
        if (Game1.player.isRidingHorse() && Game1.player.mount != null)
            return true;
        __instance.createQuestionDialogue("Your bus pass works here. Ride to the Calico Desert for free?",
            __instance.createYesNoResponses(), (who, answer) =>
            {
                if (answer == "Yes")
                    Game1.warpFarmer("Desert", 35, 43, flip: false); // where the Desert totem lands; the bus home is at the stop
            });
        return false;
    }

    private static void ApplyCombatBuff()
    {
        if (!Context.IsWorldReady || Game1.player == null)
            return;
        int level = Lvl(Skills.Combat);
        buffLevel = level;
        int def = new[] { 20, 30, 40, 50 }.Count(l => level >= l);
        int atk = new[] { 25, 35, 45 }.Count(l => level >= l);
        Game1.player.buffs.Remove(BuffId);
        if (def + atk == 0)
            return;
        var fx = new StardewValley.Buffs.BuffEffects();
        fx.Defense.Value = def;
        fx.Attack.Value = atk;
        Game1.player.applyBuff(new Buff(BuffId, "Combat level", "Combat level", Buff.ENDLESS, effects: fx,
            displayName: "Combat training", description: $"Combat level {level}: +{atk} attack, +{def} defense") { visible = false });
    }

    // ---------- server: farm forage and fish ----------

    private static int Best(string farm, int skill) => Farms.MembersOf(farm).Select(f => Skills.Level(f, skill)).DefaultIfEmpty(0).Max();

    private static readonly Dictionary<Season, string[]> RareMushrooms = new()
    {
        [Season.Spring] = new[] { "(O)257" },          // morel
        [Season.Summer] = new[] { "(O)420", "(O)259" }, // red mushroom, fiddlehead fern
        [Season.Fall] = new[] { "(O)281", "(O)422" },   // chanterelle, purple mushroom
        [Season.Winter] = new[] { "(O)416" },           // snow yam
    };

    /// <summary>Foraging 15+: wild forage appears on the farm's grass each morning (never more than 20 at once).</summary>
    private static void SpawnFarmForage()
    {
        foreach (string name in Farms.AllNames)
        {
            if (Game1.getLocationFromName(name) is not Farm farm)
                continue;
            int level = Best(name, Skills.Foraging);
            int count = level >= 45 ? 6 : level >= 25 ? 4 : level >= 15 ? 2 : 0;
            int already = farm.objects.Values.Count(o => o.modData.ContainsKey(ForageKey));
            count = Math.Min(count, 20 - already);
            int placed = 0;
            for (int tries = 0; placed < count && tries < 200; tries++)
            {
                Vector2 tile = farm.getRandomTile();
                if (!farm.doesTileHavePropertyNoNull((int)tile.X, (int)tile.Y, "Type", "Back").Equals("Grass")
                    || !farm.CanItemBePlacedHere(tile) || farm.hasTileAt((int)tile.X, (int)tile.Y, "AlwaysFront")
                    || farm.terrainFeatures.ContainsKey(tile))
                    continue;
                string? id = level >= 35 && Game1.random.NextDouble() < 0.2 && RareMushrooms.TryGetValue(Game1.season, out var rare)
                    ? rare[Game1.random.Next(rare.Length)]
                    : Utility.getRandomBasicSeasonalForageItem(Game1.season, Game1.random.Next()) is { } basic ? "(O)" + basic : null;
                if (id == null)
                    continue;
                var item = ItemRegistry.Create<SObject>(id);
                item.IsSpawnedObject = true;
                item.CanBeSetDown = false;
                item.modData[ForageKey] = "1";
                farm.objects.Add(tile, item);
                placed++;
            }
        }
    }

    private static readonly AccessTools.FieldRef<GameLocation, int> SplashTime = AccessTools.FieldRefAccess<GameLocation, int>("fishSplashPointTime");

    /// <summary>Fishing 20+: a fish bubble shows up on the farm's water now and then (vanilla only does this on Riverland).</summary>
    private static void FarmTenMinute_Postfix(Farm __instance)
    {
        if (!Farms.IsFarm(__instance) || !__instance.fishSplashPoint.Value.Equals(Point.Zero) || Game1.timeOfDay >= 2300)
            return;
        int level = Best(__instance.Name, Skills.Fishing);
        double chance = level >= 30 ? 0.5 : level >= 20 ? 0.25 : 0;
        if (chance == 0 || Game1.random.NextDouble() >= chance)
            return;
        var back = __instance.map.GetLayer("Back");
        for (int i = 0; i < 30; i++)
        {
            var p = new Point(Game1.random.Next(back.LayerWidth), Game1.random.Next(back.LayerHeight));
            if (!__instance.isOpenWater(p.X, p.Y) || __instance.doesTileHaveProperty(p.X, p.Y, "NoFishing", "Back") != null)
                continue;
            int d = FishingRod.distanceToLand(p.X, p.Y, __instance);
            if (d <= 1 || d >= 5)
                continue;
            SplashTime(__instance) = Game1.timeOfDay;
            __instance.fishSplashPoint.Value = p;
            return;
        }
    }
}
