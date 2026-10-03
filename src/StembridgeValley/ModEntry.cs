using HarmonyLib;
using StardewModdingAPI;

namespace StembridgeValley;

internal sealed class ModEntry : Mod
{
    public override void Entry(IModHelper helper)
    {
        Log.Monitor = Monitor;
        SV.Load();
        SV.Config = helper.ReadConfig<ModConfig>();
        if (int.TryParse(Environment.GetEnvironmentVariable("SV_MINUTES_PER_DAY"), out int minutes) && minutes > 0)
            SV.Config.RealMinutesPerDay = minutes;
        if (int.TryParse(Environment.GetEnvironmentVariable("SV_FARM_COUNT"), out int farmCount) && farmCount >= 0)
            SV.Config.Server.FarmCount = farmCount;
        if (int.TryParse(Environment.GetEnvironmentVariable("SV_MAX_PLAYERS"), out int maxPlayers) && maxPlayers > 1)
            SV.Config.Server.MaxPlayers = maxPlayers;
        // Cloud servers have no home router to ask for an open port.
        if (Environment.GetEnvironmentVariable("SV_NO_UPNP") == "1")
            SV.Config.Server.TryAutomaticPortForward = false;

        if (SV.Role == Role.None)
        {
            Monitor.Log("Not started by the Stembridge Valley launcher; staying off.", LogLevel.Info);
            return;
        }

        Monitor.Log($"Stembridge Valley {ModManifest.Version} as {SV.Role}, pack {SV.PackVersion}.", LogLevel.Info);
        if (string.IsNullOrEmpty(SV.Password))
            Monitor.Log("No server password set.", LogLevel.Warn);

        var harmony = new Harmony(ModManifest.UniqueID);
        Rules.Apply(helper, harmony);
        Network.Apply(harmony);
        Farms.Apply(helper, harmony);
        FarmMaps.Apply(helper, harmony);
        FarmSettings.Apply(helper);
        Skills.Apply(helper, harmony);
        Collection.Apply(helper, harmony);
        Pets.Apply(helper, harmony);
        Quarry.Apply(helper, harmony);
        SkillPerks.Apply(helper, harmony);
        if (SV.Role == Role.Server)
        {
            Server.Apply(helper, harmony);
            Discord.Apply(helper);
            Perf.Apply(helper);
        }
        else
        {
            Join.Apply(helper);
            Bot.Apply(helper, harmony);
        }
        TestProbe.Apply(helper);
    }
}
