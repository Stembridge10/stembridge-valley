using HarmonyLib;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Objects;

namespace StembridgeValley;

/// <summary>
/// The starter gift box in a new house/cabin always holds 15 parsnip seeds, which are useless after spring.
/// In this shared world people join in any season, so the box gets that season's beginner crop instead.
/// </summary>
internal static class StarterSeeds
{
    private static string SeedFor(Season season) => season switch
    {
        Season.Summer => "(O)487", // corn seeds (grows into fall too)
        Season.Fall => "(O)487",   // corn seeds
        Season.Winter => "(O)498", // winter seeds (wild forage mix)
        _ => "(O)472",             // spring: parsnip seeds, as vanilla
    };

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(FarmHouse), "AddStarterGiftBox"),
            postfix: new HarmonyMethod(typeof(StarterSeeds), nameof(Postfix)));
    }

    private static void Postfix(FarmHouse __instance)
    {
        try
        {
            string seed = SeedFor(Game1.season);
            if (seed == "(O)472")
                return;
            foreach (var chest in __instance.objects.Values.OfType<Chest>().Where(c => c.giftboxIsStarterGift.Value))
                for (int i = 0; i < chest.Items.Count; i++)
                    if (chest.Items[i]?.QualifiedItemId == "(O)472")
                        chest.Items[i] = ItemRegistry.Create(seed, chest.Items[i].Stack);
        }
        catch (Exception ex)
        {
            Log.Warn($"Starter seeds: {ex.Message}");
        }
    }
}
