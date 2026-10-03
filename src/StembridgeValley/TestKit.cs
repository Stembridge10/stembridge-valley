using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace StembridgeValley;

/// <summary>
/// Test worlds only: when the server runs with SV_TEST_KIT=1, each player who joins is raised to the skill levels
/// below (if lower), so the owner can try the quarry and the 11-50 perks without weeks of grinding. The server
/// decides; a player's game never turns this on by itself, and the live server never sets SV_TEST_KIT.
/// </summary>
internal static class TestKit
{
    private const string Message = "TestKit";
    private static readonly Dictionary<int, int> Levels = new()
    {
        [Skills.Mining] = 16, [Skills.Fishing] = 21, [Skills.Foraging] = 16, [Skills.Combat] = 16, [Skills.Farming] = 13,
    };
    private static IModHelper Helper = null!;
    private static readonly HashSet<long> Sent = new();

    public static void Apply(IModHelper helper)
    {
        Helper = helper;
        if (SV.Role == Role.Server)
        {
            if (Environment.GetEnvironmentVariable("SV_TEST_KIT") != "1")
                return;
            Log.Info("Test kit is on: joining players get raised skill levels (test world only).");
            helper.Events.GameLoop.OneSecondUpdateTicked += (_, e) =>
            {
                if (!Context.IsWorldReady || !e.IsMultipleOf(180))
                    return;
                foreach (Farmer f in Game1.getOnlineFarmers())
                    if (!f.IsMainPlayer && f.isCustomized.Value && Sent.Add(f.UniqueMultiplayerID))
                        helper.Multiplayer.SendMessage(1, Message, new[] { helper.ModRegistry.ModID }, new[] { f.UniqueMultiplayerID });
            };
        }
        else
            helper.Events.Multiplayer.ModMessageReceived += (_, e) =>
            {
                if (e.FromModID != Helper.ModRegistry.ModID || e.Type != Message || e.FromPlayerID != Game1.MasterPlayer?.UniqueMultiplayerID)
                    return;
                var raised = new List<string>();
                foreach (var (skill, level) in Levels)
                {
                    int need = Skills.XpForLevel(level) - Skills.Xp(Game1.player, skill);
                    if (need <= 0)
                        continue;
                    Game1.player.gainExperience(skill, need);
                    raised.Add($"{Skills.Name(skill)} {level}");
                }
                if (raised.Count > 0)
                    Game1.chatBox?.addInfoMessage("Test world: raised you to " + string.Join(", ", raised) + ".");
            };
    }
}
